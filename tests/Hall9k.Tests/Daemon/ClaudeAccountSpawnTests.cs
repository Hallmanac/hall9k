using System.Diagnostics;
using FluentAssertions;
using Hall9k.Connectors.ClaudeCode;
using Hall9k.Daemon;
using Hall9k.Daemon.ClaudeAccounts;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The daemon half of running a project's sessions under its bound Claude Code account: the environment every
/// spawn composes, the gate that refuses a spawn whose account fails its check, the launch-failure re-check that
/// tells a refused account from a node that cannot launch sessions, and the per-account gate on new work.
/// </summary>
public sealed class ClaudeAccountSpawnTests : IDisposable
{
    private const string LoggedIn = """{"loggedIn":true,"email":"dev@example.com","orgId":"org-1"}""";
    private const string LoggedOut = """{"loggedIn":false,"authMethod":"none"}""";

    private readonly string _directory = Directory.CreateTempSubdirectory("hall9k-account-spawn-").FullName;

    private readonly ClaudeCodeAccount _bound;

    public ClaudeAccountSpawnTests() =>
        _bound = new ClaudeCodeAccount(DomainId.New(), Path.Combine(_directory, ".claude-alt"), "dev@example.com", "org-1");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FakeProbe(params string[] answers) : IClaudeAuthStatusProbe
    {
        private int _index;

        public int Calls { get; private set; }

        public Task<ClaudeAuthStatusRead> ReadAsync(string configDirectory, CancellationToken cancellationToken)
        {
            Calls++;
            string answer = answers[Math.Min(_index++, answers.Length - 1)];
            return Task.FromResult(new ClaudeAuthStatusRead(ClaudeAuthStatus.Parse(answer), null));
        }
    }

    private sealed class RecordingLedger : IClaudeAccountRefusalLedger
    {
        public List<(Guid ConnectionId, bool Passed)> Records { get; } = [];

        public Task RecordAsync(Guid connectionId, ClaudeAccountCheckResult result, CancellationToken cancellationToken)
        {
            Records.Add((connectionId, result.Passed));
            return Task.CompletedTask;
        }
    }

    private static ClaudeAccountGate Gate(FakeProbe probe, RecordingLedger ledger) =>
        new(ledger, new ClaudeAccountChecker(probe, directoryExists: _ => true, windows: false));

    private AgentSpawnRequest Request(ClaudeCodeAccount account) =>
        new(
            DomainId.New(), DomainId.New(), _directory, _directory, "prompt", ExecutorMode.Subscription,
            AgentModel.Sonnet, AgentEffort.Unknown, SkipPermissions: false)
        {
            Account = account,
            SessionName = "test-build",
        };

    // ---- the composed environment ----

    [Fact]
    public void A_bound_account_sets_the_exact_recorded_directory_and_removes_nothing()
    {
        ProcessSpawnRequest composed = ClaudeExecutor.ComposeProcessRequest(
            Request(_bound), "claude", [new("HALL9K_OTHER", "kept"), new(ClaudeConfigDirectory.Variable, "/elsewhere")],
            null, "stream", "stderr");

        composed.Environment.Where(variable => variable.Key == ClaudeConfigDirectory.Variable)
            .Should().ContainSingle().Which.Value.Should().Be(_bound.ConfigDirectory);
        composed.Environment.Should().Contain(new KeyValuePair<string, string>("HALL9K_OTHER", "kept"));
        composed.RemovedEnvironment.Should().BeEmpty();
    }

    [Fact]
    public void The_default_account_removes_an_inherited_directory_and_otherwise_composes_as_before()
    {
        ProcessSpawnRequest composed = ClaudeExecutor.ComposeProcessRequest(
            Request(ClaudeCodeAccount.Default), "claude", [new("HALL9K_OTHER", "kept")], "prompt.md", "stream", "stderr");

        composed.Environment.Should().Equal([new KeyValuePair<string, string>("HALL9K_OTHER", "kept")]);
        composed.RemovedEnvironment.Should().Equal(ClaudeConfigDirectory.Variable);
        composed.Command.Should().Be("claude");
        composed.StandardInputFile.Should().Be("prompt.md");
    }

