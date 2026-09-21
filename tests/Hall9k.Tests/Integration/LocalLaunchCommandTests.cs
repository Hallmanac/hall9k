using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Processes;
using Hall9k.Daemon;
using Hall9k.Daemon.LocalLaunches;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <c>h9k task run-local</c> end to end against real Marten/Postgres, with a fake run skill of
/// three steps and one human step (idea b9b09779, piece 5). The acceptance criteria's own list:
/// the launch stops at the human step, continues after it, prints the address, refuses a second
/// launch of the same task, stops on close-out, and refuses with the sentence when the project has
/// no run skill.
/// <para>
/// The fake skill's commands are shell no-ops, so the walk is real — the command really does spawn
/// a process and really does record its pid — while nothing a reviewer's machine would recognise
/// gets started. The launch step is a sleep, which is the smallest honest stand-in for a product
/// that stays up: a command that exits immediately would make "the process is still alive" a race
/// rather than a fact.
/// </para>
/// </summary>
[Trait("Category", "RequiresDocker")]
[Trait("Category", "RealProcessSpawn")]
public sealed class LocalLaunchCommandTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private const string ProjectName = "run-local-demo";

    private readonly PostgresFixture _postgres;
    private readonly ScopedTestHome _scopedHome = new();

    public LocalLaunchCommandTests(PostgresFixture postgres) => _postgres = postgres;

    public Task InitializeAsync() => _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync()
    {
        _scopedHome.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The fake run skill: a prerequisite check, a setup command, a launch command, and one step
    /// nobody but a person can do. The human step is under its own heading, so the parse hoists it
    /// to the front and the very first pass stops there.
    /// </summary>
    private static string RunSkill(int seconds) =>
        $"""
        ## Prerequisites

        - A shell. Check it with `{Echo("checking")}`.

        ## One-time setup

        - Prepare the fixture: `{Echo("setting up")}`.

        ## Launch

        - Start it, which blocks: `{Sleep(seconds)}`

        ## How to know it is up

        The log prints `ready`.

        ## Address or entry point

        http://localhost:5000/app

        ## Human steps

        - Obtain the sandbox API key and put it in the environment before you walk anything.
        """;

    [Fact]
    public async Task The_launch_stops_at_the_human_step_continues_after_it_and_prints_the_address()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedAsync(RunSkill(seconds: 120), cts.Token);

        string paused = await RunAsync(seeded.TaskId, cts.Token);

        paused.Should().Contain("Stopped at step 1 of 4");
        paused.Should().Contain("Obtain the sandbox API key");
        paused.Should().Contain("--continue");
        LocalLaunchState afterPause = await LaunchAsync(seeded.RunId, cts.Token);
        afterPause.AwaitingHumanAtStep.Should().Be(1);
        afterPause.Processes.Should().BeEmpty("nothing runs before the step the reviewer has to do");

        string up = await RunAsync(seeded.TaskId, cts.Token, Mode.Continue);

        up.Should().Contain("Up.");
        up.Should().Contain("http://localhost:");
        up.Should().Contain("/app");
        up.Should().Contain("Human steps from this project's run skill, in order");
        up.Should().Contain("Obtain the sandbox API key");

        LocalLaunchState running = await LaunchAsync(seeded.RunId, cts.Token);
        running.Walked.Should().BeTrue();
        running.Port.Should().BeGreaterThan(0);
        running.Address.Should().Be($"http://localhost:{running.Port}/app");
        running.Processes.Should().ContainSingle();
        up.Should().Contain(running.Port.ToString()!);

        await StopAsync(seeded.TaskId, cts.Token);
    }

    [Fact]
    public async Task A_second_launch_of_the_same_task_is_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedAsync(RunSkill(seconds: 120), cts.Token);
        await RunAsync(seeded.TaskId, cts.Token);

        Func<Task> second = () => RunAsync(seeded.TaskId, cts.Token);

        await second.Should().ThrowAsync<DomainConflictException>()
            .Where(exception => exception.Message.Contains("is already up")
                && exception.Message.Contains("waiting on you at step 1"));

        await RunAsync(seeded.TaskId, cts.Token, Mode.Continue);
        await second.Should().ThrowAsync<DomainConflictException>()
            .Where(exception => exception.Message.Contains("still running"));

        await StopAsync(seeded.TaskId, cts.Token);
    }

    [Fact]
    public async Task Stop_ends_the_process_and_records_the_event()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedAsync(RunSkill(seconds: 120), cts.Token);
        await RunAsync(seeded.TaskId, cts.Token);
        await RunAsync(seeded.TaskId, cts.Token, Mode.Continue);
        LocalLaunchState running = await LaunchAsync(seeded.RunId, cts.Token);
        LocalLaunchProcess process = running.Processes.Should().ContainSingle().Subject;

        await StopAsync(seeded.TaskId, cts.Token);

        LocalLaunchState stopped = await LaunchAsync(seeded.RunId, cts.Token);
        stopped.Live.Should().BeFalse();
        stopped.StoppedReason.Should().Be(LocalLaunchStopReason.Requested);
        await WaitUntilGoneAsync(process, cts.Token);
    }

    /// <summary>The criterion's own teardown case: the task closes out, and the daemon's sweep takes the launch down with it.</summary>
    [Fact]
    public async Task A_task_that_closes_out_has_its_launch_torn_down_by_the_sweep()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedAsync(RunSkill(seconds: 120), cts.Token);
        await RunAsync(seeded.TaskId, cts.Token);
        await RunAsync(seeded.TaskId, cts.Token, Mode.Continue);
        LocalLaunchProcess process =
            (await LaunchAsync(seeded.RunId, cts.Token)).Processes.Should().ContainSingle().Subject;

        await CloseOutAsync(seeded, cts.Token);
        int stopped = await Sweep(seeded.Node).SweepOnceAsync(cts.Token);

        stopped.Should().Be(1);
        LocalLaunchState torn = await LaunchAsync(seeded.RunId, cts.Token);
        torn.StoppedReason.Should().Be(LocalLaunchStopReason.TaskClosedOut);
        await WaitUntilGoneAsync(process, cts.Token);

        (await Sweep(seeded.Node).SweepOnceAsync(cts.Token))
            .Should().Be(0, "a launch already torn down is never torn down twice");
    }

    /// <summary>
    /// A launch paused at its human step and then abandoned, whose checkout went away underneath
    /// it. It is left paused on purpose rather than walked all the way up: a running launch's own
    /// process has the worktree as its current directory, and Windows will not delete a directory
    /// any process is sitting in — which is the whole reason closeout ends the launch before it
    /// releases the checkout rather than leaving it to this sweep. The running case's teardown is
    /// pinned by <c>LocalLaunchTeardownTests</c>, where no real directory is involved.
    /// </summary>
    [Fact]
    public async Task A_removed_worktree_has_its_launch_torn_down_by_the_sweep()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedAsync(RunSkill(seconds: 120), cts.Token);
        await RunAsync(seeded.TaskId, cts.Token);
        (await LaunchAsync(seeded.RunId, cts.Token)).AwaitingHumanAtStep.Should().Be(1);

        Directory.Delete(seeded.Worktree, recursive: true);
        (await Sweep(seeded.Node).SweepOnceAsync(cts.Token)).Should().Be(1);

        (await LaunchAsync(seeded.RunId, cts.Token)).StoppedReason
            .Should().Be(LocalLaunchStopReason.WorktreeRemoved);
    }

    /// <summary>
    /// A launch that pauses AFTER it has already started something, which is the one shape where
    /// that happens: a prose-only item inside the Launch section, between two commands (every
    /// other human step is hoisted to the front of the plan). What it pins is that the second pass
    /// carries the first pass's process forward rather than replacing it — get that wrong and the
    /// first process is off the record, so nothing ever stops it again.
    /// </summary>
    [Fact]
    public async Task A_resume_carries_forward_what_the_earlier_pass_already_started()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedAsync(TwoLaunchesAroundAHumanStep(seconds: 120), cts.Token);

        await RunAsync(seeded.TaskId, cts.Token);

        LocalLaunchState paused = await LaunchAsync(seeded.RunId, cts.Token);
        paused.AwaitingHumanAtStep.Should().Be(2);
        paused.Processes.Should().ContainSingle("the first launch command ran before the step that needs a person");

        await RunAsync(seeded.TaskId, cts.Token, Mode.Continue);

        LocalLaunchState up = await LaunchAsync(seeded.RunId, cts.Token);
        up.Walked.Should().BeTrue();
        IReadOnlyList<LocalLaunchProcess> both = up.Processes;
        both.Should().HaveCount(2, "the resume adds to what the first pass started rather than replacing it");

        await StopAsync(seeded.TaskId, cts.Token);

        foreach (LocalLaunchProcess process in both)
        {
            await WaitUntilGoneAsync(process, cts.Token);
        }
    }

    /// <summary>Two launch commands with a step only a person can do between them, all three inside the Launch section.</summary>
    private static string TwoLaunchesAroundAHumanStep(int seconds) =>
        $"""
        ## Prerequisites

        None.

        ## One-time setup

        None.

        ## Launch

        - Start the API: `{Sleep(seconds)}`
        - Accept the development certificate prompt when the browser raises it.
        - Start the worker: `{Sleep(seconds)}`

        ## How to know it is up

        The log prints `ready`.

        ## Address or entry point

        http://localhost:5000/app

        ## Human steps

        None.
        """;

    /// <summary>
    /// A run skill carries the line a person would paste into a terminal, and a setup step is
    /// routinely two commands joined with <c>&amp;&amp;</c>. Both halves have to run, and the step
    /// has to report what the second one said: a wrapping that replaced the shell at the first
    /// command would run only that one and call the step a success, so the launch would sail on
    /// into its launch step having skipped half its own setup.
    /// </summary>
    [Fact]
    public async Task A_setup_step_of_two_commands_runs_both_and_fails_on_the_second()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedAsync(SetupOfTwoCommands(seconds: 120), cts.Token);

        string failed = await RunAsync(seeded.TaskId, cts.Token);

        failed.Should().Contain("Step 1 of 2 failed");
        LocalLaunchState launch = await LaunchAsync(seeded.RunId, cts.Token);
        launch.Live.Should().BeFalse();
        launch.FailedAtStep.Should().Be(1);
        launch.FailedReason.Should().Contain("exit code 3");
        launch.Processes.Should().BeEmpty("the launch step after the failed setup never ran");
    }

    /// <summary>
    /// One setup step whose line is two commands, the second of which fails, and one launch step
    /// after it. No human step, so the very first pass reaches the setup.
    /// </summary>
    private static string SetupOfTwoCommands(int seconds) =>
        $"""
        ## Prerequisites

        None.

        ## One-time setup

        - Prepare the fixture: `{Echo("setting up")} && {ExitWith(3)}`

        ## Launch

        - Start it, which blocks: `{Sleep(seconds)}`

        ## How to know it is up

        The log prints `ready`.

        ## Address or entry point

        http://localhost:5000/app

        ## Human steps

        None.
        """;

    [Fact]
    public async Task A_project_with_no_run_skill_refuses_with_the_sentence()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedAsync(runSkill: null, cts.Token);

        Func<Task> launch = () => RunAsync(seeded.TaskId, cts.Token);

        await launch.Should().ThrowAsync<DomainBusinessRuleException>()
            .WithMessage(LocalLaunchRefusal.NoRunSkill(ProjectName));
    }

    [Fact]
    public async Task A_worktree_that_is_gone_refuses_with_the_sentence()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedAsync(RunSkill(seconds: 120), cts.Token);
        Directory.Delete(seeded.Worktree, recursive: true);

        Func<Task> launch = () => RunAsync(seeded.TaskId, cts.Token);

        await launch.Should().ThrowAsync<DomainBusinessRuleException>()
            .WithMessage(LocalLaunchRefusal.WorktreeGone(seeded.Worktree));
    }

    [Fact]
    public async Task Continue_with_nothing_paused_refuses_rather_than_starting_a_launch()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedAsync(RunSkill(seconds: 120), cts.Token);

        Func<Task> resume = () =>
            RunAsync(seeded.TaskId, cts.Token, Mode.Continue);

        await resume.Should().ThrowAsync<DomainConflictException>()
            .WithMessage(LocalLaunchRefusal.NothingToContinue(seeded.TaskId));
    }

    private LocalLaunchSweepEngine Sweep(NodeContext node) =>
        new(_postgres.Store, node, NullLogger<LocalLaunchSweepEngine>.Instance);

    /// <summary>Which of the command's three modes a call is in — its settings are a Spectre class, not a record, so there is nothing to <c>with</c>.</summary>
    private enum Mode
    {
        Start,
        Continue,
        Stop,
    }

    private async Task<string> RunAsync(Guid taskId, CancellationToken cancellationToken, Mode mode = Mode.Start)
    {
        TaskRunLocalCommand.Settings settings = new()
        {
            Task = taskId.ToString(),
            Continue = mode == Mode.Continue,
            Stop = mode == Mode.Stop,
        };
        return await ScopedAnsiConsoleCapture.CaptureAsync(async () =>
        {
            await using IDocumentSession session = _postgres.Store.LightweightSession();
            await TaskRunLocalCommand.RunAsync(session, settings, cancellationToken);
        });
    }

    private Task StopAsync(Guid taskId, CancellationToken cancellationToken) =>
        RunAsync(taskId, cancellationToken, Mode.Stop);

    private async Task<LocalLaunchState> LaunchAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using IQuerySession session = _postgres.Store.QuerySession();
        RunDetails run = await session.LoadAsync<RunDetails>(runId, cancellationToken)
            ?? throw new InvalidOperationException($"No run {runId}.");
        return run.LocalLaunch ?? throw new InvalidOperationException($"Run {runId} recorded no local launch.");
    }

    /// <summary>
    /// A kill is asynchronous on both platforms — the call returns once the signal is delivered,
    /// not once the process table has caught up — so this polls the identity rather than asserting
    /// it the instant the stop returns. Deliberately a poll on one process and not load of any
    /// kind (AGENTS.md's own rule against generating host load to prove timing).
    /// </summary>
    private static async Task WaitUntilGoneAsync(LocalLaunchProcess process, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!WorktreeShell.IsAlive(process.ProcessId, process.StartedAt))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        WorktreeShell.IsAlive(process.ProcessId, process.StartedAt)
            .Should().BeFalse("the stop is meant to have ended it");
    }

    private async Task CloseOutAsync(Seeded seeded, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(
            seeded.TaskId, token: cancellationToken)
            ?? throw new InvalidOperationException($"No task {seeded.TaskId}.");
        TaskCompleted completed = TaskDecider.Complete(task, seeded.RunId, null, Now.AddHours(1));
        session.Events.Append(seeded.TaskId, completed);
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task<Seeded> SeedAsync(string? runSkill, CancellationToken cancellationToken)
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, cancellationToken);
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        string projectHome = Path.Combine(_scopedHome.Home, "projects", ProjectName);
        string worktree = Path.Combine(projectHome, "repo", $"wt-{DomainId.Short(taskId)}");
        Directory.CreateDirectory(worktree);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream<ProjectAggregate>(
            projectId,
            ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), ProjectName, Path.Combine(projectHome, "repo", "bare"),
                null, null, Now, homeDirectory: ProjectHome.Parse(projectHome)));
        if (runSkill is not null)
        {
            session.Events.Append(
                projectId,
                ProjectDecider.RecordRunSkill(
                    projectId, runSkill, RunSkillShape.FullText, RunSkillAuthor.Hand, "abc123",
                    node.OwnerId, Now));
        }

        TaskAdded added = TaskDecider.Add(
            taskId, projectId, "Review the change", ["The findings report is walked."],
            TaskType.PrReview, null, null, null, Now, node.OwnerId);
        TaskAggregate task = new();
        task.Apply(added);
        TaskPublished published = TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, node.OwnerId);
        task.Apply(published);
        TaskAssigned assigned = TaskDecider.Assign(task, node.OwnerId, [], Now, node.OwnerId);
        task.Apply(assigned);
        TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now);
        task.Apply(claimed);
        session.Events.StartStream<TaskAggregate>(taskId, [added, published, assigned, claimed]);
        session.Events.StartStream<RunAggregate>(
            runId,
            new RunDispatched(
                runId, taskId, node.NodeId, node.OwnerId, LeaseGeneration: 1, SessionId: DomainId.New(),
                WorktreePath: worktree, Branch: "pr/42", ExecutorMode.Subscription,
                Now, RunDirectory: Path.Combine(projectHome, "runs", runId.ToString())));
        await session.SaveChangesAsync(cancellationToken);

        return new Seeded(node, taskId, runId, worktree);
    }

    /// <summary>A command that prints and exits, on whichever shell this machine runs.</summary>
    private static string Echo(string what) => $"echo {what}";

    /// <summary>A command that fails with a code a test can name, in each shell's own spelling.</summary>
    private static string ExitWith(int code) =>
        OperatingSystem.IsWindows() ? $"exit /b {code}" : $"exit {code}";

    /// <summary>
    /// A command that stays up for <paramref name="seconds"/> and then exits on its own, so a test
    /// that fails before its own stop still leaves nothing behind.
    /// <para>
    /// Each platform's own spelling, and each carries the <c>PORT=</c> assignment the launch
    /// substitutes its ephemeral port into — one of the three forms
    /// <see cref="RunSkillLaunchPort"/> recognises. The Unix line writes it the way a person would,
    /// as a bare assignment in front of the command, which is also the form that would fail with
    /// exit 127 if the shell wrapping ever went back to <c>exec</c>. <c>ping</c> rather than
    /// <c>timeout</c> on Windows: <c>timeout</c> refuses outright when stdin is redirected, which
    /// every detached spawn's is.
    /// </para>
    /// </summary>
    private static string Sleep(int seconds) =>
        OperatingSystem.IsWindows()
            ? $"set PORT=5000 && ping -n {seconds + 1} 127.0.0.1"
            : $"PORT=5000 sleep {seconds}";

    private sealed record Seeded(NodeContext Node, Guid TaskId, Guid RunId, string Worktree);
}
