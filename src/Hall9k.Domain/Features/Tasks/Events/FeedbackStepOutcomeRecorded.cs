using Hall9k.Domain.Features.Tasks.Feedback;

namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The step is over, and this is how. For the three outcomes that release the task it is also what moves the task from
/// Blocked to Queued, in the same event. Once recorded, nothing starts another step for the task, whatever happens to it.
/// </summary>
/// <param name="Id">The task's id.</param>
/// <param name="Outcome">How the step ended.</param>
/// <param name="Reason">A sentence the daemon wrote, never text a session wrote: why a parked step could not finish, or what a person did that ended it.</param>
/// <param name="RecordedAt">When the outcome was recorded.</param>
public sealed record FeedbackStepOutcomeRecorded(
    Guid Id,
    FeedbackStepOutcome Outcome,
    string Reason,
    DateTimeOffset RecordedAt);
