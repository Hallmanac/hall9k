using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Marten;
using Marten.Linq.MatchesSql;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Owns the node-wide launch hold's own state on the node stream (task: a session that exits at
/// once with no work done is treated as the node failing to launch sessions): raising it, a run
/// joining it, clearing it, and reading who is currently held. Deliberately carries no dependency
/// on <c>RunSupervisor</c>, <c>ReviewEngine</c>, or <c>NodeContext</c> — every method takes the
/// node id explicitly instead, both of that node stream's genuine reasons: <c>RunSupervisor</c>
/// and <c>ReviewEngine</c> both depend on THIS, one direction only, since <c>RunSupervisor</c>
/// already depends on <c>ReviewEngine</c> and a dependency back from here would close the cycle
/// — and <c>ReviewEngine</c> itself carries no <c>NodeContext</c> at all, reading the node id off
/// the run it is already holding (<c>RunDetails.NodeId</c>) instead, which is the more honest
/// source in any case: the node that dispatched a run, not merely whichever node this process
/// happens to be. Actually resuming a held run is <c>RunSupervisor</c>'s own job (it already owns
/// both re-entry paths); <see cref="LaunchHoldMonitor"/> is what ties this engine's state to that
/// action, the same split <c>TokenBudgetRetryEngine</c> draws between parking (inline in
/// <c>RunSupervisor</c>/<c>ReviewEngine</c>) and resuming (its own sweep).
/// </summary>
public sealed class LaunchHoldEngine(IDocumentStore store, ILogger<LaunchHoldEngine> logger)
{
    /// <summary>
    /// Raises the hold if it is not already standing, and always records this run as joining it
    /// — whether this run is the one that raised it or a later one the same outage caught. The
    /// warn line fires only on the raise itself, never on a join, which is what keeps it to
    /// exactly one per episode (an operator watching h9kd.log sees one line, not one per task the
    /// outage happens to catch).
    /// </summary>
    public async Task RaiseOrJoinAsync(
        Guid nodeId, Guid runId, string observedMessage, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        NodeDetails? details = await session.LoadAsync<NodeDetails>(nodeId, cancellationToken);
        bool alreadyActive = details?.LaunchHoldActive == true;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!alreadyActive)
        {
            session.Events.Append(nodeId, new NodeLaunchHoldRaised(nodeId, observedMessage, now));
        }

        session.Events.Append(nodeId, new NodeLaunchHoldRunHeld(nodeId, runId, now));
        await session.SaveChangesAsync(cancellationToken);

        if (!alreadyActive)
        {
            logger.LogWarning(
                "Node-wide launch hold raised: a session exited with one turn and no tokens spent — the "
                + "dispatcher stops claiming and every in-place error retry waits on this hold instead of its "
                + "own backoff, until a relaunch records tokens. {Message}", observedMessage);
        }
    }

    /// <summary>
    /// Clears the hold when a session just proved this node can still launch working sessions —
    /// called from the very completion sites that would otherwise retry or park a session, on
    /// whichever branch is NOT the zero-work shape, whether or not a hold happens to be standing
    /// (the common case is a no-op single doc read). Returns whether it actually cleared one, so
    /// a caller can tell "nothing to clear" apart from "cleared" without a second read; nothing
    /// here resumes the runs that were waiting on it — that is <see cref="LaunchHoldMonitor"/>'s
    /// own sweep, which finds them the same way it finds the oldest run to probe.
    /// </summary>
    public async Task<bool> ClearIfActiveAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        NodeDetails? details = await session.LoadAsync<NodeDetails>(nodeId, cancellationToken);
        if (details is not { LaunchHoldActive: true })
        {
            return false;
        }

        session.Events.Append(nodeId, new NodeLaunchHoldCleared(nodeId, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);
        logger.LogWarning(
            "Node-wide launch hold cleared after {ProbeCount} probe(s) — a relaunch recorded tokens; "
            + "{HeldRuns} held run(s) resume through their own re-entry paths",
            details.LaunchHoldProbeCount, details.LaunchHoldRunCount);
        return true;
    }

    /// <summary>Records that the probe relaunched <paramref name="runId"/> — the held work itself is the probe, so this is the only record of the attempt.</summary>
    public async Task RecordProbeAsync(Guid nodeId, Guid runId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(nodeId, new NodeLaunchHoldProbed(nodeId, runId, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>This node's current hold state, or null if this node has never registered — never null once it has, whether or not a hold is standing.</summary>
    public async Task<NodeDetails?> CurrentHoldAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        return await session.LoadAsync<NodeDetails>(nodeId, cancellationToken);
    }

    /// <summary>Every run this node currently holds, whatever leg caught it — build, a review pass, the fix session, or rebase recovery.</summary>
    public async Task<IReadOnlyList<RunDetails>> HeldRunsAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        return await session.Query<RunDetails>()
            .Where(r => r.NodeId == nodeId)
            .Where(r => r.MatchesSql("d.data ->> 'state' = ?", RunState.LaunchHeld.Value))
            .ToListAsync(cancellationToken);
    }

    /// <summary>The run the probe relaunches next — the one that has waited longest, so one persistently dead run never starves the others behind it.</summary>
    public async Task<RunDetails?> OldestHeldRunAsync(Guid nodeId, CancellationToken cancellationToken) =>
        (await HeldRunsAsync(nodeId, cancellationToken)).OrderBy(run => run.LaunchHeldAt).FirstOrDefault();
}
