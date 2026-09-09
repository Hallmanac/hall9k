using FluentAssertions;
using Hall9k.Connectors.Prompts;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The run directory's <c>pr-summary.md</c> and what the opener makes of it (Decisions Log
/// #163). Both call sites that write it — the build session's capture in
/// <c>RunSupervisor</c> and a review-fix session's refresh in <c>ReviewEngine</c> — go through
/// <see cref="PrSummaryArtifact"/>, so the write rules are exercised once here and each call site
/// is pinned separately in its own integration test.
/// <para>
/// Kept out of the Docker-gated <c>PullRequestOpenerTests</c> deliberately: composing a pull
/// request is a pure function of the run, the task and this file, and nothing about it needs a
/// database, a remote, or <c>gh</c> to be true.
/// </para>
/// </summary>
public sealed class PrSummaryArtifactTests : IDisposable
{
    private readonly string _runDirectory =
        Path.Combine(Path.GetTempPath(), $"hall9k-pr-summary-{Guid.NewGuid():N}");

    [Fact]
    public async Task A_result_carrying_a_block_writes_the_file()
    {
        await CaptureAsync("Did the work.\n\nPR SUMMARY:\nTitle: A title\n\nA body.\n\nHANDOFF:\nnothing");

        PrSummaryArtifact.TryRead(NullLogger.Instance, Guid.Empty, _runDirectory)
            .Should().BeEquivalentTo(new PrSummaryParser.PrSummary("A title", "A body."));
    }

    [Fact]
    public async Task A_result_carrying_none_writes_nothing_at_all()
    {
        await CaptureAsync("Did the work. No block here.");

        File.Exists(RunPaths.PrSummaryFile(_runDirectory)).Should().BeFalse();
        PrSummaryArtifact.TryRead(NullLogger.Instance, Guid.Empty, _runDirectory).Should().BeNull();
    }

    [Fact]
    public async Task A_later_session_carrying_a_block_replaces_the_earlier_one()
    {
        await CaptureAsync("PR SUMMARY:\nTitle: The build session's\n\nIts body.");
        await CaptureAsync("PR SUMMARY:\nTitle: The fix session's\n\nA better body.\n\nRESOLUTION: fixed");

        PrSummaryArtifact.TryRead(NullLogger.Instance, Guid.Empty, _runDirectory)
            .Should().BeEquivalentTo(new PrSummaryParser.PrSummary("The fix session's", "A better body."));
    }

    [Fact]
    public async Task A_later_session_carrying_none_leaves_the_earlier_one_standing()
    {
        await CaptureAsync("PR SUMMARY:\nTitle: The build session's\n\nIts body.");
        await CaptureAsync("Nothing here changes what a reviewer needs to know.\n\nRESOLUTION: fixed");

        PrSummaryArtifact.TryRead(NullLogger.Instance, Guid.Empty, _runDirectory)
            .Should().BeEquivalentTo(new PrSummaryParser.PrSummary("The build session's", "Its body."));
    }

    /// <summary>
    /// The leave-it-alone rule above, applied to HALF a block. A review-fix session whose fixes
    /// changed the story writes fresh prose and, reading the build session's title as still
    /// standing, routinely omits the <c>Title:</c> line; replacing the file outright dropped that
    /// title and reopened the pull request under the truncated-objective fallback — a milder
    /// rerun of the shape this artifact exists to fix (independent pre-PR review, cycle 1,
    /// adversarial lens).
    /// </summary>
    [Fact]
    public async Task A_refresh_carrying_only_a_body_keeps_the_title_already_written()
    {
        await CaptureAsync("PR SUMMARY:\nTitle: The build session's\n\nIts body.");
        await CaptureAsync("PR SUMMARY:\nThe fixes changed the story.\n\nRESOLUTION: fixed");

        PrSummaryArtifact.TryRead(NullLogger.Instance, Guid.Empty, _runDirectory)
            .Should().BeEquivalentTo(
                new PrSummaryParser.PrSummary("The build session's", "The fixes changed the story."));
    }

