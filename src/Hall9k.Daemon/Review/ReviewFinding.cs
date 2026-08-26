using Hall9k.Domain.Features.Run;

namespace Hall9k.Daemon.Review;

/// <summary>
/// One finding as a reviewer wrote it (Decisions Log #63): the grade and scope tag it declared,
/// where it points, and its own text. The text lives here rather than on the run stream because
/// findings are artifacts, not event payload (log #6) — it is carried in process only as far as
/// the fix session's prompt and, for a routed finding, the draft bug task's context.
/// </summary>
/// <param name="Severity">The reviewer's grade; Unknown when it stated none.</param>
/// <param name="Scope">The reviewer's scope tag; Unknown when it stated none.</param>
/// <param name="Location">A `path/to/file.cs:123` pointer, or blank when the reviewer gave none.</param>
/// <param name="Text">The finding block verbatim, header line included.</param>
public sealed record ReviewFinding(
    ReviewSeverity Severity,
    ReviewFindingScope Scope,
    string Location,
    string Text)
{
    /// <summary>
    /// How the loop records what it decided to do with this finding (Decisions Log #63, #87).
    /// Route needs both tags stated: an out-of-scope tag on a finding the reviewer graded Medium
    /// or Low — an untagged scope and an ungraded severity alike take the reversible path rather
    /// than being routed away, exactly as before. Everything else out-of-scope (a High, or one
    /// the reviewer never graded) is fixed here — cleanup-as-you-touch for the High, and the
    /// conservative reading for the ungraded one: routing a defect nobody graded would export it
    /// out of the pull request on no evidence it is safe to.
    /// <para>
    /// In-scope is where <see cref="ReviewSeverity.MeetsFixBar"/> now decides instead of
    /// automatically fixing everything here regardless of grade (Decisions Log #87): a Medium or
    /// High is still fixed this cycle, same as always, but a Low or an ungraded finding rides
    /// along instead of earning its own fix-and-re-review cycle. Unlike the out-of-scope branch,
    /// an ungraded in-scope finding is deliberately NOT read the conservative way here — see
    /// <see cref="ReviewSeverity.MeetsFixBar"/>'s own doc for why.
    /// </para>
    /// <para>
    /// <see cref="ReviewTrackPolicy.Stated"/>'s placeholder for a needs-fixes verdict the parser
    /// could not structure at all — blank <see cref="Location"/> and blank <see cref="Text"/>,
    /// the one shape no genuinely parsed finding ever has — is always Fix, never RideAlong: it is
    /// unplaced and ungraded because nothing about it could be read, never because it was graded
    /// Low, and demoting it on the strength of a grade nobody actually gave would reopen the gap
    /// Decisions Log #86 closed for a needs-fixes verdict naming something unstructured.
    /// </para>
    /// </summary>
    public ReviewFindingDisposition Disposition
    {
        get
        {
            if (Location.IsBlank() && Text.IsBlank())
            {
                return ReviewFindingDisposition.Fix;
            }

            if (Scope.IsRoutable && Severity.IsStatedBelowHigh)
            {
                return ReviewFindingDisposition.Route;
            }

            return !Scope.IsRoutable && !Severity.MeetsFixBar
                ? ReviewFindingDisposition.RideAlong
                : ReviewFindingDisposition.Fix;
        }
    }

    /// <summary>The stream's record of this finding: its classification, never its text (log #6).</summary>
    public ReviewFindingRecord ToRecord() => new(Severity, Scope, Location, Disposition);
}
