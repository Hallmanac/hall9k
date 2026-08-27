using Hall9k.Domain.Infrastructure.Storage;
using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

using Hall9k.Tests.Fakes;

namespace Hall9k.Tests.Integration;

// Both classes redirect the process-wide HALL9K_HOME; sharing a collection serializes
// them so one test's home is never yanked out from under the other's tail loop.
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class VerificationRunnerTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly string _home = SetTempHome();
    private readonly string _worktree = Path.Combine(Path.GetTempPath(), $"hall9k-vt-{Guid.NewGuid():N}");

    private static string SetTempHome()
    {
        string home = Path.Combine(Path.GetTempPath(), $"hall9k-vhome-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("HALL9K_HOME", home);
        return home;
    }

    [Fact]
    public async Task All_gates_passing_records_verification_passed_with_logs()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId) = await SeedAsync(store,
            [new VerifyCommand("hello", "echo hello-from-gate"), new VerifyCommand("truth", "true")], cts.Token);

        await NewRunner(store).VerifyAsync(runId, taskId, cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Value.Should().Be("Verifying", "VerificationPassed does not transition; the PR step does");
        run.FailedGates.Should().BeEmpty();

        File.ReadAllText(Path.Combine(RunPaths.GlobalDirectory(runId), "verify-hello.log"))
            .Should().Contain("hello-from-gate");
    }

    [Fact]
    public async Task Failing_gate_fails_run_and_task_and_names_the_gate()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId) = await SeedAsync(store,
            [new VerifyCommand("ok", "true"), new VerifyCommand("boom", "echo exploding; exit 3"), new VerifyCommand("never", "true")], cts.Token);

        await NewRunner(store).VerifyAsync(runId, taskId, cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Value.Should().Be("Failed");
        run.FailedGates.Should().ContainSingle().Which.Should().Be("boom");
        run.FailureReason.Should().Contain("exploding");

        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Value.Should().Be("Failed");
        (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().BeNull("verification failure releases the lease");

        File.Exists(Path.Combine(RunPaths.GlobalDirectory(runId), "verify-never.log"))
            .Should().BeFalse("gates after the failure never run");
    }

    /// <summary>
    /// The origin incident (2026-08-23): a gate died on a connection-class signature — the
    /// container, not the agent's work — and was fine on the very next attempt. Backlog 53:
    /// the retry happens in place, is recorded on the stream, and never fails the run.
    /// </summary>
    [Fact]
    public async Task An_infrastructure_classified_failure_retries_once_and_passes()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        string marker = Path.Combine(_worktree, "retry-marker");
        (Guid taskId, Guid runId) = await SeedAsync(store,
            [new VerifyCommand("flaky",
                $"if test -f {marker}; then echo ok; exit 0; " +
                $"else touch {marker}; echo 'Npgsql.NpgsqlException: Connection refused'; exit 1; fi")],
            cts.Token);

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, cts.Token);

        passed.Should().BeTrue("the second attempt is what the flaky gate actually does");
        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Value.Should().Be("Verifying");
        run.FailedGates.Should().BeEmpty();

        var events = await query.Events.FetchStreamAsync(runId, token: cts.Token);
        GateRetried retried = events.Select(e => e.Data).OfType<GateRetried>().Single();
        retried.Gate.Should().Be("flaky");
        retried.Cause.Should().Contain("Connection refused");
        events.Select(e => e.Data).OfType<RunFailed>().Should().BeEmpty("a passing retry never fails the run");

        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Value.Should().NotBe("Failed", "a flake the retry absorbed spends no budget on the task");
    }

    /// <summary>
    /// A genuinely broken environment surfaces instead of looping: two consecutive
    /// infrastructure-classified failures fail the run honestly, the classification named in
    /// the reason (backlog 53).
    /// </summary>
    [Fact]
    public async Task A_second_consecutive_infrastructure_failure_fails_the_run_with_the_classification_named()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId) = await SeedAsync(store,
            [new VerifyCommand("dead", "echo 'Npgsql.NpgsqlException: Connection refused'; exit 1")], cts.Token);

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, cts.Token);

        passed.Should().BeFalse("the environment never recovered across the retry");
        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Value.Should().Be("Failed");
        run.FailureReason.Should().Contain("infrastructure-classified").And.Contain("twice in a row");

        var events = await query.Events.FetchStreamAsync(runId, token: cts.Token);
        events.Select(e => e.Data).OfType<GateRetried>().Should().ContainSingle(
            "exactly one retry is spent before the run gives up on it");

        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Value.Should().Be("Failed", "a genuinely broken environment fails the task honestly");
    }

    /// <summary>
    /// The retry budget otherwise lives only in <see cref="VerificationRunner.VerifyAsync"/>'s
    /// local state: a daemon that died after committing <see cref="GateRetried"/> but before the
    /// gate's resolution would, on adoption, resume with a fresh call and no memory of the retry
    /// already spent. <see cref="RunDetails.PendingGateRetry"/> is the persisted record that
    /// closes that gap (backlog 53, Copilot review on PR #36).
    /// </summary>
    [Fact]
    public async Task A_gate_retried_before_an_earlier_daemon_restart_never_earns_a_second_retry_on_adoption()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId) = await SeedAsync(store,
            [new VerifyCommand("dead", "echo 'Npgsql.NpgsqlException: Connection refused'; exit 1")], cts.Token);

        // The crash window: a prior daemon lifetime committed the retry and then died before
        // this gate's outcome was ever recorded — no VerificationFailed/VerificationPassed
        // follows. Adoption's fresh VerifyAsync call is what this simulates re-entering into.
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new GateRetried(runId, "dead", "prior attempt's cause", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, cts.Token);

        passed.Should().BeFalse("the gate already spent its one retry before this daemon lifetime began");
        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Value.Should().Be("Failed");
        run.FailureReason.Should().Contain("already spending its one retry before an earlier daemon restart");

        var events = await query.Events.FetchStreamAsync(runId, token: cts.Token);
        events.Select(e => e.Data).OfType<GateRetried>().Should().ContainSingle(
            "the persisted retry from before this call still counts — adoption never grants a second one");
    }

    /// <summary>
    /// Classification reads the gate's whole output, not just the 400-character tail kept for
    /// the recorded summary: a marker logged early in a large `dotnet test` run must not be
    /// pushed out of that fixed-size window and go unclassified (adversarial review, cycle 1).
    /// </summary>
    [Fact]
    public async Task A_connection_class_signature_pushed_out_of_the_tail_still_classifies_as_infrastructure()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId) = await SeedAsync(store,
            [new VerifyCommand("flaky",
                "echo 'Npgsql.NpgsqlException: Connection refused'; " +
                "for i in $(seq 1 100); do echo 'padding line long enough to push the marker past a 400-character tail'; done; " +
                "exit 1")],
            cts.Token);

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, cts.Token);

        passed.Should().BeFalse("the environment never recovered across the retry");
        await using IQuerySession query = store.QuerySession();
        var events = await query.Events.FetchStreamAsync(runId, token: cts.Token);
        List<GateRetried> retries = [.. events.Select(e => e.Data).OfType<GateRetried>()];
        retries.Should().ContainSingle(
            "the marker classifies as infrastructure even though the padding pushes it out of the recorded tail");
        retries[0].Cause.Should().Contain("Npgsql.NpgsqlException: Connection refused",
            "the durable event must still carry the matching marker even though it falls outside the " +
            "400-character tail kept for the recorded summary (adversarial review, PR #36's Copilot review)");

        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.FailureReason.Should().Contain("infrastructure-classified");
    }

    /// <summary>
    /// The retry is a second look, not a second pass: if it surfaces a real failure instead of
    /// another infrastructure signature, that real failure is what's recorded — unclassified.
    /// </summary>
    [Fact]
    public async Task A_retry_that_surfaces_a_real_failure_is_recorded_as_a_real_failure()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        string marker = Path.Combine(_worktree, "retry-marker");
        (Guid taskId, Guid runId) = await SeedAsync(store,
            [new VerifyCommand("flaky",
                $"if test -f {marker}; then echo 'Assert.Equal() Failure: Expected 3, Actual 4'; exit 1; " +
                $"else touch {marker}; echo 'Npgsql.NpgsqlException: Connection refused'; exit 1; fi")],
            cts.Token);

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, cts.Token);

        passed.Should().BeFalse();
        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Value.Should().Be("Failed");
        run.FailureReason.Should().Contain("Assert.Equal").And.NotContain("infrastructure-classified");
    }

    [Fact]
    public async Task No_gates_passes_with_an_explicit_note()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId) = await SeedAsync(store, gates: [], cts.Token);

        await NewRunner(store).VerifyAsync(runId, taskId, cts.Token);

        await using IQuerySession query = store.QuerySession();
        var events = await query.Events.FetchStreamAsync(runId, token: cts.Token);
        VerificationPassed passed = events.Select(e => e.Data).OfType<VerificationPassed>().Single();
        passed.Note.Should().Contain("No verification gates configured");
    }

    [Fact]
    public async Task Overrunning_gate_times_out_as_a_failure_not_a_hang()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId) = await SeedAsync(store, [new VerifyCommand("slow", "sleep 30")], cts.Token);

        VerificationRunner runner = new(
            store,
            Options.Create(new DaemonOptions { VerifyGateTimeout = TimeSpan.FromSeconds(1) }),
            NullLogger<VerificationRunner>.Instance);

        await runner.VerifyAsync(runId, taskId, cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Value.Should().Be("Failed");
        run.FailureReason.Should().Contain("timeout");
    }

    /// <summary>
    /// A container that never comes up can manifest as a hang, not a non-zero exit: the gate
    /// writes the connection-class marker before it gets stuck, then the timeout kills it.
    /// Classification must read what the gate actually wrote, not the synthetic timeout
    /// message, or a startup hang is never retried and is blamed on the agent's work
    /// (adversarial review, cycle 2).
    /// </summary>
    [Fact]
    public async Task A_gate_that_hangs_after_writing_an_infrastructure_marker_still_classifies_and_retries()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        string marker = Path.Combine(_worktree, "retry-marker");
        (Guid taskId, Guid runId) = await SeedAsync(store,
            [new VerifyCommand("flaky",
                $"if test -f {marker}; then echo ok; exit 0; " +
                $"else touch {marker}; echo 'Npgsql.NpgsqlException: Connection refused'; sleep 30; fi")],
            cts.Token);

        VerificationRunner runner = new(
            store,
            Options.Create(new DaemonOptions { VerifyGateTimeout = TimeSpan.FromSeconds(1) }),
            NullLogger<VerificationRunner>.Instance);

        bool passed = await runner.VerifyAsync(runId, taskId, cts.Token);

        passed.Should().BeTrue("the second attempt exits clean once the hang is behind it");
        await using IQuerySession query = store.QuerySession();
        var events = await query.Events.FetchStreamAsync(runId, token: cts.Token);
        GateRetried retried = events.Select(e => e.Data).OfType<GateRetried>().Single();
        retried.Cause.Should().Contain("timeout");
        events.Select(e => e.Data).OfType<RunFailed>().Should().BeEmpty("a passing retry never fails the run");
    }

    [Fact]
    public async Task Zero_commits_on_the_branch_fails_fast_before_any_gate_runs()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        await InitGitWorktreeAsync(withTaskCommit: false, cts.Token);
        (Guid taskId, Guid runId) = await SeedAsync(store,
            [new VerifyCommand("never", "echo should-not-run")], cts.Token);

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, cts.Token);

        passed.Should().BeFalse("gates on an unmodified tree pass vacuously and prove nothing");
        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Value.Should().Be("Failed");
        run.FailureReason.Should().Contain("produced no commits");
        run.FailedGates.Should().BeEmpty("no gate failed — no gate ever ran");

        (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Value.Should().Be("Failed");
        (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().BeNull("the failure releases the lease");
        File.Exists(Path.Combine(RunPaths.GlobalDirectory(runId), "verify-never.log"))
            .Should().BeFalse("the failure lands before the gates, not after");
    }

    [Fact]
    public async Task A_branch_with_commits_clears_the_no_commit_check_and_runs_its_gates()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        await InitGitWorktreeAsync(withTaskCommit: true, cts.Token);
        (Guid taskId, Guid runId) = await SeedAsync(store, [new VerifyCommand("truth", "true")], cts.Token);

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, cts.Token);

        passed.Should().BeTrue();
        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.State.Value.Should().Be("Verifying");
    }

    /// <summary>
    /// Backlog 57: a run whose branch carries zero commits AND still holds a modified file in
    /// the worktree gets the file named alongside the no-commits reason, not "produced no
    /// commits" alone — the file is what tells a human or a retry session finished work is
    /// sitting there instead of missing entirely.
    /// </summary>
    [Fact]
    public async Task Zero_commits_with_a_modified_file_names_the_file_alongside_the_no_commits_reason()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        await InitGitWorktreeAsync(withTaskCommit: false, cts.Token, trackedFile: "stranded.txt");
        await File.WriteAllTextAsync(Path.Combine(_worktree, "stranded.txt"), "left behind", cts.Token);
        (Guid taskId, Guid runId) = await SeedAsync(store,
            [new VerifyCommand("never", "echo should-not-run")], cts.Token);

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, cts.Token);

        passed.Should().BeFalse();
        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Value.Should().Be("Failed");
        run.FailureReason.Should().Contain("produced no commits");
        run.FailureReason.Should().Contain("stranded.txt", "the stranded file must be named, not just implied");
        run.FailedGates.Should().BeEmpty("no gate ever ran");
    }

    /// <summary>
    /// The shape the zero-commit check alone always missed (origin incident, PR #53's cycle-3
    /// fix round, 2026-08-26): some commits landed, but the session still ended with
    /// modified-but-uncommitted files sitting in the worktree. Gates on that tree would test
    /// content the pull request never actually carries, so this fails before any gate runs and
    /// names every file left behind.
    /// </summary>
    [Fact]
    public async Task Committed_work_with_an_uncommitted_file_still_fails_before_the_gates()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        await InitGitWorktreeAsync(withTaskCommit: true, cts.Token, trackedFile: "half-done.cs");
        await File.WriteAllTextAsync(Path.Combine(_worktree, "half-done.cs"), "left behind", cts.Token);
        (Guid taskId, Guid runId) = await SeedAsync(store,
            [new VerifyCommand("never", "echo should-not-run")], cts.Token);

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, cts.Token);

        passed.Should().BeFalse("finished work left uncommitted never reaches the pull request");
        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Value.Should().Be("Failed");
        run.FailureReason.Should().Contain("modified-but-uncommitted");
        run.FailureReason.Should().Contain("half-done.cs");
        run.FailureReason.Should().NotContain("produced no commits", "this branch has commits; only files are stranded");
        run.FailedGates.Should().BeEmpty("no gate ever ran");

        (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Value.Should().Be("Failed");
        File.Exists(Path.Combine(RunPaths.GlobalDirectory(runId), "verify-never.log"))
            .Should().BeFalse("the failure lands before the gates, not after");
    }

    /// <summary>
    /// An untracked file is not stranded agent work — it is as likely to be a gate's own
    /// byproduct (a coverage report, a lint cache) that the project's `.gitignore` does not yet
    /// name, and failing a run on it would be a defect a retry can never clear, since the next
    /// session's gates regenerate the same file (independent pre-PR review, adversarial finding).
    /// The uncommitted-files check separates untracked entries out of `git status --porcelain -z`
    /// for exactly this reason, and only warns about them (independent pre-PR review, cycle 2
    /// conformance finding) rather than failing the run.
    /// </summary>
    [Fact]
    public async Task An_untracked_file_left_in_the_worktree_does_not_fail_the_run()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        await InitGitWorktreeAsync(withTaskCommit: true, cts.Token);
        await File.WriteAllTextAsync(Path.Combine(_worktree, "TestResults.trx"), "gate byproduct", cts.Token);
        (Guid taskId, Guid runId) = await SeedAsync(store, [new VerifyCommand("truth", "true")], cts.Token);

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, cts.Token);

        passed.Should().BeTrue("an untracked file is not stranded agent work");
        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.State.Value.Should().Be("Verifying");
    }

    [Fact]
    public async Task A_research_task_may_legitimately_end_with_zero_commits()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        await InitGitWorktreeAsync(withTaskCommit: false, cts.Token);
        (Guid taskId, Guid runId) = await SeedAsync(store,
            [new VerifyCommand("truth", "true")], cts.Token, TaskType.Research);

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, cts.Token);

        passed.Should().BeTrue("a research task's deliverable is its transcript, not commits");
    }

    /// <summary>
    /// The no-commit exemption for Research tasks is about commits specifically — their
    /// deliverable is the transcript. It says nothing about a modified file left uncommitted:
    /// that is stranded work regardless of task type, so the exemption does not extend to it.
    /// </summary>
    [Fact]
    public async Task A_research_tasks_uncommitted_file_still_fails_before_the_gates()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        await InitGitWorktreeAsync(withTaskCommit: false, cts.Token, trackedFile: "notes.md");
        await File.WriteAllTextAsync(Path.Combine(_worktree, "notes.md"), "left behind", cts.Token);
        (Guid taskId, Guid runId) = await SeedAsync(store,
            [new VerifyCommand("truth", "true")], cts.Token, TaskType.Research);

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, cts.Token);

        passed.Should().BeFalse("a research task can still strand a modified file, exempt or not");
        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.FailureReason.Should().Contain("modified-but-uncommitted");
        run.FailureReason.Should().Contain("notes.md");
    }

    /// <summary>
    /// The generation fence (backlog 39): a requeue-and-reclaim moved the task on to
    /// generation 2 while this run — still generation 1 — was mid-gate. The run's own
    /// failure is still recorded honestly, but it must not fail the task the live
    /// generation is working, nor take that generation's lease with it.
    /// </summary>
    [Fact]
    public async Task A_stale_generations_gate_failure_does_not_touch_the_live_generations_task_or_lease()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId) = await SeedAsync(store,
            [new VerifyCommand("boom", "echo exploding; exit 3")], cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            Guid ownerId = task.AssignedOwnerId!.Value;
            Guid nodeId = DomainId.New();
            var requeued = TaskDecider.Requeue(task, RequeueReason.LeaseExpired, Now);
            task.Apply(requeued);
            var reclaimed = TaskDecider.Claim(task, nodeId, ownerId, DomainId.New(), Now);
            session.Events.Append(taskId, requeued, reclaimed);
            session.Store(new TaskLease { Id = taskId, NodeId = nodeId, LeaseGeneration = 2, HeartbeatAt = Now });
            await session.SaveChangesAsync(cts.Token);
        }

        ListLogger<VerificationRunner> logger = new();
        VerificationRunner runner = new(store, Options.Create(new DaemonOptions()), logger);
        await runner.VerifyAsync(runId, taskId, cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Value.Should().Be("Failed", "the run's own failure is an honest fact regardless of generation");

        TaskListItem task2 = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task2.State.Value.Should().Be("Claimed", "the live generation's claim survives the stale run's gate failure");
        task2.LeaseGeneration.Should().Be(2);
        (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().NotBeNull(
            "the stale run's failure must not release the live generation's lease");

        logger.Lines.Should().Contain(line =>
            line.Contains("run at generation 1") && line.Contains("at generation 2 - rejected"));
    }

    /// <summary>
    /// Turns the seeded worktree into a real repo: base branch `main`, task branch
    /// checked out — with or without a commit of its own past the base. <paramref name="trackedFile"/>,
    /// when given, is committed on the base branch so a test can later overwrite it to produce
    /// a tracked, modified-but-uncommitted file — the shape the real origin incidents were
    /// (PLAN.md §16 #90), as opposed to a brand-new untracked one.
    /// </summary>
    private async Task InitGitWorktreeAsync(
        bool withTaskCommit, CancellationToken cancellationToken, string? trackedFile = null)
    {
        Directory.CreateDirectory(_worktree);
        string seedTrackedFile = trackedFile is null
            ? string.Empty
            : $"echo original > {trackedFile} && git add {trackedFile} && ";
        string script =
            "git init -q -b main && " +
            seedTrackedFile +
            "git -c user.email=t@t -c user.name=t commit --allow-empty -m init -q && " +
            "git checkout -q -b task/verify" +
            (withTaskCommit ? " && git -c user.email=t@t -c user.name=t commit --allow-empty -m work -q" : "");

        using System.Diagnostics.Process process = new();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/sh",
            WorkingDirectory = _worktree,
            UseShellExecute = false,
        };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(script);
        process.Start();
        await process.WaitForExitAsync(cancellationToken);
        process.ExitCode.Should().Be(0, "the test repo must seed cleanly");
    }

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    private static VerificationRunner NewRunner(DocumentStore store) =>
        new(store, Options.Create(new DaemonOptions()), NullLogger<VerificationRunner>.Instance);

    private async Task<(Guid TaskId, Guid RunId)> SeedAsync(
        DocumentStore store, IReadOnlyList<VerifyCommand> gates, CancellationToken cancellationToken,
        TaskType? taskType = null)
    {
        Directory.CreateDirectory(_worktree);
        Guid ownerId = DomainId.New();
        Guid connectionId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();

        await using IDocumentSession session = store.LightweightSession();

        ProjectAggregate project = new();
        ProjectRegistered registered = ProjectDecider.Register(
            projectId, ownerId, connectionId, $"verify-{projectId:N}", _worktree, null, "main", Now);
        project.Apply(registered);
        session.Events.StartStream<ProjectAggregate>(projectId, registered, ProjectDecider.ChangeSettings(
            project,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.Of(gates),
            skipPermissions: Optional<bool>.None,
            maxParallelAgents: Optional<int>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            Now, ownerId));

        TaskAggregate task = new();
        (task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(taskId, projectId, "Verify me", ["gates run"], taskType ?? TaskType.Chore,
                null, null, null, Now, ownerId),
            ownerId, Now);
        var claimed = TaskDecider.Claim(task, DomainId.New(), ownerId, runId, Now);
        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
        session.Store(new TaskLease { Id = taskId, NodeId = claimed.NodeId, LeaseGeneration = 1, HeartbeatAt = Now });

        session.Events.StartStream<RunAggregate>(runId,
            new RunDispatched(runId, taskId, claimed.NodeId, ownerId, 1, DomainId.New(),
                _worktree, "task/verify", ExecutorMode.Subscription, Now),
            new AgentSessionCompleted(runId, Now));
        await session.SaveChangesAsync(cancellationToken);

        return (taskId, runId);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", null);
        foreach (string dir in new[] { _home, _worktree })
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
