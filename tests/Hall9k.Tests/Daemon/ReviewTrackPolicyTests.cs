using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Run;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The twin-track convergence rules (Decisions Log #63), as the pure decider behind them:
/// compliance ends clean or parks at its cap, adversarial runs under the severity gate, and
/// scope decides where a fix lives rather than how much it matters.
/// </summary>
public sealed class ReviewTrackPolicyTests
{
    private static readonly DaemonOptions Options = new();

    [Fact]
    public void A_clean_verdict_ends_the_track_clean_with_nothing_left_over()
    {
        ReviewTrackPlan plan = Decide(ReviewLens.Adversarial, cycle: 1, ReviewVerdict.MergeReady);

        plan.Continues.Should().BeFalse();
        plan.Settlement.Should().Be(ReviewSettlement.Clean);
        plan.Fix.Should().BeEmpty();
        plan.Route.Should().BeEmpty();
        plan.Residuals.Should().BeEmpty();
    }

    /// <summary>
    /// Before the gate cycle, every grade forces the next cycle. The early cycles get full
    /// rigor on purpose: the code is still converging, and the gate is for the nit-churn tail.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Before_the_gate_a_low_still_forces_another_adversarial_cycle(int cycle)
    {
        ReviewTrackPlan plan = Decide(
            ReviewLens.Adversarial, cycle, ReviewVerdict.NeedsFixes,
            Finding(ReviewSeverity.Low, ReviewFindingScope.InScope, "A.cs:1"));

        plan.Continues.Should().BeTrue();
        plan.Fix.Should().ContainSingle();
        plan.Residuals.Should().BeEmpty("a track that runs again leaves nothing behind yet");
    }

    /// <summary>
    /// From the gate cycle, mediums and lows are still fixed — they simply stop re-triggering
    /// the loop — and what ships unread is recorded as a residual rather than forgotten.
    /// </summary>
    [Fact]
    public void From_the_gate_cycle_only_a_high_forces_another_cycle()
    {
        ReviewTrackPlan settled = Decide(
            ReviewLens.Adversarial, cycle: 4, ReviewVerdict.NeedsFixes,
            Finding(ReviewSeverity.Medium, ReviewFindingScope.InScope, "A.cs:1"),
            Finding(ReviewSeverity.Low, ReviewFindingScope.InScope, "A.cs:2"));

        settled.Continues.Should().BeFalse();
        settled.Settlement.Should().Be(ReviewSettlement.Settled);
        settled.Fix.Should().HaveCount(2, "mediums and lows are still fixed, just not re-reviewed");
        settled.Residuals.Should().OnlyContain(
            residual => residual.Disposition == ReviewResidualDisposition.FixedUnreviewed);

        ReviewTrackPlan forced = Decide(
            ReviewLens.Adversarial, cycle: 4, ReviewVerdict.NeedsFixes,
            Finding(ReviewSeverity.Medium, ReviewFindingScope.InScope, "A.cs:1"),
            Finding(ReviewSeverity.High, ReviewFindingScope.InScope, "A.cs:3"));

        forced.Continues.Should().BeTrue("one high past the gate is enough");
    }

    /// <summary>An ungraded finding is not waved through the gate: unknown is not low.</summary>
    [Fact]
    public void An_ungraded_finding_still_forces_a_cycle_past_the_gate()
    {
        Decide(ReviewLens.Adversarial, cycle: 6, ReviewVerdict.NeedsFixes,
                Finding(ReviewSeverity.Unknown, ReviewFindingScope.InScope, "A.cs:1"))
            .Continues.Should().BeTrue();
    }

    /// <summary>
    /// An ungraded finding is not routable either: the reviewer stated where the defect lives
    /// but not how much it matters, and routing a defect nobody graded would export it out of
    /// the pull request AND spend none of the cycle it would otherwise have forced. Unknown is
    /// not Low anywhere the gate reads it.
    /// </summary>
    [Fact]
    public void An_out_of_scope_finding_the_reviewer_never_graded_is_fixed_here()
    {
        ReviewTrackPlan plan = Decide(
            ReviewLens.Adversarial, cycle: 6, ReviewVerdict.NeedsFixes,
            Finding(ReviewSeverity.Unknown, ReviewFindingScope.OutOfScope, "GitRunner.cs:88"),
            Finding(ReviewSeverity.Parse("critical"), ReviewFindingScope.OutOfScope, "GitRunner.cs:91"));

        plan.Route.Should().BeEmpty("a grade the parser could not read is not a stated Medium or Low");
        plan.Fix.Should().HaveCount(2);
        plan.Continues.Should().BeTrue("an ungraded finding forces the next cycle past the gate too");
    }

    /// <summary>
    /// The empty terminal case, which the acceptance criteria place from cycle four onward:
    /// past the gate, nothing was fixed, so nothing changed, so a fresh reviewer would read the
    /// identical tip and return the identical findings — the track ends instead, with the
    /// routing recorded.
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void Past_the_gate_a_cycle_whose_findings_all_route_away_ends_the_track(int cycle)
    {
        ReviewTrackPlan plan = Decide(
            ReviewLens.Adversarial, cycle, ReviewVerdict.NeedsFixes,
            Finding(ReviewSeverity.Medium, ReviewFindingScope.OutOfScope, "Old.cs:1"),
            Finding(ReviewSeverity.Low, ReviewFindingScope.OutOfScope, "Old.cs:2"));

        plan.Continues.Should().BeFalse();
        plan.Settlement.Should().Be(ReviewSettlement.Settled);
        plan.Fix.Should().BeEmpty("nothing here is this pull request's work");
        plan.Route.Should().HaveCount(2);
        plan.Residuals.Should().BeEmpty(
            "a routed finding's residual is written by the routing event, in the cycle it was routed in");
    }

    /// <summary>
    /// Before the gate the same cycle keeps the track alive, because the tip is not the fixed
    /// point it is past the gate: the other track can still be forcing fix sessions that
    /// rewrite the branch, and a track retired at cycle one is deliberately never reawakened —
    /// so it would never read the fix commits, which is where PR #21's two regressions came
    /// from. It cannot spin on an unchanged tip either: with nothing anywhere left to fix the
    /// run derives Settling and ends whatever this track said.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Before_the_gate_a_cycle_whose_findings_all_route_away_keeps_the_track_alive(int cycle)
    {
        ReviewTrackPlan plan = Decide(
            ReviewLens.Adversarial, cycle, ReviewVerdict.NeedsFixes,
            Finding(ReviewSeverity.Medium, ReviewFindingScope.OutOfScope, "Old.cs:1"));

        plan.Continues.Should().BeTrue();
        plan.Settlement.Should().BeNull("a track that runs again has not ended");
        plan.Fix.Should().BeEmpty("nothing here is this pull request's work");
        plan.Route.Should().ContainSingle();
        plan.Residuals.Should().BeEmpty();
    }

    /// <summary>
    /// The mixed cycle: something forces the track to run again AND something else leaves for a
    /// draft bug task. The plan carries no residual either way — the fix is re-read next cycle,
    /// and the routed finding's residual belongs to the routing event, which is written whether
    /// this cycle is the track's last or not. Carrying it on the plan instead would drop it
    /// here, and the run could then settle Clean over a defect it exported.
    /// </summary>
    [Fact]
    public void A_cycle_that_both_routes_and_forces_another_one_leaves_no_residual_on_the_plan()
    {
        ReviewTrackPlan plan = Decide(
            ReviewLens.Adversarial, cycle: 1, ReviewVerdict.NeedsFixes,
            Finding(ReviewSeverity.High, ReviewFindingScope.InScope, "Spawner.cs:60"),
            Finding(ReviewSeverity.Medium, ReviewFindingScope.OutOfScope, "Legacy.cs:12"));

        plan.Continues.Should().BeTrue("the high is this branch's work and forces the next cycle");
        plan.Fix.Should().ContainSingle().Which.Location.Should().Be("Spawner.cs:60");
        plan.Route.Should().ContainSingle().Which.Location.Should().Be(
            "Legacy.cs:12", "the medium still leaves for a draft, cycle or no cycle");
        plan.Residuals.Should().BeEmpty();
    }

    /// <summary>Scope routes, it does not rank: an out-of-scope high is fixed here and forces a cycle.</summary>
    [Fact]
    public void An_out_of_scope_high_is_fixed_here_and_forces_the_next_cycle()
    {
        ReviewTrackPlan plan = Decide(
            ReviewLens.Adversarial, cycle: 7, ReviewVerdict.NeedsFixes,
            Finding(ReviewSeverity.High, ReviewFindingScope.OutOfScope, "Old.cs:1"));

        plan.Continues.Should().BeTrue();
        plan.Fix.Should().ContainSingle();
        plan.Route.Should().BeEmpty();
    }

    /// <summary>
    /// The conformance track has no grades to gate on, so a low keeps it running at any cycle;
    /// it is the cap, not the severity, that ends it.
    /// </summary>
    [Fact]
    public void The_conformance_track_is_never_gated_by_severity()
    {
        Decide(ReviewLens.Conformance, cycle: 9, ReviewVerdict.NeedsFixes,
                Finding(ReviewSeverity.Low, ReviewFindingScope.InScope, "A.cs:1"))
            .Continues.Should().BeTrue();
    }

    /// <summary>
    /// A needs-fixes verdict the parser could read nothing structured out of still owes a fix:
    /// recording nothing would turn an unreadable finding into no finding at all.
    /// </summary>
    [Fact]
    public void A_needs_fixes_verdict_with_no_readable_findings_still_owes_a_fix()
    {
        ReviewTrackPlan plan = Decide(ReviewLens.Adversarial, cycle: 8, ReviewVerdict.NeedsFixes);

        plan.Continues.Should().BeTrue();
        plan.Fix.Should().ContainSingle().Which.Severity.Should().Be(ReviewSeverity.Unknown);
    }

    [Fact]
    public void The_caps_differ_per_track_and_a_human_grant_re_measures_them()
    {
        ReviewTrackPolicy.CapFor(ReviewLens.Conformance, Options).Should().Be(3);
        ReviewTrackPolicy.CapFor(ReviewLens.Adversarial, Options).Should().Be(10);

        ReviewTrackPolicy.CapReached(ReviewLens.Conformance, cycle: 3, budgetBaseCycle: 0, Options)
            .Should().BeTrue();
        ReviewTrackPolicy.CapReached(ReviewLens.Adversarial, cycle: 3, budgetBaseCycle: 0, Options)
            .Should().BeFalse("the adversarial track is only bounded at ten");
        ReviewTrackPolicy.CapReached(ReviewLens.Adversarial, cycle: 10, budgetBaseCycle: 0, Options)
            .Should().BeTrue();
        ReviewTrackPolicy.CapReached(ReviewLens.Conformance, cycle: 3, budgetBaseCycle: 3, Options)
            .Should().BeFalse("a human's needs-fixes resolution is a fresh grant, not one cycle before a re-park");
    }

    /// <summary>
    /// The cap and the gate both take a cycle number and they are deliberately not the same
    /// number. A human's needs-fixes resolution re-grants the cap (log #22), but the gate is a
    /// statement about how converged the diff is by cycle eleven — re-opening full rigor there
    /// would restart the nit-churn tail exactly where the gate exists to end it.
    /// </summary>
    [Fact]
    public void A_human_grant_re_measures_the_cap_but_never_re_opens_the_severity_gate()
    {
        ReviewTrackPolicy.CapReached(ReviewLens.Adversarial, cycle: 11, budgetBaseCycle: 10, Options)
            .Should().BeFalse("the human granted a fresh round of cycles");

        ReviewTrackPlan plan = Decide(
            ReviewLens.Adversarial, cycle: 11, ReviewVerdict.NeedsFixes,
            Finding(ReviewSeverity.Low, ReviewFindingScope.InScope, "Auth.cs:9"));

        plan.Continues.Should().BeFalse("past the gate a low is fixed without forcing another cycle");
        plan.Fix.Should().ContainSingle("it is still fixed — the gate stops re-review, not the fixing");
    }

    private static ReviewTrackPlan Decide(
        ReviewLens lens, int cycle, ReviewVerdict verdict, params ReviewFinding[] findings) =>
        ReviewTrackPolicy.Decide(lens, cycle, verdict, findings, Options);

    private static ReviewFinding Finding(ReviewSeverity severity, ReviewFindingScope scope, string location) =>
        new(severity, scope, location, $"FINDING: at={location}\nDefect: something at {location}.");
}
