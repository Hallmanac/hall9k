using FluentAssertions;
using Hall9k.Domain.Features.Plan;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The closing block of a discovery report: a pursue with one or more tasks, or a stop with none, read forgivingly about
/// decoration and strictly about meaning, because the platform cuts drafts from it.
/// </summary>
public sealed class DiscoveryReportTests
{
    [Fact]
    public void A_pursue_with_tasks_reads_in_the_order_the_tasks_are_written()
    {
        DiscoveryReadout? readout = DiscoveryReport.TryRead("Prose.\n\nDISCOVERY: pursue\nTASK: Do the first thing\nTASK: Do the second thing\n");

        readout.Should().NotBeNull();
        readout!.Decision.Should().Be(DiscoveryDecision.Pursue);
        readout.Objectives.Should().Equal("Do the first thing", "Do the second thing");
    }

    [Fact]
    public void A_stop_with_no_task_reads()
    {
        DiscoveryReport.TryRead("Nothing to build.\n\nDISCOVERY: stop\n")!.Decision.Should().Be(DiscoveryDecision.Stop);
    }

    [Fact]
    public void The_answer_line_may_come_after_the_tasks()
    {
        DiscoveryReport.TryRead("TASK: Only task\nDISCOVERY: pursue")!.Objectives.Should().Equal("Only task");
    }

    [Theory]
    [InlineData("**DISCOVERY:** pursue\n**TASK:** Do the thing.\n", "Do the thing")]
    [InlineData("- DISCOVERY: `pursue`\n- TASK: **Do the thing**.\n", "Do the thing")]
    [InlineData("`DISCOVERY: pursue.`\n`TASK: Do the thing.`\n", "Do the thing")]
    [InlineData("```\nDISCOVERY: pursue\nTASK: Do the thing\n```\n", "Do the thing")]
    [InlineData("discovery: Pursue\ntask: Do the thing\n\n\n", "Do the thing")]
    [InlineData("DISCOVERY: pursue\r\nTASK: Do the thing\r\n", "Do the thing")]
    public void Emphasis_a_trailing_period_a_list_marker_and_a_wrapping_fence_do_not_make_a_report_unreadable(string closing, string objective)
    {
        DiscoveryReport.TryRead($"Analysis.\n\n{closing}")!.Objectives.Should().Equal(objective);
    }

    [Fact]
    public void A_code_span_inside_an_objective_keeps_its_backticks()
    {
        DiscoveryReport.TryRead("DISCOVERY: pursue\nTASK: Make `a` and `b` agree.\n")!.Objectives.Should().Equal("Make `a` and `b` agree");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Just prose, no closing block.")]
    [InlineData("DISCOVERY: pursue")]
    [InlineData("DISCOVERY: stop\nTASK: Something")]
    [InlineData("DISCOVERY: pursue\nDISCOVERY: stop")]
    [InlineData("DISCOVERY: pursue\nTASK: ")]
    [InlineData("DISCOVERY: maybe\nTASK: Do it")]
    [InlineData("DISCOVERY: pursue or stop\nTASK: Do it")]
    [InlineData("TASK: Do it")]
    [InlineData("DISCOVERY: pursue\nTASK: Do it\nand then a closing sentence of prose")]
    public void Any_other_report_is_unreadable(string report)
    {
        DiscoveryReport.TryRead(report).Should().BeNull();
    }

    [Fact]
    public void A_quoted_example_of_the_markers_earlier_in_the_prose_counts_for_nothing()
    {
        string report = "The contract is:\n\nDISCOVERY: pursue\nTASK: an example\n\nAnd I conclude.\n\nDISCOVERY: stop\n";

        DiscoveryReport.TryRead(report)!.Decision.Should().Be(DiscoveryDecision.Stop);
    }

    [Fact]
    public void A_marker_shaped_line_of_prose_above_a_blank_line_is_not_part_of_the_closing_block()
    {
        string report = "Open questions:\n\n- Task: confirm who owns X\n\nDISCOVERY: stop\n";

        DiscoveryReadout? readout = DiscoveryReport.TryRead(report);

        readout.Should().NotBeNull();
        readout.Decision.Should().Be(DiscoveryDecision.Stop);
        readout.Objectives.Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_that_is_missing_is_told_apart_from_one_that_is_not_readable()
    {
        string directory = Directory.CreateTempSubdirectory("h9k-discovery-report-").FullName;
        try
        {
            string path = Path.Combine(directory, "report.md");
            (await DiscoveryReport.ReadFileAsync(path, CancellationToken.None)).Missing.Should().BeTrue();

            await File.WriteAllTextAsync(path, "no closing block");
            DiscoveryReading unreadable = await DiscoveryReport.ReadFileAsync(path, CancellationToken.None);
            (unreadable.Missing, unreadable.Readable).Should().Be((false, false));

            await File.WriteAllTextAsync(path, new string('x', 600_000) + "\nDISCOVERY: stop\n");
            (await DiscoveryReport.ReadFileAsync(path, CancellationToken.None)).Readout!.Decision.Should().Be(DiscoveryDecision.Stop);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
