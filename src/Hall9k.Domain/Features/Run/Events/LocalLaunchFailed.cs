namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A command step the launch ran reported failure, so the launch stopped there (idea b9b09779,
/// piece 5). Whatever it had already started comes down with it — a half-launched product on a
/// reviewer's machine is worse than none, because it looks like the one they were offered — which
/// is why this event ends the launch on its own rather than being followed by a stop: there is one
/// fact here, not two.
/// </summary>
/// <param name="Reason">What the step actually reported, quoted rather than summarized, so a reviewer can tell a missing runtime from a compile error without going and reading a log.</param>
/// <param name="ProcessesEnded">The pids of anything an earlier launch step had already started and this failure took down with it; empty when it had started nothing.</param>
public sealed record LocalLaunchFailed(
    Guid Id,
    Guid LaunchId,
    int StepNumber,
    string Command,
    string Reason,
    IReadOnlyList<int> ProcessesEnded,
    DateTimeOffset FailedAt);
