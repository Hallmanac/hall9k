using Hall9k.Domain.Features.Tasks.Feedback;

namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// How the feedback check ended: its answer, or the closed reason it gave none. Holds nothing the session wrote. A failure is
/// recorded here and the task is released, so one bad check cannot stall the tasks behind it.
/// </summary>
/// <param name="Id">The task's id.</param>
/// <param name="SessionId">The check session, or null when it ended before any session was spawned.</param>
/// <param name="NodeId">The node that ran it.</param>
/// <param name="Answer">What the closing line answered, or <see cref="FeedbackCheckAnswer.Failed"/>.</param>
/// <param name="Failure">Why there is no answer, or <see cref="RefinementFailureReason.Unknown"/> when there is one.</param>
/// <param name="RecordedAt">When the outcome was recorded.</param>
public sealed record FeedbackCheckRecorded(
    Guid Id,
    Guid? SessionId,
    Guid NodeId,
    FeedbackCheckAnswer Answer,
    RefinementFailureReason Failure,
    DateTimeOffset RecordedAt);
