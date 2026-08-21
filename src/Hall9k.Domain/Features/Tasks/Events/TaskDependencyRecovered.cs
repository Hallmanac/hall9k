namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A blocker this task recorded as dead was observed capable of reaching true closeout again —
/// retried back into the queue, or otherwise returned to the pipeline (Decisions Log #34, #61).
/// The dependent returns to plain Blocked with the ordinary waiting-on display, and the
/// <see cref="TaskDependencyFailed"/> that held it stays on the stream: the hold happened, and
/// so did the recovery. Observed rather than assumed — a retry that fails again is recorded as
/// a fresh failure by the next sweep, so the pattern is hold, recover, hold.
/// </summary>
/// <remarks>
/// Carries what was observed about this one blocker and nothing else. What still holds the task
/// afterwards is derived where the event is applied, from the deaths that reader has recorded:
/// a snapshot taken by the pass that appended this cannot see a death appended concurrently, and
/// a hold silenced by a stale snapshot would stay silenced, since every later sweep compares
/// against the recorded deaths and finds nothing new to say.
/// </remarks>
/// <param name="Observation">What was observed about the blocker that lifts the hold.</param>
public sealed record TaskDependencyRecovered(
    Guid Id,
    Guid DependencyId,
    string Observation,
    DateTimeOffset ObservedAt);
