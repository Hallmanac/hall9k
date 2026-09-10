using System.Text;
using Hall9k.Daemon.ProcessManagement;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// A completed wait: the session's own result, or null when it genuinely died without one, plus
/// every pid <see cref="SessionResultWaiter.WaitAsync"/> itself found still running behind it and
/// terminated (root included when it was still alive at the moment of the call) — a caller logs
/// these by the session they belonged to instead of calling
/// <see cref="IProcessManager.TerminateTree"/> a second time on an already-dead root.
/// <paramref name="EndedAfterResultGrace"/> is true when the wait gave up on the root exiting by
/// itself — <see cref="SessionResultWaiter.PostResultGrace"/> elapsed with a result already seen
/// and the process still alive — and forced the termination that produced
/// <paramref name="Lingering"/>, rather than the root having already died on its own; a caller
/// logs this the same way it already logs a non-empty <paramref name="Lingering"/> (independent
/// pre-PR review, cycle 3, human verdict).
/// </summary>
public sealed record SessionWaitResult(AgentResult? Result, IReadOnlyList<int> Lingering, bool EndedAfterResultGrace = false);

/// <summary>
/// Waits for a spawned session's terminal result event by tailing its stream file. A result
/// line alone does not finalize the wait: a stream can hold more than one, so completion also
/// requires <paramref name="processManager"/> to report the process gone before the whole
/// file is re-read for the session's real, combined result (discovery cc9b7aec) — the exit
/// code itself is still never consulted (Decisions Log #2), only whether the process has
/// exited. Shared by every caller that spawns a session and then blocks on it — the pre-PR
/// review loop's legs (log #24) and the context-synthesis pass (log #36) — so the grace
/// window after process death, which exists so buffered output still gets read, behaves
/// identically wherever a session is awaited.
/// <para>
/// Also owns the process-tree cleanup a completed session leaves behind (task: the daemon
/// terminates a completed session's process tree before it starts any gate or another session
/// in the same worktree): a result line being on disk is not proof the process that wrote it,
/// or whatever it backgrounded, has actually exited. Waiting for the root's own death — required
/// so a still-running session's first result line is never read as its last (independent pre-PR
/// review, cycle 3, adversarial lens) — means <see cref="IProcessManager.TerminateTree"/> can no
/// longer be called while the root is likely still alive the way it once was; by the time this
/// method is ready to call it, the root is already gone, and a dead root's reparented children
/// are no longer discoverable through it at all. So this keeps its own rolling, best-effort
/// snapshot of the root's descendants (<see cref="IProcessManager.SnapshotDescendants"/>) refreshed
/// on every poll while the root is still alive, and terminates whatever that last snapshot still
/// shows running once the root's own death confirms nothing legitimate is coming from it.
/// </para>
/// <para>
/// Waiting for the root's own death is itself unbounded only up to <see cref="PostResultGrace"/>
/// once a result has actually been seen: before that, a still-running process might still be
/// mid-leg and there is nothing safe to act on, so the wait stays genuinely unbounded (bounded
/// only by whatever <see cref="CancellationToken"/> a caller supplies) — but once a result is on
/// disk, an unbounded wait for the root to exit on its own is a liveness regression, not a
/// stronger correctness guarantee (independent pre-PR review, cycle 3, human verdict): before the
/// whole-session-usage fix this waiter is part of, a session that wrote its result and then never
/// exited was ended immediately by the tree kill on the result line, so the pipeline could never
/// hang on a stuck process. <see cref="PostResultGrace"/> restores that bound — the root gets a
/// short window to exit on its own so the whole-session usage record already on disk is read
/// cleanly, and once it expires the root and its last known descendants are terminated and
/// whatever the stream held by then is finalized, with <see cref="SessionWaitResult.EndedAfterResultGrace"/>
/// telling a caller to log that this session was ended after its result because it did not exit,
/// rather than let one stuck process block the daemon's whole monitor loop forever.
/// </para>
/// </summary>
public static class SessionResultWaiter
{
    private static readonly TimeSpan TailInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DeadProcessGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a root that has already written a result line gets to exit on its own before
    /// <see cref="WaitAsync"/> gives up and forces the termination itself — see this class's own
    /// doc comment for why an unbounded wait past that point is a regression, not a stronger
    /// guarantee. Not a configuration knob on purpose: nothing about "how long is safe to let a
    /// finished session's process linger" is a per-install tuning question.
    /// </summary>
    internal static readonly TimeSpan PostResultGrace = TimeSpan.FromSeconds(30);

