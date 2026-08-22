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
/// seam scripted: a cycle runs every still-active track (log #59, #61), merge-ready proceeds
/// only when every track has concluded, needs-fixes drives one fix → gates → a fresh pass per
/// live track, a track that goes clean goes dormant while the other continues alone, a cap or
/// a dispute or a missing verdict parks for the human, and a dead session fails the run
/// honestly.
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
        run.ReviewCycle.Should().Be(1, "two tracks are one cycle, not two");
        run.LastReviewVerdict.Should().Be(ReviewVerdict.MergeReady);
        run.ReviewSettlement.Should().Be(
            ReviewSettlement.Clean, "both tracks read this exact tip and found nothing");
        run.ReviewResidualsFixed.Should().Be(0);
        run.ReviewResidualsRouted.Should().Be(0);
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
            "Criteria met.\n\nVERDICT: merge-ready");
        await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        executor.Spawns.Should().HaveCount(
            4, "two passes → one fix → the one track still active, since the adversarial track went dormant");
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
    /// A track that comes back clean goes dormant and the other continues alone (Decisions Log
    /// #61) — and it stays dormant through the other track's fix session, deliberately. The
    /// review history has to teach which track earns its keep, which it only can if every
    /// recorded pass says which lens produced it (log #59).
    /// </summary>
    [Fact]
    public async Task A_clean_track_goes_dormant_and_the_other_continues_alone()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "Criteria met, doctrine followed.\n\nVERDICT: merge-ready",
            "FINDING: severity=high; scope=in-scope; at=Spawner.cs:60\n"
            + "Defect: the child process is never reaped. Scenario: a failed run leaks a claude process.\n\n"
            + "VERDICT: needs-fixes",
            "Reaped the child on the failure path.\n\nRESOLUTION: fixed",
            "The lifetime holds now.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue("the adversarial track found it, the fix resolved it, and it then went clean");
        executor.Spawns.Should().HaveCount(
            4, "the conformance track concluded at cycle 1 and is never dispatched again");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];

        events.OfType<ReviewDispatched>().Select(e => e.Lens).Should().Equal(
            [ReviewLens.Conformance, ReviewLens.Adversarial, ReviewLens.Adversarial],
            "cycle 2 dispatches only the track that is still active");
        events.OfType<ReviewPassCompleted>().Select(e => (e.Cycle, e.Lens, e.Verdict)).Should().Equal(
        [
            (1, ReviewLens.Conformance, ReviewVerdict.MergeReady),
            (1, ReviewLens.Adversarial, ReviewVerdict.NeedsFixes),
            (2, ReviewLens.Adversarial, ReviewVerdict.MergeReady),
        ], "which track found the defect is a fact on the stream, not an impression");
        events.OfType<ReviewTrackConcluded>().Select(e => (e.Lens, e.Cycle, e.Settlement)).Should().Equal(
            [
                (ReviewLens.Conformance, 1, ReviewSettlement.Clean),
                (ReviewLens.Adversarial, 2, ReviewSettlement.Clean),
            ], "each track's own cycle count is where it ended, not where the run did");
        events.OfType<ReviewCompleted>().Select(e => (e.Cycle, e.Verdict)).Should().Equal(
            [(1, ReviewVerdict.NeedsFixes), (2, ReviewVerdict.MergeReady)],
            "the cycle's verdict is the merge of the tracks that were live for it");
    }

    /// <summary>
    /// Every finding's grade, scope tag, and disposition ride on the pass milestone (Decisions
    /// Log #61), so "which severities forced which cycles, on which track" is a query over the
    /// stream. The finding's own text stays an artifact (log #6).
    /// </summary>
    [Fact]
    public async Task Each_findings_severity_scope_and_disposition_land_on_the_pass_event()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "Criteria met.\n\nVERDICT: merge-ready",
            "FINDING: severity=high; scope=in-scope; at=Spawner.cs:60\nDefect: the child is never reaped.\n\n"
            + "FINDING: severity=low; scope=out-of-scope; at=Legacy.cs:12\nDefect: a stale comment misleads.\n\n"
            + "VERDICT: needs-fixes",
            "Reaped the child.\n\nRESOLUTION: fixed",
            "Clean now.\n\nVERDICT: merge-ready");
        await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];

        ReviewPassCompleted adversarial = events.OfType<ReviewPassCompleted>()
            .Single(pass => pass.Cycle == 1 && pass.Lens == ReviewLens.Adversarial);
        adversarial.Findings!.Select(f => (f.Severity, f.Scope, f.Location, f.Disposition)).Should().Equal(
        [
            (ReviewSeverity.High, ReviewFindingScope.InScope, "Spawner.cs:60", ReviewFindingDisposition.Fix),
            (ReviewSeverity.Low, ReviewFindingScope.OutOfScope, "Legacy.cs:12", ReviewFindingDisposition.Route),
        ]);
        events.OfType<ReviewPassCompleted>()
            .Single(pass => pass.Cycle == 1 && pass.Lens == ReviewLens.Conformance)
            .Findings.Should().BeEmpty("a clean pass records no findings, which is a different fact from none recorded");
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

    /// <summary>
    /// The severity gate (Decisions Log #61). Cycle 1 is ungated, so a medium forces cycle 2;
    /// cycle 2 is gated here, so its mediums are fixed and the loop ends without another review
    /// pass. That is deliberate, and the residual record is what keeps it honest: the verdict
    /// stays MergeReady, and the settlement says it was Settled rather than Clean.
    /// </summary>
    [Fact]
    public async Task Past_the_gate_a_medium_is_fixed_and_ships_without_another_review_pass()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "Criteria met.\n\nVERDICT: merge-ready",
            "FINDING: severity=medium; scope=in-scope; at=Auth.cs:42\nDefect: the limiter never resets.\n\n"
            + "VERDICT: needs-fixes",
            "Reset the limiter.\n\nRESOLUTION: fixed",
            "FINDING: severity=medium; scope=in-scope; at=Auth.cs:44\nDefect: the window is off by one.\n\n"
            + "FINDING: severity=low; scope=in-scope; at=Auth.cs:9\nDefect: the name reads badly.\n\n"
            + "VERDICT: needs-fixes",
            "Narrowed the window and renamed it.\n\nRESOLUTION: fixed");
        bool mergeReady = await NewEngine(
            store, executor, new DaemonOptions { AdversarialSeverityGateFromCycle = 2 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue("the gate ended the loop rather than parking a converging run");
        executor.Spawns.Should().HaveCount(
            5, "two passes, a fix, one more adversarial pass, and the terminal fix — no cycle 3");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.LastReviewVerdict.Should().Be(ReviewVerdict.MergeReady, "the terminal verdict is MergeReady either way");
        run.ReviewSettlement.Should().Be(
            ReviewSettlement.Settled, "no reviewer read the tip the terminal fix produced");
        run.ReviewResidualsFixed.Should().Be(2, "the medium and the low were fixed but never re-reviewed");
        run.ReviewResidualsRouted.Should().Be(0);

        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewSettled>().Should().ContainSingle().Which.Settlement
            .Should().Be(ReviewSettlement.Settled);
        events.FindLastIndex(recorded => recorded is VerificationPassed).Should().BeGreaterThan(
            events.FindIndex(recorded => recorded is ReviewFixCompleted fix && fix.Cycle == 2),
            "what a settled ending ships unreviewed is the reviewers' reading of the terminal fix, "
            + "never the build and the tests — the gates run over its commits before the pull request opens");
        events.FindIndex(recorded => recorded is ReviewSettled).Should().BeGreaterThan(
            events.FindLastIndex(recorded => recorded is VerificationPassed),
            "the loop settles only after those gates have passed");
        events.OfType<ReviewTrackConcluded>()
            .Single(track => track.Lens == ReviewLens.Adversarial)
            .Residuals.Select(residual => residual.Severity).Should().Equal(
                [ReviewSeverity.Medium, ReviewSeverity.Low]);
    }

    /// <summary>
    /// An out-of-scope non-High is not this pull request's work (Decisions Log #61): the daemon
    /// turns it into a draft bug task carrying the provenance the observation-gates doctrine
    /// asks for, and the merged findings tell the fix session to leave it alone.
    /// </summary>
    [Fact]
    public async Task An_out_of_scope_non_high_becomes_a_draft_bug_task_instead_of_a_fix()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "Criteria met.\n\nVERDICT: merge-ready",
            "FINDING: severity=high; scope=in-scope; at=Spawner.cs:60\nDefect: the child is never reaped.\n\n"
            + "FINDING: severity=medium; scope=out-of-scope; at=Legacy.cs:12\n"
            + "Defect: the retry duplicates the effect. Scenario: a transient failure charges twice.\n\n"
            + "VERDICT: needs-fixes",
            "Reaped the child.\n\nRESOLUTION: fixed",
            "Clean now.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        ReviewFindingRouted routed = events.OfType<ReviewFindingRouted>().Should().ContainSingle().Subject;
        routed.Severity.Should().Be(ReviewSeverity.Medium);
        routed.Location.Should().Be("Legacy.cs:12");
        routed.Lens.Should().Be(ReviewLens.Adversarial);
        routed.FailureReason.Should().BeNull();

        TaskDetails draft = (await query.LoadAsync<TaskDetails>(routed.DraftTaskId!.Value, cts.Token))!;
        draft.State.Should().Be(TaskState.Draft, "it is inert until a human publishes it");
        draft.Type.Should().Be(TaskType.Bugfix);
        draft.Objective.Should().Contain("Legacy.cs:12");
        draft.AgentContext.Should().Contain(taskId.ToString(), "the originating task is recorded, not implied")
            .And.Contain(runId.ToString())
            .And.Contain("Pull request: none", "no pull request existed yet, and the draft says so")
            .And.Contain("charges twice", "the reviewer's own words travel verbatim");

        string merged = File.ReadAllText(RunPaths.ReviewFindingsFile(runId, 1));
        merged.Should().Contain("routed to draft bug tasks").And.Contain(routed.DraftTaskId!.Value.ToString());
        executor.Spawns[2].Prompt.Should().Contain("Do NOT fix here",
            "the fix session is told which findings are not its work");

        // The high forced cycle two and both tracks ended on a reviewer that found nothing, so
        // every track concluded Clean. The ending is Settled all the same: this pull request
        // shipped with a known defect exported to a draft, and "clean" would say it did not.
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewSettlement.Should().Be(ReviewSettlement.Settled);
        run.ReviewResidualsRouted.Should().Be(1, "the routed medium is a residual of the cycle it was routed in");
        run.ReviewResidualsFixed.Should().Be(0, "the high was fixed and re-read clean, so it left nothing behind");
        events.OfType<ReviewTrackConcluded>().Should().OnlyContain(
            track => track.Settlement == ReviewSettlement.Clean);
    }

    /// <summary>
    /// The empty terminal case (Decisions Log #61): a cycle whose findings all route away leaves
    /// nothing anywhere to fix, so no fix session runs and the run settles. Re-reviewing would
    /// read the identical tip and return the identical findings, which is a loop with no exit
    /// rather than convergence — and here it is the run, not one track's convergence rule, that
    /// closes it: with no track owed a fix, the phase derives Settling whatever the tracks said.
    /// </summary>
    [Fact]
    public async Task A_cycle_whose_findings_all_route_away_ends_the_loop_with_no_fix_session()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "Criteria met.\n\nVERDICT: merge-ready",
            "FINDING: severity=medium; scope=out-of-scope; at=Legacy.cs:12\nDefect: pre-existing, and real.\n\n"
            + "FINDING: severity=low; scope=out-of-scope; at=Legacy.cs:31\nDefect: also pre-existing.\n\n"
            + "VERDICT: needs-fixes");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(2, "there was nothing in this branch to fix, so no fix session ran");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.LastReviewVerdict.Should().Be(ReviewVerdict.MergeReady);
        run.ReviewSettlement.Should().Be(ReviewSettlement.Settled);
        run.ReviewResidualsRouted.Should().Be(2);
        run.ReviewResidualsFixed.Should().Be(0);

        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewFixDispatched>().Should().BeEmpty();
        events.OfType<ReviewFindingRouted>().Should().HaveCount(2);
    }

    /// <summary>
    /// The same cycle with the other track still live is not the empty terminal case at all
    /// (Decisions Log #61): the conformance track forces a fix session that rewrites the branch,
    /// so the adversarial track has something new to read and stays alive to read it. Retiring
    /// it at cycle one over an out-of-scope medium would leave the fix commits reviewed by
    /// nobody — a dormant track is deliberately never reawakened — and the fix commits are
    /// where PR #21's two regressions came from. The acceptance criteria put the empty terminal
    /// case at cycle four onward for exactly this reason.
    /// </summary>
    [Fact]
    public async Task A_routing_only_cycle_keeps_the_track_alive_while_the_other_one_rewrites_the_branch()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "The third acceptance criterion is not met.\n\nVERDICT: needs-fixes",
            "FINDING: severity=medium; scope=out-of-scope; at=Legacy.cs:12\nDefect: pre-existing, and real.\n\n"
            + "VERDICT: needs-fixes",
            "Met the criterion; left the pre-existing one alone.\n\nRESOLUTION: fixed",
            "Criteria met now.\n\nVERDICT: merge-ready",
            "Read the fix commits too; nothing survived verification.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewDispatched>().Select(dispatched => (dispatched.Cycle, dispatched.Lens)).Should().Equal(
            [
                (1, ReviewLens.Conformance),
                (1, ReviewLens.Adversarial),
                (2, ReviewLens.Conformance),
                (2, ReviewLens.Adversarial),
            ], "the adversarial track had routed, not finished — it still owes the rewritten tip a reading");
        events.OfType<ReviewTrackConcluded>().Should().NotContain(
            concluded => concluded.Lens == ReviewLens.Adversarial && concluded.Cycle == 1,
            "a cycle that only routed is not a track's ending before the severity gate applies");
        events.OfType<ReviewFindingRouted>().Should().ContainSingle(
            "the pre-existing defect is exported once, however many cycles report it");

        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewSettlement.Should().Be(ReviewSettlement.Settled, "a routed defect leaves a residual behind");
        run.ReviewResidualsRouted.Should().Be(1);
    }

    /// <summary>
    /// A routed defect is deliberately left in the tree — the fix session is told to leave it
    /// alone — and every later reviewer has fresh context, so the same pre-existing line comes
    /// back for as long as anything else keeps the loop alive. It is exported once (Decisions
    /// Log #61): one draft, one routing event, one residual. Otherwise a single defect becomes
    /// a draft per cycle and "3 routed" on the line a human reads to decide how much to trust
    /// the pull request.
    /// </summary>
    [Fact]
    public async Task A_finding_that_survives_into_a_later_cycle_is_not_routed_a_second_time()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        const string preExisting = "FINDING: severity=medium; scope=out-of-scope; at=Legacy.cs:12\n"
            + "Defect: the retry duplicates the effect. Scenario: a transient failure charges twice.";
        ScriptedExecutor executor = new(
            "Criteria met.\n\nVERDICT: merge-ready",
            "FINDING: severity=high; scope=in-scope; at=Spawner.cs:60\nDefect: the child is never reaped.\n\n"
            + $"{preExisting}\n\nVERDICT: needs-fixes",
            "Reaped the child; left the pre-existing one alone.\n\nRESOLUTION: fixed",
            // Cycle two: the high is gone, and the reviewer reports the untouched legacy line again.
            $"{preExisting}\n\nVERDICT: needs-fixes");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewFindingRouted>().Should().ContainSingle(
            "the same defect at the same location was already exported in cycle one");

        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewResidualsRouted.Should().Be(1, "one exported defect is one residual, however often it is reported");

        string merged = File.ReadAllText(RunPaths.ReviewFindingsFile(runId, 2));
        merged.Should().Contain("already routed to a draft bug task by an earlier cycle of this run",
            "the fix session is still told the defect is not its work");
    }

    /// <summary>
    /// Every cycle's reviewer is a fresh session writing the location in its own hand, so the
    /// once-per-run check compares places rather than strings (Decisions Log #61): `Legacy.cs:12`
    /// and `./src/Legacy.cs:12` are one defect written twice, and matching them as strings would
    /// hand a human two inert drafts and a residual tally claiming two exported defects.
    /// </summary>
    [Fact]
    public async Task The_same_defect_written_a_different_way_is_still_routed_only_once()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "Criteria met.\n\nVERDICT: merge-ready",
            "FINDING: severity=high; scope=in-scope; at=Spawner.cs:60\nDefect: the child is never reaped.\n\n"
            + "FINDING: severity=medium; scope=out-of-scope; at=src/Legacy.cs:12\nDefect: the retry duplicates.\n\n"
            + "VERDICT: needs-fixes",
            "Reaped the child; left the pre-existing one alone.\n\nRESOLUTION: fixed",
            // Cycle two: the same untouched line, written the way this reviewer writes paths.
            "FINDING: severity=medium; scope=out-of-scope; at=./Legacy.cs:12\nDefect: the retry duplicates.\n\n"
            + "VERDICT: needs-fixes");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewFindingRouted>().Should().ContainSingle(
            "the second rendering of a place names the defect cycle one already exported");

        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewResidualsRouted.Should().Be(1);
    }

    /// <summary>
    /// An ungraded finding forces another adversarial cycle by design, so a reviewer whose
    /// grades never parsed can drive the track to its cap without a single stated High. The
    /// park reason says what it observed rather than asserting highs nobody recorded: telling
    /// the human "still returning high-severity findings" there steers them to restart correct
    /// work over a reviewer that was writing its findings wrong (never guess at unobserved facts).
    /// </summary>
    [Fact]
    public async Task An_adversarial_track_capped_on_ungraded_findings_says_the_grades_did_not_parse()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        // No FINDING header at all, so nothing carries a grade the platform can read.
        const string ungraded = "The spawner's failure path looks wrong to me.\n\nVERDICT: needs-fixes";
        ScriptedExecutor executor = new(
            "Criteria met.\n\nVERDICT: merge-ready",
            ungraded,
            "Adjusted the failure path.\n\nRESOLUTION: fixed",
            ungraded);
        bool mergeReady = await NewEngine(
            store, executor, new DaemonOptions { MaxAdversarialReviewCycles = 2 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse();

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked);
        run.ParkedReason.Should().Contain("no grade the platform could read")
            .And.Contain("none is graded high")
            .And.NotContain("still returning high-severity findings",
                "no finding on this run was ever graded high, and the park may not say one was");
    }

    /// <summary>
    /// The adversarial cap is not a spent budget (Decisions Log #61): reaching it means the
    /// machine kept finding real high-severity problems, and the park reason says exactly that
    /// so the human knows what they are being asked to look at.
    /// </summary>
    [Fact]
    public async Task An_adversarial_track_still_finding_highs_at_its_cap_parks_and_says_why()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        const string high = "FINDING: severity=high; scope=in-scope; at=Auth.cs:42\n"
            + "Defect: the token check can be bypassed.\n\nVERDICT: needs-fixes";
        ScriptedExecutor executor = new(
            "Criteria met.\n\nVERDICT: merge-ready",
            high,
            "Tightened the check.\n\nRESOLUTION: fixed",
            high);
        bool mergeReady = await NewEngine(
            store, executor, new DaemonOptions { MaxAdversarialReviewCycles = 2 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse();

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked);
        run.ParkedReason.Should().Contain("not a spent budget")
            .And.Contain("a human should look at why")
            .And.Contain("fresh agent", "restarting is offered as a resolution, never taken automatically");
    }

    /// <summary>
    /// The conformance track grades nothing, so its bound is simply how many times a machine
    /// may be told the same thing (Decisions Log #61). Still returning findings at its cap parks
    /// the run, and the reason says why: nothing automated is left to try.
    /// </summary>
    [Fact]
    public async Task A_conformance_track_still_finding_things_at_its_cap_parks_the_run()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "1. `A.cs:1` — the criterion is not met. Scenario: boom.\n\nVERDICT: needs-fixes",
            "Nothing of my own.\n\nVERDICT: merge-ready",
            "Tried.\n\nRESOLUTION: fixed",
            "2. `A.cs:1` — still not met. Scenario: boom.\n\nVERDICT: needs-fixes",
            "Tried again.\n\nRESOLUTION: fixed",
            "3. `A.cs:1` — still not met. Scenario: boom.\n\nVERDICT: needs-fixes");
        bool mergeReady = await NewEngine(
            store, executor, new DaemonOptions { MaxComplianceReviewCycles = 3 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse();
        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked);
        run.ReviewCycle.Should().Be(3, "the adversarial track went dormant at cycle 1 and never held the run up");
        run.ParkedReason.Should().Contain("conformance review is still returning findings")
            .And.Contain("nothing automated is left to try")
            .And.Contain(RunPaths.ReviewFindingsFile(runId, 3));

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
            "Criteria met.\n\nVERDICT: merge-ready");

        bool mergeReady = await NewEngine(store, executor, options).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Select(spawn => spawn.Model.Value).Should().Equal(
            ["sonnet", "sonnet", "haiku", "sonnet"], "each leg resolves the chain for its own role");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewDispatched>().Select(e => e.Model!.Value).Should().Equal(
            ["sonnet", "sonnet", "sonnet"], "every pass of every cycle records its model");
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

    private static ReviewEngine NewEngine(DocumentStore store, ScriptedExecutor executor) =>
        NewEngine(store, executor, new DaemonOptions());

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
