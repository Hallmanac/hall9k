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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// Two of <see cref="PrReviewEngine"/>'s own primitives, tested without a store because
/// neither one touches it: <see cref="PrReviewEngine.SessionStillLive"/> reads only the run
/// aggregate and the OS process seam, and <see cref="PrReviewEngine.EnsureAdversarialResultRecordedAsync"/>
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

    private static RunAggregate DispatchedConformance(Guid sessionId, int processId, DateTimeOffset startedAt)
    {
        RunAggregate run = new();
        run.Apply(new PrReviewConformanceDispatched(DomainId.New(), sessionId, processId, startedAt, Now, AgentModel.Sonnet));
        return run;
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
        await engine.EnsureAdversarialResultRecordedAsync(_runDirectory, CancellationToken.None);

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
        await engine.EnsureAdversarialResultRecordedAsync(_runDirectory, CancellationToken.None);

        string findingsFile = RunPaths.ReviewLensFindingsFile(_runDirectory, 1, ReviewLens.Adversarial.Slug);
        File.Exists(findingsFile).Should().BeTrue("the primary session's own terminal result was on disk to recover from");
        (await File.ReadAllTextAsync(findingsFile)).Should().Contain("src/Auth.cs:1");
    }

    [Fact]
    public async Task Ensuring_the_adversarial_result_stays_absent_when_neither_file_exists()
    {
        PrReviewEngine engine = NewEngine(new FakeProcessManager());
        await engine.EnsureAdversarialResultRecordedAsync(_runDirectory, CancellationToken.None);

        File.Exists(RunPaths.ReviewLensFindingsFile(_runDirectory, 1, ReviewLens.Adversarial.Slug)).Should().BeFalse(
            "there was nothing anywhere to recover — DriveAsync's own dispatch has not happened yet in this scenario");
    }
}
