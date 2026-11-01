using FluentAssertions;
using Hall9k.Daemon.Challenge;
using Hall9k.Domain.Features.Tasks;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The contract of a challenge report's closing block: one verdict line, then one line per change marked must, should,
/// or park with non-blank text for both the change and its evidence. The prose above it is free.
/// </summary>
public sealed class ChallengeReportTests
{
    private static string Report(params string[] closing) => "Some free prose.\n\n" + string.Join('\n', closing) + "\n";

    [Theory]
    [InlineData("VERDICT: publish as written", "publish as written")]
    [InlineData("VERDICT: Publish As Written", "publish as written")]
    [InlineData("**VERDICT: publish as written.**", "publish as written")]
    [InlineData("**VERDICT:** publish as written.", "publish as written")]
    [InlineData("- VERDICT: publish as written", "publish as written")]
    [InlineData("`VERDICT: publish as written`", "publish as written")]
    [InlineData("verdict: publish-as-written", "publish as written")]
    public void Emphasis_and_a_trailing_period_do_not_make_the_verdict_line_unreadable(string line, string expected)
    {
        ChallengeReading? reading = ChallengeReport.TryRead(Report(line, "CHANGE: park - a thing - why"));

        reading.Should().NotBeNull();
        reading!.Verdict.Phrase.Should().Be(expected);
        reading.ParkCount.Should().Be(1);
    }

    [Theory]
    [InlineData("VERDICT: publish as revised (after Brian runs the one check)")]
    [InlineData("VERDICT: publish as revised, with one condition")]
    [InlineData("VERDICT: publish as written or do not publish")]
    [InlineData("VERDICT: maybe")]
    [InlineData("VERDICT:")]
    public void Other_text_on_the_verdict_line_makes_it_unreadable(string line)
    {
        ChallengeReport.TryRead(Report(line, "CHANGE: must - a thing - why")).Should().BeNull();
    }

    [Fact]
    public void Counts_every_class_and_reads_the_changes_with_emphasis_and_a_trailing_period()
    {
        ChallengeReading? reading = ChallengeReport.TryRead(Report(
            "VERDICT: publish as revised",
            "CHANGE: must - make the request node scoped - EventScopeRegistry.cs:138",
            "**CHANGE: should - say the home - file.cs:10.**",
            "- CHANGE: Should - another - evidence - with a dash inside it",
            "CHANGE: park - parallel challenges - trigger: a complaint"));

        reading.Should().Be(new ChallengeReading(ChallengeVerdict.PublishAsRevised, 1, 2, 1));
    }

    [Theory]
    [InlineData("CHANGE: must - only the change")]
    [InlineData("CHANGE: must - - evidence")]
    [InlineData("CHANGE: must -  - evidence")]
    [InlineData("CHANGE: must - the change - ")]
    [InlineData("CHANGE: maybe - the change - evidence")]
    [InlineData("CHANGE: the change - evidence")]
    [InlineData("CHANGE: must the change evidence")]
    public void A_change_line_missing_its_class_its_text_or_its_evidence_is_unreadable(string change)
    {
        ChallengeReport.TryRead(Report("VERDICT: publish as revised", "CHANGE: should - fine - fine", change)).Should().BeNull();
    }

    [Theory]
    [InlineData("publish as written", "CHANGE: must - x - y")]
    [InlineData("publish as written", "CHANGE: should - x - y")]
    [InlineData("publish as revised", "CHANGE: park - x - y")]
    [InlineData("do not publish", "CHANGE: should - x - y")]
    [InlineData("do not publish", "CHANGE: park - x - y")]
    public void A_verdict_that_disagrees_with_its_changes_is_unreadable(string verdict, string change)
    {
        ChallengeReport.TryRead(Report($"VERDICT: {verdict}", change)).Should().BeNull();
    }

    [Fact]
    public void A_publish_as_written_with_only_parks_and_a_do_not_publish_with_a_must_are_readable()
    {
        ChallengeReport.TryRead(Report("VERDICT: publish as written", "CHANGE: park - x - y")).Should().NotBeNull();
        ChallengeReport.TryRead(Report("VERDICT: publish as written")).Should().NotBeNull();
        ChallengeReport.TryRead(Report("VERDICT: do not publish", "CHANGE: must - x - y")).Should().NotBeNull();
    }

    [Fact]
    public void Two_verdict_lines_or_none_in_the_closing_block_are_unreadable()
    {
        ChallengeReport.TryRead(Report(
            "VERDICT: publish as revised", "VERDICT: do not publish", "CHANGE: must - x - y")).Should().BeNull();
        ChallengeReport.TryRead("prose only\nCHANGE: must - x - y\n").Should().BeNull();
        ChallengeReport.TryRead(string.Empty).Should().BeNull();
    }