    [Fact]
    public void A_process_manager_removes_a_variable_the_request_names_before_setting_its_own()
    {
        ProcessStartInfo startInfo = new("claude");
        startInfo.Environment[ClaudeConfigDirectory.Variable] = "/inherited";
        ProcessSpawnRequest request = new("claude", _directory, [new("HALL9K_OTHER", "set")], null, "stream", "stderr")
        {
            RemovedEnvironment = [ClaudeConfigDirectory.Variable],
        };

        ProcessManagerBase.ApplyEnvironment(startInfo, request);

        startInfo.Environment.Should().NotContainKey(ClaudeConfigDirectory.Variable);
        startInfo.Environment["HALL9K_OTHER"].Should().Be("set");
    }

    // ---- the pre-spawn gate ----

    private ClaudeExecutor Executor(FakeProcessManager processManager, ClaudeAccountGate gate) =>
        new(
            NullLogger<ClaudeExecutor>.Instance, processManager,
            Options.Create(new DaemonOptions { SessionContainment = false }), SessionContainment.Off, gate);

    [Fact]
    public async Task A_refused_account_spawns_nothing_and_records_the_refusal()
    {
        FakeProcessManager processManager = new();
        RecordingLedger ledger = new();

        Func<Task> spawn = () => Executor(processManager, Gate(new FakeProbe(LoggedOut), ledger))
            .SpawnAsync(Request(_bound), CancellationToken.None);

        (await spawn.Should().ThrowAsync<ClaudeAccountRefusedException>())
            .Which.ConnectionId.Should().Be(_bound.ConnectionId!.Value);
        processManager.Spawns.Should().BeEmpty("nothing falls back to the default login");
        ledger.Records.Should().Equal((_bound.ConnectionId.Value, false));
    }

    [Fact]
    public async Task A_passing_account_spawns_with_its_directory_set()
    {
        FakeProcessManager processManager = new();

        await Executor(processManager, Gate(new FakeProbe(LoggedIn), new RecordingLedger()))
            .SpawnAsync(Request(_bound), CancellationToken.None);

        processManager.Spawns.Should().ContainSingle().Which.Environment
            .Should().Contain(new KeyValuePair<string, string>(ClaudeConfigDirectory.Variable, _bound.ConfigDirectory!));
    }

    [Fact]
    public async Task An_unbound_project_is_never_probed()
    {
        FakeProbe probe = new(LoggedOut);
        FakeProcessManager processManager = new();

        await Executor(processManager, Gate(probe, new RecordingLedger()))
            .SpawnAsync(Request(ClaudeCodeAccount.Default), CancellationToken.None);

        probe.Calls.Should().Be(0);
        processManager.Spawns.Should().ContainSingle().Which.RemovedEnvironment.Should().Equal(ClaudeConfigDirectory.Variable);
    }

    // ---- a launch failure under a bound account ----

    [Fact]
    public async Task A_launch_failure_re_checks_past_a_recent_pass_and_a_failing_check_refuses_the_account()
    {
        FakeProbe probe = new(LoggedIn, LoggedOut);
        RecordingLedger ledger = new();
        ClaudeAccountGate gate = Gate(probe, ledger);
        await gate.EnsureUsableAsync(_bound, CancellationToken.None);

        ClaudeAccountRefusedException? refusal = await gate.RefusalOnLaunchFailureAsync(_bound, CancellationToken.None);

        refusal.Should().NotBeNull("the caller holds the run on the account and leaves the node-wide hold down");
        probe.Calls.Should().Be(2, "the pass a moment ago is not reused for a session that just failed to launch");
        ledger.Records[^1].Should().Be((_bound.ConnectionId!.Value, false));
    }

    [Fact]
    public async Task A_launch_failure_whose_account_still_passes_is_left_to_the_node_wide_hold()
    {
        FakeProbe probe = new(LoggedIn);
        ClaudeAccountGate gate = Gate(probe, new RecordingLedger());

        (await gate.RefusalOnLaunchFailureAsync(_bound, CancellationToken.None)).Should().BeNull();
        (await gate.RefusalOnLaunchFailureAsync(ClaudeCodeAccount.Default, CancellationToken.None)).Should().BeNull();
        probe.Calls.Should().Be(1, "the default login is never probed");
    }

    // ---- new work and held work ----

    private sealed record QueuedTask(Guid Id, Guid ProjectId);

