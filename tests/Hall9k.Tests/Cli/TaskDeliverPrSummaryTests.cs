using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Prompts;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <c>h9k task deliver --pr-summary &lt;file&gt;</c>: the file becomes the run's pull request summary
/// artifact, the same file a headless session's block becomes, so the pull request is built from
/// it by the same code. Nothing here pushes a branch or reaches a remote.
/// </summary>
public sealed class TaskDeliverPrSummaryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"hall9k-deliver-pr-summary-{Guid.NewGuid():N}");

    public TaskDeliverPrSummaryTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task A_file_with_a_title_line_and_a_body_becomes_the_runs_artifact_the_opener_reads()
    {
        string file = WriteFile("summary.md", "Title: Add the browse surface\n\nThe browse surface lists projects.\n\nIt pages by name.");
        string runDirectory = Path.Combine(_directory, "run");

        await TaskDeliverCommand.CapturePrSummaryAsync(
            runDirectory, await TaskDeliverCommand.ReadPrSummaryAsync(file, CancellationToken.None), CancellationToken.None);

        PrSummaryArtifact.TryRead(NullLogger.Instance, Guid.Empty, runDirectory)
            .Should().BeEquivalentTo(new PrSummaryParser.PrSummary(
                "Add the browse surface", "The browse surface lists projects.\n\nIt pages by name."));
    }

    [Fact]
    public async Task The_artifact_is_byte_for_byte_what_a_headless_sessions_block_becomes()
    {
        const string title = "Add the browse surface";
        const string body = "The browse surface lists projects.\n\nIt pages by name.";
        string delivered = Path.Combine(_directory, "delivered");
        string headless = Path.Combine(_directory, "headless");
        string file = WriteFile("summary.md", $"Title: {title}\n\n{body}\n");

        await TaskDeliverCommand.CapturePrSummaryAsync(
            delivered, await TaskDeliverCommand.ReadPrSummaryAsync(file, CancellationToken.None), CancellationToken.None);
        await PrSummaryArtifact.CaptureAsync(
            NullLogger.Instance, Guid.Empty, headless,
            $"Done.\n\nPR SUMMARY:\nTitle: {title}\n\n{body}\n\nHANDOFF:\nnothing", CancellationToken.None);

        File.ReadAllText(RunPaths.PrSummaryFile(delivered)).Should().Be(File.ReadAllText(RunPaths.PrSummaryFile(headless)));
    }

    [Fact]
    public async Task A_file_that_carries_the_summary_marker_is_read_as_a_headless_block_would_be()
    {
        string file = WriteFile("summary.md", "PR SUMMARY:\nTitle: A title\n\nA body.\n");

        PrSummaryParser.PrSummary summary = await TaskDeliverCommand.ReadPrSummaryAsync(file, CancellationToken.None);

        summary.Should().BeEquivalentTo(new PrSummaryParser.PrSummary("A title", "A body."));
    }

    [Fact]
    public async Task A_fenced_file_is_tolerated_because_the_skill_asks_for_a_fence()
    {
        string file = WriteFile("summary.md", "```\nTitle: A title\n\nA body.\n```\n");

        PrSummaryParser.PrSummary summary = await TaskDeliverCommand.ReadPrSummaryAsync(file, CancellationToken.None);

        summary.Should().BeEquivalentTo(new PrSummaryParser.PrSummary("A title", "A body."));
    }

    [Fact]
    public async Task A_missing_file_is_refused_before_anything_is_pushed()
    {
        Func<Task> reading = () => TaskDeliverCommand.ReadPrSummaryAsync(Path.Combine(_directory, "nope.md"), CancellationToken.None);

        await reading.Should().ThrowAsync<DomainValidationException>().WithMessage("*does not exist*");
    }

    [Fact]
    public async Task An_empty_file_is_refused_and_says_what_a_summary_looks_like()
    {
        string file = WriteFile("empty.md", "  \n");

        Func<Task> reading = () => TaskDeliverCommand.ReadPrSummaryAsync(file, CancellationToken.None);

        await reading.Should().ThrowAsync<DomainValidationException>().WithMessage("*holds no summary*Title: <the title>*");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }
}
