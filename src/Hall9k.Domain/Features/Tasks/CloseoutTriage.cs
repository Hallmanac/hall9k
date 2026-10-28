namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// Where the read-only triage of one set of unresolved review threads stands, as the task's stream
/// records it (task: analysis-only closeout). Held per thread set, so a poll that sees the same set
/// again finds the answer here and starts nothing.
/// </summary>
/// <param name="ThreadSetKey">Which set of thread ids this triage covers.</param>
/// <param name="Status">Running once dispatched; Assessed, Failed or PastCap once recorded.</param>
/// <param name="ThreadCount">How many threads the set holds.</param>
/// <param name="SessionId">The session dispatched, or null for a set skipped past the cap.</param>
/// <param name="ReportPath">The file the report is written to, from the dispatch.</param>
/// <param name="Fix">Threads classified as fix, once assessed.</param>
/// <param name="Decline">Threads classified as decline, once assessed.</param>
/// <param name="Route">Threads classified as route, once assessed.</param>
/// <param name="Reason">Why there is no report, for a failed or skipped triage.</param>
/// <param name="RecordedAt">When the latest event for this set landed.</param>
public sealed record CloseoutTriage(
    string ThreadSetKey,
    FeedbackEvaluationStatus Status,
    int ThreadCount,
    Guid? SessionId,
    string? ReportPath,
    int Fix,
    int Decline,
    int Route,
    string? Reason,
    DateTimeOffset RecordedAt);
