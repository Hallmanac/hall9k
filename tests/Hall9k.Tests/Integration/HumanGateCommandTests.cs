using System.Diagnostics;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Processes;
using Hall9k.Daemon;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The two commands that read a run parked at its human gate, against a real store and a real throwaway git
/// repository: <c>h9k review brief</c> and the gate branch of <c>h9k task drive</c>. Neither calls GitHub,
/// and the drive branch records nothing.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class HumanGateCommandTests : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private readonly PostgresFixture _postgres;
    private readonly ScopedTestHome _home;

    public HumanGateCommandTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _home = new ScopedTestHome(postgres.ConnectionString);
    }

    public void Dispose() => _home.Dispose();

    private sealed record Seeded(Guid TaskId, Guid RunId, string Worktree, string Branch, string BaseSha, string TipSha);

    private static void Git(string directory, params string[] arguments)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, output);
    }

    private static string GitOut(string directory, params string[] arguments)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Trim();
    }

    /// <summary>A run parked at the gate (or at another park) over a real repository with two commits on its branch.</summary>
    private async Task<Seeded> SeedAsync(
        bool humanGate = true, bool atGate = true, bool worktreeExists = true, string? summary = "Title: T\n\nThe pane now lists parked runs first.\n\nDetails.")
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, CancellationToken.None);
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        string worktree = Path.Combine(_home.Home, $"wt-{runId:N}");
        string branch = "task/gate-me";
        Directory.CreateDirectory(worktree);
        Git(worktree, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(worktree, "base.txt"), "base\n");
        Git(worktree, "add", "-A");
        Git(worktree, "-c", "user.name=Test", "-c", "user.email=test@test", "commit", "-q", "-m", "init");
        string baseSha = GitOut(worktree, "rev-parse", "HEAD");
        Git(worktree, "checkout", "-q", "-b", branch);
        File.WriteAllText(Path.Combine(worktree, "one.txt"), "one\ntwo\n");
        Git(worktree, "add", "-A");
        Git(worktree, "-c", "user.name=Test", "-c", "user.email=test@test", "commit", "-q", "-m", "add one");
        File.WriteAllText(Path.Combine(worktree, "two.txt"), "x\n");
        Git(worktree, "add", "-A");
        Git(worktree, "-c", "user.name=Test", "-c", "user.email=test@test", "commit", "-q", "-m", "add two");
        string tip = GitOut(worktree, "rev-parse", "HEAD");

        string runDirectory = Path.Combine(_home.Home, "runs", runId.ToString());
        Directory.CreateDirectory(runDirectory);
        if (summary is not null)
        {
            File.WriteAllText(RunPaths.PrSummaryFile(runDirectory), summary);
        }

        if (!worktreeExists)
        {
            Directory.Delete(worktree, recursive: true);
        }

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        ProjectAggregate project = new();
        var registered = ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), $"gate-{taskId:N}", worktree, null, "main", Now);
        project.Apply(registered);
        session.Events.StartStream<ProjectAggregate>(projectId, registered);

        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(taskId, projectId, "Rework the status pane", ["Parked runs come first"],
                TaskType.Feature, null, null, null, Now, node.OwnerId, humanGate: humanGate),
            node.OwnerId, Now);
        var claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now);
        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);

        List<object> events =
        [
            new RunDispatched(
                runId, taskId, node.NodeId, node.OwnerId, 1, DomainId.New(), worktree, branch,
                ExecutorMode.Subscription, Now, RunDirectory: runDirectory, BaseBranch: "", BaseCommit: baseSha),
            new AgentSessionCompleted(runId, Now),
            new VerificationPassed(runId, Now, RanFullScope: true, HeadSha: tip),
            new ReviewDispatched(runId, DomainId.New(), 1, 5001, Now, Now),
            new ReviewCompleted(runId, 1, ReviewVerdict.MergeReady, Now),
            new ReviewSettled(runId, 1, ReviewSettlement.Settled, 0, 0, 0, Now, ResidualsUnfixed: 2),
        ];
        events.Add(atGate
            ? new ReviewParked(runId, "This task has a human gate.", Now, IsInteractiveGate: true, IsHumanGate: true, HumanGateTipSha: tip)
            : new ReviewParked(runId, "Cycle cap spent.", Now));
        session.Events.StartStream<RunAggregate>(runId, [.. events]);
        await session.SaveChangesAsync();
        return new Seeded(taskId, runId, worktree, branch, baseSha, tip);
    }

    private async Task<string> BriefAsync(Guid taskId, List<string>? commands = null)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        ProcessRunner recording = async (fileName, arguments, directory, token) =>
        {
            commands?.Add(fileName);
            return await ExternalProcess.Runner(fileName, arguments, directory, token);
        };
        return await ReviewBriefCommand.ComposeAsync(session, taskId, recording, CancellationToken.None);
    }

    [Fact]
    public async Task The_briefing_reads_the_branch_the_summary_and_the_settlement_and_calls_nothing_but_git()
    {
        Seeded seeded = await SeedAsync();
        List<string> programs = [];

        string page = await BriefAsync(seeded.TaskId, programs);

        programs.Should().OnlyContain(program => program == "git", "no GitHub call, no session, no network");
        page.Should().Contain("stopped at its human gate")
            .And.Contain("The pane now lists parked runs first.")
            .And.Contain("Rework the status pane").And.Contain("Parked runs come first")
            .And.Contain("settled, after 1 review cycle, with 2 findings left unfixed.")
            .And.Contain("review-1-findings.md");
        page.IndexOf("add one", StringComparison.Ordinal).Should().BeLessThan(
            page.IndexOf("add two", StringComparison.Ordinal), "oldest first");
        page.Should().Contain("In total 2 files and 3 lines changed.");
        page.Should().Contain($"Worktree: {seeded.Worktree}").And.Contain($"Branch: {seeded.Branch}")
            .And.Contain($"Base commit: {seeded.BaseSha}").And.Contain($"Head commit: {seeded.TipSha}");
        page.Should().Contain("difftool --dir-diff").And.Contain($"h9k task drive {seeded.TaskId}");
    }

    [Fact]
    public async Task The_briefing_without_a_summary_says_so_and_still_prints_everything_else()
    {
        Seeded seeded = await SeedAsync(summary: null);

        string page = await BriefAsync(seeded.TaskId);

        page.Should().Contain("No pull request summary was written")
            .And.Contain("add one").And.Contain("Your choices");
    }

    [Fact]
    public async Task The_briefing_is_refused_at_any_other_park_naming_task_next()
    {
        Seeded seeded = await SeedAsync(atGate: false);

        Func<Task> brief = () => BriefAsync(seeded.TaskId);

        await brief.Should().ThrowAsync<DomainConflictException>().WithMessage($"*h9k task next {seeded.TaskId}*");
    }

    [Fact]
    public async Task The_briefing_on_a_machine_without_the_worktree_is_refused_naming_the_node_that_holds_it()
    {
        Seeded seeded = await SeedAsync(worktreeExists: false);

        Func<Task> brief = () => BriefAsync(seeded.TaskId);

        await brief.Should().ThrowAsync<DomainConflictException>().WithMessage("*not on this machine*node *");
    }

    [Fact]
    public async Task Drive_at_the_gate_prints_a_launch_line_writes_the_gate_anchor_and_records_nothing()
    {
        Seeded seeded = await SeedAsync();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(seeded.TaskId))!;
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, CancellationToken.None);
        long taskVersion = (await session.Events.FetchStreamStateAsync(seeded.TaskId))!.Version;
        long runVersion = (await session.Events.FetchStreamStateAsync(seeded.RunId))!.Version;

        int? exit = await TaskDriveCommand.TryPrintGateStepInAsync(
            session, task, context, new TaskDriveCommand.Settings { Id = seeded.TaskId.ToString() }, CancellationToken.None);

        exit.Should().Be(0);
        (await session.Events.FetchStreamStateAsync(seeded.TaskId))!.Version.Should().Be(taskVersion, "no claim is taken");
        (await session.Events.FetchStreamStateAsync(seeded.RunId))!.Version.Should().Be(
            runVersion, "the session registers nothing and the park, the claim, and the interactive flag are untouched");
        string anchor = File.ReadAllText(RunPaths.TaskAnchorFile(Path.Combine(_home.Home, "runs", seeded.RunId.ToString())));
        anchor.Should().Contain("human gate").And.Contain($"h9k review brief {seeded.TaskId}")
            .And.Contain("leave the worktree alone");
    }

    [Fact]
    public async Task Drive_at_any_other_park_on_a_headless_run_is_not_handled_here_and_so_refuses_as_it_always_has()
    {
        Seeded seeded = await SeedAsync(atGate: false);
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(seeded.TaskId))!;
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, CancellationToken.None);

        int? exit = await TaskDriveCommand.TryPrintGateStepInAsync(
            session, task, context, new TaskDriveCommand.Settings { Id = seeded.TaskId.ToString() }, CancellationToken.None);

        exit.Should().BeNull("the ordinary claim path, and its ordinary refusal, take over");
    }

    [Fact]
    public async Task Drive_at_the_gate_on_a_machine_without_the_worktree_is_refused_naming_the_node_that_holds_it()
    {
        Seeded seeded = await SeedAsync(worktreeExists: false);
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(seeded.TaskId))!;
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, CancellationToken.None);

        Func<Task> drive = () => TaskDriveCommand.TryPrintGateStepInAsync(
            session, task, context, new TaskDriveCommand.Settings { Id = seeded.TaskId.ToString() }, CancellationToken.None);

        await drive.Should().ThrowAsync<DomainConflictException>().WithMessage("*not on this machine*node *");
    }

    [Fact]
    public async Task Evolving_from_a_task_copies_what_the_discard_outcome_needs_and_names_the_source()
    {
        Seeded seeded = await SeedAsync();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            TaskAggregate source = (await session.Events.AggregateStreamAsync<TaskAggregate>(seeded.TaskId))!;
            session.Events.Append(seeded.TaskId, TaskDecider.Abandon(source, "the approach was wrong", Now, source.AddedByOwnerId));
            await session.SaveChangesAsync();
        }

        await TaskAddCommand.RunAsync(
            _postgres.Store,
            new TaskAddCommand.Settings { EvolvedFrom = seeded.TaskId.ToString(), AgentContext = "Try the other approach." },
            new FakeLedger(),
            _ => throw new InvalidOperationException("no tracker is read"),
            CancellationToken.None);

        await using IQuerySession query = _postgres.Store.QuerySession();
        Hall9k.Domain.Features.Tasks.Projections.TaskDetails evolved = (await query
            .Query<Hall9k.Domain.Features.Tasks.Projections.TaskDetails>()
            .Where(task => task.EvolvedFromTaskId == seeded.TaskId)
            .ToListAsync()).Single();
        evolved.State.Should().Be(TaskState.Draft);
        evolved.Objective.Should().Be("Rework the status pane");
        evolved.AcceptanceCriteria.Should().Equal("Parked runs come first");
        evolved.Type.Should().Be(TaskType.Feature);
        evolved.HumanGate.Should().BeTrue("the gate travels with the work");
        evolved.AgentContext.Should().Contain("abandoned with this reason: the approach was wrong")
            .And.Contain("Try the other approach.");
    }

    [Fact]
    public async Task Evolving_from_a_task_cannot_be_combined_with_another_seed()
    {
        Seeded seeded = await SeedAsync();

        Func<Task> both = () => TaskAddCommand.RunAsync(
            _postgres.Store,
            new TaskAddCommand.Settings { EvolvedFrom = seeded.TaskId.ToString(), FromIssue = "42" },
            new FakeLedger(),
            _ => throw new InvalidOperationException("no tracker is read"),
            CancellationToken.None);

        await both.Should().ThrowAsync<DomainValidationException>().WithMessage("*pass one*");
    }
}
