using System.Text.Json;
using FluentAssertions;
using Hall9k.Connectors.Worktrees;
using Hall9k.Daemon;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <see cref="SpikeEngine"/>'s own driver path (task: a spike is a run, not a walk), over a real
/// local git origin-and-clone this file sets up itself — never a real GitHub remote. Every test
/// seeds a Claimed spike task and a matching <c>RunDispatched</c> directly, the way
/// <c>PrReviewTaskEngineTests</c> seeds its own engine's runs, then drives the SAME calls
/// <c>RunSupervisor.CompleteRunAsync</c> makes for a spike build session's own completion
/// (<see cref="SpikeEngine.RecordFindingsAsync"/>, the caller's own gate-or-skip decision off
/// <see cref="SpikeKind.RunsGates"/>, then <see cref="SpikeEngine.ReviewAsync"/>) — so this file
/// tests the engine's own contract without needing the monitor loop's background polling or a
/// real spawned process.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection("Hall9kHome")]
[Trait("Category", "Hall9kHome")]
public sealed class SpikeEngineTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    // -------------------------------------------------------------------------------------
    // a. Prototype: gates run and pass, review says met, branch is pushed to the local origin.
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_prototype_spike_that_meets_its_criterion_runs_gates_and_pushes_its_branch()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        string root = Path.Combine(Path.GetTempPath(), $"hall9k-spike-prototype-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("HALL9K_HOME", Path.Combine(root, "home"));
        try
        {
            (string originPath, string repoPath) = await CreateOriginAndCloneAsync(root, cts.Token);
            VerifyCommand gate = new("gate", GateScript.New().Print("prototype-gate-ran").Command);

            SeededSpike seed = await SeedClaimedSpikeAsync(
                store, repoPath, SpikeKind.Prototype, "A working demo proves the approach is sound.",
                [gate], sourceIdeaId: null, cts.Token);

            ScriptedSpikeExecutor executor = new(new Dictionary<string, string>
            {
                ["spike-review-1"] = VerdictText("met", "The demo runs end to end and satisfies the criterion."),
            });
            FakeProcessManager processManager = new();
            GitWorktreeManager worktrees = new(NullLogger<GitWorktreeManager>.Instance);
            VerificationRunner verification = NewVerificationRunner(store, executor, processManager, worktrees);
            SpikeEngine spike = NewSpikeEngine(store, executor, processManager, worktrees, seed.Node);

            await spike.RecordFindingsAsync(seed.RunDirectory, "Findings: the approach demonstrably works.", cts.Token);
            bool gatesOk = await verification.VerifyAsync(
                seed.RunId, seed.TaskId, null, "spike gate", RunSessionLeg.Build, cts.Token);
            gatesOk.Should().BeTrue("the prototype's own gate is a trivial pass");

            await spike.ReviewAsync(seed.RunId, seed.TaskId, cts.Token);

            await using IQuerySession query = store.QuerySession();
            TaskDetails task = (await query.LoadAsync<TaskDetails>(seed.TaskId, cts.Token))!;
            task.State.Should().Be(TaskState.Done);
            task.SpikeVerdict.Should().Be(SpikeVerdict.Met);
            task.PullRequestUrl.Should().BeNull("a spike never opens a pull request");

            IReadOnlyList<object> taskEvents = [.. (await query.Events.FetchStreamAsync(seed.TaskId, token: cts.Token)).Select(e => e.Data)];
            taskEvents.OfType<SpikeConcluded>().Should().ContainSingle();
            taskEvents.OfType<TaskCompleted>().Should().ContainSingle().Which.PullRequestUrl.Should().BeNull();

            IReadOnlyList<object> runEvents = [.. (await query.Events.FetchStreamAsync(seed.RunId, token: cts.Token)).Select(e => e.Data)];
            runEvents.OfType<VerificationPassed>().Should().ContainSingle("the prototype's own gates actually ran and passed");

            Directory.Exists(seed.WorktreePath).Should().BeFalse("the worktree is released once a spike concludes");

            string branchOnOrigin = await TestGit.CaptureAsync(originPath, ["branch", "--list", seed.Branch], cts.Token);
            branchOnOrigin.Should().Contain(seed.Branch, "a prototype's branch is pushed to origin as evidence");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HALL9K_HOME", null);
            TemporaryTree.TryDelete(root);
        }
    }

    // -------------------------------------------------------------------------------------
    // b. Research: gates never run (a trap gate would fail loudly if it did), branch stays
    // local and is never pushed.
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_research_spike_that_meets_its_criterion_skips_gates_and_keeps_its_branch_locally()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        string root = Path.Combine(Path.GetTempPath(), $"hall9k-spike-research-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("HALL9K_HOME", Path.Combine(root, "home"));
        try
        {
            (string originPath, string repoPath) = await CreateOriginAndCloneAsync(root, cts.Token);
            VerifyCommand trapGate = new("would-fail", GateScript.New().Print("should-never-run").Exit(1).Command);

            SeededSpike seed = await SeedClaimedSpikeAsync(
                store, repoPath, SpikeKind.Research, "The findings answer the stated question.",
                [trapGate], sourceIdeaId: null, cts.Token);

            ScriptedSpikeExecutor executor = new(new Dictionary<string, string>
            {
                ["spike-review-1"] = VerdictText("met", "The findings squarely answer the question."),
            });
            FakeProcessManager processManager = new();
            GitWorktreeManager worktrees = new(NullLogger<GitWorktreeManager>.Instance);
            VerificationRunner verification = NewVerificationRunner(store, executor, processManager, worktrees);
            SpikeEngine spike = NewSpikeEngine(store, executor, processManager, worktrees, seed.Node);

            await spike.RecordFindingsAsync(seed.RunDirectory, "Findings: the question is answered.", cts.Token);
            // Mirrors RunSupervisor.CompleteRunAsync's own caller contract exactly: only
            // Prototype's own gates ever run, so this never calls VerifyAsync at all — the trap
            // gate above would fail loudly (and the assertion below would catch it) if it did.
            bool gatesOk = !SpikeKind.Research.RunsGates
                || await verification.VerifyAsync(seed.RunId, seed.TaskId, null, "spike gate", RunSessionLeg.Build, cts.Token);
            gatesOk.Should().BeTrue();
            await spike.ReviewAsync(seed.RunId, seed.TaskId, cts.Token);

            await using IQuerySession query = store.QuerySession();
            TaskDetails task = (await query.LoadAsync<TaskDetails>(seed.TaskId, cts.Token))!;
            task.State.Should().Be(TaskState.Done);
            task.SpikeVerdict.Should().Be(SpikeVerdict.Met);

            IReadOnlyList<object> runEvents = [.. (await query.Events.FetchStreamAsync(seed.RunId, token: cts.Token)).Select(e => e.Data)];
            runEvents.OfType<VerificationPassed>().Should().BeEmpty("research never runs gates");
            runEvents.OfType<VerificationFailed>().Should().BeEmpty("the trap gate must never actually run");

            Directory.Exists(seed.WorktreePath).Should().BeFalse("the worktree is released once a spike concludes");

            string branchOnClone = await TestGit.CaptureAsync(repoPath, ["branch", "--list", seed.Branch], cts.Token);
            branchOnClone.Should().Contain(seed.Branch, "research keeps its branch as the record, locally");

            string branchOnOrigin = await TestGit.CaptureAsync(originPath, ["branch", "--list", seed.Branch], cts.Token);
            branchOnOrigin.Should().BeEmpty("research's branch is kept locally, never pushed");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HALL9K_HOME", null);
            TemporaryTree.TryDelete(root);
        }
    }

    // -------------------------------------------------------------------------------------
    // c. Experiment: branch is deleted locally once findings are copied out; findings survive.
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task An_experiment_spike_that_meets_its_criterion_deletes_its_branch_but_keeps_findings()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        string root = Path.Combine(Path.GetTempPath(), $"hall9k-spike-experiment-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("HALL9K_HOME", Path.Combine(root, "home"));
        try
        {
            (string originPath, string repoPath) = await CreateOriginAndCloneAsync(root, cts.Token);

            SeededSpike seed = await SeedClaimedSpikeAsync(
                store, repoPath, SpikeKind.Experiment, "The experiment measurably moves the metric.",
                gates: [], sourceIdeaId: null, cts.Token);

            ScriptedSpikeExecutor executor = new(new Dictionary<string, string>
            {
                ["spike-review-1"] = VerdictText("met", "The metric moved as the experiment predicted."),
            });
            FakeProcessManager processManager = new();
            GitWorktreeManager worktrees = new(NullLogger<GitWorktreeManager>.Instance);
            SpikeEngine spike = NewSpikeEngine(store, executor, processManager, worktrees, seed.Node);

            const string findingsText = "Findings: the metric moved by 12%.";
            await spike.RecordFindingsAsync(seed.RunDirectory, findingsText, cts.Token);
            await spike.ReviewAsync(seed.RunId, seed.TaskId, cts.Token);

            await using IQuerySession query = store.QuerySession();
            TaskDetails task = (await query.LoadAsync<TaskDetails>(seed.TaskId, cts.Token))!;
            task.State.Should().Be(TaskState.Done);
            task.SpikeVerdict.Should().Be(SpikeVerdict.Met);
            task.SpikeFindingsPath.Should().Be(RunPaths.SpikeFindingsFile(seed.RunDirectory));

            string branchOnClone = await TestGit.CaptureAsync(repoPath, ["branch", "--list", seed.Branch], cts.Token);
            branchOnClone.Should().BeEmpty("an experiment's branch is deleted once its findings are copied out");

            File.Exists(task.SpikeFindingsPath).Should().BeTrue("findings survive branch deletion");
            (await File.ReadAllTextAsync(task.SpikeFindingsPath!, cts.Token)).Should().Be(findingsText);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HALL9K_HOME", null);
            TemporaryTree.TryDelete(root);
        }
    }

    // -------------------------------------------------------------------------------------
    // d. Budget-exhausted: closes out normally, never Failed, no session ever dispatched.
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_spike_ended_over_budget_closes_out_done_with_a_budget_exhausted_verdict_never_failed()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        string root = Path.Combine(Path.GetTempPath(), $"hall9k-spike-budget-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("HALL9K_HOME", Path.Combine(root, "home"));
        try
        {
            (_, string repoPath) = await CreateOriginAndCloneAsync(root, cts.Token);

            SeededSpike seed = await SeedClaimedSpikeAsync(
                store, repoPath, SpikeKind.Research, "The findings answer the stated question.",
                gates: [], sourceIdeaId: null, cts.Token);

            // Never scripted with a response: EndOverBudgetAsync must never dispatch a review or
            // fix session at all, so any SpawnAsync call here is itself the failure.
            ScriptedSpikeExecutor executor = new(new Dictionary<string, string>());
            FakeProcessManager processManager = new();
            GitWorktreeManager worktrees = new(NullLogger<GitWorktreeManager>.Instance);
            SpikeEngine spike = NewSpikeEngine(store, executor, processManager, worktrees, seed.Node);

            await spike.RecordFindingsAsync(seed.RunDirectory, "Findings so far: partial results only.", cts.Token);
            await spike.EndOverBudgetAsync(
                seed.RunId, seed.TaskId, "the build session's own wall-clock budget was crossed", cts.Token);

            await using IQuerySession query = store.QuerySession();
            TaskDetails task = (await query.LoadAsync<TaskDetails>(seed.TaskId, cts.Token))!;
            task.State.Should().Be(TaskState.Done, "a budget-ended spike closes out normally");
            task.SpikeVerdict.Should().Be(SpikeVerdict.BudgetExhausted);
            task.SpikeVerdictReason.Should().Be("the build session's own wall-clock budget was crossed");

            executor.SpawnedArtifactNames.Should().BeEmpty("a budget-ended spike never dispatches a review or fix session");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HALL9K_HOME", null);
            TemporaryTree.TryDelete(root);
        }
    }

    // -------------------------------------------------------------------------------------
    // d2. A wall-clock or live-token kill's own RunKilled already stands by the time
    // EndOverBudgetAsync's own FinalizeAsync runs — it must never append a second terminal
    // event (RunCompleted) over it.
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_budget_kill_s_own_terminal_run_event_is_never_overwritten_by_the_finalize_it_leads_to()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        string root = Path.Combine(Path.GetTempPath(), $"hall9k-spike-budget-kill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("HALL9K_HOME", Path.Combine(root, "home"));
        try
        {
            (_, string repoPath) = await CreateOriginAndCloneAsync(root, cts.Token);

            SeededSpike seed = await SeedClaimedSpikeAsync(
                store, repoPath, SpikeKind.Research, "The findings answer the stated question.",
                gates: [], sourceIdeaId: null, cts.Token);

            // Mirrors what EndRunsOverWallClockBudgetAsync/EndRunsOverTokenBudgetAsync's own
            // KillOverBudgetRunAsync already did to this run's stream before either ever calls
            // EndOverBudgetAsync: a terminal RunKilled stands ahead of the finalize below.
            await using (IDocumentSession killSession = store.LightweightSession())
            {
                killSession.Events.Append(
                    seed.RunId, new RunKilled(seed.RunId, KillReason.BudgetExceeded, KilledByOwnerId: null, Now));
                await killSession.SaveChangesAsync(cts.Token);
            }

            ScriptedSpikeExecutor executor = new(new Dictionary<string, string>());
            FakeProcessManager processManager = new();
            GitWorktreeManager worktrees = new(NullLogger<GitWorktreeManager>.Instance);
            SpikeEngine spike = NewSpikeEngine(store, executor, processManager, worktrees, seed.Node);

            await spike.RecordFindingsAsync(seed.RunDirectory, "Findings so far: partial results only.", cts.Token);
            await spike.EndOverBudgetAsync(
                seed.RunId, seed.TaskId, "the build session's own wall-clock budget was crossed", cts.Token);

            await using IQuerySession query = store.QuerySession();
            RunDetails run = (await query.LoadAsync<RunDetails>(seed.RunId, cts.Token))!;
            run.State.Should().Be(RunState.Killed, "the kill's own terminal record must stand, not flip to Completed");
            run.FailureReason.Should().Be(
                KillReason.BudgetExceeded, "a second terminal event over a killed run must never erase why it was killed");

            TaskDetails task = (await query.LoadAsync<TaskDetails>(seed.TaskId, cts.Token))!;
            task.State.Should().Be(TaskState.Done, "the task-level verdict still lands even though the run stream stays Killed");
            task.SpikeVerdict.Should().Be(SpikeVerdict.BudgetExhausted);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HALL9K_HOME", null);
            TemporaryTree.TryDelete(root);
        }
    }

    // -------------------------------------------------------------------------------------
    // d3. Live token-budget watch: a build session still running is killed and finalized the
    // moment its own transcript's summed spend crosses its stated token budget — the proactive
    // half EndOverBudgetAsync's own reactive check (above) exists to back up, not replace.
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task EndRunsOverTokenBudgetAsync_kills_a_live_session_whose_transcript_already_crossed_its_own_token_budget()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        string root = Path.Combine(Path.GetTempPath(), $"hall9k-spike-token-watch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("HALL9K_HOME", Path.Combine(root, "home"));
        try
        {
            (_, string repoPath) = await CreateOriginAndCloneAsync(root, cts.Token);

            SeededSpike seed = await SeedClaimedSpikeAsync(
                store, repoPath, SpikeKind.Research, "The findings answer the stated question.",
                gates: [], sourceIdeaId: null, cts.Token,
                constraints: new TaskConstraints(MaxTurns: null, MaxTokens: 100, MaxWallClock: null));

            int processId = 70_123;
            DateTimeOffset startedAt = Now;
            await using (IDocumentSession session = store.LightweightSession())
            {
                session.Events.Append(seed.RunId, new RunProcessStarted(seed.RunId, processId, startedAt));
                await session.SaveChangesAsync(cts.Token);
            }

            // The build session's own still-growing transcript: two intermediate turns whose
            // summed usage crosses the stated 100-token budget — never a terminal "result" line,
            // since the session this watch exists to catch is still running.
            string streamFile = RunPaths.StreamFile(seed.RunDirectory);
            await File.WriteAllTextAsync(
                streamFile,
                """{"type":"assistant","message":{"usage":{"input_tokens":60,"output_tokens":10}}}"""
                + "\n"
                + """{"type":"assistant","message":{"usage":{"input_tokens":40,"output_tokens":5}}}"""
                + "\n",
                cts.Token);

            FakeProcessManager processManager = new();
            // The process this run's own ActiveSession names is otherwise a made-up pid the fake
            // never spawned: TerminateTree's own contract reads an untracked pid as "already
            // gone" and returns without recording anything, the same as the real implementation
            // would for a pid that no longer exists — marking it alive here is what makes this a
            // live session for the fake to actually find and kill.
            processManager.MarkAlive(70_123);
            GitWorktreeManager worktrees = new(NullLogger<GitWorktreeManager>.Instance);
            ScriptedSpikeExecutor executor = new(new Dictionary<string, string>());
            SpikeEngine spike = NewSpikeEngine(store, executor, processManager, worktrees, seed.Node);

            await spike.EndRunsOverTokenBudgetAsync(cts.Token);

            processManager.TreeTerminations.Should().ContainSingle(t => t.ProcessId == processId,
                "the live session over its own token budget must be terminated, not left to run unbounded");

            await using IQuerySession query = store.QuerySession();
            RunDetails run = (await query.LoadAsync<RunDetails>(seed.RunId, cts.Token))!;
            run.State.Should().Be(RunState.Killed);
            run.FailureReason.Should().Be(KillReason.BudgetExceeded);

            TaskDetails task = (await query.LoadAsync<TaskDetails>(seed.TaskId, cts.Token))!;
            task.State.Should().Be(TaskState.Done, "a budget-ended spike closes out normally");
            task.SpikeVerdict.Should().Be(SpikeVerdict.BudgetExhausted);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HALL9K_HOME", null);
            TemporaryTree.TryDelete(root);
        }
    }

    // -------------------------------------------------------------------------------------
    // e. Not-met after the one fix lap: exactly one fix session, second review's verdict final.
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_spike_still_not_met_after_its_one_fix_lap_closes_done_not_met_with_the_second_reason()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        string root = Path.Combine(Path.GetTempPath(), $"hall9k-spike-notmet-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("HALL9K_HOME", Path.Combine(root, "home"));
        try
        {
            (_, string repoPath) = await CreateOriginAndCloneAsync(root, cts.Token);

            SeededSpike seed = await SeedClaimedSpikeAsync(
                store, repoPath, SpikeKind.Research, "The findings answer the stated question.",
                gates: [], sourceIdeaId: null, cts.Token);

            ScriptedSpikeExecutor executor = new(new Dictionary<string, string>
            {
                ["spike-review-1"] = VerdictText("not-met", "The findings do not yet answer the question."),
                ["spike-fix"] = "Findings, revised: still inconclusive after another pass.",
                ["spike-review-2"] = VerdictText("not-met", "Still inconclusive even after the fix lap."),
            });
            FakeProcessManager processManager = new();
            GitWorktreeManager worktrees = new(NullLogger<GitWorktreeManager>.Instance);
            SpikeEngine spike = NewSpikeEngine(store, executor, processManager, worktrees, seed.Node);

            await spike.RecordFindingsAsync(seed.RunDirectory, "Findings: inconclusive so far.", cts.Token);
            await spike.ReviewAsync(seed.RunId, seed.TaskId, cts.Token);

            await using IQuerySession query = store.QuerySession();
            TaskDetails task = (await query.LoadAsync<TaskDetails>(seed.TaskId, cts.Token))!;
            task.State.Should().Be(TaskState.Done, "a spike never parks for a human, whatever the verdict");
            task.SpikeVerdict.Should().Be(SpikeVerdict.NotMet);
            task.SpikeVerdictReason.Should().Be(
                "Still inconclusive even after the fix lap.", "the SECOND review's reason is what survives, not the first");

            executor.SpawnedArtifactNames.Count(name => name.StartsWith("spike-fix-", StringComparison.Ordinal))
                .Should().Be(1, "a spike gets at most one fix lap");
            executor.SpawnedArtifactNames.Count(name => name.StartsWith("spike-review-", StringComparison.Ordinal))
                .Should().Be(2, "exactly two review passes: before and after the one fix lap");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HALL9K_HOME", null);
            TemporaryTree.TryDelete(root);
        }
    }

    // -------------------------------------------------------------------------------------
    // f. From-idea provenance: IdeaSpikeConcluded lands, findings copy into the idea workspace,
    // journal.md is never touched.
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_spike_cut_from_an_idea_records_the_idea_side_event_and_copies_findings_into_its_workspace()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        string root = Path.Combine(Path.GetTempPath(), $"hall9k-spike-idea-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("HALL9K_HOME", Path.Combine(root, "home"));
        try
        {
            (_, string repoPath) = await CreateOriginAndCloneAsync(root, cts.Token);

            Guid ideaId = DomainId.New();
            Guid ownerId = DomainId.New();

            await using (IDocumentSession ideaSession = store.LightweightSession())
            {
                IdeaCaptured captured = IdeaDecider.Capture(
                    ideaId, ownerId, "Would a spike prove this approach out?", projectId: null, Now, ProjectHome.None);
                IdeaAggregate idea = new();
                idea.Apply(captured);
                IdeaTaskCut cut = IdeaDecider.CutTask(idea, DomainId.New(), "placeholder", Now, ownerId);
                ideaSession.Events.StartStream<IdeaAggregate>(ideaId, captured, cut);
                await ideaSession.SaveChangesAsync(cts.Token);
            }

            SeededSpike seed = await SeedClaimedSpikeAsync(
                store, repoPath, SpikeKind.Research, "The findings answer whether the approach holds.",
                gates: [], sourceIdeaId: ideaId, cts.Token);

            ScriptedSpikeExecutor executor = new(new Dictionary<string, string>
            {
                ["spike-review-1"] = VerdictText("met", "The findings confirm the approach holds."),
            });
            FakeProcessManager processManager = new();
            GitWorktreeManager worktrees = new(NullLogger<GitWorktreeManager>.Instance);
            SpikeEngine spike = NewSpikeEngine(store, executor, processManager, worktrees, seed.Node);

            const string findingsText = "Findings: the approach holds up under scrutiny.";
            await spike.RecordFindingsAsync(seed.RunDirectory, findingsText, cts.Token);
            await spike.ReviewAsync(seed.RunId, seed.TaskId, cts.Token);

            await using IQuerySession query = store.QuerySession();
            TaskDetails task = (await query.LoadAsync<TaskDetails>(seed.TaskId, cts.Token))!;
            task.State.Should().Be(TaskState.Done);
            task.SpikeVerdict.Should().Be(SpikeVerdict.Met);

            IReadOnlyList<object> ideaEvents = [.. (await query.Events.FetchStreamAsync(ideaId, token: cts.Token)).Select(e => e.Data)];
            ideaEvents.OfType<IdeaSpikeConcluded>().Should().ContainSingle().Which.TaskId.Should().Be(seed.TaskId);

            string ideaDirectory = IdeaPaths.GlobalDirectory(ideaId);
            string expectedFindingsCopy = Path.Combine(
                IdeaPaths.WorkspaceDirectory(ideaDirectory), "spikes", seed.TaskId.ToString(), "findings.md");
            File.Exists(expectedFindingsCopy).Should().BeTrue("the findings document is copied into the idea's own workspace");
            (await File.ReadAllTextAsync(expectedFindingsCopy, cts.Token)).Should().Be(findingsText);

            File.Exists(Path.Combine(ideaDirectory, "journal.md")).Should().BeFalse(
                "copying a spike's findings must never touch the idea's own journal.md");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HALL9K_HOME", null);
            TemporaryTree.TryDelete(root);
        }
    }

    // -------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------

    private static string VerdictText(string verdict, string reason) =>
        $"{AgentPromptBuilder.SpikeVerdictMarker} {verdict}\n{AgentPromptBuilder.SpikeReasonMarker} {reason}";

    private static VerificationRunner NewVerificationRunner(
        DocumentStore store, IExecutor executor, IProcessManager processManager, IWorktreeManager worktrees) =>
        new(store, Options.Create(new DaemonOptions()), NullLogger<VerificationRunner>.Instance, worktrees, executor, processManager);

    private static SpikeEngine NewSpikeEngine(
        DocumentStore store, IExecutor executor, IProcessManager processManager, IWorktreeManager worktrees, NodeContext node) =>
        new(store, executor, processManager, worktrees, node, Options.Create(new DaemonOptions()), NullLogger<SpikeEngine>.Instance);

    /// <summary>A local bare "origin" plus a working clone of it — never a real GitHub remote.</summary>
    private static async Task<(string OriginPath, string RepoPath)> CreateOriginAndCloneAsync(
        string root, CancellationToken cancellationToken)
    {
        string originPath = Path.Combine(root, "origin.git");
        string repoPath = Path.Combine(root, "repo");
        await TestGit.RunAsync(root, ["init", "--bare", "-q", "-b", "main", originPath], cancellationToken);
        await TestGit.RunAsync(root, ["clone", "-q", originPath, repoPath], cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repoPath, "README.md"), "# spike test repo\n", cancellationToken);
        await TestGit.RunAsync(repoPath, ["add", "-A"], cancellationToken);
        await TestGit.RunAsync(repoPath, TestGit.CommitAs("commit", "-qm", "init"), cancellationToken);
        await TestGit.RunAsync(repoPath, ["push", "-q", "origin", "main"], cancellationToken);
        return (originPath, repoPath);
    }

    private sealed record SeededSpike(
        NodeContext Node, Guid ProjectId, Guid TaskId, Guid RunId, string RunDirectory, string Branch, string WorktreePath);

    /// <summary>
    /// A Claimed spike task with a real <c>RunDispatched</c> pointing at a real worktree cut from
    /// <paramref name="repoPath"/> — the same shape <c>RunLauncher</c> would have left behind right
    /// before a spike's primary build session completes, so a test can drive
    /// <see cref="SpikeEngine"/> exactly the way <c>RunSupervisor.CompleteRunAsync</c> does.
    /// </summary>
    private static async Task<SeededSpike> SeedClaimedSpikeAsync(
        DocumentStore store, string repoPath, SpikeKind kind, string exitCriterion,
        IReadOnlyList<VerifyCommand> gates, Guid? sourceIdeaId, CancellationToken cancellationToken,
        TaskConstraints? constraints = null)
    {
        NodeContext node = await NodeBootstrapSeed.NewIsolatedNodeAsync(store, cancellationToken);
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();

        GitWorktreeManager seedingWorktrees = new(NullLogger<GitWorktreeManager>.Instance);
        Worktree worktree = await seedingWorktrees.CreateAsync(
            new WorktreeRequest(
                repoPath, "main", taskId, runId, "Spike whether the approach holds", BranchNameTemplate.Default,
                ExternalReference: null),
            cancellationToken);

        string runDirectory = RunPaths.GlobalDirectory(runId);
        Directory.CreateDirectory(runDirectory);

        await using IDocumentSession session = store.LightweightSession();

        ProjectAggregate project = new();
        ProjectRegistered registered = ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), $"spike-{taskId:N}", repoPath, null, "main", Now);
        project.Apply(registered);
        session.Events.StartStream<ProjectAggregate>(projectId, registered, ProjectDecider.ChangeSettings(
            project,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.Of(gates),
            skipPermissions: Optional<bool>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            Now, node.OwnerId));

        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(
                taskId, projectId, "Spike whether the approach holds", ["the exit criterion is judged"],
                TaskType.Spike, agentContext: null, constraints: constraints, externalReference: null,
                addedAt: Now, addedByOwnerId: node.OwnerId, spikeKind: kind, exitCriterion: exitCriterion,
                sourceIdeaId: sourceIdeaId),
            node.OwnerId, Now);
        TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now);
        task.Apply(claimed);
        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
        session.Store(new TaskLease
        {
            Id = taskId, NodeId = node.NodeId, LeaseGeneration = claimed.LeaseGeneration, HeartbeatAt = Now,
        });

        session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
            runId, taskId, node.NodeId, node.OwnerId, claimed.LeaseGeneration, DomainId.New(),
            worktree.Path, worktree.Branch, ExecutorMode.Subscription, Now, RunDirectory: runDirectory));

        await session.SaveChangesAsync(cancellationToken);

        return new SeededSpike(node, projectId, taskId, runId, runDirectory, worktree.Branch, worktree.Path);
    }

    /// <summary>
    /// Stands in for <c>claude</c>: writes a canned stream-json result line straight to the
    /// session's own stream file instead of spawning a real process, keyed off the ROLE prefix of
    /// <see cref="AgentSpawnRequest.SessionArtifactName"/> (<c>SpikeEngine</c> mints the exact
    /// artifact name itself, suffixed with a fresh session id it never predicts). Never marks the
    /// returned pid alive on the shared <see cref="FakeProcessManager"/>, so
    /// <c>SessionResultWaiter.WaitAsync</c> reads the process as already dead the instant a result
    /// is on disk and completes immediately — the identical trick <c>ScriptedExecutor</c> in
    /// <c>PrReviewTaskEngineTests</c> and <c>ScriptedResumeExecutor</c> in
    /// <c>RunSupervisorTests</c> both already rely on.
    /// </summary>
    private sealed class ScriptedSpikeExecutor(IReadOnlyDictionary<string, string> responsesByRolePrefix) : IExecutor
    {
        private int _nextProcessId = 60_000;

        public List<string> SpawnedArtifactNames { get; } = [];

        public async Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
        {
            string artifactName = request.SessionArtifactName ?? string.Empty;
            SpawnedArtifactNames.Add(artifactName);

            string? role = responsesByRolePrefix.Keys.FirstOrDefault(
                prefix => artifactName.StartsWith(prefix + "-", StringComparison.Ordinal));
            if (role is null)
            {
                throw new InvalidOperationException(
                    $"SpikeEngineTests.ScriptedSpikeExecutor: unscripted session artifact '{artifactName}'.");
            }

            string line = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "result",
                ["subtype"] = "success",
                ["is_error"] = false,
                ["usage"] = new Dictionary<string, long> { ["input_tokens"] = 100, ["output_tokens"] = 50 },
                ["result"] = responsesByRolePrefix[role],
            });

            Directory.CreateDirectory(request.RunDirectory);
            await File.WriteAllTextAsync(
                RunPaths.SessionStreamFile(request.RunDirectory, artifactName), line + "\n", cancellationToken);

            return new SpawnedAgent(_nextProcessId++, DateTimeOffset.UtcNow);
        }
    }
}
