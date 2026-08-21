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
    public void A_cycle_is_short_until_every_lens_has_looked()
    {
        ReviewLens.MissingFrom([]).Should().Equal([ReviewLens.Conformance, ReviewLens.Adversarial]);
        ReviewLens.MissingFrom([ReviewLens.Conformance]).Should().Equal([ReviewLens.Adversarial]);
        ReviewLens.MissingFrom([ReviewLens.Adversarial, ReviewLens.Conformance]).Should().BeEmpty();
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
        ReviewLens.MissingFrom([ReviewLens.Unknown]).Should().Equal([ReviewLens.Adversarial]);
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
