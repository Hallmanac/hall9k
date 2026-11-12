using FluentAssertions;
using Hall9k.Daemon.Plan;
using Hall9k.Domain.Features.Plan;
using Hall9k.Domain.Features.Tasks;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>The contract of a refinement report: prose, then one closing line naming exactly one outcome.</summary>
public sealed class RefinementReportTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("h9k-refinement-report-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("REFINEMENT: revised", "Revised")]
    [InlineData("REFINEMENT: unchanged", "Unchanged")]
    [InlineData("REFINEMENT: question", "Question")]
    [InlineData("**REFINEMENT: revised**", "Revised")]
    [InlineData("**REFINEMENT:** revised.", "Revised")]
    [InlineData("- REFINEMENT: Unchanged", "Unchanged")]
    [InlineData("`REFINEMENT: question`", "Question")]
    [InlineData("refinement: revised", "Revised")]
    public void The_closing_line_names_the_outcome_whatever_decoration_a_model_put_on_it(string line, string expected)
    {
        RefinementReport.TryRead($"I checked the claims.\n\n{line}\n")!.Value.Should().Be(expected);
    }

    [Fact]
    public void A_code_fence_around_the_closing_line_and_trailing_blank_lines_are_formatting()
    {
        RefinementReport.TryRead("Prose.\n\n```\nREFINEMENT: revised\n```\n\n\n")!.Should().Be(RefinementOutcome.Revised);
    }

    [Theory]
    [InlineData("REFINEMENT: revised, mostly")]
    [InlineData("REFINEMENT: revised or unchanged")]
    [InlineData("REFINEMENT: maybe")]
    [InlineData("REFINEMENT:")]
    public void Other_text_on_the_closing_line_makes_the_report_unreadable(string line)
    {
        RefinementReport.TryRead($"Prose.\n\n{line}\n").Should().BeNull();
    }

    [Fact]
    public void Two_outcomes_in_the_closing_run_are_unreadable_and_a_quoted_example_earlier_in_the_prose_counts_for_nothing()
    {
        RefinementReport.TryRead("Prose.\n\nREFINEMENT: revised\nREFINEMENT: unchanged\n").Should().BeNull();
        RefinementReport.TryRead("The format is REFINEMENT: revised.\n\nREFINEMENT: example below\nMore prose.\n\nREFINEMENT: unchanged\n")!
            .Should().Be(RefinementOutcome.Unchanged);
    }

    [Fact]
    public void Prose_after_the_closing_line_or_no_line_at_all_is_unreadable()
    {
        RefinementReport.TryRead("REFINEMENT: revised\n\nOne more thing.\n").Should().BeNull();
        RefinementReport.TryRead("I revised the draft and it is all fine.\n").Should().BeNull();
        RefinementReport.TryRead(string.Empty).Should().BeNull();
    }

    [Fact]
    public async Task A_missing_file_has_no_report_and_an_unreadable_one_says_so()
    {
        string missing = Path.Combine(_directory, "nope.md");
        (RefinementOutcome? none, RefinementFailureReason? noReport) = await RefinementReport.ReadAsync(missing, CancellationToken.None);
        none.Should().BeNull();
        noReport.Should().Be(RefinementFailureReason.NoReport);

        string unreadable = Path.Combine(_directory, "bad.md");
        await File.WriteAllTextAsync(unreadable, "No closing line.\n");
        (RefinementOutcome? still, RefinementFailureReason? why) = await RefinementReport.ReadAsync(unreadable, CancellationToken.None);
        still.Should().BeNull();
        why.Should().Be(RefinementFailureReason.UnreadableReport);

        string good = Path.Combine(_directory, "good.md");
        await File.WriteAllTextAsync(good, "Fine.\n\nREFINEMENT: unchanged\n");
        (RefinementOutcome? outcome, RefinementFailureReason? failure) = await RefinementReport.ReadAsync(good, CancellationToken.None);
        outcome.Should().Be(RefinementOutcome.Unchanged);
        failure.Should().BeNull();
    }

    private const string Split = "I checked the claims; the draft is three increments.\n\nREFINEMENT: split\nTASK: Read the flag\nTASK: Act on the flag\nTASK: Report the flag\n";

    [Fact]
    public void A_split_block_reads_as_a_split_with_its_increments_in_order()
    {
        RefinementReport.TryRead(Split).Should().Be(RefinementOutcome.Split);
        RefinementSplitReport.TryRead(Split).Should().Equal("Read the flag", "Act on the flag", "Report the flag");
    }

    [Fact]
    public void An_increment_line_forgives_what_a_discovery_task_line_forgives_and_a_fence_and_blank_lines_may_close_the_block()
    {
        string report = "Prose.\n\n```\n**REFINEMENT: split.**\n- **TASK:** `Read the flag`.\n1. *TASK: Act on the `flag` value*\n```\n\n";

        RefinementSplitReport.TryRead(report).Should().Equal("Read the flag", "Act on the `flag` value");
        RefinementReport.TryRead(report).Should().Be(RefinementOutcome.Split);
    }

    [Theory]
    [InlineData("Prose.\n\nREFINEMENT: split\nTASK: Only one\n")]
    [InlineData("Prose.\n\nREFINEMENT: split\n")]
    [InlineData("Prose.\n\nREFINEMENT: split\nTASK: One\nTASK: **.**\n")]
    [InlineData("Prose.\n\nREFINEMENT: split\nTASK: One\n\nTASK: Two\n")]
    [InlineData("Prose.\n\nREFINEMENT: split\n\nTASK: One\nTASK: Two\n")]
    [InlineData("Prose.\n\nTASK: Zero\nREFINEMENT: split\nTASK: One\nTASK: Two\n")]
    [InlineData("Prose.\n\nREFINEMENT: question\nREFINEMENT: split\nTASK: One\nTASK: Two\n")]
    [InlineData("Prose.\n\nREFINEMENT: question\n\nREFINEMENT: split\nTASK: One\nTASK: Two\n")]
    [InlineData("Prose.\n\nREFINEMENT: split\nTASK: One\nREFINEMENT: split\nTASK: Two\nTASK: Three\n")]
    [InlineData("Prose.\n\nREFINEMENT: question\nTASK: One\nTASK: Two\n")]
    [InlineData("Prose.\n\nREFINEMENT: revised\nTASK: One\n")]
    [InlineData("Prose.\n\nREFINEMENT: split\nTASK: One\nTASK: Two\nMore prose.\n")]
    public void A_split_block_with_fewer_than_two_increments_a_blank_one_a_blank_line_a_stray_task_line_or_a_second_outcome_is_unreadable(string report)
    {
        RefinementSplitReport.TryRead(report).Should().BeNull();
        RefinementReport.TryRead(report).Should().BeNull();
    }

    [Theory]
    [InlineData("Prose.\n\nREFINEMENT: revised\n", "Revised")]
    [InlineData("Prose.\n\nTASK: Not an increment\nREFINEMENT: unchanged\n", "Unchanged")]
    [InlineData("Prose with REFINEMENT: split\nTASK: quoted\nTASK: quoted\n\nMore prose.\n\nREFINEMENT: question\n", "Question")]
    public void A_report_ending_in_any_other_outcome_reads_as_it_did_before_the_split(string report, string expected)
    {
        RefinementReport.TryRead(report)!.Value.Should().Be(expected);
        RefinementSplitReport.TryRead(report).Should().BeNull();
    }

    [Fact]
    public async Task A_split_report_file_is_read_whole_and_a_missing_one_says_so()
    {
        string path = Path.Combine(_directory, "split.md");
        await File.WriteAllTextAsync(path, Split);

        (await RefinementReport.ReadAsync(path, CancellationToken.None)).Outcome.Should().Be(RefinementOutcome.Split);
        RefinementSplitReading reading = await RefinementSplitReport.ReadFileAsync(path, CancellationToken.None);
        reading.Increments.Should().Equal("Read the flag", "Act on the flag", "Report the flag");
        (await RefinementSplitReport.ReadFileAsync(Path.Combine(_directory, "nope.md"), CancellationToken.None)).Missing.Should().BeTrue();
    }
}
