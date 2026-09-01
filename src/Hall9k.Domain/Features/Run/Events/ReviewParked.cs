namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The pre-PR review loop stopped short of merge-ready and handed the run to a human
/// (Decisions Log #23, the CloseoutParked pattern): the automatic fix budget is spent, a
/// fix run disputed a finding, or the reviewer returned no parseable verdict. The reason
/// names the artifact files carrying the unresolved findings (and, on a dispute, both
/// positions). The task stays Claimed and the lease is retained — the worktree is the
/// human's workspace for resolving it. Surfaces as NeedsHuman in h9k status.
/// </summary>
/// <param name="NeedsFixesOffersNoProgress">
/// Whether granting <c>--needs-fixes</c> here would just re-park identically without a fix
/// session ever running: true for a cap-0 takeover park or the lifetime-budget park, both of
/// which accept the verdict but cannot use it (independent pre-PR review: the review cycle
/// caps become settable). Defaults false — the ordinary case, where a fix session genuinely
/// runs — so every park that predates this field, and every other park site that never sets
/// it, reads as the lever <c>h9k status</c> has always offered.
/// </param>
public sealed record ReviewParked(
    Guid Id,
    string Reason,
    DateTimeOffset ParkedAt,
    bool NeedsFixesOffersNoProgress = false);
