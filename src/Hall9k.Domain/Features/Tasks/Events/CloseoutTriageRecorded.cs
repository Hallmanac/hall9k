namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// How the triage of one set of review threads ended, or that none ran: the session classified the
/// threads and wrote its report, it failed (timed out, wrote nothing, wrote nothing countable, or the
/// daemon restarted while it ran), or the task was already past its triage allowance. It carries the
/// counts of the dispositions and a reason the daemon wrote, never a word of the report, so nothing
/// that renders it can pass the report's prose on.
/// </summary>
/// <param name="ThreadSetKey">The key <see cref="CloseoutTriageDispatched.ThreadSetKey"/> named.</param>
/// <param name="Outcome">Assessed, Failed or PastCap.</param>
/// <param name="SessionId">The triage session this outcome belongs to; null for a skip past the cap.</param>
/// <param name="Fix">How many threads the report classified as fix.</param>
/// <param name="Decline">How many threads the report classified as decline.</param>
/// <param name="Route">How many threads the report classified as route.</param>
/// <param name="Reason">Why there is no report, for any outcome but Assessed.</param>
/// <param name="ReportPath">The file the owner reads the triage in, for an assessed outcome.</param>
public sealed record CloseoutTriageRecorded(
    Guid Id,
    string ThreadSetKey,
    FeedbackEvaluationStatus Outcome,
    Guid? SessionId,
    int Fix,
    int Decline,
    int Route,
    string? Reason,
    DateTimeOffset RecordedAt,
    string? ReportPath = null);
