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
    /// Whether this finding is fixed in this pull request rather than routed away. Everything
    /// in the branch's own code is, and so is an out-of-scope High — cleanup-as-you-touch, in
    /// its own commit. Routing needs both tags stated: an out-of-scope tag on a finding the
    /// reviewer graded Medium or Low. An untagged scope and an ungraded severity alike take the
    /// reversible path and stay here, which is the same reading the gate gives an ungraded
    /// finding — routing a defect nobody graded would export it out of the pull request AND
    /// spend none of the cycle it would otherwise have forced.
    /// </summary>
    public bool IsFixedHere => !Scope.IsRoutable || !Severity.IsStatedBelowHigh;

    /// <summary>How the loop records what it decided to do with this finding.</summary>
    public ReviewFindingDisposition Disposition =>
        IsFixedHere ? ReviewFindingDisposition.Fix : ReviewFindingDisposition.Route;

    /// <summary>The stream's record of this finding: its classification, never its text (log #6).</summary>
    public ReviewFindingRecord ToRecord() => new(Severity, Scope, Location, Disposition);
}
