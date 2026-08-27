using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The two-lens review's arithmetic (Decisions Log #59): which lenses a cycle owes, and how
/// their verdicts become the one verdict the loop acts on.
/// </summary>
public sealed class ReviewLensTests
{
    [Fact]
    public void A_cycle_runs_conformance_and_adversarial_in_that_order()
    {
        ReviewLens.CycleLenses.Should().Equal(
            [ReviewLens.Conformance, ReviewLens.Adversarial], "two is the shipped shape, and the list is the seam");
        ReviewLens.Conformance.Slug.Should().Be("conformance", "the slug names this lens's artifacts");
        ReviewLens.Unknown.Slug.Should().BeEmpty("a lens-less pass keeps the pre-lens artifact names");
    }

    [Fact]
    public void A_cycle_is_short_until_every_active_lens_has_looked()
    {
        ReviewLens.MissingFrom(ReviewLens.CycleLenses, [])
            .Should().Equal([ReviewLens.Conformance, ReviewLens.Adversarial]);
        ReviewLens.MissingFrom(ReviewLens.CycleLenses, [ReviewLens.Conformance])
            .Should().Equal([ReviewLens.Adversarial]);
        ReviewLens.MissingFrom(ReviewLens.CycleLenses, [ReviewLens.Adversarial, ReviewLens.Conformance])
            .Should().BeEmpty();
    }

    /// <summary>
    /// A track that concluded is not missing from the cycle, it is finished with the run
    /// (Decisions Log #63) — which is what lets a dormant conformance track stay dormant while
    /// the adversarial one keeps going alone.
    /// </summary>
    [Fact]
    public void A_concluded_track_is_not_owed_another_pass()
    {
        ReviewLens.MissingFrom([ReviewLens.Adversarial], []).Should().Equal([ReviewLens.Adversarial]);
        ReviewLens.MissingFrom([ReviewLens.Adversarial], [ReviewLens.Adversarial]).Should().BeEmpty();
    }

    /// <summary>
    /// A pass recorded before lenses existed was the conformance reviewer — the only reviewer
    /// there was. Reading it that way is a fact about what shipped; it is also why resuming a
    /// pre-lens run adds the adversarial pass rather than re-running everything.
    /// </summary>
    [Fact]
    public void A_pass_recorded_without_a_lens_accounts_for_conformance_and_nothing_else()
    {
        ReviewLens.Unknown.Covers(ReviewLens.Conformance).Should().BeTrue();
        ReviewLens.Unknown.Covers(ReviewLens.Adversarial).Should().BeFalse();
        ReviewLens.MissingFrom(ReviewLens.CycleLenses, [ReviewLens.Unknown])
            .Should().Equal([ReviewLens.Adversarial]);
    }

    /// <summary>
    /// A Verify cycle's single reviewer stands in for every still-active track (task: review
    /// cycles after the first) — the direct extension of the precedent <see cref="ReviewLens.Unknown"/>
    /// already set, widened to cover both real lenses rather than fixed to conformance alone.
    /// </summary>
    [Fact]
    public void A_verify_pass_accounts_for_both_real_lenses()
    {
        ReviewLens.Verify.Covers(ReviewLens.Conformance).Should().BeTrue();
        ReviewLens.Verify.Covers(ReviewLens.Adversarial).Should().BeTrue();
        ReviewLens.MissingFrom(ReviewLens.CycleLenses, [ReviewLens.Verify]).Should().BeEmpty(
            "one Verify pass answers for the whole cycle, not one lens of it");
    }

    [Fact]
    public void Merge_ready_takes_every_lens_and_one_needs_fixes_carries_the_cycle()
    {
        ReviewVerdict.Merge([ReviewVerdict.MergeReady, ReviewVerdict.MergeReady])
            .Should().Be(ReviewVerdict.MergeReady);
        ReviewVerdict.Merge([ReviewVerdict.MergeReady, ReviewVerdict.NeedsFixes])
            .Should().Be(ReviewVerdict.NeedsFixes, "either lens finding real problems needs fixes");
        ReviewVerdict.Merge([ReviewVerdict.NeedsFixes, ReviewVerdict.MergeReady])
            .Should().Be(ReviewVerdict.NeedsFixes);
    }

    /// <summary>
    /// An unread pass outranks a needs-fixes: "we needed fixes anyway" would silently discard
    /// whatever the silent lens found, which is the guessing the re-prompt exists to avoid.
    /// </summary>
    [Fact]
    public void A_pass_that_stated_no_verdict_leaves_the_cycle_unknown()
    {
        ReviewVerdict.Merge([ReviewVerdict.MergeReady, ReviewVerdict.Unknown])
            .Should().Be(ReviewVerdict.Unknown);
        ReviewVerdict.Merge([ReviewVerdict.NeedsFixes, ReviewVerdict.Unknown])
            .Should().Be(ReviewVerdict.Unknown);
        ReviewVerdict.Merge([]).Should().Be(ReviewVerdict.Unknown, "no lens looked, so nothing is known");
    }
}
