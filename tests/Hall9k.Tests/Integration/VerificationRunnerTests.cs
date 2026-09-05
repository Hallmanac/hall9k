using Hall9k.Domain.Infrastructure.Storage;
using FluentAssertions;
using Hall9k.Connectors.Worktrees;
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

        await NewRunner(store).VerifyAsync(runId, taskId, scopeSinceSha: null, "test", cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Value.Should().Be("Verifying", "VerificationPassed does not transition; the PR step does");
        run.FailedGates.Should().BeEmpty();

        File.ReadAllText(Path.Combine(RunPaths.GlobalDirectory(runId), "verify-hello.log"))
            .Should().Contain("hello-from-gate");

        var events = await query.Events.FetchStreamAsync(runId, token: cts.Token);
        VerificationPassed passed = events.Select(e => e.Data).OfType<VerificationPassed>().Single();
        passed.GateDurations.Should().NotBeNull("task: gate wall-clock duration is recorded and surfaced")
            .And.HaveCount(2, "both configured gates ran and each carries its own duration");
        passed.GateDurations!.Select(gate => gate.Gate).Should().Equal("hello", "truth");
        passed.GateDurations!.Should().AllSatisfy(gate =>
        {
            gate.Passed.Should().BeTrue();
            gate.Duration.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        });

        RunListItem listItem = (await query.LoadAsync<RunListItem>(runId, cts.Token))!;
        listItem.GateDurations.Should().NotBeNull().And.HaveCount(2, "h9k task show reads the lean row's own copy");
    }

    [Fact]
    public async Task Failing_gate_fails_run_and_task_and_names_the_gate()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId) = await SeedAsync(store,
            [new VerifyCommand("ok", "true"), new VerifyCommand("boom", "echo exploding; exit 3"), new VerifyCommand("never", "true")], cts.Token);

        await NewRunner(store).VerifyAsync(runId, taskId, scopeSinceSha: null, "test", cts.Token);

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

        var events = await query.Events.FetchStreamAsync(runId, token: cts.Token);
        VerificationFailed failed = events.Select(e => e.Data).OfType<VerificationFailed>().Single();
        failed.GateDurations.Should().NotBeNull()
            .And.HaveCount(2, "the passing gate before the failure and the failed gate itself, never the gate after it");
        failed.GateDurations!.Select(gate => gate.Gate).Should().Equal("ok", "boom");
        failed.GateDurations![0].Passed.Should().BeTrue();
        failed.GateDurations![1].Passed.Should().BeFalse("boom is the gate that stopped the line");
    }

    /// <summary>
    /// The Windows field report's own origin incident (item 11b): a gate that was never going to
    /// pass — here, unconditionally — fails every run the same way a real regression would, and a
    /// human reading only "gate failure (test)" has to rediscover by hand that the gate itself,
    /// not the agent's work, is what is broken. The comparison checkout is a real, separate,
    /// genuinely clean git repository on <c>main</c> (not <c>_worktree</c>, which the run's own
    /// no-commit pre-gate check needs to stay a plain non-git directory), so the headline's own
    /// claim of "a clean checkout" is actually true here.
    /// </summary>
    [Fact]
    public async Task A_gate_that_also_fails_on_a_clean_checkout_of_the_base_branch_says_so()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        string cleanBase = Path.Combine(Path.GetTempPath(), $"hall9k-vt-base-{Guid.NewGuid():N}");
        await InitializeCleanCheckoutAsync(cleanBase, "main", cts.Token);
        try
        {
            (Guid taskId, Guid runId) = await SeedAsync(
                store, [new VerifyCommand("broken", "echo unconditionally-broken; exit 1")], cts.Token,
                repositoryPath: cleanBase);

            await NewRunner(store).VerifyAsync(runId, taskId, scopeSinceSha: null, "test", cts.Token);

            await using IQuerySession query = store.QuerySession();
            RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
            run.State.Value.Should().Be("Failed");
            run.FailureReason.Should().Contain("unconditionally-broken")
                .And.Contain("also fails when run against a clean checkout of 'main'",
                    "the report must distinguish a gate that was never going to pass from a bare gate failure");
        }
        finally
        {
            Directory.Delete(cleanBase, recursive: true);
        }
    }

    /// <summary>
    /// The conformance finding this fix addresses: a comparison checkout that could not be
    /// confirmed clean must never be asserted as "a clean checkout" in the same headline whose own
    /// parenthetical then takes it back. The comparison checkout here is a real git repository, but
    /// on a different branch entirely, so the headline itself must say so plainly instead of
    /// calling it clean and contradicting itself mid-sentence.
    /// </summary>
    [Fact]
    public async Task A_gate_that_fails_on_a_checkout_not_confirmed_clean_does_not_call_it_clean()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        string uncleanBase = Path.Combine(Path.GetTempPath(), $"hall9k-vt-unclean-{Guid.NewGuid():N}");
        await InitializeCleanCheckoutAsync(uncleanBase, "feature-x", cts.Token);
        try
        {
            (Guid taskId, Guid runId) = await SeedAsync(
                store, [new VerifyCommand("broken", "echo unconditionally-broken; exit 1")], cts.Token,
                repositoryPath: uncleanBase);

            await NewRunner(store).VerifyAsync(runId, taskId, scopeSinceSha: null, "test", cts.Token);

            await using IQuerySession query = store.QuerySession();
            RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
            run.State.Value.Should().Be("Failed");
            run.FailureReason.Should().Contain("unconditionally-broken")
                .And.Contain($"'{uncleanBase}', not confirmed clean")
                .And.Contain("is on 'feature-x', not 'main'")
                .And.NotContain("a clean checkout of 'main'",
                    "the checkout was never confirmed clean and on main, so the headline must not assert it was");
        }
        finally
        {
            Directory.Delete(uncleanBase, recursive: true);
        }
    }

    /// <summary>
    /// The other half of the same distinction: a gate that fails only because of what THIS run's
    /// own branch did must not claim it also fails on clean base — that would be exactly the
    /// misleading signal item 11b's origin incident already produced, just pointed the wrong way.
    /// The base checkout here is a real, separate directory that never sees the marker file the
    /// run's own worktree carries, so the same gate command genuinely passes there.
    /// </summary>
    [Fact]
    public async Task A_gate_that_fails_only_on_this_runs_own_branch_does_not_claim_it_also_fails_on_clean_base()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        string cleanBase = Path.Combine(Path.GetTempPath(), $"hall9k-vt-base-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cleanBase);
        try
        {
            Directory.CreateDirectory(_worktree);
            File.WriteAllText(Path.Combine(_worktree, "bug-marker"), "this run's own branch introduced a bug\n");
            (Guid taskId, Guid runId) = await SeedAsync(
                store,
                [new VerifyCommand("regressed", "test -f bug-marker && exit 1 || exit 0")],
                cts.Token,
                repositoryPath: cleanBase);

            await NewRunner(store).VerifyAsync(runId, taskId, scopeSinceSha: null, "test", cts.Token);

            await using IQuerySession query = store.QuerySession();
            RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
            run.State.Value.Should().Be("Failed");
            run.FailureReason.Should().NotContain(
                "also fails when run against a clean checkout",
                "this gate passes on a clean checkout of the base branch — only this run's own branch broke it");
        }
        finally
        {
            Directory.Delete(cleanBase, recursive: true);
        }
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

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, scopeSinceSha: null, "test", cts.Token);

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

        VerificationPassed recordedPass = events.Select(e => e.Data).OfType<VerificationPassed>().Single();
        recordedPass.GateDurations.Should().NotBeNull().And.ContainSingle(
            "a retried gate that eventually passes is one entry, its own two attempts summed, not two");
        recordedPass.GateDurations![0].Gate.Should().Be("flaky");
        recordedPass.GateDurations![0].Passed.Should().BeTrue();
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

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, scopeSinceSha: null, "test", cts.Token);

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

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, scopeSinceSha: null, "test", cts.Token);

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

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, scopeSinceSha: null, "test", cts.Token);

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

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, scopeSinceSha: null, "test", cts.Token);

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

        await NewRunner(store).VerifyAsync(runId, taskId, scopeSinceSha: null, "test", cts.Token);

        await using IQuerySession query = store.QuerySession();
        var events = await query.Events.FetchStreamAsync(runId, token: cts.Token);
        VerificationPassed passed = events.Select(e => e.Data).OfType<VerificationPassed>().Single();
        passed.Note.Should().Contain("No verification gates configured");
        passed.GateDurations.Should().NotBeNull().And.BeEmpty(
            "zero gates genuinely ran — an observed empty list, not the unknown a missing field would mean");
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
            NullLogger<VerificationRunner>.Instance,
            NewWorktreeManager());

        await runner.VerifyAsync(runId, taskId, scopeSinceSha: null, "test", cts.Token);

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
            NullLogger<VerificationRunner>.Instance,
            NewWorktreeManager());

        bool passed = await runner.VerifyAsync(runId, taskId, scopeSinceSha: null, "test", cts.Token);

        passed.Should().BeTrue("the second attempt exits clean once the hang is behind it");
        await using IQuerySession query = store.QuerySession();
        var events = await query.Events.FetchStreamAsync(runId, token: cts.Token);
        GateRetried retried = events.Select(e => e.Data).OfType<GateRetried>().Single();
        retried.Cause.Should().Contain("timeout");
        events.Select(e => e.Data).OfType<RunFailed>().Should().BeEmpty("a passing retry never fails the run");
    }

    /// <summary>
    /// The gate's own permit wait (CrossProcessContainerGate.AcquireAsync, PLAN.md §16 #132) is
    /// deliberately unbounded, so ordinary cross-process contention under a raised node ceiling or
    /// a concurrent foreground run can legitimately outlast VerifyGateTimeout. A gate still queued
    /// on it when it is killed must classify as infrastructure and retry rather than being blamed
    /// on the agent's own work (conformance/adversarial review, cycle 1) — and it has to be told
    /// apart from an ordinary hang without ever trusting the gate's own captured console output:
    /// a real dotnet-test-shaped gate's wait line is written from inside its testhost, which
    /// VerificationRunner's own process.Kill(entireProcessTree: true) tears down together with
    /// vstest.console before vstest.console ever gets a chance to relay anything it captured, so
    /// the line never reaches the redirected log (adversarial review, this cycle, reproduced
    /// against this repo's own package versions). What actually reaches VerificationRunner is the
    /// wait-evidence file CrossProcessContainerGate.AcquireAsync writes directly to the directory
    /// named by HALL9K_VERIFY_GATE_WAIT_DIR — a real file write, not anything that depends on a
    /// parent process surviving long enough to report it — so this fake gate writes there itself
    /// rather than echoing a marker into its own redirected stdout, the same channel the real
    /// wait loop actually uses.
    /// </summary>
    [Fact]
    public async Task A_gate_still_queued_on_the_container_gate_when_killed_classifies_and_retries()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        string marker = Path.Combine(_worktree, "retry-marker");
        (Guid taskId, Guid runId) = await SeedAsync(store,
            [new VerifyCommand("flaky",
                $"if test -f {marker}; then echo ok; exit 0; " +
                $"else touch {marker}; " +
                "echo 'Waiting on cross-process container gate /tmp/hall9k-postgres-container-gate " +
                "(3s elapsed, 4 max concurrent)' > " +
                $"\"${GateInfrastructureFailureClassifier.GateWaitEvidenceDirectoryEnvironmentVariable}/waiting.txt\"; " +
                "sleep 30; fi")],
            cts.Token);

        VerificationRunner runner = new(
            store,
            Options.Create(new DaemonOptions { VerifyGateTimeout = TimeSpan.FromSeconds(1) }),
            NullLogger<VerificationRunner>.Instance,
            NewWorktreeManager());

        bool passed = await runner.VerifyAsync(runId, taskId, scopeSinceSha: null, "test", cts.Token);

        passed.Should().BeTrue("the second attempt exits clean once the queue wait is behind it");
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

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, scopeSinceSha: null, "test", cts.Token);

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

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, scopeSinceSha: null, "test", cts.Token);

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

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, scopeSinceSha: null, "test", cts.Token);

        passed.Should().BeFalse();
        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Value.Should().Be("Failed");
        run.FailureReason.Should().Contain("produced no commits");
        run.FailureReason.Should().Contain("stranded.txt", "the stranded file must be named, not just implied");
        run.FailedGates.Should().BeEmpty("no gate ever ran");
    }

    /// <summary>
    /// The failure message names untracked new files alongside modified ones (origin incident,
    /// 2026-08-29, the Jira compose/execute task): a session left a whole feature's own core
    /// files uncommitted, but the failure named only the modified files — a resuming agent that
    /// faithfully committed just the named list would have shipped a hollow branch missing the
    /// untracked source files entirely. A brand-new file under src/ is exactly that shape.
    /// </summary>
    [Fact]
    public async Task An_untracked_source_file_fails_the_run_alongside_a_modified_one()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        await InitGitWorktreeAsync(withTaskCommit: true, cts.Token, trackedFile: "half-done.cs");
        await File.WriteAllTextAsync(Path.Combine(_worktree, "half-done.cs"), "left behind", cts.Token);
        Directory.CreateDirectory(Path.Combine(_worktree, "src", "Hall9k.Connectors"));
        await File.WriteAllTextAsync(
            Path.Combine(_worktree, "src", "Hall9k.Connectors", "NewFeature.cs"), "brand new", cts.Token);
        (Guid taskId, Guid runId) = await SeedAsync(store,
            [new VerifyCommand("never", "echo should-not-run")], cts.Token);

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, scopeSinceSha: null, "test", cts.Token);

        passed.Should().BeFalse("an untracked new source file is stranded work, not a gate byproduct");
        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Value.Should().Be("Failed");
        run.FailureReason.Should().Contain("half-done.cs", "the modified file must still be named");
        run.FailureReason.Should().Contain("NewFeature.cs", "the untracked new source file must be named too");
        run.FailedGates.Should().BeEmpty("no gate ever ran");
    }

    /// <summary>
    /// A brand-new file under tests/ is the same shape as one under src/: it is the feature's
    /// own work, not a gate byproduct, so it fails the run and is named even with no modified
    /// file alongside it.
    /// </summary>
    [Fact]
    public async Task An_untracked_test_file_alone_fails_the_run()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        await InitGitWorktreeAsync(withTaskCommit: true, cts.Token);
        Directory.CreateDirectory(Path.Combine(_worktree, "tests", "Hall9k.Tests"));
        await File.WriteAllTextAsync(
            Path.Combine(_worktree, "tests", "Hall9k.Tests", "NewFeatureTests.cs"), "brand new test", cts.Token);
        (Guid taskId, Guid runId) = await SeedAsync(store,
            [new VerifyCommand("never", "echo should-not-run")], cts.Token);

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, scopeSinceSha: null, "test", cts.Token);

        passed.Should().BeFalse("an untracked new test file is stranded work, not a gate byproduct");
        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Value.Should().Be("Failed");
        run.FailureReason.Should().Contain("NewFeatureTests.cs");
        run.FailedGates.Should().BeEmpty("no gate ever ran");
    }

    /// <summary>
    /// `TestResults/` is VSTest's own default results directory (`dotnet test --logger trx`,
    /// `--collect:"XPlat Code Coverage"`), and it commonly lands inside a test project's own
    /// directory under tests/ — exactly the tree the check above treats as first-class strandable
    /// work. Without a byproduct exclusion that reaches inside src/ and tests/ too (not just
    /// `.gitignore`, which this repo's own does not name `TestResults/`), a fully committed
    /// session's own gate output would fail the run, a defect no retry could ever clear since the
    /// next session's gates regenerate the same file (independent pre-PR review cycle 1).
    /// </summary>
    [Fact]
    public async Task An_untracked_TestResults_directory_under_tests_does_not_fail_the_run()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        await InitGitWorktreeAsync(withTaskCommit: true, cts.Token);
        Directory.CreateDirectory(Path.Combine(_worktree, "tests", "Hall9k.Tests", "TestResults"));
        await File.WriteAllTextAsync(
            Path.Combine(_worktree, "tests", "Hall9k.Tests", "TestResults", "host.trx"), "gate byproduct", cts.Token);
        (Guid taskId, Guid runId) = await SeedAsync(store, [new VerifyCommand("truth", "true")], cts.Token);

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, scopeSinceSha: null, "test", cts.Token);

        passed.Should().BeTrue("TestResults/ under tests/ is a gate byproduct, not stranded agent work");
        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.State.Value.Should().Be("Verifying");
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

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, scopeSinceSha: null, "test", cts.Token);

        passed.Should().BeFalse("finished work left uncommitted never reaches the pull request");
        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Value.Should().Be("Failed");
        run.FailureReason.Should().Contain("uncommitted files");
        run.FailureReason.Should().Contain("half-done.cs");
        run.FailureReason.Should().NotContain("produced no commits", "this branch has commits; only files are stranded");
        run.FailedGates.Should().BeEmpty("no gate ever ran");

        (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Value.Should().Be("Failed");
        File.Exists(Path.Combine(RunPaths.GlobalDirectory(runId), "verify-never.log"))
            .Should().BeFalse("the failure lands before the gates, not after");
    }

    /// <summary>
    /// Outside src/ and tests/, an untracked file is not stranded agent work — it is as likely to
    /// be a gate's own byproduct (a coverage report, a lint cache) that the project's
    /// `.gitignore` does not yet name, and failing a run on it would be a defect a retry can
    /// never clear, since the next session's gates regenerate the same file (independent pre-PR
    /// review, adversarial finding). The uncommitted-files check separates untracked entries out
    /// of `git status --porcelain -z` for exactly this reason, and only warns about them
    /// (independent pre-PR review, cycle 2 conformance finding) rather than failing the run. This
    /// fixture plants the file at the repo root rather than under a test project's own directory,
    /// deliberately: an untracked file under src/ or tests/ instead fails the run (see
    /// <see cref="An_untracked_source_file_fails_the_run_alongside_a_modified_one"/> and
    /// <see cref="An_untracked_test_file_alone_fails_the_run"/>), except for a well-known .NET
    /// build/test output directory such as `TestResults/` even there.
    /// </summary>
    [Fact]
    public async Task An_untracked_file_left_in_the_worktree_does_not_fail_the_run()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        await InitGitWorktreeAsync(withTaskCommit: true, cts.Token);
        await File.WriteAllTextAsync(Path.Combine(_worktree, "TestResults.trx"), "gate byproduct", cts.Token);
        (Guid taskId, Guid runId) = await SeedAsync(store, [new VerifyCommand("truth", "true")], cts.Token);

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, scopeSinceSha: null, "test", cts.Token);

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

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, scopeSinceSha: null, "test", cts.Token);

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

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, scopeSinceSha: null, "test", cts.Token);

        passed.Should().BeFalse("a research task can still strand a modified file, exempt or not");
        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.FailureReason.Should().Contain("uncommitted files");
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
        VerificationRunner runner = new(store, Options.Create(new DaemonOptions()), logger, NewWorktreeManager());
        await runner.VerifyAsync(runId, taskId, scopeSinceSha: null, "test", cts.Token);

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
    /// A gate whose own command is `dotnet test`-shaped, run with no scope sha (task: a fix
    /// cycle's verification gate) — the run's own artifacts say full and why, unscoped.
    /// </summary>
    [Fact]
    public async Task An_unscoped_test_gate_records_full_with_the_reason_in_the_log_and_the_pass_note()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId) = await SeedAsync(store, [new VerifyCommand("test", "dotnet test --help")], cts.Token);

        bool passed = await NewRunner(store).VerifyAsync(
            runId, taskId, scopeSinceSha: null, "mandatory final full pass: nothing merges on scoped green alone", cts.Token);

        passed.Should().BeTrue();
        string log = File.ReadAllText(Path.Combine(RunPaths.GlobalDirectory(runId), "verify-test.log"));
        log.Should().Contain("hall9k test gate: full").And.Contain("mandatory final full pass");

        await using IQuerySession query = store.QuerySession();
        var events = await query.Events.FetchStreamAsync(runId, token: cts.Token);
        VerificationPassed recorded = events.Select(e => e.Data).OfType<VerificationPassed>().Single();
        recorded.Note.Should().Contain("Test gate ran full").And.Contain("mandatory final full pass");
    }

    /// <summary>
    /// A fix cycle's own reverify (task: a fix cycle's verification gate): a real commit since
    /// the reviewed cycle's head, touching a source type one test class references, narrows the
    /// `dotnet test`-shaped gate's own command with an injected `--filter` — recorded on both the
    /// gate's log and the pass note, exactly as an unscoped full pass records its own reason.
    /// </summary>
    [Fact]
    public async Task A_scoped_test_gate_injects_the_filter_and_records_it_in_the_log_and_the_pass_note()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        string sinceSha = await InitScopableRepoAsync(cts.Token);
        await CommitAsync(
            "src/Hall9k.Domain/Widget.cs", "public sealed class Widget\n{\n    public int Count;\n}\n", "fix widget", cts.Token);
        (Guid taskId, Guid runId) = await SeedAsync(store,
            [new VerifyCommand(
                "test",
                "dotnet test --help; echo 'Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3, Duration: 1 s'")],
            cts.Token);

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, sinceSha, "cycle 2 fix (Discovery)", cts.Token);

        passed.Should().BeTrue();
        string log = File.ReadAllText(Path.Combine(RunPaths.GlobalDirectory(runId), "verify-test.log"));
        log.Should().Contain("hall9k test gate: scoped")
            .And.Contain("filter: FullyQualifiedName~WidgetTests")
            .And.Contain("cycle 2 fix (Discovery)");

        await using IQuerySession query = store.QuerySession();
        var events = await query.Events.FetchStreamAsync(runId, token: cts.Token);
        VerificationPassed recorded = events.Select(e => e.Data).OfType<VerificationPassed>().Single();
        recorded.Note.Should().Contain("Test gate scoped").And.Contain("WidgetTests");
    }

    /// <summary>
    /// A scoped filter can intersect with a gate's own already-configured filter to nothing even
    /// though <see cref="TestScopeResolver"/> mapped a real class — VSTest's own default exits 0
    /// on "no test matches the given testcase filter" (verified against this repo's real VSTest
    /// console; see <see cref="VerificationRunnerScopedGateTests"/> for the marker string itself),
    /// which would otherwise stand an empty run in for a passed one (independent pre-PR review,
    /// cycle 1). The gate command below stands in for that shape without spawning a real `dotnet
    /// test`: it always exits 0 and always echoes the exact marker regardless of which filter (if
    /// any) got appended, so the first, scoped attempt looks exactly like an empty-intersection
    /// run, and the fallback's own full run (no filter appended) is what the run actually settles
    /// on and records.
    /// </summary>
    [Fact]
    public async Task A_scoped_filter_that_matches_no_tests_falls_back_to_a_full_run_and_records_it_honestly()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        string sinceSha = await InitScopableRepoAsync(cts.Token);
        await CommitAsync(
            "src/Hall9k.Domain/Widget.cs", "public sealed class Widget\n{\n    public int Count;\n}\n", "fix widget", cts.Token);
        (Guid taskId, Guid runId) = await SeedAsync(store,
            [new VerifyCommand("test", "dotnet test --help; echo 'No test matches the given testcase filter'")], cts.Token);

        ListLogger<VerificationRunner> logger = new();
        VerificationRunner runner = new(store, Options.Create(new DaemonOptions()), logger, NewWorktreeManager());
        bool passed = await runner.VerifyAsync(runId, taskId, sinceSha, "cycle 2 fix (Discovery)", cts.Token);

        passed.Should().BeTrue("the fallback's own full run genuinely passed");
        string log = File.ReadAllText(Path.Combine(RunPaths.GlobalDirectory(runId), "verify-test.log"));
        log.Should().Contain("hall9k test gate: full").And.Contain("the scoped filter matched no tests");

        await using IQuerySession query = store.QuerySession();
        var events = await query.Events.FetchStreamAsync(runId, token: cts.Token);
        VerificationPassed recorded = events.Select(e => e.Data).OfType<VerificationPassed>().Single();
        recorded.Note.Should().Contain("Test gate ran full").And.Contain("no executed tests were recorded");

        logger.Lines.Should().Contain(line =>
            line.Contains("matched no tests") && line.Contains("falling back to a full run"));
    }

    /// <summary>
    /// A project can configure more than one `dotnet test`-shaped gate (task: a fix cycle's
    /// verification gate, independent pre-PR review cycle 4): when one of them falls back to a
    /// full run because its own filter intersects to nothing while a sibling gate runs genuinely
    /// scoped, the pass as a whole must not be recorded as full-scope over this HEAD — a sibling
    /// gate that never ran unscoped is exactly the gap the mandatory pre-Settling full gate exists
    /// to close, so <see cref="VerificationPassed.RanFullScope"/> must stay false and the note must
    /// say the pass was mixed rather than either "scoped" or "full".
    /// </summary>
    [Fact]
    public async Task A_mixed_scope_pass_across_multiple_test_gates_is_never_recorded_as_fully_scoped()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        string sinceSha = await InitScopableRepoAsync(cts.Token);
        await CommitAsync(
            "src/Hall9k.Domain/Widget.cs", "public sealed class Widget\n{\n    public int Count;\n}\n", "fix widget", cts.Token);
        (Guid taskId, Guid runId) = await SeedAsync(store,
            [
                new VerifyCommand("unit", "dotnet test --help; echo 'No test matches the given testcase filter'"),
                new VerifyCommand(
                    "integration",
                    "dotnet test --help; echo 'Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3, Duration: 1 s'"),
            ],
            cts.Token);

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, sinceSha, "cycle 2 fix (Discovery)", cts.Token);

        passed.Should().BeTrue();
        File.ReadAllText(Path.Combine(RunPaths.GlobalDirectory(runId), "verify-unit.log"))
            .Should().Contain("hall9k test gate: full").And.Contain("the scoped filter matched no tests");
        File.ReadAllText(Path.Combine(RunPaths.GlobalDirectory(runId), "verify-integration.log"))
            .Should().Contain("hall9k test gate: scoped");

        await using IQuerySession query = store.QuerySession();
        var events = await query.Events.FetchStreamAsync(runId, token: cts.Token);
        VerificationPassed recorded = events.Select(e => e.Data).OfType<VerificationPassed>().Single();
        recorded.RanFullScope.Should().BeFalse(
            "the integration gate ran genuinely scoped and was never covered at full scope over this HEAD");
        recorded.Note.Should().Contain("1 of 2 test gate(s)").And.Contain("the rest ran scoped");

        // GateDuration.RanFullScope is this one gate's own scope, not the whole pass's
        // pass-level flag above: the "unit" gate fell back to a full run of itself, while
        // "integration" ran genuinely narrowed, and the two must not be tagged alike (task: gate
        // wall-clock duration is recorded and surfaced).
        recorded.GateDurations.Should().NotBeNull();
        recorded.GateDurations!.Single(gate => gate.Gate == "unit").RanFullScope.Should().BeTrue(
            "it fell back to a full run of itself after its own filter intersected to nothing");
        recorded.GateDurations!.Single(gate => gate.Gate == "integration").RanFullScope.Should().BeFalse(
            "it ran genuinely narrowed by the scoped filter");
    }

    /// <summary>
    /// Windows field report item 3 (ruled 2026-09-01): a gate spawned on Windows carries
    /// MSBUILDDISABLENODEREUSE=1 in its own process environment, set by the platform at the
    /// spawn rather than asked of each project's own verify command. Every other platform's
    /// spawn is unchanged — the variable is never set at all there. `echo %VAR%` on cmd.exe
    /// prints the literal token back when unset, and `echo $VAR` on sh prints nothing, so the
    /// expected content genuinely differs by platform rather than this test special-casing one.
    /// <para>
    /// The ambient value on the machine running this test is cleared for the duration and
    /// restored after, on both branches: <see cref="ProcessStartInfo.Environment"/> starts as a
    /// copy of this test host's own environment, so a machine that already exports
    /// MSBUILDDISABLENODEREUSE globally (a cross-platform variable some teams do set, to stop
    /// stray MSBuild node processes) would otherwise decide either branch's assertion by ambient
    /// state rather than by what the gate spawn under test actually did (conformance review,
    /// cycle 1).
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_gate_spawned_on_windows_carries_MSBUILDDISABLENODEREUSE_and_other_platforms_do_not()
    {
        const string VariableName = "MSBUILDDISABLENODEREUSE";
        string? previousValue = Environment.GetEnvironmentVariable(VariableName);
        Environment.SetEnvironmentVariable(VariableName, null);
        try
        {
            using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
            using DocumentStore store = NewStore();
            (Guid taskId, Guid runId) = await SeedAsync(
                store, [new VerifyCommand("envcheck", PrintEnvironmentVariableCommand(VariableName))], cts.Token);

            bool passed = await NewRunner(store).VerifyAsync(runId, taskId, scopeSinceSha: null, "test", cts.Token);

            passed.Should().BeTrue();
            string log = File.ReadAllText(Path.Combine(RunPaths.GlobalDirectory(runId), "verify-envcheck.log")).Trim();
            if (OperatingSystem.IsWindows())
            {
                log.Should().Be("1", "the platform sets MSBUILDDISABLENODEREUSE=1 on every Windows gate spawn");
            }
            else
            {
                log.Should().BeEmpty("non-Windows gate spawns are unchanged and never set this variable, so $VAR expands to nothing");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(VariableName, previousValue);
        }
    }

    /// <summary>
    /// A project whose own verify command already sets the variable is unaffected: the inline
    /// `set` runs inside the same shell session, after this process's environment was inherited,
    /// so it simply overwrites the platform's own default for that gate. Windows-only — there is
    /// nothing for a non-Windows spawn to override since it never sets the variable at all.
    /// <para>
    /// Observed through `cmd /c set VAR` (the query form, which reads the environment when it
    /// runs) rather than any `%VAR%` readback, nested or not. cmd.exe expands `%VAR%` as a
    /// textual substitution while parsing the whole raw line — the whole parenthesized block
    /// here, since <see cref="VerificationRunner"/> wraps every gate command in `(…) > log` for
    /// redirection — before any statement on it executes, `set` included. Nesting the readback
    /// inside a second `cmd /c` does not escape that: the outer parse rewrites the nested
    /// invocation's own argument text to `cmd /c echo 1` before the child is ever spawned, so
    /// the child has no `%` left to expand and the log reads the platform's `1`. `set VAR`
    /// carries no `%` at all, so nothing about it is decided at parse time, and running it in a
    /// nested `cmd /c` makes it the same observation the production case rests on: a child
    /// process spawned after the `set`, inheriting the environment at launch — OS-level
    /// inheritance, which is exactly how `dotnet test` on that line would see the override too.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_verify_command_that_sets_the_variable_itself_still_wins_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId) = await SeedAsync(
            store, [new VerifyCommand("envcheck", "set MSBUILDDISABLENODEREUSE=0 && cmd /c set MSBUILDDISABLENODEREUSE")], cts.Token);

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, scopeSinceSha: null, "test", cts.Token);

        passed.Should().BeTrue();
        // `set NAME=VALUE` keeps everything up to the line separator, so the assigned value here
        // is "0 " (the space before `&&`) and the query form echoes it back the same way: the
        // assertion matches the assignment rather than the whole line for that reason.
        File.ReadAllText(Path.Combine(RunPaths.GlobalDirectory(runId), "verify-envcheck.log"))
            .Should().Contain("MSBUILDDISABLENODEREUSE=0",
                "the project's own inline assignment overrides the platform default");
    }

    private static string PrintEnvironmentVariableCommand(string variableName) =>
        OperatingSystem.IsWindows() ? $"echo %{variableName}%" : $"echo ${variableName}";

    /// <summary>
    /// Seeds the worktree as a real repo shaped like this one — `main`, a task branch ahead of it
    /// (the same shape <see cref="InitGitWorktreeAsync"/> gives the uncommitted-files tests,
    /// needed here too: <see cref="VerificationRunner"/>'s own no-commit check would otherwise
    /// fail the run before the gates, since <c>main..HEAD</c> is empty on <c>main</c> itself) —
    /// with a source type and the one test class that references it already committed on the
    /// task branch, and returns that commit's sha: the boundary before any fix commit,
    /// <see cref="TestScopeResolver"/>'s own diff target for a fix's own commits.
    /// </summary>
    private async Task<string> InitScopableRepoAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_worktree);
        await RunShellAsync(_worktree, "git init -q -b main", cancellationToken);
        await RunShellAsync(
            _worktree, "git -c user.email=t@t -c user.name=t commit --allow-empty -m init -q", cancellationToken);
        await RunShellAsync(_worktree, "git checkout -q -b task/verify", cancellationToken);
        await CommitAsync("src/Hall9k.Domain/Widget.cs", "public sealed class Widget\n{\n}\n", "add widget", cancellationToken);
        await CommitAsync(
            "tests/Hall9k.Tests/WidgetTests.cs",
            "public sealed class WidgetTests\n{\n    private readonly Widget _widget = new();\n}\n",
            "add widget tests", cancellationToken);
        return (await RunShellCapturingAsync(_worktree, "git rev-parse HEAD", cancellationToken)).Trim();
    }

    /// <summary>
    /// A minimal, genuinely clean git repository on <paramref name="branch"/> — nothing modified,
    /// nothing untracked — for a test that needs the clean-base comparison's own
    /// <see cref="CheckoutCleanliness.DescribeNotConfirmedCleanAsync"/> check to actually confirm
    /// the checkout it just spawned a gate against, rather than the plain non-git directory most
    /// tests in this file use (which git can never confirm anything about at all).
    /// </summary>
    private static async Task InitializeCleanCheckoutAsync(string directory, string branch, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        await RunShellAsync(
            directory,
            $"git init -q -b {branch} && git -c user.email=t@t -c user.name=t commit --allow-empty -m init -q",
            cancellationToken);
    }

    private async Task CommitAsync(string relativePath, string content, string message, CancellationToken cancellationToken)
    {
        string fullPath = Path.Combine(_worktree, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content, cancellationToken);
        await RunShellAsync(_worktree, "git add -A", cancellationToken);
        await RunShellAsync(
            _worktree, $"git -c user.email=t@t -c user.name=t commit -q -m \"{message}\"", cancellationToken);
    }

    private static async Task RunShellAsync(string workingDirectory, string script, CancellationToken cancellationToken)
    {
        using System.Diagnostics.Process process = new();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/sh",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(script);
        process.Start();
        await process.WaitForExitAsync(cancellationToken);
        process.ExitCode.Should().Be(0, $"'{script}' must succeed for the test repo to be usable");
    }

    private static async Task<string> RunShellCapturingAsync(
        string workingDirectory, string script, CancellationToken cancellationToken)
    {
        using System.Diagnostics.Process process = new();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/sh",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(script);
        process.Start();
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        process.ExitCode.Should().Be(0, $"'{script}' must succeed for the test repo to be usable");
        return output;
    }

    /// <summary>
    /// Turns the seeded worktree into a real repo: base branch `main`, task branch
    /// checked out — with or without a commit of its own past the base. <paramref name="trackedFile"/>,
    /// when given, is committed on the base branch so a test can later overwrite it to produce
    /// a tracked, modified-but-uncommitted file — the shape the real origin incidents were
    /// (PLAN.md §16 #91), as opposed to a brand-new untracked one.
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
        new(store, Options.Create(new DaemonOptions()), NullLogger<VerificationRunner>.Instance, NewWorktreeManager());

    // No test in this file exercises the clean-base comparison's own worktree refresh (every
    // seeded project here has no HomeDirectory, so ProjectCheckout.IsHomeDevWorktree is always
    // false and this manager's own methods are never actually called) — a real GitWorktreeManager
    // is still what every other VerificationRunner constructor call site in the daemon and its own
    // tests uses, so a fake here would be one more thing to keep in sync with the interface.
    private static GitWorktreeManager NewWorktreeManager() => new(NullLogger<GitWorktreeManager>.Instance);

    private async Task<(Guid TaskId, Guid RunId)> SeedAsync(
        DocumentStore store, IReadOnlyList<VerifyCommand> gates, CancellationToken cancellationToken,
        TaskType? taskType = null, string? repositoryPath = null)
    {
        Directory.CreateDirectory(_worktree);
        Guid ownerId = DomainId.New();
        Guid connectionId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();

        await using IDocumentSession session = store.LightweightSession();

        ProjectAggregate project = new();
        // The clean-base comparison (task: a verify gate that cannot pass on clean main is caught
        // before it costs a run) reads the project's own RepositoryPath as its stand-in for a
        // clean checkout of the base branch — a real project keeps that separate from any given
        // run's own worktree, and a caller that wants to exercise the "fails on the branch, not on
        // clean base" distinction passes its own directory here instead of reusing _worktree.
        ProjectRegistered registered = ProjectDecider.Register(
            projectId, ownerId, connectionId, $"verify-{projectId:N}", repositoryPath ?? _worktree, null, "main", Now);
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
