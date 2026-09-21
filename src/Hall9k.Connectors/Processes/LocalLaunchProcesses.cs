using Hall9k.Domain.Features.Run;

namespace Hall9k.Connectors.Processes;

/// <summary>
/// Ending what a local launch left running (idea b9b09779, piece 5), in the one place every caller
/// reaches: <c>h9k task run-local --stop</c>, the daemon's own teardown sweep, and each of the four
/// places the daemon releases a run's worktree. In Connectors because those sit on both sides of
/// the reference graph — the CLI cannot see the daemon, and this is the half they genuinely share.
/// <para>
/// <strong>Every worktree release calls this first.</strong> A launch's own sweep would tear the
/// launch down on its next tick, but not before the release runs, and on Windows a process's
/// current directory cannot be deleted at all — so the release fails outright and strands the
/// checkout for a later prune. The four sites are <c>CloseoutEngine</c>'s two,
/// <c>PrReviewEngine.FinalizeAsync</c> (which releases the very checkout a review's own offer
/// invites the reviewer to launch in, so it is the likeliest of the four to hit a live one),
/// <c>RunLauncher</c>'s two previous-worktree cleanups, and <c>SpikeEngine</c>'s. The stop event
/// stays the sweep's to record, because the sweep is what can read the true reason; this only has
/// to make the directory releasable.
/// </para>
/// </summary>
public static class LocalLaunchProcesses
{
    /// <summary>
    /// Kills every process tree in <paramref name="processes"/> and returns the pids actually found
    /// alive and killed. Fewer than were handed in is the ordinary case, not a failure: a product
    /// the reviewer already closed leaves nothing here to end, and this reports what it observed
    /// rather than what the record hoped for.
    /// </summary>
    public static IReadOnlyList<int> EndAll(IReadOnlyList<LocalLaunchProcess> processes) =>
    [
        .. processes
            .Where(process => WorktreeShell.TerminateTree(process.ProcessId, process.StartedAt))
            .Select(process => process.ProcessId),
    ];

    /// <summary>Everything a live launch left running; nothing at all for one that already ended, whose record carries no processes anybody still owns.</summary>
    public static IReadOnlyList<int> EndAll(LocalLaunchState? launch) =>
        launch is { Live: true } ? EndAll(launch.Processes) : [];
}
