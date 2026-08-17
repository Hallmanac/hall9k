namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The pre-PR review loop stopped short of merge-ready and handed the run to a human
/// (Decisions Log #23, the CloseoutParked pattern): the automatic fix budget is spent, a
/// fix run disputed a finding, or the reviewer returned no parseable verdict. The reason
/// names the artifact files carrying the unresolved findings (and, on a dispute, both
/// positions). The task stays Claimed and the lease is retained — the worktree is the
/// human's workspace for resolving it. Surfaces as NeedsHuman in h9k status.
/// </summary>
public sealed record ReviewParked(
    Guid Id,
    string Reason,
    DateTimeOffset ParkedAt);
