using Hall9k.Connectors.Processes;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Marten;

namespace Hall9k.Daemon.LocalLaunches;

/// <summary>
/// The other half of "never left running" (idea b9b09779, piece 5). <c>h9k task run-local --stop</c>
/// is the reviewer ending a launch they are finished with; this is the platform ending one nobody
/// is coming back to, because the task closed out, the worktree was released, or whatever the
/// launch started died on its own.
/// <para>
/// It only ever touches a launch this node started (<see cref="LocalLaunchState.NodeId"/>). The
/// launch events are node-scoped, so a record from another machine should never be here to begin
/// with; the check is the second fence behind that, because recording a stop for processes this
/// node cannot see would be writing an observation nobody made.
/// </para>
/// </summary>
public sealed class LocalLaunchSweepEngine(
    IDocumentStore store, NodeContext node, ILogger<LocalLaunchSweepEngine> logger)
{
    /// <summary>How many launches this tick tore down.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        IReadOnlyList<RunDetails> runs = await session.Query<RunDetails>()
            .Where(run => run.LocalLaunch != null)
            .ToListAsync(cancellationToken);

        int stopped = 0;
        foreach (RunDetails run in runs)
        {
            if (run.LocalLaunch is not { Live: true } launch || launch.NodeId != node.NodeId)
            {
                continue;
            }

            TaskDetails? task = await session.LoadAsync<TaskDetails>(launch.TaskId, cancellationToken);
            LocalLaunchStopReason? reason = LocalLaunchTeardown.Reason(
                launch, TaskStateOf(task), Directory.Exists(launch.WorktreePath), Alive);
            if (reason is null)
            {
                continue;
            }

            IReadOnlyList<int> ended = LocalLaunchProcesses.EndAll(launch);
            session.Events.Append(
                run.Id,
                new LocalLaunchStopped(run.Id, launch.LaunchId, reason, ended, DateTimeOffset.UtcNow));
            stopped++;
            logger.LogInformation(
                "Local launch of task {TaskId} torn down ({Reason}); ended {Count} process tree(s)",
                launch.TaskId, reason.Value, ended.Count);
        }

        if (stopped > 0)
        {
            await session.SaveChangesAsync(cancellationToken);
        }

        return stopped;
    }

    /// <summary>
    /// Null when the task document is not there at all, which <see cref="LocalLaunchTeardown"/>
    /// treats as no answer rather than as a closed task. <see cref="TaskState.Unknown"/> would be
    /// the wrong stand-in: it reads as a state that was observed and could not be recognized, and
    /// this is the case where nothing was observed.
    /// </summary>
    private static TaskState? TaskStateOf(TaskDetails? task) => task?.State;

    private static bool Alive(LocalLaunchProcess process) =>
        WorktreeShell.IsAlive(process.ProcessId, process.StartedAt);
}
