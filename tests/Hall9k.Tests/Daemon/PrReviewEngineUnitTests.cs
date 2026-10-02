using System.Text.Json;
using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// Two of <see cref="PrReviewEngine"/>'s own primitives, tested without a store because
/// neither one touches it: <see cref="PrReviewEngine.SessionStillLive"/> reads only the run
/// aggregate and the OS process seam, and <see cref="PrReviewEngine.EnsurePrimarySessionResultRecordedAsync"/>
/// only reads and writes files under a run directory. Coverage follow-up to the cycle-1
/// conformance finding at <c>PrReviewEngine.cs:50</c> — before this, neither had any test at all.
/// </summary>
public sealed class PrReviewEngineUnitTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    private readonly string _runDirectory = Path.Combine(Path.GetTempPath(), $"hall9k-pr-review-unit-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_runDirectory))
        {
            Directory.Delete(_runDirectory, recursive: true);
        }
    }

    private static PrReviewEngine NewEngine(FakeProcessManager processes) =>
        new(null!, null!, processes, null!, null!, Options.Create(new DaemonOptions()), NullLogger<PrReviewEngine>.Instance);

    private static PrReviewEngine NewEngine(FakeProcessManager processes, ILogger<PrReviewEngine> logger) =>
        new(null!, null!, processes, null!, null!, Options.Create(new DaemonOptions()), logger);

    private static RunAggregate DispatchedConformance(Guid sessionId, int processId, DateTimeOffset startedAt)
    {
        RunAggregate run = new();
        run.Apply(new PrReviewConformanceDispatched(DomainId.New(), sessionId, processId, startedAt, Now, AgentModel.Sonnet));
        return run;
    }

    [Fact]
    public void The_park_line_of_an_answer_only_task_says_it_answers_the_comment_on_the_owners_own_pull_request()
    {
        string reason = PrReviewEngine.ComposeMentionFollowUpParkReason(
            answersOwnPullRequest: true, "AgelessRx/arx-platform#2166", "taylor-dennison",
            "@Hallmanac just curious what the motivating factor is here?", "/runs/r1/mention-followup-addendum.md",
            unwalkedReportNote: string.Empty);

        reason.Should().Contain("AgelessRx/arx-platform#2166: answers taylor-dennison's comment on your own pull request")
            .And.Contain("just curious what the motivating factor is here?")
            .And.Contain("Drafted reply and analysis: /runs/r1/mention-followup-addendum.md")
            .And.Contain("Walk it with walk-pr-review-findings")
            .And.Contain("only on the owner's explicit go")
            .And.NotContain("tagged you")
            .And.NotContainEquivalentOf("review complete");
    }

    [Fact]
    public void The_park_line_of_a_follow_up_on_a_task_that_reviewed_keeps_its_tagged_you_wording()
    {
        string reason = PrReviewEngine.ComposeMentionFollowUpParkReason(
            answersOwnPullRequest: false, "acme/web#7", "ryan", "@brian one more thing", "/runs/r1/addendum.md",
            unwalkedReportNote: string.Empty);

        reason.Should().StartWith("acme/web#7: ryan tagged you")
            .And.Contain("Addendum: /runs/r1/addendum.md");
    }

    [Fact]
    public void A_dispatched_session_whose_process_is_still_alive_is_still_live()
    {
        FakeProcessManager processes = new();
        processes.MarkAlive(4242);
        RunAggregate run = DispatchedConformance(DomainId.New(), 4242, Now);

        NewEngine(processes).SessionStillLive(run, _runDirectory).Should().BeTrue(
            "the OS still reports the process running, so nothing needs to be redispatched");
    }

    [Fact]
    public void A_dispatched_session_whose_process_died_with_no_result_file_is_not_live()
    {
        FakeProcessManager processes = new();
        RunAggregate run = DispatchedConformance(DomainId.New(), 4242, Now);

        NewEngine(processes).SessionStillLive(run, _runDirectory).Should().BeFalse(
            "a daemon restart racing a crash leaves neither a live process nor a result — treated as never dispatched");
    }

    /// <summary>
    /// A dead process whose stream file already carries content is still treated as resumable
    /// (DriveAsync reads the result from that file rather than waiting on a process that no
    /// longer exists) — the third discrimination <see cref="PrReviewEngine.SessionStillLive"/>
    /// makes, alongside plain alive/dead.
    /// </summary>
    [Fact]
    public void A_dead_sessions_own_stream_file_with_content_still_counts_as_live()
    {
        FakeProcessManager processes = new();
        Guid sessionId = DomainId.New();
        RunAggregate run = DispatchedConformance(sessionId, 4242, Now);

        string streamFile = RunPaths.SessionStreamFile(_runDirectory, PrReviewEngine.ConformanceArtifactName(sessionId));
        Directory.CreateDirectory(_runDirectory);
        File.WriteAllText(streamFile, "{\"type\":\"result\",\"result\":\"merge-ready\"}\n");

        NewEngine(processes).SessionStillLive(run, _runDirectory).Should().BeTrue(
            "the session's own result already landed on disk even though the process that wrote it is gone");
    }

    /// <summary>
    /// A dead process whose stream file carries content but no terminal result line — the shape
    /// <c>claude -p --output-format stream-json</c> leaves when it is killed moments after
    /// spawning, having written only its <c>{"type":"system","subtype":"init",…}</c> line — is
    /// NOT live (cycle-1 adversarial finding, <c>PrReviewEngine.cs:232</c>): a non-empty file was
    /// once enough to count as resumable, which sent this case to <c>AwaitConformanceAsync</c>
    /// to wait out a process that no longer exists and fail the run, rather than back to
    /// <c>DriveAsync</c>'s redispatch branch.
    /// </summary>
    [Fact]
    public void A_dead_sessions_own_stream_file_with_only_an_init_line_is_not_live()
    {
        FakeProcessManager processes = new();
        Guid sessionId = DomainId.New();
        RunAggregate run = DispatchedConformance(sessionId, 4242, Now);

        string streamFile = RunPaths.SessionStreamFile(_runDirectory, PrReviewEngine.ConformanceArtifactName(sessionId));
        Directory.CreateDirectory(_runDirectory);
        File.WriteAllText(streamFile, "{\"type\":\"system\",\"subtype\":\"init\"}\n");

        NewEngine(processes).SessionStillLive(run, _runDirectory).Should().BeFalse(
            "the process died before ever writing a result — the file has content but nothing to resume from");
    }

    [Fact]
    public void A_session_already_marked_completed_is_live_regardless_of_the_process()
    {
        FakeProcessManager processes = new();
        Guid sessionId = DomainId.New();
        RunAggregate run = DispatchedConformance(sessionId, 4242, Now);
        run.Apply(new PrReviewConformanceCompleted(DomainId.New(), sessionId, Now));

        NewEngine(processes).SessionStillLive(run, _runDirectory).Should().BeTrue();
    }

    [Fact]
    public async Task Ensuring_the_adversarial_result_is_a_no_op_once_the_findings_file_already_exists()
    {
        string findingsFile = RunPaths.ReviewLensFindingsFile(_runDirectory, 1, ReviewLens.Adversarial.Slug);
        Directory.CreateDirectory(_runDirectory);
        await File.WriteAllTextAsync(findingsFile, "already recorded");

        // No main stream.jsonl at all — if this tried to re-derive, it would find nothing to
        // read and could only get this wrong by overwriting the real content with nothing.
        PrReviewEngine engine = NewEngine(new FakeProcessManager());
        await engine.EnsurePrimarySessionResultRecordedAsync(_runDirectory, ReviewLens.Adversarial.Slug, CancellationToken.None);

        (await File.ReadAllTextAsync(findingsFile)).Should().Be("already recorded");
    }

    /// <summary>
    /// The recovery half of the re-entrancy this exists for (cycle-1 conformance finding,
    /// `PrReviewEngine.cs:50`): a daemon restart landing between the primary adversarial
    /// session's own <c>AgentSessionCompleted</c> commit and <c>RunSupervisor</c>'s immediate
    /// follow-up call to record its result reaches <c>DriveAsync</c> — and so this method —
    /// with the findings file still missing, but the primary session's own stream.jsonl already
    /// on disk with its terminal result. Re-deriving from that file, rather than losing the
    /// result, is the whole point.
    /// </summary>
    [Fact]
    public async Task Ensuring_the_adversarial_result_re_derives_it_from_the_main_stream_when_the_findings_file_is_missing()
    {
        Directory.CreateDirectory(_runDirectory);
        string line = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "result",
            ["subtype"] = "success",
            ["is_error"] = false,
            ["result"] = "FINDING: severity=high; scope=in-scope; at=src/Auth.cs:1\nDefect: none, this is a test.",
        });
        await File.WriteAllTextAsync(RunPaths.StreamFile(_runDirectory), line + "\n");

        PrReviewEngine engine = NewEngine(new FakeProcessManager());
        await engine.EnsurePrimarySessionResultRecordedAsync(_runDirectory, ReviewLens.Adversarial.Slug, CancellationToken.None);

        string findingsFile = RunPaths.ReviewLensFindingsFile(_runDirectory, 1, ReviewLens.Adversarial.Slug);
        File.Exists(findingsFile).Should().BeTrue("the primary session's own terminal result was on disk to recover from");
        (await File.ReadAllTextAsync(findingsFile)).Should().Contain("src/Auth.cs:1");
    }

    [Fact]
    public async Task Ensuring_the_adversarial_result_stays_absent_when_neither_file_exists()
    {
        PrReviewEngine engine = NewEngine(new FakeProcessManager());
        await engine.EnsurePrimarySessionResultRecordedAsync(_runDirectory, ReviewLens.Adversarial.Slug, CancellationToken.None);

        File.Exists(RunPaths.ReviewLensFindingsFile(_runDirectory, 1, ReviewLens.Adversarial.Slug)).Should().BeFalse(
            "there was nothing anywhere to recover — DriveAsync's own dispatch has not happened yet in this scenario");
    }

    /// <summary>
    /// Security review idea 6be68ee2, daemon-consumers finding B: the primary session has ordinary
    /// file-system access to its own findings file throughout its run, so something could land
    /// there before the daemon ever writes the session's own recorded stdout. A "write only when
    /// absent" guard used to let that planted content stand forever; the write is now
    /// unconditional and a warning names the file when what was already there differed.
    /// </summary>
    [Fact]
    public async Task Writing_the_primary_result_replaces_differing_content_already_on_disk_and_warns()
    {
        string findingsFile = RunPaths.ReviewLensFindingsFile(_runDirectory, 1, ReviewLens.Adversarial.Slug);
        Directory.CreateDirectory(_runDirectory);
        await File.WriteAllTextAsync(findingsFile, "planted before the session's own result was recorded");

        ListLogger<PrReviewEngine> logger = new();
        PrReviewEngine engine = NewEngine(new FakeProcessManager(), logger);
        await engine.WritePrimarySessionResultAsync(
            _runDirectory, ReviewLens.Adversarial.Slug, "the session's own real stdout", CancellationToken.None);

        (await File.ReadAllTextAsync(findingsFile)).Should().Be(
            "the session's own real stdout", "the session's own recorded stdout is the only trustworthy source");
        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning && entry.Message.Contains(findingsFile),
            "the file that already carried different content is named in the warning");
    }

    /// <summary>The same overwrite-and-warn behavior as the pr-review lens above, for a bounded mention follow-up's own result file.</summary>
    [Fact]
    public async Task Recording_a_mention_follow_up_result_replaces_differing_content_already_on_disk_and_warns()
    {
        string resultFile = PrReviewEngine.MentionFollowUpResultFile(_runDirectory);
        Directory.CreateDirectory(_runDirectory);
        await File.WriteAllTextAsync(resultFile, "planted before the session's own result was recorded");

        ListLogger<PrReviewEngine> logger = new();
        PrReviewEngine engine = NewEngine(new FakeProcessManager(), logger);
        await engine.RecordMentionFollowUpResultAsync(_runDirectory, "the session's own real stdout", CancellationToken.None);

        (await File.ReadAllTextAsync(resultFile)).Should().Be(
            "the session's own real stdout", "the session's own recorded stdout is the only trustworthy source");
        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning && entry.Message.Contains(resultFile),
            "the file that already carried different content is named in the warning");
    }

    /// <summary>No planted content, no diff to warn about — the ordinary case stays quiet.</summary>
    [Fact]
    public async Task Recording_a_mention_follow_up_result_with_nothing_already_on_disk_writes_quietly()
    {
        ListLogger<PrReviewEngine> logger = new();
        PrReviewEngine engine = NewEngine(new FakeProcessManager(), logger);
        await engine.RecordMentionFollowUpResultAsync(_runDirectory, "the session's own real stdout", CancellationToken.None);

        string resultFile = PrReviewEngine.MentionFollowUpResultFile(_runDirectory);
        (await File.ReadAllTextAsync(resultFile)).Should().Be("the session's own real stdout");
        logger.Entries.Should().BeEmpty("nothing was there to differ from, so there is nothing to warn about");
    }
}
