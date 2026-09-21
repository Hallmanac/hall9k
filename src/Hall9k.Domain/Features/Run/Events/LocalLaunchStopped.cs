namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The launch is down (idea b9b09779, piece 5) — because the reviewer asked, because the task
/// closed out, because the worktree went away, or because the process was already gone when the
/// platform next looked (<see cref="LocalLaunchStopReason"/>).
/// </summary>
/// <param name="ProcessesEnded">The pids actually found alive and killed, which can be fewer than the launch recorded: a process that had already exited is not reported as one this stop ended.</param>
public sealed record LocalLaunchStopped(
    Guid Id,
    Guid LaunchId,
    LocalLaunchStopReason Reason,
    IReadOnlyList<int> ProcessesEnded,
    DateTimeOffset StoppedAt);
