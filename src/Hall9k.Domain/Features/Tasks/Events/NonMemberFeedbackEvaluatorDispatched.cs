namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// Closeout dispatched the one read-only evaluator session for a piece of non-member feedback that
/// is held for the owner, before the session was spawned, so a poll that observes the feedback again,
/// or a daemon that restarts mid-wait, finds the dispatch already on the stream and never starts a
/// second. Counted against the task's lifetime evaluator allowance
/// (<see cref="TaskAggregate.NonMemberEvaluatorsDispatched"/>).
/// </summary>
/// <param name="FeedbackKey">The review's url, or the thread's node id, of the item this evaluator assesses.</param>
/// <param name="SessionId">The evaluator's session id, which ties its outcome to this instance of the item.</param>
/// <param name="AssessmentPath">The one file in the run directory the evaluator is permitted to write.</param>
public sealed record NonMemberFeedbackEvaluatorDispatched(
    Guid Id,
    string FeedbackKey,
    Guid SessionId,
    string AssessmentPath,
    DateTimeOffset DispatchedAt);
