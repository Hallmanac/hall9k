namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One human <c>h9k review proceed</c> at an interactive-mode phase boundary (task: interactive
/// mode becomes a recorded property of the task) — accumulated on
/// <see cref="Projections.RunDetails.BoundaryApprovals"/>, oldest first, across every run the
/// task has had, the same way <see cref="ReviewParkResolution"/> already is. The settled-rulings
/// surface's (#88) third source: unlike a park resolution or a logged human directive, a bare
/// proceed carries no defect text or redirect to re-raise or suppress — it is context for a
/// fresh-context reviewer that this task runs under interactive mode and a human is actively
/// walking its boundaries, not a ruling to weigh.
/// </summary>
public sealed record BoundaryApprovalRecord(DateTimeOffset ApprovedAt);
