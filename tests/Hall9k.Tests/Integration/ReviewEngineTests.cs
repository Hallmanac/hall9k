using System.Text.Json;
using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

using Hall9k.Tests.Fakes;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The pre-PR review loop (Decisions Log #23) against a real store with the executor
/// seam scripted: every cycle runs both lenses (log #59), merge-ready proceeds only when
/// both are clean, needs-fixes drives one fix → gates → a fresh pair of passes, a spent
/// budget or a dispute or a missing verdict parks for the human, and a dead session fails
/// the run honestly.
/// </summary>
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class ReviewEngineTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly string _home = SetTempHome();

    private static string SetTempHome()
    {
        string home = Path.Combine(Path.GetTempPath(), $"hall9k-home-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("HALL9K_HOME", home);
        return home;
    }

    /// <summary>
    /// Scripted stand-in for claude sessions: each spawn writes the next scripted summary
    /// as a terminal result event (a null script entry spawns nothing and reports a
    /// process that never existed — the died-without-a-result path). Every spawn gets its
    /// own pid so a test can tell the cycle's two passes apart, and the process seam is
    /// faked so a terminating engine can never reach a real process.
    /// </summary>
    private sealed class ScriptedExecutor(params string?[] summaries) : IExecutor
    {
        private readonly Queue<string?> _summaries = new(summaries);
        private int _nextProcessId = 6_000;

        public List<AgentSpawnRequest> Spawns { get; } = [];

        /// <summary>The OS seam the engine shares with this executor: scripted sessions are alive, dead ones are not.</summary>
        public FakeProcessManager Processes { get; } = new();

        /// <summary>Lets a test mutate configuration between legs, the way a config edit mid-run would.</summary>
        public Action? OnFirstSpawn { get; set; }

        public async Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
        {
            if (Spawns.Count == 0)
            {
                OnFirstSpawn?.Invoke();
            }

            Spawns.Add(request);
            request.SessionArtifactName.Should().NotBeNull("review legs must never overwrite the main session's files");

            int processId = _nextProcessId++;
            string? summary = _summaries.Count > 0 ? _summaries.Dequeue() : null;
            if (summary is null)
            {
                return new SpawnedAgent(processId, Now);
            }

            string line = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "result",
                ["subtype"] = "success",
                ["is_error"] = false,
                ["usage"] = new Dictionary<string, long> { ["input_tokens"] = 1_000, ["output_tokens"] = 200 },
                ["total_cost_usd"] = 0.01,
                ["result"] = summary,
            });
            Directory.CreateDirectory(RunPaths.RunDirectory(request.RunId));
            await File.WriteAllTextAsync(
                RunPaths.SessionStreamFile(request.RunId, request.SessionArtifactName!),
                line + "\n", cancellationToken);

            Processes.MarkAlive(processId);
            return new SpawnedAgent(processId, Now);
        }
    }

    [Fact]
    public async Task Merge_ready_needs_both_lenses_clean_and_archives_each_lens_findings()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, Guid mainSessionId) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "Every acceptance criterion is met.\n\nVERDICT: merge-ready",
            "Hunted the trust boundaries and the lifetimes; nothing survived verification.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue("both lenses clean is what merge-ready means");
        executor.Spawns.Should().HaveCount(2, "one cycle is two independent passes");
        executor.Spawns.Select(spawn => spawn.SessionId).Should().OnlyHaveUniqueItems(
            "each pass is its own fresh session");
        executor.Spawns.Select(spawn => spawn.SessionId).Should().NotContain(
            mainSessionId, "no reviewer is the session that wrote the code");
        executor.Spawns.Select(spawn => spawn.SessionArtifactName).Should().OnlyHaveUniqueItems(
            "two passes in one cycle must not overwrite each other's transcripts");
        executor.Spawns[0].Prompt.Should().Contain("independent reviewer with fresh context");
        executor.Spawns[1].Prompt.Should().Contain("assume this diff is wrong somewhere");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.UnderReview, "the PR event, appended by the opener, is what moves the run on");
        run.ReviewCycle.Should().Be(1, "two lenses are one cycle, not two");
        run.LastReviewVerdict.Should().Be(ReviewVerdict.MergeReady);
        run.InputTokens.Should().Be(2_000, "both passes record tokens on the run — the cost is visible, not hidden");

        File.ReadAllText(RunPaths.ReviewLensFindingsFile(runId, 1, ReviewLens.Conformance.Slug))
            .Should().Contain("Every acceptance criterion is met");
        File.ReadAllText(RunPaths.ReviewLensFindingsFile(runId, 1, ReviewLens.Adversarial.Slug))
            .Should().Contain("Hunted the trust boundaries");

        string merged = File.ReadAllText(RunPaths.ReviewFindingsFile(runId, 1));
        merged.Should().Contain("Conformance lens").And.Contain("Adversarial lens",
            "the merged document says which lens produced what");
        merged.Should().Contain("VERDICT: merge-ready");
    }

    /// <summary>
    /// The lens that finds something carries the cycle: one NeedsFixes verdict, one merged
    /// finding list, one fix session for all of it (Decisions Log #59).
    /// </summary>
    [Fact]
    public async Task Either_lens_finding_defects_produces_one_verdict_and_one_fix_session_over_the_merged_findings()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        const string conformanceFinding = "1. `Auth.cs:42` — the limiter never resets. Scenario: the second request always 429s.";
        const string adversarialFinding = "1. `WorkItemContext.cs:18` — task text reaches the prompt unfenced. Scenario: a crafted objective redirects the agent.";
        ScriptedExecutor executor = new(
            $"{conformanceFinding}\n\nVERDICT: needs-fixes",
            $"{adversarialFinding}\n\nVERDICT: needs-fixes",
            "Reset the limiter window and fenced the task text.\n\nRESOLUTION: fixed",
            "Criteria met.\n\nVERDICT: merge-ready",
            "Hunted again; the boundary holds.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(5, "two passes → one fix → two passes");
        executor.Spawns[2].Prompt.Should().Contain(conformanceFinding).And.Contain(adversarialFinding,
            "one fix session addresses both lenses' findings");
        executor.Spawns[2].Prompt.Should().Contain("Conformance lens").And.Contain("Adversarial lens",
            "the fix session still sees which lens produced which finding");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewCycle.Should().Be(2);
        run.LastReviewVerdict.Should().Be(ReviewVerdict.MergeReady);

        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewCompleted>().Should().HaveCount(2, "one merged verdict per cycle, not one per lens");
        events.OfType<ReviewFixDispatched>().Should().HaveCount(1, "one fix session per cycle, however many lenses spoke");
        events.OfType<VerificationPassed>().Should().HaveCount(2, "gates re-ran after the fix");
    }

    /// <summary>
    /// The cycle's passes run at the same time in one worktree (Decisions Log #59), so
    /// everything a session writes has to be its own. Two things were shared before the second
    /// lens existed and are not any more: the settings file the child is handed at startup
    /// (one per run meant the second spawn truncating it under the first child's feet), and
    /// git's opportunistic index lock, which two concurrent readers cannot both take. The fix
    /// session runs alone and commits, so it keeps the environment it always had.
    /// </summary>
    [Fact]
    public async Task Concurrent_passes_share_no_settings_file_and_take_no_optional_git_locks()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "1. `Auth.cs:42` — the limiter never resets.\n\nVERDICT: needs-fixes",
            "Nothing survived verification.\n\nVERDICT: merge-ready",
            "Reset the limiter window.\n\nRESOLUTION: fixed",
            "Criteria met.\n\nVERDICT: merge-ready",
            "Hunted again; nothing.\n\nVERDICT: merge-ready");
        await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        executor.Spawns.Should().HaveCount(5, "two passes → one fix → two passes");
        List<AgentSpawnRequest> passes = [executor.Spawns[0], executor.Spawns[1]];

        passes.Select(SettingsArgument).Should().OnlyHaveUniqueItems(
            "a session that owns its settings file has no writer but itself");
        passes.Should().OnlyContain(
            pass => pass.Environment.ContainsKey("GIT_OPTIONAL_LOCKS") && pass.Environment["GIT_OPTIONAL_LOCKS"] == "0",
            "read-only git must not contend for .git/index.lock with the sibling pass");

        executor.Spawns[2].Environment.Should().BeEmpty(
            "the fix session runs alone and commits — it needs git's locks");
    }

    private static string SettingsArgument(AgentSpawnRequest request) =>
        ClaudeExecutor.Arguments(request).Single(
            argument => argument.StartsWith("--settings", StringComparison.Ordinal));

    /// <summary>
    /// The review history has to teach which lens earns its keep, which it only can if every
    /// recorded finding says which lens produced it (Decisions Log #59).
    /// </summary>
    [Fact]
    public async Task Every_recorded_pass_says_which_lens_reached_which_verdict()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "Criteria met, doctrine followed.\n\nVERDICT: merge-ready",
            "1. `Spawner.cs:60` — the child process is never reaped. Scenario: a failed run leaks a claude process.\n\nVERDICT: needs-fixes",
            "Reaped the child on the failure path.\n\nRESOLUTION: fixed",
            "Criteria still met.\n\nVERDICT: merge-ready",
            "The lifetime holds now.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue("the adversarial lens found it, the fix resolved it, both lenses then agreed");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];

        events.OfType<ReviewDispatched>().Select(e => e.Lens).Should().Equal(
            [ReviewLens.Conformance, ReviewLens.Adversarial, ReviewLens.Conformance, ReviewLens.Adversarial],
            "every dispatch records the lens it was spawned for");
        events.OfType<ReviewPassCompleted>().Select(e => (e.Cycle, e.Lens, e.Verdict)).Should().Equal(
        [
            (1, ReviewLens.Conformance, ReviewVerdict.MergeReady),
            (1, ReviewLens.Adversarial, ReviewVerdict.NeedsFixes),
            (2, ReviewLens.Conformance, ReviewVerdict.MergeReady),
            (2, ReviewLens.Adversarial, ReviewVerdict.MergeReady),
        ], "which lens found the defect is a fact on the stream, not an impression");
        events.OfType<ReviewCompleted>().Select(e => (e.Cycle, e.Verdict)).Should().Equal(
            [(1, ReviewVerdict.NeedsFixes), (2, ReviewVerdict.MergeReady)],
            "the cycle's verdict is the merge of its lenses");
    }

    /// <summary>
    /// A daemon that died between a cycle's two spawns comes back to a half-dispatched cycle.
    /// It tops the cycle up instead of concluding on the lens that happens to be recorded — a
    /// merge-ready reached by one lens would be the blind spot the second lens exists to close.
    /// </summary>
    [Fact]
    public async Task A_cycle_missing_a_lens_tops_itself_up_instead_of_concluding_on_one()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        Guid strandedSession = DomainId.New();
        await WriteScriptedResultAsync(
            runId, $"review-conformance-1-{strandedSession.ToString("N")[..8]}",
            "Criteria met.\n\nVERDICT: merge-ready", cts.Token);
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewDispatched(
                runId, strandedSession, 1, 5_100, Now, Now, AgentModel.Sonnet, ReviewLens.Conformance));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor executor = new("Nothing survived verification.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().ContainSingle("only the missing lens is dispatched — the surviving pass is adopted");
        executor.Spawns[0].Prompt.Should().Contain("assume this diff is wrong somewhere");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewCompleted>().Should().ContainSingle(
            "the cycle concluded once, after both lenses answered");
        events.OfType<ReviewPassCompleted>().Select(e => e.Lens).Should().Equal(
            [ReviewLens.Conformance, ReviewLens.Adversarial]);
    }

    [Fact]
    public async Task A_spent_fix_budget_parks_the_run_with_the_findings_attached()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "1. `A.cs:1` — broken. Scenario: boom.\n\nVERDICT: needs-fixes",
            "Nothing beyond that.\n\nVERDICT: merge-ready",
            "Tried.\n\nRESOLUTION: fixed",
            "2. `A.cs:1` — still broken. Scenario: boom.\n\nVERDICT: needs-fixes",
            "Still nothing of my own.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor, maxFixRuns: 1).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse();
        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked);
        run.ParkedReason.Should().Contain("budget is spent").And.Contain(RunPaths.ReviewFindingsFile(runId, 2));

        (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Should().Be(
            TaskState.Claimed, "parking is a waiting state — the task is not failed");
        (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().NotBeNull(
            "the lease is retained so the worktree stays the human's workspace");
    }

    [Fact]
    public async Task A_disputed_finding_parks_with_both_positions_recorded()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "1. `Api.cs:7` — envelope type differs from spec. Scenario: clients break.\n\nVERDICT: needs-fixes",
            "No defects of my own.\n\nVERDICT: merge-ready",
            "That envelope change is the task's stated design; changing it back is a scope decision.\n\nRESOLUTION: disputed");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse();
        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked);
        run.ParkedReason.Should().Contain(RunPaths.ReviewFindingsFile(runId, 1), "the review position is attached")
            .And.Contain(RunPaths.ReviewFixPositionFile(runId, 1), "and so is the fix run's position");

        File.ReadAllText(RunPaths.ReviewFixPositionFile(runId, 1)).Should().Contain("scope decision");
        (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Should().Be(TaskState.Claimed);
    }

    [Fact]
    public async Task A_verdict_less_pass_is_reprompted_once_in_the_same_session_and_may_still_conclude()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        // The origin incident's shape: a promise of a future verdict instead of one.
        ScriptedExecutor executor = new(
            "Checks are still running; I'll deliver findings and the verdict when it completes.",
            "Hunted; nothing stands.\n\nVERDICT: merge-ready",
            "The checks finished clean.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue("the resumed session concluded properly and the other lens was already clean");
        executor.Spawns.Should().HaveCount(3, "two passes, one re-prompt — never more");
        executor.Spawns[2].ResumeSessionId.Should().Be(
            executor.Spawns[0].SessionId, "the re-prompt resumes the pass that already read the diff");
        executor.Spawns[2].SessionId.Should().NotBe(
            executor.Spawns[0].SessionId, "the resumed leg's artifacts must not collide with the original's");
        executor.Spawns[2].Prompt.Should().Contain("without the required VERDICT line");
    }

    /// <summary>
    /// The re-prompt budget belongs to the cycle, not to each lens: two verdict-less passes
    /// still get one re-prompt between them, and then the run parks (Decisions Log #59 — two
    /// lenses must not double the parking math).
    /// </summary>
    [Fact]
    public async Task Two_verdict_less_passes_share_the_cycles_single_reprompt_before_parking()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "Looks good to me, probably.",
            "Still poking at it.",
            "I'll get back to you.");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("an unstated verdict is never treated as merge-ready");
        executor.Spawns.Should().HaveCount(
            3, "two passes and exactly one re-prompt — the cycle's, not one per lens");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked);
        run.ParkedReason.Should().Contain("no parseable verdict").And.Contain("re-prompt");

        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewVerdictReprompted>().Should().ContainSingle();
    }

    [Fact]
    public async Task A_park_resolved_merge_ready_proceeds_straight_to_the_pull_request()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);
        await SeedParkedReviewAsync(store, runId, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.MergeReady, null, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor executor = new();
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue("the human's verdict stands in for both lenses'");
        executor.Spawns.Should().BeEmpty("no further session second-guesses the human");
    }

    [Fact]
    public async Task A_park_resolved_needs_fixes_dispatches_a_fix_session_over_the_human_findings()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);
        await SeedParkedReviewAsync(store, runId, cts.Token);

        const string humanFindings = "The limiter reset finding is real; fix it as the reviewer described.";
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.NeedsFixes, humanFindings, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor executor = new(
            "Fixed as instructed.\n\nRESOLUTION: fixed",
            "Criteria met.\n\nVERDICT: merge-ready",
            "Nothing stands.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(3, "fix over the human findings, then a fresh pass per lens");
        executor.Spawns[0].Prompt.Should().Contain(humanFindings, "the human's reason is the fix session's findings")
            .And.Contain("Human review verdict");
    }

    /// <summary>
    /// Review and fix are separate roles with separate knobs (Decisions Log #33), and each
    /// leg records what it actually ran on, because the record is what makes spend-by-model a
    /// query rather than a guess. Both lenses are review work, so both resolve the Review
    /// role — and both dispatches record the model, per pass (log #59).
    /// </summary>
    [Fact]
    public async Task Every_review_pass_and_the_fix_session_resolve_their_role_model_and_record_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        DaemonOptions options = new()
        {
            DefaultModel = "claude-opus-5",
            ModelByRole = new RoleModelDefaults { Review = "sonnet", Fix = "haiku" },
        };
        ScriptedExecutor executor = new(
            "1. `Auth.cs:42`: limiter never resets.\n\nVERDICT: needs-fixes",
            "Nothing of my own.\n\nVERDICT: merge-ready",
            "Reset the limiter.\n\nRESOLUTION: fixed",
            "Criteria met.\n\nVERDICT: merge-ready",
            "The fix holds.\n\nVERDICT: merge-ready");

        bool mergeReady = await NewEngine(store, executor, options).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Select(spawn => spawn.Model.Value).Should().Equal(
            ["sonnet", "sonnet", "haiku", "sonnet", "sonnet"], "each leg resolves the chain for its own role");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewDispatched>().Select(e => e.Model!.Value).Should().Equal(
            ["sonnet", "sonnet", "sonnet", "sonnet"], "both passes of both cycles record their model");
        events.OfType<ReviewFixDispatched>().Select(e => e.Model!.Value).Should().Equal(["haiku"]);

        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewModel.Should().Be(AgentModel.Sonnet, "the projection shows the latest review leg's model");
    }

    /// <summary>
    /// A resumed session keeps the model it started with, so the re-prompt records that
    /// model rather than re-resolving the chain, which is visible here because the role
    /// default changes between the legs, exactly as a config edit mid-run would. The pass
    /// dispatched after the edit honestly records the new model; the resumed one does not.
    /// </summary>
    [Fact]
    public async Task A_verdict_reprompt_records_the_resumed_sessions_model_instead_of_re_resolving()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        DaemonOptions options = new()
        {
            DefaultModel = "claude-opus-5",
            ModelByRole = new RoleModelDefaults { Review = "sonnet" },
        };
        ScriptedExecutor executor = new(
            "Checks are still running; I'll deliver the verdict when it completes.",
            "Nothing stands.\n\nVERDICT: merge-ready",
            "The checks finished clean.\n\nVERDICT: merge-ready")
        {
            // The conformance pass is dispatched on sonnet, then the node's role default
            // changes. The resumed leg must still be recorded as sonnet: that is the session
            // actually running, and recording anything else would be a guess.
            OnFirstSpawn = () => options.ModelByRole.Review = "haiku",
        };

        bool mergeReady = await NewEngine(store, executor, options).ReviewAsync(runId, taskId, cts.Token);
        mergeReady.Should().BeTrue();

        executor.Spawns[1].Model.Should().Be(
            AgentModel.Haiku, "the second lens was dispatched after the edit and records what it got");
        executor.Spawns[2].ResumeSessionId.Should().Be(executor.Spawns[0].SessionId);
        executor.Spawns[2].Model.Should().Be(
            AgentModel.Sonnet, "the resumed session keeps the model it started with");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewVerdictReprompted>().Single().Model!.Value.Should().Be("sonnet");
        events.OfType<ReviewVerdictReprompted>().Single().Lens.Should().Be(
            ReviewLens.Conformance, "the re-prompt records which lens went quiet");
    }

    [Fact]
    public async Task A_review_session_dying_without_a_result_fails_the_run_and_takes_its_sibling_pass_down()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        // The conformance pass dies without a result; the adversarial pass is still reading.
        ScriptedExecutor executor = new(null, "Still hunting.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse();
        executor.Processes.Terminations.Should().ContainSingle(
            "the surviving pass is reading a diff nobody will act on, so it goes down with the run");
        executor.Processes.Terminations.Single().ProcessId.Should().Be(6_001, "the sibling pass, not the dead one");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.Failed);
        run.FailureReason.Should().Contain("died without a result").And.Contain(
            ReviewLens.Conformance.Slug, "the failure says which pass went silent");
        (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Should().Be(TaskState.Failed);
        (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().BeNull("failure releases the lease");
    }

    /// <summary>
    /// The crash sweep covers whichever session the loop was holding, not review passes
    /// alone: a crash lands just as easily in the fix phase, and a fix agent left editing a
    /// worktree whose result nobody will read is the same leak the sweep exists to prevent.
    /// The crash is induced the way a real one would arrive — an artifact write that fails,
    /// here because the fix-position path is already a directory.
    /// </summary>
    [Fact]
    public async Task A_crash_while_the_fix_session_is_in_flight_terminates_it_too()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        // Recording the fix outcome writes this file first, so the loop throws while the
        // stream still shows the fix session in flight and its completion unrecorded.
        Directory.CreateDirectory(RunPaths.ReviewFixPositionFile(runId, 1));

        ScriptedExecutor executor = new(
            "Criteria met.\n\nVERDICT: merge-ready",
            "1. `Spawner.cs:60` — the child is never reaped. Scenario: a failed run leaks a process.\n\nVERDICT: needs-fixes",
            "Reaped the child on the failure path.\n\nRESOLUTION: fixed");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse();
        executor.Processes.Terminations.Should().ContainSingle(
            "both passes had already concluded, so the fix session is all that is still in flight");
        executor.Processes.Terminations.Single().ProcessId.Should().Be(
            6_002, "the fix session, dispatched after the cycle's two passes");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.Failed);
        run.FailureReason.Should().Contain("Review loop failed", "the crash is reported as itself, not as a verdict");
    }

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    private static ReviewEngine NewEngine(DocumentStore store, ScriptedExecutor executor, int maxFixRuns = 2) =>
        NewEngine(store, executor, new DaemonOptions { MaxAutomaticReviewFixRuns = maxFixRuns });

    private static ReviewEngine NewEngine(DocumentStore store, ScriptedExecutor executor, DaemonOptions options) =>
        new(store, executor, executor.Processes,
            new VerificationRunner(store, Options.Create(new DaemonOptions()), NullLogger<VerificationRunner>.Instance),
            Options.Create(options),
            NullLogger<ReviewEngine>.Instance);

    /// <summary>Writes a terminal result for a session this test seeded rather than spawned.</summary>
    private static async Task WriteScriptedResultAsync(
        Guid runId, string artifactName, string summary, CancellationToken cancellationToken)
    {
        string line = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "result",
            ["subtype"] = "success",
            ["is_error"] = false,
            ["usage"] = new Dictionary<string, long> { ["input_tokens"] = 1_000, ["output_tokens"] = 200 },
            ["result"] = summary,
        });
        Directory.CreateDirectory(RunPaths.RunDirectory(runId));
        await File.WriteAllTextAsync(
            RunPaths.SessionStreamFile(runId, artifactName), line + "\n", cancellationToken);
    }

    /// <summary>
    /// A run that just passed its gates: task claimed with a lease, project registered
    /// (no verify commands, so re-verification auto-passes), run stream ending in
    /// VerificationPassed — exactly where the review loop takes over.
    /// </summary>
    private async Task<(Guid TaskId, Guid RunId, Guid MainSessionId)> SeedVerifiedRunAsync(
        DocumentStore store, CancellationToken cancellationToken)
    {
        NodeContext node = new();
        await node.InitializeAsync(store, cancellationToken);

        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid mainSessionId = DomainId.New();
        string worktreePath = Path.Combine(_home, $"wt-{runId:N}");
        Directory.CreateDirectory(worktreePath);

        await using IDocumentSession session = store.LightweightSession();

        var registered = Hall9k.Domain.Features.Project.Handlers.ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), $"review-{taskId:N}", worktreePath, null, "main", Now);
        session.Events.StartStream<Hall9k.Domain.Features.Project.ProjectAggregate>(registered.Id, registered);

        TaskAggregate task = new();
        (task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(taskId, projectId, "Review me before the PR", ["reviewed"],
                TaskType.Chore, null, null, null, Now, node.OwnerId),
            node.OwnerId, Now);
        var claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now);
        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
        session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = Now });

        session.Events.StartStream<RunAggregate>(runId,
            new RunDispatched(runId, taskId, node.NodeId, node.OwnerId, 1, mainSessionId,
                worktreePath, "task/review-me", ExecutorMode.Subscription, Now),
            new AgentSessionCompleted(runId, Now),
            new VerificationPassed(runId, Now));
        await session.SaveChangesAsync(cancellationToken);

        return (taskId, runId, mainSessionId);
    }

    /// <summary>
    /// Extends a seeded run to a review-parked stream: one review cycle whose passes ended
    /// verdict-less and parked — exactly what h9k review resolve acts on.
    /// </summary>
    private static async Task SeedParkedReviewAsync(DocumentStore store, Guid runId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId,
            new ReviewDispatched(runId, DomainId.New(), 1, 5_001, Now, Now, null, ReviewLens.Conformance),
            new ReviewDispatched(runId, DomainId.New(), 1, 5_002, Now, Now, null, ReviewLens.Adversarial),
            new ReviewPassCompleted(runId, 1, ReviewLens.Conformance, ReviewVerdict.Unknown, Now),
            new ReviewPassCompleted(runId, 1, ReviewLens.Adversarial, ReviewVerdict.Unknown, Now),
            new ReviewCompleted(runId, 1, ReviewVerdict.Unknown, Now),
            new ReviewParked(runId, "No parseable verdict, even after a re-prompt.", Now));
        await session.SaveChangesAsync(cancellationToken);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", null);
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
