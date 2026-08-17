namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A done task returns to the queue for a follow-up run on its existing pull-request
/// branch (PR closeout, Decisions Log #18/#20). Branch comes from the completing run's
/// record — it lives nowhere else on the Task stream; the pull-request URL already does
/// (TaskCompleted) and is not repeated here. Kind selects the follow-up prompt (null on
/// events recorded before the vocabulary existed reads as Unknown); Automatic marks
/// reopens driven by the closeout monitor rather than a human — the bounded-retry
/// counter counts only these, and a human-initiated reopen resets it (Decisions Log #22).
/// </summary>
public sealed record TaskReopened(
    Guid Id,
    Guid PreviousRunId,
    string Branch,
    string? Reason,
    DateTimeOffset ReopenedAt,
    Guid ReopenedByOwnerId,
    FollowUpKind? Kind = null,
    bool Automatic = false);
