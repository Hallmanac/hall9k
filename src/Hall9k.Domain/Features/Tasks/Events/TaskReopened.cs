namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A done task returns to the queue for a follow-up run on its existing pull-request
/// branch (PR closeout, Decisions Log #18/#20). Branch comes from the completing run's
/// record — it lives nowhere else on the Task stream; the pull-request URL already does
/// (TaskCompleted) and is not repeated here.
/// </summary>
public sealed record TaskReopened(
    Guid Id,
    Guid PreviousRunId,
    string Branch,
    string? Reason,
    DateTimeOffset ReopenedAt,
    Guid ReopenedByOwnerId);
