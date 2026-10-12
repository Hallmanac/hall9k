namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// How the evaluation of a piece of held non-member feedback ended, or that none ran: the evaluator
/// assessed it with a recommendation of fix, decline or ask, it failed (timed out, wrote nothing, wrote
/// a recommendation outside those three, or the daemon restarted while it ran), or the task was already
/// past its evaluator cap. It carries the recommendation word and a reason the daemon wrote, never a
/// word of the assessment, so nothing that renders it can pass the assessment's prose on.
/// </summary>
/// <param name="FeedbackKey">The review's url, or the thread's node id.</param>
/// <param name="Outcome">Assessed, Failed or PastCap.</param>
/// <param name="SessionId">The evaluator session this outcome belongs to; null for a skip past the cap.</param>
/// <param name="Recommendation">The recommendation, for an assessed outcome.</param>
/// <param name="Reason">Why there is no assessment, for any other outcome.</param>
/// <param name="AssessmentPath">The file the owner reads the assessment in, for an assessed outcome; named here so the orchestrator feed can point at it without reading the item.</param>
public sealed record NonMemberFeedbackEvaluationRecorded(
    Guid Id,
    string FeedbackKey,
    FeedbackEvaluationStatus Outcome,
    Guid? SessionId,
    FeedbackRecommendation? Recommendation,
    string? Reason,
    DateTimeOffset RecordedAt,
    string? AssessmentPath = null);
