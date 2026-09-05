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
/// Whether granting <c>--needs-fixes</c> here would just re-park identically: true for a
/// per-track cap-0 takeover park (no fix session ever dispatches before the identical park
/// reappears), a final-full-pass cap-0 park, or the lifetime-budget park — the latter two do
/// dispatch one more fix session over the grant's findings, but nothing about the grant resets
/// the cap or budget that reparks the run right behind it, so the park reappears with no
/// externally visible progress either way (independent pre-PR review: the review cycle caps
/// become settable). Defaults false — the ordinary case, where a fix session genuinely clears
/// the park — so every park that predates this field, and every other park site that never
/// sets it, reads as the lever <c>h9k status</c> has always offered.
/// </param>
/// <param name="IsInteractiveGate">
/// Whether this park is interactive mode's own routine boundary gate (task: interactive mode
/// becomes a recorded property of the task, design rulings R2/R9) rather than the ordinary
/// disputed-finding or cap/budget park this event otherwise records. True only when
/// <see cref="Hall9k.Daemon.Review.ReviewEngine"/> parks because the task's own
/// <c>InteractiveModeEnabled</c> flag is set and this specific boundary has not yet had a fresh
/// <c>h9k review proceed</c> — never set for a dispute or a cap/budget park, which still take
/// only <c>h9k review resolve</c>. <see cref="RunAggregate.ParkedIsInteractiveGate"/> is what
/// <c>ReviewProceedCommand</c> checks before accepting a bare proceed.
/// </param>
public sealed record ReviewParked(
    Guid Id,
    string Reason,
    DateTimeOffset ParkedAt,
    bool NeedsFixesOffersNoProgress = false,
    bool IsInteractiveGate = false);
