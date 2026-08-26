using System.Text.Json;
using System.Text.RegularExpressions;
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
/// seam scripted: a cycle runs every still-active track (log #59, #63), merge-ready proceeds
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
            Directory.CreateDirectory(request.RunDirectory);
            await File.WriteAllTextAsync(
                RunPaths.SessionStreamFile(request.RunDirectory, request.SessionArtifactName!),
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

        File.ReadAllText(RunPaths.ReviewLensFindingsFile(RunPaths.GlobalDirectory(runId), 1, ReviewLens.Conformance.Slug))
            .Should().Contain("Every acceptance criterion is met");
        File.ReadAllText(RunPaths.ReviewLensFindingsFile(RunPaths.GlobalDirectory(runId), 1, ReviewLens.Adversarial.Slug))
            .Should().Contain("Hunted the trust boundaries");

        string merged = File.ReadAllText(RunPaths.ReviewFindingsFile(RunPaths.GlobalDirectory(runId), 1));
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
    /// #63) — and it stays dormant through the other track's fix session, deliberately. The
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
    /// Log #63), so "which severities forced which cycles, on which track" is a query over the
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
    /// A routing that failed is deliberately offered again next cycle, so one defect can leave
    /// two records on the stream: the attempt that created no draft, and the retry that did.
    /// The settlement reports defects rather than records — "1 routed, 1 not routed" about a
    /// single exported defect sends a human looking for one that lives nowhere but this stream.
    /// </summary>
    [Fact]
    public async Task A_routing_that_failed_and_later_succeeded_settles_as_one_routed_defect()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        const string preExisting = "FINDING: severity=medium; scope=out-of-scope; at=Legacy.cs:12\n"
            + "Defect: the retry duplicates the effect. Scenario: a transient failure charges twice.";
        await using (IDocumentSession session = store.LightweightSession())
        {
            // An adopted run whose first cycle reported this line and could not write its draft
            // bug task: the store was unreachable, so the finding is recorded as routed-and-failed
            // and no draft exists for it.
            session.Events.Append(runId,
                new ReviewPassCompleted(runId, 1, ReviewLens.Conformance, ReviewVerdict.MergeReady, Now, []),
                new ReviewPassCompleted(runId, 1, ReviewLens.Adversarial, ReviewVerdict.NeedsFixes, Now,
                [
                    new ReviewFindingRecord(ReviewSeverity.Medium, ReviewFindingScope.OutOfScope,
                        "Legacy.cs:12", ReviewFindingDisposition.Route),
                ]),
                new ReviewFindingRouted(runId, ReviewLens.Adversarial, 1, ReviewSeverity.Medium,
                    "Legacy.cs:12", null, "the draft bug task could not be stored", Now),
                new ReviewCompleted(runId, 1, ReviewVerdict.NeedsFixes, Now),
                new ReviewTrackConcluded(runId, ReviewLens.Conformance, 1, ReviewSettlement.Clean, [], Now),
                new ReviewFixDispatched(runId, DomainId.New(), 1, 5_300, Now, Now, AgentModel.Sonnet),
                new ReviewFixCompleted(runId, 1, ReviewFixOutcome.Fixed, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor executor = new(
            // The next cycle's reviewer reports the untouched legacy line again, and this time
            // the routing succeeds — the retry the failed disposition exists to allow.
            $"{preExisting}\n\nVERDICT: needs-fixes",
            "Nothing new survived verification.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewFindingRouted>().Should().HaveCount(2,
            "both attempts happened, and the stream records what happened rather than what it wishes had");
        events.OfType<ReviewFindingRouted>().Last().DraftTaskId.Should().NotBeNull("the retry created the draft");

        ReviewSettled settled = events.OfType<ReviewSettled>().Should().ContainSingle().Subject;
        settled.ResidualsRouted.Should().Be(1, "one defect was exported, on the second attempt");
        settled.ResidualsRoutingFailed.Should().Be(0,
            "the draft the first attempt could not write exists, so nothing survives only in this stream");
    }

    /// <summary>
    /// A pre-lens run adopted mid-review: its one lens-less pass covers the conformance track,
    /// and the cycle is topped up with an adversarial one. The lens-less pass must keep reading
    /// back its OWN findings, so it files them under a name of its own rather than the merged
    /// document's — the merge overwrites that file, and a cycle recorded twice (here: the one
    /// verdict re-prompt) would otherwise hand the lens-less track the other lens's findings.
    /// Borrowed findings are not a cosmetic mix-up: they suppress the "something must be fixed"
    /// placeholder a needs-fixes verdict implies, which settles a track that did find something.
    /// </summary>
    [Fact]
    public async Task A_lens_less_pass_reads_back_its_own_findings_and_not_the_merged_document()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        Guid preLensSession = DomainId.New();
        const string prose = "The second acceptance criterion is not met: `Observer.cs` records nothing.";
        await WriteScriptedResultAsync(
            runId, $"review-1-{preLensSession.ToString("N")[..8]}",
            $"{prose}\n\nVERDICT: needs-fixes", cts.Token);
        await using (IDocumentSession session = store.LightweightSession())
        {
            // A dispatch from before lenses existed: no lens on the event, so the pass is
            // Unknown and covers conformance without claiming to have been told it was that.
            session.Events.Append(runId, new ReviewDispatched(
                runId, preLensSession, 1, 5_200, Now, Now, AgentModel.Sonnet, null));
            await session.SaveChangesAsync(cts.Token);
        }

        const string preExisting = "FINDING: severity=medium; scope=out-of-scope; at=Legacy.cs:12\n"
            + "Defect: the retry duplicates the effect. Scenario: a transient failure charges twice.";
        ScriptedExecutor executor = new(
            // The topped-up adversarial pass ends without a verdict, so the cycle concludes
            // nothing, writes its merged document, and spends its one re-prompt.
            preExisting,
            $"{preExisting}\n\nVERDICT: needs-fixes",
            "Left the pre-existing one alone; recorded the observation.\n\nRESOLUTION: fixed",
            "Every acceptance criterion is met now.\n\nVERDICT: merge-ready",
            "Nothing survived verification.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();

        File.ReadAllText(RunPaths.ReviewLensFindingsFile(RunPaths.GlobalDirectory(runId), 1, "unlensed"))
            .Should().Contain(prose).And.NotContain("FINDING:",
                "the merge writes its own file, so the lens-less pass still has its own words");
        string merged = File.ReadAllText(RunPaths.ReviewFindingsFile(RunPaths.GlobalDirectory(runId), 1));
        merged.Should().Contain(prose, "the merged document quotes the lens-less pass");
        Regex.Matches(merged, "^# Independent pre-PR review", RegexOptions.Multiline)
            .Should().ContainSingle("a cycle recorded twice re-derives the merge rather than nesting its previous self");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        List<ReviewTrackConcluded> concluded = [.. events.OfType<ReviewTrackConcluded>()];
        concluded.Should().NotContain(track => track.Lens == ReviewLens.Unknown,
            "the lens-less track read its own needs-fixes rather than the other lens's routed medium, "
            + "so cycle one retired nobody");
        concluded.Should().Contain(track =>
            track.Lens == ReviewLens.Conformance && track.Cycle == 2 && track.Settlement == ReviewSettlement.Clean,
            "it ends where a reviewer read the tip the fix produced");
    }

    /// <summary>
    /// The severity gate (Decisions Log #63). Cycle 1 is ungated, so a medium forces cycle 2;
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
    /// The core of Decisions Log #87: a needs-fixes verdict whose only finding is graded low is
    /// downgraded to merge-ready before it ever costs a fix session — the platform's own
    /// safety net behind the prompt instruction telling the lens to do this itself. Both lenses
    /// answer needs-fixes here so the demotion is exercised on conformance too, the population
    /// the origin telemetry actually named.
    /// </summary>
    [Fact]
    public async Task A_pass_whose_only_finding_is_graded_low_is_demoted_to_merge_ready_with_a_ride_along_and_no_fix_session()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "FINDING: severity=low; scope=in-scope; at=Docs.md:3\nDefect: the comment is stale.\n\n"
            + "VERDICT: needs-fixes",
            "Criteria met.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(2, "only the two review passes — no fix session was ever owed one");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewFixDispatched>().Should().BeEmpty(
            "a low-only verdict earns no fix-and-re-review cycle of its own (Decisions Log #87)");
        ReviewPassCompleted conformancePass = events.OfType<ReviewPassCompleted>()
            .Single(pass => pass.Lens == ReviewLens.Conformance);
        conformancePass.Verdict.Should().Be(
            ReviewVerdict.MergeReady, "the platform's own bar overrides the lens's literal VERDICT line");
        conformancePass.Findings.Should().ContainSingle()
            .Which.Disposition.Should().Be(ReviewFindingDisposition.RideAlong);

        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.LastReviewVerdict.Should().Be(ReviewVerdict.MergeReady);
        run.ReviewSettlement.Should().Be(ReviewSettlement.Settled, "the low finding is a residual, not a clean tip");
        run.ReviewResidualsRideAlong.Should().Be(1, "recorded, never fixed — no cycle was ever spent earning it one");
    }

    /// <summary>
    /// An out-of-scope non-High is not this pull request's work (Decisions Log #63): the daemon
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

        string merged = File.ReadAllText(RunPaths.ReviewFindingsFile(RunPaths.GlobalDirectory(runId), 1));
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
    /// The empty terminal case (Decisions Log #63): a cycle whose findings all route away leaves
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
        // The adversarial track was still saying "continue" when the run settled out from under
        // it, and its ending is recorded all the same: a lens with no per-track record cannot be
        // told from one still running, which is the question the record exists to answer.
        events.OfType<ReviewTrackConcluded>().Select(track => (track.Lens, track.Cycle, track.Settlement))
            .Should().BeEquivalentTo(
                [
                    (ReviewLens.Conformance, 1, ReviewSettlement.Clean),
                    (ReviewLens.Adversarial, 1, ReviewSettlement.Settled),
                ], "every track has an ending by the time the run is merge-ready");
    }

    /// <summary>
    /// The same cycle with the other track still live is not the empty terminal case at all
    /// (Decisions Log #63): the conformance track forces a fix session that rewrites the branch,
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
            "`Program.cs` — the third acceptance criterion is not met.\n\nVERDICT: needs-fixes",
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
    /// Log #63): one draft, one routing event, one residual. Otherwise a single defect becomes
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

        string merged = File.ReadAllText(RunPaths.ReviewFindingsFile(RunPaths.GlobalDirectory(runId), 2));
        merged.Should().Contain("already routed to a draft bug task by cycle 1 of this run",
            "the fix session is still told the defect is not its work, and by which cycle it was observed to leave");
    }

    /// <summary>
    /// Every cycle's reviewer is a fresh session writing the location in its own hand, so the
    /// once-per-run check compares places rather than strings (Decisions Log #63): `Legacy.cs:12`
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
    /// The once-per-run check compares places, and a file with no line on it is not a place
    /// (Decisions Log #63). Two different pre-existing defects in one legacy file, neither of
    /// which the reviewer put a line number on, are two defects: reading them as one would route
    /// the first, tell the fix session to leave the second alone as somebody else's work, and
    /// leave that second defect recorded nowhere but the cycle's artifact file. The duplicate
    /// draft the other reading risks is inert and a human discards it in a moment; a defect
    /// routed away from the pull request and written down nowhere is gone for good.
    /// </summary>
    [Fact]
    public async Task Two_defects_in_one_file_that_neither_names_a_line_are_two_defects()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "Criteria met.\n\nVERDICT: merge-ready",
            "FINDING: severity=medium; scope=out-of-scope; at=src/Legacy.cs\n"
            + "Defect: the retry duplicates the effect. Scenario: a transient failure charges twice.\n\n"
            + "FINDING: severity=low; scope=out-of-scope; at=src/Legacy.cs\n"
            + "Defect: the log line prints the token. Scenario: a support bundle carries a live credential.\n\n"
            + "VERDICT: needs-fixes");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        List<ReviewFindingRouted> routed = [.. events.OfType<ReviewFindingRouted>()];
        routed.Should().HaveCount(2, "two defects the reviewer never placed on a line are two defects");
        routed.Should().OnlyContain(entry => entry.DraftTaskId != null, "each one is its own draft bug task");

        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewResidualsRouted.Should().Be(2, "the human is told how many defects left this pull request");
    }

    /// <summary>
    /// Both tracks read the same tip, so both report the same pre-existing line in the cycle
    /// they share. That is agreement rather than two defects, so it is exported once — and the
    /// merged document says which cycle exported it rather than asserting an earlier cycle that
    /// may not exist (AGENTS.md: never guess at unobserved facts). The disposition line is what
    /// a human reads at a park and what the fix session is steered by, so a provenance claim in
    /// it has to be one the platform actually observed.
    /// </summary>
    [Fact]
    public async Task Both_tracks_reporting_one_place_in_one_cycle_export_it_once_and_say_which_cycle()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        const string preExisting = "Defect: the retry duplicates the effect. Scenario: a transient failure charges twice.";
        ScriptedExecutor executor = new(
            $"FINDING: severity=medium; scope=out-of-scope; at=src/Legacy.cs:12\n{preExisting}\n\nVERDICT: needs-fixes",
            $"FINDING: severity=medium; scope=out-of-scope; at=./Legacy.cs:12\n{preExisting}\n\nVERDICT: needs-fixes");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewFindingRouted>().Should().ContainSingle(
            "one place two tracks named in one cycle is one exported defect");

        string merged = File.ReadAllText(RunPaths.ReviewFindingsFile(RunPaths.GlobalDirectory(runId), 1));
        merged.Should().Contain("already routed to a draft bug task earlier in this cycle")
            .And.NotContain("by an earlier cycle",
                "there is no earlier cycle here, and the disposition may not claim one");
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
        const string ungraded = "`Spawner.cs` — the failure path looks wrong to me.\n\nVERDICT: needs-fixes";
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
    /// The adversarial cap is not a spent budget (Decisions Log #63): reaching it means the
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
    /// may be told the same thing (Decisions Log #63). Still returning findings at its cap parks
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
            .And.Contain(RunPaths.ReviewFindingsFile(RunPaths.GlobalDirectory(runId), 3));

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
        run.ParkedReason.Should().Contain(RunPaths.ReviewFindingsFile(RunPaths.GlobalDirectory(runId), 1), "the review position is attached")
            .And.Contain(RunPaths.ReviewFixPositionFile(RunPaths.GlobalDirectory(runId), 1), "and so is the fix run's position");

        File.ReadAllText(RunPaths.ReviewFixPositionFile(RunPaths.GlobalDirectory(runId), 1)).Should().Contain("scope decision");
        (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Should().Be(TaskState.Claimed);
    }

    /// <summary>
    /// The non-rebase sibling of <see cref="A_rebase_dispute_that_disputes_again_after_resuming_parks_with_its_own_rebase_reason"/>:
    /// a review-thread dispute resumed at cycle 0 that disputes again must point at
    /// <see cref="RunPaths.ReviewThreadDisputeFile"/>, the file <c>RunSupervisor.ParkedOnThreadDisputeAsync</c>
    /// actually writes for this follow-up kind — not <see cref="RunPaths.ReviewFindingsFile"/> for
    /// cycle 0, which nothing has ever written since no review pass has run yet (adversarial
    /// review, cycle 4, finding 3 on this feature's own diff: the rebase-specific arm of
    /// <c>DisputedParkReason</c> was gated on <c>FollowUpKind == Rebase</c>, leaving this
    /// non-rebase cycle-0 case to fall through to the generic, cycle-ge-1 message).
    /// </summary>
    [Fact]
    public async Task A_review_thread_dispute_that_disputes_again_after_resuming_points_at_the_thread_dispute_file()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedReviewThreadDisputeParkedRunAsync(store, cts.Token);

        const string humanResolution = "It is genuinely a design call — decide it yourself.";
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.NeedsFixes, humanResolution, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor executor = new(
            "Still cannot honestly call this without product input.\n\nRESOLUTION: disputed");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse();
        executor.Spawns.Should().HaveCount(1, "only the resumed review-fix session runs before parking again");
        executor.Spawns[0].Prompt.Should().NotContain(
            "rebase an existing pull request onto its base branch",
            "a review-thread dispute is not a rebase — it must not get the rebase prompt");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked);
        string disputeFile = RunPaths.ReviewThreadDisputeFile(RunPaths.GlobalDirectory(runId));
        run.ParkedReason.Should().Contain(disputeFile, "the second dispute points at the real review-thread artifact")
            .And.NotContain(RunPaths.ReviewFindingsFile(RunPaths.GlobalDirectory(runId), 0),
                "no review pass ever ran at cycle 0, so pointing at a review-findings file would name a file nothing wrote");

        File.ReadAllText(disputeFile).Should().Contain("product input");
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
        run.ParkedReason.Should().Contain(
            "conformance review returned no parseable verdict, even after this cycle's re-prompt",
            "the conformance pass is the one RepromptForVerdictAsync actually resumed");
        run.ParkedReason.Should().Contain(
            "adversarial review returned no parseable verdict, and this lens was never itself re-prompted this cycle",
            "the adversarial pass was never itself resumed, so the reason must not credit it with the cycle's re-prompt "
                + "(adversarial cycle-1 finding, ReviewEngine.cs:174)");

        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewVerdictReprompted>().Should().ContainSingle();
    }

    /// <summary>
    /// A needs-fixes verdict that names nothing is not a real answer (task filed 2026-08-25, ten
    /// occurrences — a conformance lens that said needs-fixes over findings it never enumerated,
    /// and an adversarial lens that returned a bare "VERDICT: needs-fixes"). The engine reads it
    /// the same as an unparseable verdict: the cycle's one re-prompt fires, quoting the
    /// requirement, and — naming nothing a second time — the run parks through the exact same
    /// path a genuinely verdict-less pass takes, rather than the hollow verdict being recorded as
    /// findings that were never stated.
    /// </summary>
    [Fact]
    public async Task A_needs_fixes_verdict_naming_nothing_is_reprompted_then_parks_if_it_still_names_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        const string hollow = "I found six verified findings, reported above.\n\nVERDICT: needs-fixes";
        ScriptedExecutor executor = new(
            hollow,
            "Hunted; nothing stands.\n\nVERDICT: merge-ready",
            hollow);
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("a needs-fixes verdict naming nothing is never treated as real findings");
        executor.Spawns.Should().HaveCount(3, "two passes and the cycle's one re-prompt");
        executor.Spawns[2].ResumeSessionId.Should().Be(
            executor.Spawns[0].SessionId, "the re-prompt resumes the pass that claimed needs-fixes and named nothing");
        executor.Spawns[2].Prompt.Should().Contain(
            "must name at least one finding", "the re-prompt quotes the requirement it failed");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked);
        run.ParkedReason.Should().Contain("needs-fixes naming nothing").And.Contain("re-prompt");

        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewPassCompleted>().Where(pass => pass.Lens == ReviewLens.Conformance).Should().OnlyContain(
            pass => pass.Verdict == ReviewVerdict.Unknown && pass.Findings!.Count == 0,
            "a needs-fixes verdict that named nothing is recorded as no findings, never as a placeholder it never stated");
        events.OfType<ReviewVerdictReprompted>().Should().ContainSingle();

        File.ReadAllText(RunPaths.ReviewLensFindingsFile(RunPaths.GlobalDirectory(runId), 1, ReviewLens.Conformance.Slug))
            .Should().Contain(hollow, "the malformed output is preserved verbatim for a human to read at the park");
    }

    /// <summary>
    /// The requirement applies to both lenses identically (task filed 2026-08-25): a bare
    /// "VERDICT: needs-fixes" with nothing above it — the adversarial lens's exact origin shape —
    /// is reprompted the same way a missing verdict is, and a reviewer that names something real
    /// on the re-prompt gets to conclude normally rather than being parked over its first, hollow
    /// answer.
    /// </summary>
    [Fact]
    public async Task A_bare_needs_fixes_verdict_is_reprompted_and_may_still_conclude_with_a_real_finding()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "Criteria met.\n\nVERDICT: merge-ready",
            "VERDICT: needs-fixes",
            "On reflection: `Auth.cs:42` — the limiter never resets.\n\nVERDICT: needs-fixes",
            "Reset the limiter.\n\nRESOLUTION: fixed",
            "Hunted again; the boundary holds.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue("the adversarial lens named a real finding on its one re-prompt");
        executor.Spawns.Should().HaveCount(5, "two passes, one re-prompt, one fix, and the surviving track's final pass");
        executor.Spawns[2].ResumeSessionId.Should().Be(
            executor.Spawns[1].SessionId, "the re-prompt resumes the adversarial pass, not its clean sibling");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewPassCompleted>().Should().Contain(
            pass => pass.Lens == ReviewLens.Adversarial && pass.Cycle == 1 && pass.Verdict == ReviewVerdict.NeedsFixes,
            "the resumed leg's real finding is what the platform ultimately reads for this pass");
        events.OfType<ReviewVerdictReprompted>().Should().ContainSingle();
    }

    /// <summary>
    /// The objective and acceptance-criteria echo screens must never run over an adversarial
    /// pass's output (cycle-4 adversarial finding, `ReviewEngine.cs:614`):
    /// <c>AgentPromptBuilder.BuildAdversarialReview</c> never prints either into that lens's own
    /// prompt, so an adversarial finding that happens to coincide with the task's own wording is
    /// independent phrasing, not an echo — stripping it anyway can delete the finding's only
    /// location and defect language, downgrading a real needs-fixes to Unknown over content the
    /// adversarial pass never read in the first place.
    /// </summary>
    [Fact]
    public async Task An_adversarial_finding_that_coincides_with_the_tasks_own_criterion_still_names_a_finding()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        const string criterion = "Auth.cs:42 no longer drops the token";
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, [criterion], cts.Token);

        ScriptedExecutor executor = new(
            "Criteria met.\n\nVERDICT: merge-ready",
            $"{criterion}.\n\nVERDICT: needs-fixes",
            "Reset the limiter.\n\nRESOLUTION: fixed",
            "Hunted again; the boundary holds.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue(
            "the adversarial finding survives its own cycle and the fix session clears it");
        executor.Spawns.Should().HaveCount(4, "two passes, one fix, and the surviving track's final pass — no " +
            "re-prompt, because the adversarial pass's finding was never stripped");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewPassCompleted>().Should().Contain(
            pass => pass.Lens == ReviewLens.Adversarial && pass.Cycle == 1
                && pass.Verdict == ReviewVerdict.NeedsFixes && pass.Findings!.Count == 1,
            "the adversarial pass's own wording, coinciding with the task's criterion, is still a real finding");
        events.OfType<ReviewVerdictReprompted>().Should().BeEmpty(
            "a wrongly stripped finding would have driven the same hollow-verdict re-prompt path");
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
    /// A rebase-conflict dispute parks through the same mechanism a review-thread dispute does
    /// (<c>RunSupervisor.ParkedOnThreadDisputeAsync</c>), from <c>RunState.Verifying</c>
    /// (<c>RunAggregate.ParkedFromState</c>) before any gate or review pass ran, so it resumes
    /// through this same FixNeeded phase — but the branch is still un-rebased (the parked
    /// attempt ran `git rebase --abort`) and a generic review-fix prompt knows nothing about the
    /// base branch or the conflict. The resumed session must get the rebase prompt, carrying the
    /// human's resolution, not <see cref="AgentPromptBuilder.BuildReviewFix"/> (adversarial
    /// review, cycle 1, on this feature's own diff). See the next test for the sibling case this
    /// one must NOT cover — an ordinary review cycle on the same rebase-kind task (independent
    /// pre-PR review, cycle 2).
    /// </summary>
    [Fact]
    public async Task A_park_resolved_needs_fixes_on_a_rebase_dispute_resumes_the_rebase_prompt()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedRebaseDisputeParkedRunAsync(store, cts.Token);

        const string humanResolution =
            "Keep the daemon side's retry policy; the CLI side's version predates the incident fix.";
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.NeedsFixes, humanResolution, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor executor = new(
            "Applied the human's decision and rebased cleanly.\n\nRESOLUTION: fixed",
            "Criteria met.\n\nVERDICT: merge-ready",
            "Nothing stands.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(3, "the resumed rebase, then a fresh pass per lens");
        executor.Spawns[0].Prompt.Should().Contain(
            "rebase an existing pull request onto its base branch",
            "the fix session must resume the rebase, not the generic review-fix prompt");
        executor.Spawns[0].Prompt.Should().Contain("The human's decision on the disputed conflict");
        executor.Spawns[0].Prompt.Should().Contain(humanResolution);
    }

    /// <summary>
    /// The resumed rebase session's own prompt explicitly invites a second dispute
    /// (<c>AgentPromptBuilder.AppendRebaseDisputeRules</c>: "raise a new dispute if you hit a
    /// DIFFERENT conflict that is genuinely undecidable"), and no review pass has ever run at
    /// this point (<c>RunAggregate.ReviewCycle</c> is still 0 — cycle numbers start at 1, at
    /// the first review pass), so the generic disputed-cycle park — built to point at a
    /// review-findings file and a review-fix-position file — would name a review-findings file
    /// nothing ever wrote and describe a review-thread dispute that never happened. This second
    /// park must get the same rebase-specific treatment the first one did: its own reason text
    /// naming the conflict, and its own <c>rebase-conflict-dispute.md</c> artifact (independent
    /// pre-PR review, cycle 3).
    /// </summary>
    [Fact]
    public async Task A_rebase_dispute_that_disputes_again_after_resuming_parks_with_its_own_rebase_reason()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedRebaseDisputeParkedRunAsync(store, cts.Token);

        const string humanResolution = "Keep the daemon side's retry policy.";
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.NeedsFixes, humanResolution, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor executor = new(
            "Resolved the first conflict, but `Billing.cs` conflicts again and both sides changed "
            + "pricing rounding.\n\nRESOLUTION: disputed");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse();
        executor.Spawns.Should().HaveCount(1, "only the resumed rebase session runs before parking again");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked);
        string disputeFile = RunPaths.RebaseConflictDisputeFile(RunPaths.GlobalDirectory(runId));
        run.ParkedReason.Should().Contain(disputeFile, "the second dispute gets its own rebase artifact")
            .And.Contain("Decide the conflict yourself")
            .And.NotContain(RunPaths.ReviewFindingsFile(RunPaths.GlobalDirectory(runId), 0),
                "no review pass ever ran at cycle 0, so pointing at a review-findings file would name a file nothing wrote");

        File.ReadAllText(disputeFile).Should().Contain("Billing.cs");
    }

    /// <summary>
    /// The gap the fix-session prompt selection left open (adversarial review, cycle 4, on this
    /// feature's own diff): resolving the SECOND rebase dispute must still resume the rebase
    /// prompt, not the generic review-fix prompt. By this dispatch <c>RunAggregate.ParkedFromState</c>
    /// reads <c>UnderReview</c> — the fix session that resumed the first dispute already moved
    /// <c>State</c> off <c>Verifying</c> (<c>Apply(ReviewFixDispatched)</c>) before this second
    /// park ever landed — so a discriminator keyed on <c>ParkedFromState</c> instead of
    /// <c>ReviewCycle == 0</c> would send this resumed session a prompt that knows nothing about
    /// the conflict, over a branch still un-rebased.
    /// </summary>
    [Fact]
    public async Task A_second_rebase_dispute_resolution_still_resumes_the_rebase_prompt()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedRebaseDisputeParkedRunAsync(store, cts.Token);

        const string firstResolution = "Keep the daemon side's retry policy.";
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.NeedsFixes, firstResolution, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor firstAttemptExecutor = new(
            "Resolved the first conflict, but `Billing.cs` conflicts again and both sides changed "
            + "pricing rounding.\n\nRESOLUTION: disputed");
        bool firstMergeReady = await NewEngine(store, firstAttemptExecutor).ReviewAsync(runId, taskId, cts.Token);
        firstMergeReady.Should().BeFalse("the resumed session disputed again and parked a second time");

        const string secondResolution = "Take the daemon side's rounding for Billing.cs too.";
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.NeedsFixes, secondResolution, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor secondAttemptExecutor = new(
            "Applied the human's decision and rebased cleanly.\n\nRESOLUTION: fixed",
            "Criteria met.\n\nVERDICT: merge-ready",
            "Nothing stands.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, secondAttemptExecutor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        secondAttemptExecutor.Spawns.Should().HaveCount(3, "the second resumed rebase, then a fresh pass per lens");
        secondAttemptExecutor.Spawns[0].Prompt.Should().Contain(
            "rebase an existing pull request onto its base branch",
            "the SECOND resolution must still resume the rebase prompt, not the generic review-fix prompt");
        secondAttemptExecutor.Spawns[0].Prompt.Should().Contain("The human's decision on the disputed conflict");
        secondAttemptExecutor.Spawns[0].Prompt.Should().Contain(secondResolution);
    }

    /// <summary>
    /// The sibling case the fix-session prompt selection must NOT route to the rebase prompt: a
    /// rebase follow-up whose branch already rebased cleanly and pushed (verification already
    /// passed, so <c>RunAggregate.ParkedFromState</c> never became Verifying) reaches FixNeeded
    /// through its own ordinary review cycle, with nothing disputed and nothing left un-rebased.
    /// Keying the fix-session prompt on <c>task.FollowUpKind == Rebase</c> alone — instead of
    /// pairing it with the dispute-park marker — sent every such cycle the rebase prompt instead
    /// of the reviewers' findings, so the loop never actually applied them (independent pre-PR
    /// review, cycle 2).
    /// </summary>
    [Fact]
    public async Task A_needs_fixes_verdict_on_an_ordinary_rebase_follow_up_cycle_gets_the_review_fix_prompt()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRebaseFollowUpRunAsync(store, cts.Token);

        const string conformanceFinding = "1. `Auth.cs:42` — the limiter never resets.";
        ScriptedExecutor executor = new(
            $"{conformanceFinding}\n\nVERDICT: needs-fixes",
            "Nothing of my own.\n\nVERDICT: merge-ready",
            "Reset the limiter.\n\nRESOLUTION: fixed",
            "Criteria met.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(
            4, "two passes → one fix → the one track still active, since the adversarial track went dormant");
        executor.Spawns[2].Prompt.Should().Contain(
            "Fix the verified findings from an independent pre-PR review",
            "an ordinary needs-fixes cycle on a rebase follow-up must still get the review-fix prompt");
        executor.Spawns[2].Prompt.Should().Contain(conformanceFinding);
        executor.Spawns[2].Prompt.Should().NotContain(
            "rebase an existing pull request onto its base branch",
            "the branch is already rebased — resuming the rebase prompt here would ask for a no-op");
        executor.Spawns[2].Prompt.Should().NotContain("The human's decision on the disputed conflict");
    }

    /// <summary>
    /// The parked-then-resumed shape the sibling tests above only cover half of: a rebase
    /// dispute that actually parked and was resumed (unlike
    /// <see cref="SeedVerifiedRebaseFollowUpRunAsync"/>, whose run never parked, so
    /// <c>RunAggregate.ParkedFromState</c> stayed <c>Unknown</c> and could never exercise the
    /// staleness this guards against), whose resumed fix session then succeeds and reaches an
    /// ORDINARY review cycle later in the same run. <c>ParkedFromState</c> is read off the
    /// stream rather than reset once consumed (<c>RunAggregate.Apply(ReviewParked)</c> is its
    /// only writer), so it is still <c>Verifying</c> at this later cycle's fix dispatch even
    /// though the dispute is long resolved — pairing the rebase-prompt check with
    /// <c>PendingHumanFindings</c> being present (cleared the moment the resumed fix session
    /// completes) is what keeps this automated cycle's needs-fixes verdict from being routed
    /// back to the rebase prompt for a conflict that no longer exists (independent pre-PR
    /// review, cycle 3).
    /// </summary>
    [Fact]
    public async Task An_ordinary_cycle_after_a_resumed_rebase_dispute_still_gets_the_review_fix_prompt()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedRebaseDisputeParkedRunAsync(store, cts.Token);

        const string humanResolution = "Keep the daemon side's retry policy.";
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.NeedsFixes, humanResolution, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        const string conformanceFinding = "1. `Auth.cs:42` — the limiter never resets.";
        ScriptedExecutor executor = new(
            "Applied the human's decision and rebased cleanly.\n\nRESOLUTION: fixed",
            $"{conformanceFinding}\n\nVERDICT: needs-fixes",
            "Nothing of my own.\n\nVERDICT: merge-ready",
            "Reset the limiter.\n\nRESOLUTION: fixed",
            "Criteria met.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(
            5, "the resumed rebase, two passes → one ordinary fix → the surviving track's final pass");
        executor.Spawns[3].Prompt.Should().Contain(
            "Fix the verified findings from an independent pre-PR review",
            "an ordinary needs-fixes cycle reached after a resumed rebase dispute must still get the review-fix prompt");
        executor.Spawns[3].Prompt.Should().Contain(conformanceFinding);
        executor.Spawns[3].Prompt.Should().NotContain(
            "rebase an existing pull request onto its base branch",
            "ParkedFromState is stale Verifying here, but the dispute was already resolved — resuming the rebase prompt would ask for a no-op");
        executor.Spawns[3].Prompt.Should().NotContain("The human's decision on the disputed conflict");
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
        Directory.CreateDirectory(RunPaths.ReviewFixPositionFile(RunPaths.GlobalDirectory(runId), 1));

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

    /// <summary>
    /// The generation fence (backlog 39): a requeue-and-reclaim moved the task on to
    /// generation 2 while this run — still generation 1, exactly the shape a catch-up
    /// double-booking or a lease-expiry-then-retry leaves behind — sat ready to re-enter
    /// the review loop. The loop must stop at the very first check, before it ever asks
    /// the executor to spawn a session into a worktree the live generation now owns.
    /// </summary>
    [Fact]
    public async Task A_stale_generations_review_loop_stops_before_dispatching_and_never_touches_the_task()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        NodeContext node = new();
        await node.InitializeAsync(store, cts.Token);
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            var requeued = TaskDecider.Requeue(task, RequeueReason.LeaseExpired, Now);
            task.Apply(requeued);
            var reclaimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, DomainId.New(), Now);
            session.Events.Append(taskId, requeued, reclaimed);
            session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 2, HeartbeatAt = Now });
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor executor = new("Ignored — the fence must stop before this is ever read.");
        ListLogger<ReviewEngine> logger = new();
        ReviewEngine engine = new(store, executor, executor.Processes,
            new VerificationRunner(store, Options.Create(new DaemonOptions()), NullLogger<VerificationRunner>.Instance),
            Options.Create(new DaemonOptions()), logger);

        bool mergeReady = await engine.ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("a stale generation never reports merge-ready");
        executor.Spawns.Should().BeEmpty(
            "the fence stops the loop before it dispatches a pass into a superseded worktree");
        logger.Lines.Should().Contain(line =>
            line.Contains("run at generation 1") && line.Contains("at generation 2 - rejected"));

        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Should().Be(
            TaskState.Claimed, "the live generation's claim is untouched by the stale lane");
    }

    /// <summary>
    /// A stale generation's own park check (backlog 39, Copilot review PR #30's finding):
    /// DriveAsync's loop-top fence can pass, then a requeue-and-reclaim lands in the gap
    /// before ParkAsync's own fence check — a race no test can land through the public
    /// ReviewAsync entry point, since DriveAsync would already have retired the run at the
    /// very next loop-top check. This drives ParkAsync directly (internal for exactly this)
    /// against a task already reclaimed onto generation 2, so the rejection must retire the
    /// run with RunSuperseded itself rather than leaving it live with no monitor.
    /// </summary>
    [Fact]
    public async Task A_stale_generations_own_park_retires_the_run_instead_of_leaving_it_live()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();

        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid liveNodeId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid staleRunId = DomainId.New();
        string worktreePath = Path.Combine(_home, $"wt-{staleRunId:N}");
        Directory.CreateDirectory(worktreePath);

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = new();
            (task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, projectId, "Stale generation park", ["never parks as generation 1"],
                    TaskType.Chore, null, null, null, Now, ownerId),
                ownerId, Now);
            var staleClaim = TaskDecider.Claim(task, DomainId.New(), ownerId, staleRunId, Now);
            task.Apply(staleClaim);
            // A requeue-and-reclaim moved the task on to generation 2 under a different run
            // while this run's review loop was still parking — the exact double-booking shape.
            var requeued = TaskDecider.Requeue(task, RequeueReason.LeaseExpired, Now);
            task.Apply(requeued);
            var liveClaim = TaskDecider.Claim(task, liveNodeId, ownerId, DomainId.New(), Now);
            task.Apply(liveClaim);
            session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, staleClaim, requeued, liveClaim]);
            session.Store(new TaskLease { Id = taskId, NodeId = liveNodeId, LeaseGeneration = 2, HeartbeatAt = Now });

            session.Events.StartStream<RunAggregate>(staleRunId,
                new RunDispatched(staleRunId, taskId, staleClaim.NodeId, ownerId, 1, DomainId.New(),
                    worktreePath, "task/stale-park", ExecutorMode.Subscription, Now),
                new AgentSessionCompleted(staleRunId, Now),
                new VerificationPassed(staleRunId, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor executor = new("Ignored — parking never dispatches.");
        ListLogger<ReviewEngine> logger = new();
        ReviewEngine engine = new(store, executor, executor.Processes,
            new VerificationRunner(store, Options.Create(new DaemonOptions()), NullLogger<VerificationRunner>.Instance),
            Options.Create(new DaemonOptions()), logger);

        await engine.ParkAsync(staleRunId, taskId, "No parseable verdict.", cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(staleRunId, cts.Token))!;
        run.State.Should().Be(RunState.Superseded,
            "the stale generation's own park check must retire the run itself, not leave it live with no monitor");

        TaskListItem task2 = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task2.State.Should().Be(TaskState.Claimed, "the live generation's claim survives the stale run's park");
        task2.LeaseGeneration.Should().Be(2);
        (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().NotBeNull(
            "the stale run's park must not release the live generation's lease");

        logger.Lines.Should().Contain(line =>
            line.Contains("run at generation 1") && line.Contains("at generation 2 - rejected"));
        logger.Lines.Should().Contain(line =>
            line.Contains("retired as superseded") && line.Contains("review loop's park"));
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
        Directory.CreateDirectory(RunPaths.GlobalDirectory(runId));
        await File.WriteAllTextAsync(
            RunPaths.SessionStreamFile(RunPaths.GlobalDirectory(runId), artifactName), line + "\n", cancellationToken);
    }

    /// <summary>
    /// A run that just passed its gates: task claimed with a lease, project registered
    /// (no verify commands, so re-verification auto-passes), run stream ending in
    /// VerificationPassed — exactly where the review loop takes over.
    /// </summary>
    private Task<(Guid TaskId, Guid RunId, Guid MainSessionId)> SeedVerifiedRunAsync(
        DocumentStore store, CancellationToken cancellationToken) =>
        SeedVerifiedRunAsync(store, ["reviewed"], cancellationToken);

    private async Task<(Guid TaskId, Guid RunId, Guid MainSessionId)> SeedVerifiedRunAsync(
        DocumentStore store, IReadOnlyList<string> acceptanceCriteria, CancellationToken cancellationToken)
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
            TaskDecider.Add(taskId, projectId, "Review me before the PR", acceptanceCriteria,
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
    /// Like <see cref="SeedVerifiedRunAsync"/>, but the task carries the shape a rebase
    /// follow-up actually reaches this loop with: completed once (so it has a pull request),
    /// reopened as a <see cref="FollowUpKind.Rebase"/> follow-up, and reclaimed under the
    /// stream this test seeds. <c>DispatchFixSessionAsync</c> reads <c>context.Task.FollowUpKind</c>
    /// and <c>context.Task.PullRequestUrl</c> to pick the rebase prompt, so both have to be real.
    /// </summary>
    private async Task<(Guid TaskId, Guid RunId, Guid MainSessionId)> SeedVerifiedRebaseFollowUpRunAsync(
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
        var firstClaim = TaskDecider.Claim(task, node.NodeId, node.OwnerId, DomainId.New(), Now);
        task.Apply(firstClaim);
        var completed = TaskDecider.Complete(task, DomainId.New(), "https://github.com/x/y/pull/7", Now);
        task.Apply(completed);
        var reopened = TaskDecider.Reopen(
            task, DomainId.New(), "task/review-me", "The pull request's branch conflicts with its base branch.",
            FollowUpKind.Rebase, automatic: true, Now, node.OwnerId);
        task.Apply(reopened);
        var claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now);
        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, firstClaim, completed, reopened, claimed]);
        session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = Now });

        session.Events.StartStream<RunAggregate>(runId,
            new RunDispatched(runId, taskId, node.NodeId, node.OwnerId, 1, mainSessionId,
                worktreePath, "task/review-me", ExecutorMode.Subscription, Now, IsFollowUp: true),
            new AgentSessionCompleted(runId, Now),
            new VerificationPassed(runId, Now));
        await session.SaveChangesAsync(cancellationToken);

        return (taskId, runId, mainSessionId);
    }

    /// <summary>
    /// Like <see cref="SeedVerifiedRebaseFollowUpRunAsync"/>, but the run stops at
    /// <c>AgentSessionCompleted</c> and parks straight from there — the exact shape
    /// <c>RunSupervisor.ParkedOnThreadDisputeAsync</c> leaves behind for a rebase-conflict
    /// dispute (RunAggregate.ParkedFromState reads Verifying, and no review cycle has ever run).
    /// This is the ONLY shape that should resume through the rebase prompt; a FixNeeded reached
    /// through an ordinary review cycle on this same rebase-kind task — verification already
    /// passed, a cycle already ran — must not (independent pre-PR review, cycle 2).
    /// </summary>
    private async Task<(Guid TaskId, Guid RunId, Guid MainSessionId)> SeedRebaseDisputeParkedRunAsync(
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
        var firstClaim = TaskDecider.Claim(task, node.NodeId, node.OwnerId, DomainId.New(), Now);
        task.Apply(firstClaim);
        var completed = TaskDecider.Complete(task, DomainId.New(), "https://github.com/x/y/pull/7", Now);
        task.Apply(completed);
        var reopened = TaskDecider.Reopen(
            task, DomainId.New(), "task/review-me", "The pull request's branch conflicts with its base branch.",
            FollowUpKind.Rebase, automatic: true, Now, node.OwnerId);
        task.Apply(reopened);
        var claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now);
        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, firstClaim, completed, reopened, claimed]);
        session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = Now });

        session.Events.StartStream<RunAggregate>(runId,
            new RunDispatched(runId, taskId, node.NodeId, node.OwnerId, 1, mainSessionId,
                worktreePath, "task/review-me", ExecutorMode.Subscription, Now, IsFollowUp: true),
            new AgentSessionCompleted(runId, Now),
            new ReviewParked(runId,
                "A follow-up could not honestly resolve a rebase conflict — both sides changed the same "
                + "behavior, not just the same lines.", Now));
        await session.SaveChangesAsync(cancellationToken);

        return (taskId, runId, mainSessionId);
    }

    /// <summary>
    /// Like <see cref="SeedRebaseDisputeParkedRunAsync"/>, but the follow-up disputed a review
    /// thread rather than a rebase conflict (Decisions Log #62): still a pre-gate park at
    /// <c>ReviewCycle == 0</c>, but the disputed-park reason and its artifact are the
    /// review-thread ones, not the rebase ones (adversarial review, cycle 4, finding 3 on this
    /// feature's own diff).
    /// </summary>
    private async Task<(Guid TaskId, Guid RunId, Guid MainSessionId)> SeedReviewThreadDisputeParkedRunAsync(
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
        var firstClaim = TaskDecider.Claim(task, node.NodeId, node.OwnerId, DomainId.New(), Now);
        task.Apply(firstClaim);
        var completed = TaskDecider.Complete(task, DomainId.New(), "https://github.com/x/y/pull/7", Now);
        task.Apply(completed);
        var reopened = TaskDecider.Reopen(
            task, DomainId.New(), "task/review-me", "A reviewer left feedback on the merged pull request.",
            FollowUpKind.ReviewFeedback, automatic: true, Now, node.OwnerId);
        task.Apply(reopened);
        var claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now);
        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, firstClaim, completed, reopened, claimed]);
        session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = Now });

        session.Events.StartStream<RunAggregate>(runId,
            new RunDispatched(runId, taskId, node.NodeId, node.OwnerId, 1, mainSessionId,
                worktreePath, "task/review-me", ExecutorMode.Subscription, Now, IsFollowUp: true),
            new AgentSessionCompleted(runId, Now),
            new ReviewParked(runId,
                "A follow-up disputed a review thread as a design call it cannot honestly make.", Now));
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