    [Fact]
    public void A_quoted_example_of_the_format_earlier_in_the_prose_is_not_part_of_the_closing_block()
    {
        string report = "The format is:\n\nVERDICT: do not publish\nCHANGE: must - example - example\n\nI found nothing wrong.\n\n"
            + "VERDICT: publish as written\n";

        ChallengeReport.TryRead(report)!.Verdict.Should().Be(ChallengeVerdict.PublishAsWritten);
    }

    [Fact]
    public void A_code_fence_around_the_closing_block_is_formatting_and_a_fence_inside_it_is_not()
    {
        ChallengeReport.TryRead("Prose.\n\n```\nVERDICT: publish as revised\nCHANGE: must - x - y\n```\n")!
            .MustCount.Should().Be(1);
        ChallengeReport.TryRead("Prose.\n\n```text\nVERDICT: publish as written\n```\n\n")!
            .Verdict.Should().Be(ChallengeVerdict.PublishAsWritten);
        ChallengeReport.TryRead("VERDICT: publish as revised\n```\nCHANGE: must - x - y\n").Should().BeNull();
    }

    [Fact]
    public void A_wrapped_change_is_unreadable_and_blank_lines_after_the_block_are_not_text()
    {
        ChallengeReport.TryRead(Report(
            "VERDICT: publish as revised", "CHANGE: must - x - y", "", "")).Should().NotBeNull();
        ChallengeReport.TryRead(Report(
            "VERDICT: publish as revised", "CHANGE: must - x - y", "CHANGE: should - wrapped",
            "- the evidence on the next line")).Should().BeNull("a change is one line, and the tail ends the block before the verdict");
    }

    [Fact]
    public void Prose_after_the_block_makes_the_report_unreadable_so_an_example_of_the_format_cannot_stand_as_the_verdict()
    {
        ChallengeReport.TryRead("The format is:\n\n    VERDICT: publish as written\n\nI could not finish checking the rest.\n")
            .Should().BeNull();
        ChallengeReport.TryRead(Report("VERDICT: publish as revised", "CHANGE: must - x - y", "", "Done.")).Should().BeNull();
    }

    [Fact]
    public void A_quoted_verdict_line_after_the_real_block_does_not_replace_it()
    {
        ChallengeReport.TryRead(Report(
            "VERDICT: do not publish", "CHANGE: must - x - y", "", "Appendix, the prompt I ran:", "> VERDICT: publish as written"))
            .Should().BeNull();
    }

    [Fact]
    public void A_verdict_written_after_its_changes_is_unreadable_so_a_wrapped_change_cannot_drop_a_must()
    {
        ChallengeReport.TryRead(Report(
            "CHANGE: must - a - b", "the evidence, wrapped", "CHANGE: should - c - d", "VERDICT: publish as revised"))
            .Should().BeNull();
    }

    [Fact]
    public void Windows_line_endings_read_the_same()
    {
        ChallengeReport.TryRead("prose\r\nVERDICT: publish as revised\r\nCHANGE: must - x - y\r\n")
            .Should().Be(new ChallengeReading(ChallengeVerdict.PublishAsRevised, 1, 0, 0));
    }

    [Fact]
    public async Task A_missing_file_is_no_report_and_an_unreadable_one_is_unreadable()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"h9k-challenge-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "report.md");
            (await ChallengeReport.ReadAsync(path, CancellationToken.None)).Failure.Should().Be(ChallengeFailureReason.NoReport);

            await File.WriteAllTextAsync(path, "no block here");
            (await ChallengeReport.ReadAsync(path, CancellationToken.None)).Failure.Should().Be(ChallengeFailureReason.UnreadableReport);

            await File.WriteAllTextAsync(path, Report("VERDICT: publish as revised", "CHANGE: should - x - y"));
            (await ChallengeReport.ReadAsync(path, CancellationToken.None)).Reading.Should().NotBeNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_report_larger_than_the_cap_is_read_from_its_end()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"h9k-challenge-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "report.md");
            string padding = string.Concat(Enumerable.Repeat("prose that goes on and on and on\n", 40_000));
            await File.WriteAllTextAsync(path, padding + "VERDICT: do not publish\nCHANGE: must - x - y\n");

            (await ChallengeReport.ReadAsync(path, CancellationToken.None)).Reading.Should()
                .Be(new ChallengeReading(ChallengeVerdict.DoNotPublish, 1, 0, 0));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
