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
/// <param name="Track">
/// Which track a <see cref="ReviewMode.Verify"/> pass's single reviewer says this finding belongs
/// to (task: review cycles after the first) — read from the finding's own `track=` tag. Null for
/// every finding a real, single-lens pass (Discovery or FinalFullPass) produces, where the pass's
/// own <c>ReviewPassResult.Lens</c> already says which track it is, and null for a Verify pass's
/// finding that named no track, or an unrecognized one — read as "applies to every still-active
/// track" (<c>ReviewEngine.PlanCycleAsync</c>'s own conservative default) rather than guessed at,
/// the same reading an ungraded severity or an untagged scope already gets elsewhere in this file.
/// A tag naming a real lens that is no longer among this cycle's active tracks gets the identical
/// reading (<c>ReviewEngine.SplitForTrack</c>): that track is never iterated to claim it, so
/// treating the tag as unattributable rather than as a claim nobody will read is what keeps the
/// finding from vanishing.
/// </param>
public sealed record ReviewFinding(
    ReviewSeverity Severity,
    ReviewFindingScope Scope,
    string Location,
    string Text,
    ReviewLens? Track = null)
{
    /// <summary>
    /// How the loop records what it decided to do with this finding (Decisions Log #63, #87, and
    /// the mandatory-<see cref="ReviewMode.FinalFullPass"/> narrowing below). Route needs both
    /// tags stated: an out-of-scope tag on a finding the reviewer graded Medium or Low — an
    /// untagged scope and an ungraded severity alike take the reversible path rather than being
    /// routed away, exactly as before. Everything else out-of-scope (a High, or one the reviewer
    /// never graded) is fixed here — cleanup-as-you-touch for the High, and the conservative
    /// reading for the ungraded one: routing a defect nobody graded would export it out of the
    /// pull request on no evidence it is safe to. Nothing about the out-of-scope Route-or-Fix
    /// split reads <paramref name="mode"/> at all, so it is identical on every mode.
    /// <para>
    /// In-scope is where <see cref="ReviewSeverity.MeetsFixBar"/> decides instead of
    /// automatically fixing everything here regardless of grade (Decisions Log #87): a Medium or
    /// High is fixed this cycle, same as always, but a Low or an ungraded finding rides along
    /// instead of earning its own fix-and-re-review cycle. Unlike the out-of-scope branch, an
    /// ungraded in-scope finding is deliberately NOT read the conservative way here — see
    /// <see cref="ReviewSeverity.MeetsFixBar"/>'s own doc for why.
    /// </para>
    /// <para>
    /// A <see cref="ReviewMode.FinalFullPass"/> cycle tightens that in-scope bar to High alone
    /// (task: a mandatory FinalFullPass records merge-ready when every finding it attaches is
    /// below High — origin measurement: 3 High findings in 172 final passes, 101 of 104
    /// needs-fixes final passes carried no High at all): the mandatory pass mostly forced a
    /// fix-and-reverify iteration over Mediums nothing graded critical asked for, so an in-scope
    /// Medium there rides along exactly as a Low already did, rather than earning a cycle of its
    /// own. Discovery and Verify are untouched — every earlier cycle still fixes a Medium the
    /// ordinary way, since the code is still converging there and the severity gate (adversarial's
    /// own multi-cycle convergence rule) is a separate question from this bar. This includes a
    /// Medium-graded unmet-acceptance-criterion finding, deliberately: PLAN.md #119 says so
    /// explicitly, because that finding is otherwise promised the fix bar unconditionally
    /// (<c>AgentPromptBuilder.AppendFindingContract</c>'s non-FinalFullPass prompt text) and this
    /// mode is where that promise is overridden, not honored by accident. The criterion is still
    /// graded honestly and the Medium still reaches the owner by name as a residual on the pull
    /// request — only the extra fix-and-reverify cycle is what this mode no longer spends on it.
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
    public ReviewFindingDisposition Disposition(ReviewMode mode)
    {
        if (Location.IsBlank() && Text.IsBlank())
        {
            return ReviewFindingDisposition.Fix;
        }

        if (Scope.IsRoutable && Severity.IsStatedBelowHigh)
        {
            return ReviewFindingDisposition.Route;
        }

        bool meetsFixBarThisMode = mode == ReviewMode.FinalFullPass
            ? Severity == ReviewSeverity.High
            : Severity.MeetsFixBar;

        return !Scope.IsRoutable && !meetsFixBarThisMode
            ? ReviewFindingDisposition.RideAlong
            : ReviewFindingDisposition.Fix;
    }

    /// <summary>The stream's record of this finding: its classification, never its text (log #6).</summary>
    public ReviewFindingRecord ToRecord(ReviewMode mode) => new(Severity, Scope, Location, Disposition(mode), Track);
}