    /// <summary>The same rule mirrored: a refresh that is only a title keeps the prose under it.</summary>
    [Fact]
    public async Task A_refresh_carrying_only_a_title_keeps_the_body_already_written()
    {
        await CaptureAsync("PR SUMMARY:\nTitle: The build session's\n\nIts body.");
        await CaptureAsync("PR SUMMARY:\nTitle: The fix session's\n\nRESOLUTION: fixed");

        PrSummaryArtifact.TryRead(NullLogger.Instance, Guid.Empty, _runDirectory)
            .Should().BeEquivalentTo(new PrSummaryParser.PrSummary("The fix session's", "Its body."));
    }

    /// <summary>
    /// Nothing is inherited from a file that does not exist: the first session to compose one
    /// writes exactly what it wrote, half a block included.
    /// </summary>
    [Fact]
    public async Task A_first_block_carrying_only_a_body_is_written_as_the_body_alone()
    {
        await CaptureAsync("PR SUMMARY:\nProse and no title line.\n\nHANDOFF:\nnothing");

        PrSummaryArtifact.TryRead(NullLogger.Instance, Guid.Empty, _runDirectory)
            .Should().BeEquivalentTo(new PrSummaryParser.PrSummary(null, "Prose and no title line."));
    }

    /// <summary>
    /// The whole point of the artifact: the pull request the daemon sends is the one the session
    /// wrote, title and prose alike.
    /// </summary>
    [Fact]
    public async Task What_the_opener_sends_comes_from_the_artifact_when_one_exists()
    {
        await CaptureAsync(
            "PR SUMMARY:\nTitle: Resolve references in every host\n\nEvery host uses the shared provider now.");
        PrSummaryParser.PrSummary? summary = PrSummaryArtifact.TryRead(NullLogger.Instance, Guid.Empty, _runDirectory);

        PullRequestBody.Title(Task(), summary).Should().Be("ARX-4861: Resolve references in every host");
        PullRequestBody.Build(Run(), Task(), "run narration", sourceUrl: null, summary)
            .Should().Contain("Every host uses the shared provider now.")
            .And.NotContain("run narration")
            .And.NotContain("## Agent summary");
    }

    /// <summary>
    /// An interactive claim delivered by hand, a session killed before its final message, or a run
    /// whose stream predates the artifact: the daemon falls back to the skeleton it always wrote,
    /// with the key-prefixed short title as the one thing that improves.
    /// </summary>
    [Fact]
    public void What_the_opener_sends_falls_back_to_the_skeleton_when_no_session_composed_one()
    {
        PrSummaryParser.PrSummary? summary = PrSummaryArtifact.TryRead(NullLogger.Instance, Guid.Empty, _runDirectory);

        summary.Should().BeNull();
        PullRequestBody.Title(Task(), summary)
            .Should().Be("ARX-4861: Turn an external work item into a task with one command");
        PullRequestBody.Build(Run(), Task(), "run narration", sourceUrl: null, summary)
            .Should().Contain("## Agent summary").And.Contain("run narration");
    }

    public void Dispose()
    {
        if (Directory.Exists(_runDirectory))
        {
            Directory.Delete(_runDirectory, recursive: true);
        }
    }

    private Task CaptureAsync(string summary) =>
        PrSummaryArtifact.CaptureAsync(NullLogger.Instance, Guid.Empty, _runDirectory, summary, CancellationToken.None);

    private static RunDetails Run() => new() { Id = DomainId.New() };

    private static TaskDetails Task() => new()
    {
        Id = DomainId.New(),
        Objective = "Turn an external work item into a task with one command",
        AcceptanceCriteria = ["The importer refuses a closed issue"],
        ExternalReference = "jira:ARX-4861",
    };
}
