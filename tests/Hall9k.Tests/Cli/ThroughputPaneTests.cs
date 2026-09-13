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
}
