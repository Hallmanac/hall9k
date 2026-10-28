using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Tasks.Queries;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// What the throughput block actually renders (task: h9k status reports throughput beside spend),
/// asserted off the composed lines rather than the console — the same split
/// <see cref="TaskShowCommand.ComposePassageLines"/> already uses for the passage section.
/// </summary>
public sealed class ThroughputPaneTests
{
    private static ThroughputSummary Enough(
        int mergedCount = 8, double firstPassShare = 0.4, double lapsPerMergedTask = 1.5,
        double queuedShare = 0.26, double humanWaitShare = 0.14) =>
        new(mergedCount, TimeSpan.FromHours(6.8), TimeSpan.FromHours(22.7), firstPassShare, lapsPerMergedTask,
            queuedShare, humanWaitShare);

    [Fact]
    public void Fewer_than_five_merged_prints_the_count_and_never_a_median()
    {
        ThroughputSummary summary = new(2, null, null, null, null, null, null);

        IReadOnlyList<string> lines = ThroughputPane.ComposeLines(summary, "week");

        lines.Should().ContainSingle();
        lines[0].Should().Contain("2").And.Contain("too few to summarize");
        lines[0].Should().NotContain("median");
    }

    [Fact]
    public void Zero_merged_says_nothing_merged_yet_rather_than_a_count_of_zero_too_few()
    {
        ThroughputSummary summary = new(0, null, null, null, null, null, null);

        ThroughputPane.ComposeLines(summary, "day")[0].Should().Contain("nothing merged yet");
    }

    [Fact]
    public void Enough_data_renders_every_figure()
    {
        IReadOnlyList<string> lines = ThroughputPane.ComposeLines(Enough(), "week");

        lines.Should().HaveCount(2);
        lines[0].Should().Contain("8 merged");
        lines[0].Should().Contain("median 6.8h");
        lines[0].Should().Contain("p90 22.7h");
        lines[1].Should().Contain("first-pass 40%");
        lines[1].Should().Contain("laps/merged task 1.5");
        lines[1].Should().Contain("queued 26% of task time");
        lines[1].Should().Contain("waiting on a human 14% of task time");
    }

    [Fact]
    public void Project_show_carries_the_previous_period_beside_the_current_one()
    {
        IReadOnlyList<string> lines = ThroughputPane.ComposeLines(
            Enough(mergedCount: 8), Enough(mergedCount: 5, firstPassShare: 0.2), "week");

        lines.Should().HaveCount(3);
        lines[0].Should().Contain("8 merged");
        lines[2].Should().Contain("previous week");
        lines[2].Should().Contain("5 merged");
        lines[2].Should().Contain("first-pass 20%");
    }

    [Fact]
    public void A_previous_period_with_too_few_merges_says_so_rather_than_a_median()
    {
        ThroughputSummary previous = new(1, null, null, null, null, null, null);

        IReadOnlyList<string> lines = ThroughputPane.ComposeLines(Enough(), previous, "week");

        lines[^1].Should().Contain("previous week");
        lines[^1].Should().Contain("too few to summarize");
        lines[^1].Should().NotContain("median");
    }

    // ---- phase shares (idea 6e6d0200, Card 6) ----

    private static readonly PhaseClassShares Shares = new(0.1, 0.05, 0.8, 0.05);

    private static ThroughputSummary WithShares(
        PhaseClassShares? shares, params OwnerPhaseShares[] owners) =>
        Enough() with { PhaseShares = shares, OwnerShares = owners };

    [Fact]
    public void The_status_block_shows_the_phase_shares_install_wide()
    {
        IReadOnlyList<string> lines = ThroughputPane.ComposeLines(WithShares(Shares), "week");

        lines.Should().HaveCount(3);
        lines[2].Should().Be("  phases: by hand 10% · delegated 5% · automatic 80% · skipped 5%");
    }

    [Fact]
    public void The_project_block_shows_the_shares_for_the_current_period_beside_the_previous_ones()
    {
        IReadOnlyList<string> lines = ThroughputPane.ComposeLines(
            WithShares(Shares), WithShares(new PhaseClassShares(0.5, 0, 0.5, 0)), "week");

        lines.Should().HaveCount(4);
        lines[2].Should().Contain("automatic 80%");
        lines[3].Should().Contain("previous week").And.Contain("by hand 50%").And.Contain("automatic 50%");
    }

    [Fact]
    public void With_one_owner_holding_claims_there_is_no_line_of_your_own()
    {
        ThroughputSummary summary = WithShares(Shares, new OwnerPhaseShares("SHA256:me", 8, Shares));

        ThroughputPane.ComposeLines(summary, "week", "SHA256:me").Should().HaveCount(3);
    }

    [Fact]
    public void With_several_owners_the_block_adds_only_the_viewers_own_line()
    {
        ThroughputSummary summary = WithShares(
            Shares,
            new OwnerPhaseShares("SHA256:me", 6, new PhaseClassShares(0.25, 0, 0.75, 0)),
            new OwnerPhaseShares("SHA256:teammate", 5, new PhaseClassShares(0.9, 0, 0.1, 0)));

        IReadOnlyList<string> lines = ThroughputPane.ComposeLines(summary, "week", "SHA256:me");

        lines.Should().HaveCount(4);
        lines[3].Should().Be("  your phases (6 merged): by hand 25% · delegated 0% · automatic 75% · skipped 0%");
        string.Join('\n', lines).Should().NotContain("teammate").And.NotContain("90%");
    }

    [Fact]
    public void A_viewers_slice_under_the_floor_says_so_rather_than_printing_shares()
    {
        ThroughputSummary summary = WithShares(
            Shares, new OwnerPhaseShares("SHA256:me", 2, null), new OwnerPhaseShares("SHA256:teammate", 6, Shares));

        ThroughputPane.ComposeLines(summary, "week", "SHA256:me")[3]
            .Should().Be("  your phases: 2 merged — too few to summarize");
    }

    [Fact]
    public void A_viewer_with_no_claim_in_the_period_gets_a_line_that_says_so_and_nothing_of_anyone_elses()
    {
        ThroughputSummary summary = WithShares(
            Shares, new OwnerPhaseShares("SHA256:a", 6, Shares), new OwnerPhaseShares("SHA256:b", 5, Shares));

        IReadOnlyList<string> lines = ThroughputPane.ComposeLines(summary, "week", "SHA256:me");

        lines[3].Should().Contain("none of this period's merged tasks ran under a claim of yours");
    }

    [Fact]
    public void An_unresolved_viewer_prints_no_line_of_their_own()
    {
        ThroughputSummary summary = WithShares(
            Shares, new OwnerPhaseShares("SHA256:a", 6, Shares), new OwnerPhaseShares("SHA256:b", 5, Shares));

        ThroughputPane.ComposeLines(summary, "week", viewerOwnerKey: null).Should().HaveCount(3);
    }

    [Fact]
    public void A_summary_with_no_shares_prints_what_it_always_has()
    {
        ThroughputPane.ComposeLines(Enough(), "week").Should().HaveCount(2);
    }
}
