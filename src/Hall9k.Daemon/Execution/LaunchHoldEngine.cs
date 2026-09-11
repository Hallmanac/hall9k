using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using JasperFx.Events;
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
    /// Bounds the retry in <see cref="AppendWithConcurrencyRetryAsync"/> — a handful of attempts
    /// is enough to ride out genuine contention on this node's own stream (independent pre-PR
    /// review, cycle 1, adversarial lens), and a caller that still loses the race after this many
    /// immediate retries is in a state worth logging rather than looping on forever.
    /// </summary>
    private const int MaxConcurrentAppendAttempts = 5;

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
        bool raised = await AppendWithConcurrencyRetryAsync(async () =>
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
            return !alreadyActive;
        }, cancellationToken);

        if (raised)
        {
            logger.LogWarning(
                "Node-wide launch hold raised: a session exited with one turn and no tokens spent — the "
                + "dispatcher stops claiming and every in-place error retry waits on this hold instead of its "
                + "own backoff, until a relaunch records tokens. {Message}", observedMessage);
        }
    }

    /// <summary>
    /// Clears the hold unconditionally when one stands, whether or not the evidence for it is
    /// fresh — the general-purpose clear, kept for a caller that has already decided the hold
    /// should end (test teardown, an operator-facing force-clear) rather than one weighing
    /// whether a particular session's completion counts as proof. <see cref="ClearIfEvidencedAsync"/>
    /// is what <c>RunSupervisor</c> and <c>ReviewEngine</c> call from a session's own completion.
    /// </summary>
    public Task<bool> ClearIfActiveAsync(Guid nodeId, CancellationToken cancellationToken) =>
        ClearIfAsync(nodeId, sessionStartedAt: null, cancellationToken);

    /// <summary>
    /// Clears the hold when a session just proved this node can still launch working sessions —
    /// called from the very completion sites that would otherwise retry or park a session, on
    /// whichever branch is NOT the zero-work shape, whether or not a hold happens to be standing
    /// (the common case is a no-op single doc read). <paramref name="sessionStartedAt"/> is when
    /// THIS session's own process started: a session already running before the hold's own raise
    /// is not evidence a fresh launch works right now, only that a session already in flight
    /// eventually finished for its own, unrelated reason (independent pre-PR review, cycle 1,
    /// adversarial lens) — such a completion is left alone, neither clearing the hold nor raising
    /// it, since it is not itself a launch failure either. Returns whether it actually cleared
    /// one, so a caller can tell "nothing to clear" (or "not fresh enough to count") apart from
    /// "cleared" without a second read; nothing here resumes the runs that were waiting on it —
    /// that is <see cref="LaunchHoldMonitor"/>'s own sweep, which finds them the same way it finds
    /// the oldest run to probe.
    /// </summary>
    public Task<bool> ClearIfEvidencedAsync(
        Guid nodeId, DateTimeOffset sessionStartedAt, CancellationToken cancellationToken) =>
        ClearIfAsync(nodeId, sessionStartedAt, cancellationToken);

    /// <summary>
    /// How far before the hold's own raise <paramref name="sessionStartedAt"/> in
    /// <see cref="ClearIfAsync"/> may still count as fresh — the same tolerance
    /// <c>ProcessManagerBase.StartTimeTolerance</c> already gives <see cref="System.Diagnostics.Process.StartTime"/>
    /// elsewhere in this daemon, and for the identical reason: an OS-reported process start time
    /// is not guaranteed sub-second precision, so a session that genuinely started after the raise
    /// can still read a start time a moment before it. Small enough that a session actually
    /// already running for minutes before the outage — the shape this check exists to reject — is
    /// still correctly excluded.
    /// </summary>
    private static readonly TimeSpan EvidenceClockGrace = TimeSpan.FromSeconds(2);

    private async Task<bool> ClearIfAsync(
        Guid nodeId, DateTimeOffset? sessionStartedAt, CancellationToken cancellationToken) =>
        await AppendWithConcurrencyRetryAsync(async () =>
        {
            await using IDocumentSession session = store.LightweightSession();
            NodeDetails? details = await session.LoadAsync<NodeDetails>(nodeId, cancellationToken);
            if (details is not { LaunchHoldActive: true })
            {
                return false;
            }

            if (sessionStartedAt is { } startedAt && details.LaunchHoldRaisedAt is { } raisedAt
                && startedAt < raisedAt - EvidenceClockGrace)
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
        }, cancellationToken);

    /// <summary>Records that the probe relaunched <paramref name="runId"/> — the held work itself is the probe, so this is the only record of the attempt.</summary>
    public Task RecordProbeAsync(Guid nodeId, Guid runId, CancellationToken cancellationToken) =>
        AppendWithConcurrencyRetryAsync(async () =>
        {
            await using IDocumentSession session = store.LightweightSession();
            session.Events.Append(nodeId, new NodeLaunchHoldProbed(nodeId, runId, DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);

    /// <summary>
    /// Retries the whole load-decide-append-save cycle when a concurrent writer to this same node
    /// stream — another run's own raise or join, another probe, another clear — committed first
    /// (independent pre-PR review, cycle 1, adversarial lens: this node stream is now written by
    /// every monitor task and review loop at once, and an unversioned append can still lose that
    /// race and throw <see cref="EventStreamUnexpectedMaxEventIdException"/>). Unlike the
    /// task-stream generation fence elsewhere in this daemon, where the same exception means the
    /// caller's own write is stale and must be dropped, every operation here is additive or
    /// idempotent against fresh state — a run joining, a probe recorded, a hold cleared — so
    /// replaying it against the now-current stream is always safe rather than a race to drop.
    /// </summary>
    private async Task<T> AppendWithConcurrencyRetryAsync<T>(
        Func<Task<T>> attempt, CancellationToken cancellationToken)
    {
        for (int i = 1; i < MaxConcurrentAppendAttempts; i++)
        {
            try
            {
                return await attempt();
            }
            catch (EventStreamUnexpectedMaxEventIdException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(
                    "Node-wide launch hold: lost a concurrent append race on attempt {Attempt} of {Max} — "
                    + "retrying against the current stream", i, MaxConcurrentAppendAttempts);
            }
        }

        return await attempt();
    }

    /// <summary>This node's current hold state, or null if this node has never registered — never null once it has, whether or not a hold is standing.</summary>
    public async Task<NodeDetails?> CurrentHoldAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        return await session.LoadAsync<NodeDetails>(nodeId, cancellationToken);
    }

    /// <summary>The run the daemon's own launch-hold probe most recently relaunched, or null if nothing has been probed this episode.</summary>
    public async Task<RunDetails?> LoadRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        return await session.LoadAsync<RunDetails>(runId, cancellationToken);
    }

    /// <summary>
    /// Every run this node currently holds, whatever leg caught it — build, a review pass, the fix
    /// session, or rebase recovery. Widened with <see cref="SentinelPrReviewCandidatesAsync"/> for
    /// the one class this node-only filter misses (independent pre-PR review, cycle 1, both
    /// lenses): a Now-speed auto-pr-review sentinel run carries the ceiling-exempt
    /// <see cref="Guid.Empty"/> on <see cref="RunDetails.NodeId"/>, exactly like
    /// <c>RunSupervisor.SentinelPrReviewCandidatesAsync</c> and
    /// <c>TokenBudgetRetryEngine.SentinelPrReviewCandidatesAsync</c> already widen for — without
    /// this, such a run is held (<c>CompleteRunAsync</c> raises the hold on the dispatching node
    /// regardless), but never found again: the probe never relaunches it, and if it is the only
    /// run held, the hold never clears and the dispatcher never claims again.
    /// </summary>
    public async Task<IReadOnlyList<RunDetails>> HeldRunsAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        IReadOnlyList<RunDetails> ownNode = await session.Query<RunDetails>()
            .Where(r => r.NodeId == nodeId)
            .Where(r => r.MatchesSql("d.data ->> 'state' = ?", RunState.LaunchHeld.Value))
            .ToListAsync(cancellationToken);
        IReadOnlyList<RunDetails> sentinelPrReview = await SentinelPrReviewCandidatesAsync(
            session, nodeId, cancellationToken);
        return [.. ownNode, .. sentinelPrReview];
    }

    /// <summary>
    /// The same sentinel-run widening <c>RunSupervisor.SentinelPrReviewCandidatesAsync</c> and
    /// <c>TokenBudgetRetryEngine.SentinelPrReviewCandidatesAsync</c> already apply for adoption
    /// and the budget retry sweep, applied here for the launch-hold probe: every launch-held run
    /// carrying the ceiling-exempt <see cref="Guid.Empty"/> whose own
    /// <see cref="RunDetails.DispatchingNodeId"/> names this node and whose owning task is a
    /// pr-review task.
    /// </summary>
    private static async Task<IReadOnlyList<RunDetails>> SentinelPrReviewCandidatesAsync(
        IQuerySession session, Guid nodeId, CancellationToken cancellationToken)
    {
        IReadOnlyList<RunDetails> sentinel = await session.Query<RunDetails>()
            .Where(r => r.NodeId == Guid.Empty)
            .Where(r => r.DispatchingNodeId == nodeId)
            .Where(r => r.MatchesSql("d.data ->> 'state' = ?", RunState.LaunchHeld.Value))
            .ToListAsync(cancellationToken);
        if (sentinel.Count == 0)
        {
            return [];
        }

        List<RunDetails> prReview = [];
        foreach (RunDetails run in sentinel)
        {
            TaskDetails? owner = await session.LoadAsync<TaskDetails>(run.TaskId, cancellationToken);
            if (owner?.Type == TaskType.PrReview)
            {
                prReview.Add(run);
            }
        }

        return prReview;
    }

    /// <summary>The run the probe relaunches next — the one that has waited longest, so one persistently dead run never starves the others behind it.</summary>
    public async Task<RunDetails?> OldestHeldRunAsync(Guid nodeId, CancellationToken cancellationToken) =>
        (await HeldRunsAsync(nodeId, cancellationToken)).OrderBy(run => run.LaunchHeldAt).FirstOrDefault();
}
