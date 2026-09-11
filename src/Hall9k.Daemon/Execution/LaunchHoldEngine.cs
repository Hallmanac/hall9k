using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using JasperFx.Events;
using Marten;
using Marten.Events;
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
            // Pinned with expectedVersion below, not left as a bare append (independent pre-PR
            // review, cycle 3, conformance lens): two runs finishing within milliseconds of the
            // same outage both used to read LaunchHoldActive false, both append, and both save
            // clean — a document load carries no version to conflict on, so nothing ever threw
            // EventStreamUnexpectedMaxEventIdException for AppendWithConcurrencyRetryAsync to
            // retry. The result was two NodeLaunchHoldRaised events (two warn lines instead of
            // exactly one) and the second one resetting LaunchHoldRunIds, silently dropping the
            // first run's own join from the count. Pinning the stream's version here makes the
            // second writer's save lose the race for real, so its retry replays against the
            // now-current state and correctly sees the hold already active.
            StreamState? streamState = await session.Events.FetchStreamStateAsync(nodeId, cancellationToken);
            NodeDetails? details = await session.LoadAsync<NodeDetails>(nodeId, cancellationToken);
            bool alreadyActive = details?.LaunchHoldActive == true;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            object[] events = alreadyActive
                ? [new NodeLaunchHoldRunHeld(nodeId, runId, now)]
                : [new NodeLaunchHoldRaised(nodeId, observedMessage, now), new NodeLaunchHoldRunHeld(nodeId, runId, now)];
            session.Events.Append(nodeId, expectedVersion: (streamState?.Version ?? 0) + events.Length, events);
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
        ClearIfAsync(nodeId, sessionStartedAt: null, reason: "force-cleared", cancellationToken);

    /// <summary>
    /// Clears a standing hold once nothing is left that could ever supply
    /// <see cref="ClearIfEvidencedAsync"/>'s own evidence (independent pre-PR review, cycle 3,
    /// both lenses): <see cref="LaunchHoldMonitor.SweepOnceAsync"/> calls this the moment
    /// <see cref="OldestHeldRunAsync"/> finds an active hold with no held run left to probe — the
    /// shape a run reaches when its own claim moved on (<c>h9k task abandon</c>, a lease-expiry
    /// requeue-and-reclaim) while it sat <see cref="RunState.LaunchHeld"/>, since retiring a stale
    /// claim with <c>RunSuperseded</c> takes that run out of every held-run query without ever
    /// completing a session for <see cref="ClearIfEvidencedAsync"/> to read. Left standing, this
    /// hold would never clear again: no future session could complete on this run to supply
    /// evidence, the dispatcher's claim gate would stay shut for good, and <c>h9k status</c> would
    /// keep telling a human to sign in long after they already have. A genuinely broken node
    /// raises a fresh hold the moment its very next launch fails the same way.
    /// <para>
    /// The caller's own "nothing left held" read is never trusted on its own (independent pre-PR
    /// review, cycle 4, adversarial lens): between that read and this clear, an unrelated run can
    /// hit a genuine launch failure and join the still-standing hold, and clearing over that join
    /// reopens the dispatcher's claim gate into a node that is still broken and resets the probe's
    /// own backoff. So this re-checks inside the same attempt that saves the clear, with the
    /// node stream's version pinned: a join that commits after that check makes the save lose
    /// the race, and <see cref="AppendWithConcurrencyRetryAsync"/>'s replay then sees it. The
    /// check itself reads <see cref="NodeDetails.LaunchHoldRunIds"/>, not only
    /// <see cref="HeldRunsAsync"/>, because a join lands on the node stream BEFORE the run's own
    /// <see cref="RunLaunchHeld"/> lands on the run stream (the crash-safe order
    /// <c>RunSupervisor.CompleteRunAsync</c> documents): for that window the run is already
    /// named by this episode but still reads the live state its session completed in, invisible
    /// to any <see cref="RunState.LaunchHeld"/> query.
    /// </para>
    /// </summary>
    public Task<bool> ClearIfNothingLeftHeldAsync(Guid nodeId, CancellationToken cancellationToken) =>
        AppendWithConcurrencyRetryAsync(async () =>
        {
            await using IDocumentSession session = store.LightweightSession();
            StreamState? streamState = await session.Events.FetchStreamStateAsync(nodeId, cancellationToken);
            NodeDetails? details = await session.LoadAsync<NodeDetails>(nodeId, cancellationToken);
            if (details is not { LaunchHoldActive: true }
                || await AnyRunStillHeldAsync(session, nodeId, details, cancellationToken))
            {
                return false;
            }

            session.Events.Append(
                nodeId, expectedVersion: (streamState?.Version ?? 0) + 1,
                new NodeLaunchHoldCleared(nodeId, DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(cancellationToken);
            LogCleared(details, "every held run's own claim moved on since — nothing left to probe");
            return true;
        }, cancellationToken);

    /// <summary>
    /// Whether anything this episode holds could still supply evidence or rejoin: a run the probe
    /// can still find (<see cref="HeldRunsAsync"/>, exactly the set <see cref="OldestHeldRunAsync"/>
    /// draws from, so this never waits on a held run no probe could ever reach), or one this
    /// episode has already named that is still <see cref="RunState.IsLive"/>: a join whose own
    /// <see cref="RunLaunchHeld"/> has not landed yet, or a probe still in flight in a leg
    /// <c>LaunchHoldMonitor</c>'s own still-running check does not read (a review pass, the fix
    /// session, rebase recovery, or the pr-review conformance lens). Every live run leaves that
    /// state on its own, through a completion that clears the hold with evidence, rejoins it, or
    /// parks or ends the run, so waiting on one can never wedge the hold open the way a
    /// superseded run did.
    /// </summary>
    private static async Task<bool> AnyRunStillHeldAsync(
        IQuerySession session, Guid nodeId, NodeDetails details, CancellationToken cancellationToken)
    {
        if ((await HeldRunsAsync(session, nodeId, cancellationToken)).Count > 0)
        {
            return true;
        }

        if (details.LaunchHoldRunIds.Count == 0)
        {
            return false;
        }

        IReadOnlyList<RunDetails> named = await session.LoadManyAsync<RunDetails>(
            cancellationToken, details.LaunchHoldRunIds);
        return named.Any(run => run.State.IsLive);
    }

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
        ClearIfAsync(nodeId, sessionStartedAt, reason: "a relaunch recorded tokens", cancellationToken);

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
        Guid nodeId, DateTimeOffset? sessionStartedAt, string reason, CancellationToken cancellationToken) =>
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
            LogCleared(details, reason);
            return true;
        }, cancellationToken);

    private void LogCleared(NodeDetails details, string reason) =>
        logger.LogWarning(
            "Node-wide launch hold cleared after {ProbeCount} probe(s) — {Reason}; "
            + "{HeldRuns} held run(s) resume through their own re-entry paths",
            details.LaunchHoldProbeCount, reason, details.LaunchHoldRunCount);

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
        return await HeldRunsAsync(session, nodeId, cancellationToken);
    }

    private static async Task<IReadOnlyList<RunDetails>> HeldRunsAsync(
        IQuerySession session, Guid nodeId, CancellationToken cancellationToken)
    {
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