    /// <summary>
    /// <paramref name="onOutput"/> is invoked whenever new output lands, which is how a caller
    /// keeps the run's last-activity fresh so stall detection covers the leg.
    /// <paramref name="terminateAfterResultGrace"/> defaults on, restoring the bound documented on
    /// this class; <see cref="Publication.CardPublicationEngine"/>'s own adopted/detached session
    /// wait is the one caller that opts out, since keeping a session alive past its own result on
    /// purpose is that call site's whole point (its own doc comment explains why), not the
    /// pathology this bound exists to catch.
    /// </summary>
    public static async Task<SessionWaitResult> WaitAsync(
        string streamFile,
        int processId,
        DateTimeOffset processStartedAt,
        IProcessManager processManager,
        Func<CancellationToken, Task>? onOutput,
        CancellationToken cancellationToken,
        bool terminateAfterResultGrace = true)
    {
        DateTimeOffset? deadSince = null;
        DateTimeOffset? resultSeenAt = null;
        long cursor = 0;
        bool sawAnyResult = false;
        StringBuilder partialLine = new();
        IReadOnlyList<(int Id, DateTimeOffset StartedAt)> lastKnownDescendants = [];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            (long newCursor, bool sawResult) =
                await StreamTailReader.ReadNewLinesAsync(streamFile, cursor, partialLine, cancellationToken);

            if (newCursor > cursor)
            {
                cursor = newCursor;
                if (onOutput is { } notify)
                {
                    await notify(cancellationToken);
                }
            }

            // The line this poll found only signals a leg is done — a stream can hold more than
            // one result line, and in the common shape the process keeps running for seconds to
            // minutes after the first one before the leg's real terminal line lands. Acting on
            // the first line the instant it appears would read that still-running leg as
            // finished, so this only remembers a result was seen and keeps tailing; the
            // process's own death — checked below, on every poll from here on — is what confirms
            // no further result is coming (independent pre-PR review, cycle 3, adversarial lens).
            sawAnyResult |= sawResult;
            if (sawAnyResult)
            {
                resultSeenAt ??= DateTimeOffset.UtcNow;
            }

            if (processManager.IsAlive(processId, processStartedAt))
            {
                deadSince = null;
                if (sawAnyResult)
                {
                    // Refreshed every poll rather than taken once: a descendant reparents away
                    // from its dying parent essentially atomically with the parent's own exit
                    // (IProcessManager.SnapshotDescendants' own doc), so only the most recent
                    // pre-death snapshot can still name what a since-dead root left running
                    // (independent pre-PR review, cycle 3, conformance + adversarial lenses).
                    lastKnownDescendants = processManager.SnapshotDescendants(processId, processStartedAt);

                    if (terminateAfterResultGrace
                        && resultSeenAt is { } seenAt
                        && DateTimeOffset.UtcNow - seenAt > PostResultGrace)
                    {
                        // The root had its chance to exit on its own and did not take it — force
                        // it now rather than waiting on it forever, and finalize with whatever the
                        // stream holds at this instant (ReadFinalResultAsync's own dedupe already
                        // handles a stream holding more than one result line).
                        AgentResult timedOutResult = await StreamTailReader.ReadFinalResultAsync(streamFile, cancellationToken);
                        IReadOnlyList<int> forcedLingering =
                            TerminateLingering(processManager, processId, processStartedAt, lastKnownDescendants);
                        return new SessionWaitResult(timedOutResult, forcedLingering, EndedAfterResultGrace: true);
                    }
                }
            }
            else if (sawAnyResult)
            {
                // Only a re-read of the whole file finds the line that accounts for the whole
                // leg (StreamTailReader.ReadFinalResultAsync's own doc comment; discovery
                // cc9b7aec) — safe to do now that the process dying confirms this is the last
                // one there will be.
                AgentResult result = await StreamTailReader.ReadFinalResultAsync(streamFile, cancellationToken);
                IReadOnlyList<int> lingering =
                    TerminateLingering(processManager, processId, processStartedAt, lastKnownDescendants);
                return new SessionWaitResult(result, lingering);
            }
            else
            {
                // The grace window keeps polling above, so buffered output that lands
                // after death still gets read before this gives up.
                deadSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - deadSince > DeadProcessGrace)
                {
                    return new SessionWaitResult(null, []);
                }
            }

            await Task.Delay(TailInterval, cancellationToken);
        }
    }

    /// <summary>
    /// Terminates whatever the session's process tree left running now that its root is
    /// confirmed dead (or, on the rare poll that catches the root mid-exit, still alive):
    /// <see cref="IProcessManager.TerminateTree"/> handles the root itself and anything still
    /// parented under it at the moment of the call (a no-op once the root is fully gone, per its
    /// own doc), and <paramref name="lastKnownDescendants"/> — the most recent pre-death
    /// snapshot, already reparented away from the tree <see cref="IProcessManager.TerminateTree"/>
    /// can still see — covers what that call alone can no longer find. Shared by
    /// <see cref="WaitAsync"/> and <see cref="RunSupervisor.MonitorAsync"/>'s own inline poll
    /// loop, which needs the identical sequence but never calls this method through
    /// <see cref="WaitAsync"/> itself.
    /// </summary>
    internal static IReadOnlyList<int> TerminateLingering(
        IProcessManager processManager,
        int processId,
        DateTimeOffset processStartedAt,
        IReadOnlyList<(int Id, DateTimeOffset StartedAt)> lastKnownDescendants)
    {
        List<int> lingering = [.. processManager.TerminateTree(processId, processStartedAt)];
        foreach ((int id, DateTimeOffset startedAt) in lastKnownDescendants)
        {
            if (lingering.Contains(id) || !processManager.IsAlive(id, startedAt))
            {
                continue;
            }

            processManager.Terminate(id, startedAt);
            lingering.Add(id);
        }

        return lingering;
    }
}
