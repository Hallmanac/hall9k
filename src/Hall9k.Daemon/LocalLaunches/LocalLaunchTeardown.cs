using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;

namespace Hall9k.Daemon.LocalLaunches;

/// <summary>
/// Whether a local launch should come down, and why (idea b9b09779, piece 5). A pure function over
/// four observations so the rule itself is testable without a process, a worktree, or a store:
/// what the launch's own record says, what state its task is in, whether the checkout is still
/// there, and whether anything it started is still alive.
/// <para>
/// The criterion this exists for is "never left running". A reviewer who walked a change and
/// wandered off leaves a server bound to a port on their own machine indefinitely, and the two
/// events that make it certainly unwanted — the task closing out, and the worktree being released
/// — are both things the platform does to them rather than things they do, so nobody is around to
/// notice.
/// </para>
/// </summary>
public static class LocalLaunchTeardown
{
    /// <summary>
    /// Why this launch should be torn down, or null to leave it alone.
    /// </summary>
    /// <param name="launch">The launch as its run's stream last left it.</param>
    /// <param name="taskState">
    /// The state of the task it belongs to, or null when that task cannot be read at all. Null is
    /// deliberately not a teardown: a projection this node has not replicated yet is not evidence
    /// the work is over, and killing a reviewer's live walk over a document that has not arrived
    /// would be acting on an absence as though it were an observation.
    /// </param>
    /// <param name="worktreeExists">Whether the checkout the launch is standing in is still on disk.</param>
    /// <param name="isAlive">Whether one recorded process is still that same process (pid and start time both, Decisions Log #2).</param>
    public static LocalLaunchStopReason? Reason(
        LocalLaunchState launch, TaskState? taskState, bool worktreeExists,
        Func<LocalLaunchProcess, bool> isAlive)
    {
        if (!launch.Live)
        {
            return null;
        }

        if (taskState is { IsTerminal: true })
        {
            return LocalLaunchStopReason.TaskClosedOut;
        }

        if (!worktreeExists)
        {
            return LocalLaunchStopReason.WorktreeRemoved;
        }

        // Nothing started yet is not nothing left running: a launch paused at a human step has no
        // processes precisely because the reviewer has not finished the step that comes before the
        // launch command, and reading that as "its process is gone" would tear down the walk they
        // are in the middle of.
        return launch.Processes.Count > 0 && !launch.Processes.Any(isAlive)
            ? LocalLaunchStopReason.ProcessGone
            : null;
    }
}
