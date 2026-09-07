namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One human-applied fix at interactive mode's review-verdict-to-fix boundary
/// (<c>h9k review fixed</c>, task: a human at the wheel takes the fix role herself) — accumulated
/// on <see cref="Projections.RunDetails.HumanFixes"/>, oldest first, across every run the task has
/// had, the same way <see cref="ReviewParkResolution"/> and <see cref="BoundaryApprovalRecord"/>
/// already are. The settled-rulings surface's (#88) fourth source.
/// <para>
/// Unlike a park resolution, an ordinary entry here carries nothing to re-raise or suppress: the
/// commits are the statement, and the next review pass reads them directly. A
/// <see cref="NoChangeReason"/> entry is the one that carries text — the findings were considered
/// and deliberately changed nothing — and that reason is dismissal-shaped, so a later pass is told
/// not to re-raise the question without new evidence, exactly as it is told for a
/// <c>--merge-ready</c> ruling's reason.
/// </para>
/// </summary>
/// <param name="Cycle">The review cycle whose findings the human fixed, so a reader can place it.</param>
/// <param name="NoChangeReason">The operator's own <c>--no-change</c> text; null on an ordinary fix, where commits landed instead — never both, because <c>h9k review fixed</c> refuses the flag over a tip it watched move.</param>
/// <param name="AppliedAt">When it was recorded.</param>
public sealed record HumanFixRecord(
    int Cycle,
    string? NoChangeReason,
    DateTimeOffset AppliedAt);