    [Fact]
    public void A_refused_projects_task_is_left_unclaimed_while_an_unbound_projects_is_admitted()
    {
        Guid refusedConnection = DomainId.New();
        Guid refusedProject = DomainId.New();
        Guid unboundProject = DomainId.New();
        Guid otherAccountProject = DomainId.New();
        IReadOnlySet<Guid> refused = ClaudeAccountRefusals.RefusedProjects(
            [(refusedProject, refusedConnection), (unboundProject, null), (otherAccountProject, DomainId.New())],
            new HashSet<Guid> { refusedConnection });
        QueuedTask blocked = new(DomainId.New(), refusedProject);
        QueuedTask unbound = new(DomainId.New(), unboundProject);
        QueuedTask other = new(DomainId.New(), otherAccountProject);

        IReadOnlyList<QueuedTask> admitted = ClaudeAccountRefusals.Admitted([blocked, unbound, other], task => task.ProjectId, refused);

        admitted.Should().Equal(unbound, other);
    }

    private RunDetails HeldLaunch(string? mentionCommentId = null)
    {
        // What RunLauncher records for a refused launch: dispatched, never started, held as its primary session.
        RunDetails run = new()
        {
            Id = DomainId.New(),
            SessionId = DomainId.New(),
            State = RunState.Dispatched,
            RunDirectory = _directory,
            PrReviewMentionCommentId = mentionCommentId,
        };
        new RunDetailsProjection().Apply(
            new FakeEvent<RunAccountHeld>(new RunAccountHeld(
                run.Id, _bound.ConnectionId!.Value, "logged out", DateTimeOffset.UtcNow, PrimarySession: true)),
            run);
        return run;
    }

    [Fact]
    public async Task A_refused_launch_starts_fresh_under_its_dispatched_session_from_the_prompt_it_wrote()
    {
        RunDetails held = HeldLaunch();
        await File.WriteAllTextAsync(RunPaths.PromptFile(_directory), "the original build prompt");
        TaskDetails task = new() { Id = DomainId.New(), Constraints = new TaskConstraints(25, null, null) };

        PrimarySessionResumer.SessionStart start = await PrimarySessionResumer.StartForAsync(
            held, task, "the error-retry prompt", new DaemonOptions(), CancellationToken.None);

        held.AccountHeldFromState.Should().BeNull("a held primary session resumes by starting it, not at the gates");
        start.SessionId.Should().Be(held.SessionId, "the session it was dispatched with never existed yet");
        start.ResumeSessionId.Should().BeNull("there is no session to --resume");
        start.Prompt.Should().Be("the original build prompt");
        start.MaxTurns.Should().Be(25);
    }

    [Fact]
    public async Task A_refused_mention_follow_up_keeps_its_hard_turn_cap_and_a_session_that_ran_is_resumed()
    {
        DaemonOptions options = new() { PrReviewMentionFollowUpMaxTurns = 7 };
        TaskDetails task = new() { Id = DomainId.New() };
        RunDetails mention = HeldLaunch(mentionCommentId: "comment-1");
        RunDetails ran = HeldLaunch();
        ran.ProcessStartedAt = DateTimeOffset.UtcNow;

        PrimarySessionResumer.SessionStart mentionStart =
            await PrimarySessionResumer.StartForAsync(mention, task, "retry", options, CancellationToken.None);
        PrimarySessionResumer.SessionStart ranStart =
            await PrimarySessionResumer.StartForAsync(ran, task, "retry", options, CancellationToken.None);

        mentionStart.MaxTurns.Should().Be(7);
        mentionStart.Prompt.Should().Be("retry", "no prompt was written, so the retry prompt stands");
        ranStart.ResumeSessionId.Should().Be(ran.SessionId, "a session that ran is resumed, not restarted");
        ranStart.SessionId.Should().NotBe(ran.SessionId);
        ranStart.Prompt.Should().Be("retry");
    }

    [Fact]
    public void The_account_monitor_resumes_only_runs_whose_account_passes_again_and_never_a_node_wide_hold()
    {
        RunDetails nodeHeld = new() { Id = DomainId.New(), State = RunState.LaunchHeld, AccountHeldConnectionId = null };
        RunDetails accountHeld = HeldLaunch();

        ClaudeAccountMonitor.Resumable([nodeHeld, accountHeld], new HashSet<Guid> { _bound.ConnectionId!.Value })
            .Should().BeEmpty("the account still fails its check");
        ClaudeAccountMonitor.Resumable([nodeHeld, accountHeld], new HashSet<Guid>()).Should().Equal(accountHeld);
    }
}
