using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Worktrees;
using Hall9k.Daemon;
using Hall9k.Daemon.Closeout;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

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
public sealed class ReviewEngineTests(PostgresFixture postgres, SeededGitOriginFixture origins)
    : IClassFixture<PostgresFixture>, IClassFixture<SeededGitOriginFixture>, IDisposable
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

        /// <summary>
        /// The OS seam the engine shares with this executor. A spawn that writes a result here
        /// already ran the scripted session to completion synchronously before returning, exactly
        /// the shape a real single-shot review-pass or fix-session invocation leaves once it has
        /// exited — so no pid is ever marked alive, and SessionResultWaiter.WaitAsync completes
        /// off the result file alone instead of waiting out a process that will never die.
        /// </summary>
        public FakeProcessManager Processes { get; } = new();

        /// <summary>Lets a test mutate configuration between legs, the way a config edit mid-run would.</summary>
        public Action? OnFirstSpawn { get; set; }

        /// <summary>Lets a test act like the spawn actually touched the worktree — a fix session's own commit, keyed by that spawn's zero-based index — since every session here is scripted rather than real.</summary>
        public Dictionary<int, Action> OnSpawnByIndex { get; } = [];

        /// <summary>
        /// Spawn indexes (zero-based, dispatch order) whose scripted summary comes back as a
        /// generic `is_error: true` result instead of a clean success — task: a session that
        /// reports an error result is retried once in place. Distinct from a null script entry,
        /// which reports the session dying without any result at all.
        /// </summary>
        public HashSet<int> ErrorAtSpawnIndex { get; } = [];

        /// <summary>
        /// Spawn indexes whose error result carries no "result" string field at all — the shape
        /// Claude Code emits for some error subtypes (e.g. error_max_turns), distinct from
        /// <see cref="ErrorAtSpawnIndex"/>'s own error-with-message shape: StreamJsonParser
        /// reads a missing (or non-string) "result" property as a null Summary rather than
        /// failing to parse, so the terminal event still arrives, just without a message.
        /// </summary>
        public HashSet<int> NullSummaryErrorAtSpawnIndex { get; } = [];

        /// <summary>
        /// Spawn indexes whose process is marked alive for slightly more than one
        /// SessionResultWaiter/RunSupervisor poll interval before dying on its own, instead of
        /// this class's own default "never alive" shape (this class's own doc above) — the shape
        /// a real session takes when a test needs at least one poll with the root still alive so
        /// IProcessManager.SnapshotDescendants can capture a lingering descendant before it
        /// reparents away and becomes unreachable (task: a leg's recorded token usage is read
        /// from a fresh, full re-read of its stream file).
        /// </summary>
        public HashSet<int> SpawnIndexesThatStayAliveBriefly { get; } = [];

        public async Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
        {
            if (Spawns.Count == 0)
            {
                OnFirstSpawn?.Invoke();
            }

            if (OnSpawnByIndex.TryGetValue(Spawns.Count, out Action? onSpawn))
            {
                onSpawn();
            }

            int spawnIndex = Spawns.Count;
            Spawns.Add(request);
            request.SessionArtifactName.Should().NotBeNull("review legs must never overwrite the main session's files");

            int processId = _nextProcessId++;
            string? summary = _summaries.Count > 0 ? _summaries.Dequeue() : null;
            if (summary is null)
            {
                return new SpawnedAgent(processId, Now);
            }

            bool isError = ErrorAtSpawnIndex.Contains(spawnIndex);
            bool nullSummaryError = NullSummaryErrorAtSpawnIndex.Contains(spawnIndex);
            string line = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "result",
                ["subtype"] = isError || nullSummaryError ? "error_during_execution" : "success",
                ["is_error"] = isError || nullSummaryError,
                ["usage"] = new Dictionary<string, long> { ["input_tokens"] = 1_000, ["output_tokens"] = 200 },
                ["total_cost_usd"] = 0.01,
                ["num_turns"] = 12,
                ["result"] = nullSummaryError ? null : summary,
            });
            Directory.CreateDirectory(request.RunDirectory);
            await File.WriteAllTextAsync(
                RunPaths.SessionStreamFile(request.RunDirectory, request.SessionArtifactName!),
                line + "\n", cancellationToken);

            if (SpawnIndexesThatStayAliveBriefly.Contains(spawnIndex))
            {
                Processes.MarkAlive(processId);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(1_100));
                    Processes.MarkDead(processId);
                });
            }

            return new SpawnedAgent(processId, Now);
        }
    }

    [Fact]
    public async Task Merge_ready_needs_both_lenses_clean_and_archives_each_lens_findings()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
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
    /// Task: the review pipeline's stage composition becomes configuration recorded per run —
    /// composition none skips the loop entirely: no reviewer is ever dispatched, and the run
    /// settles straight off the gates VerificationRunner already ran before ReviewAsync started.
    /// </summary>
    [Fact]
    public async Task Composition_none_settles_immediately_with_no_review_pass_ever_dispatched()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(
            store, ["reviewed"], cts.Token, ReviewStageComposition.None);

        ScriptedExecutor executor = new();
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue("nothing left to wait for — the gates already ran and no reviewer is owed a look");
        executor.Spawns.Should().BeEmpty("composition none never dispatches a reviewer");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.LastReviewVerdict.Should().Be(ReviewVerdict.MergeReady);
        run.ReviewSettlement.Should().Be(
            ReviewSettlement.Settled, "nobody read the final tip, so this can never make the narrower Clean claim");
        run.ReviewCycle.Should().Be(0, "no cycle ever started");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, both lenses (high): composition none reaching
    /// <see cref="ReviewPhase.Reverify"/> — the one way it can, a pre-gate rebase or review-thread
    /// dispute resumed with <c>h9k review resolve --needs-fixes</c> — used to dispatch
    /// <see cref="ReviewStageComposition.OpeningLenses"/>'s permanently-empty list forever: no
    /// event ever appended, so <c>DriveAsync</c> reloaded the identical state and re-ran the full
    /// verification gate on every iteration, never settling and never opening the pull request.
    /// The fix session itself must still run (the dispute's own fix, over the human's resolution),
    /// but nothing after it may ever dispatch a reviewer for composition none.
    /// </summary>
    [Fact]
    public async Task Composition_none_resuming_a_pre_gate_dispute_settles_after_the_fix_without_dispatching_a_reviewer()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedRebaseDisputeParkedRunAsync(
            store, cts.Token, ReviewStageComposition.None);

        const string humanResolution = "Keep the daemon side's retry policy; it is the one this run actually ships.";
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.NeedsFixes, humanResolution, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor executor = new("Applied the human's decision and rebased cleanly.\n\nRESOLUTION: fixed");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue("the dispute's own fix is all composition none ever owes this run");
        executor.Spawns.Should().ContainSingle(
            "only the dispute's own fix session dispatches — composition none never dispatches a reviewer, "
            + "and a second spawn here would mean the empty-lens dispatch bug is back");
        executor.Spawns[0].Prompt.Should().Contain(
            "rebase an existing pull request onto its base branch",
            "the fix session must resume the rebase, not the generic review-fix prompt");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.LastReviewVerdict.Should().Be(ReviewVerdict.MergeReady);
        run.ReviewSettlement.Should().Be(
            ReviewSettlement.Settled, "nobody read the final tip, so this can never make the narrower Clean claim");
    }

    /// <summary>
    /// Adversarial-only never opens the conformance track, including on the cycle immediately
    /// before merge that would otherwise be a mandatory two-lens FinalFullPass.
    /// </summary>
    [Fact]
    public async Task Composition_adversarial_only_never_dispatches_the_conformance_lens()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(
            store, ["reviewed"], cts.Token, ReviewStageComposition.AdversarialOnly);

        ScriptedExecutor executor = new("Nothing here.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().ContainSingle("only the adversarial lens ever opens — one pass, not two");
        executor.Spawns[0].Prompt.Should().Contain(
            "assume this diff is wrong somewhere", "the one pass dispatched is the adversarial lens, never conformance");
    }

    /// <summary>
    /// Conformance-only never opens the adversarial track — the mirror of
    /// <see cref="Composition_adversarial_only_never_dispatches_the_conformance_lens"/>, added as
    /// this composition's own engine-level coverage (independent pre-PR review, cycle 1,
    /// adversarial finding: only None and AdversarialOnly had engine tests before this).
    /// </summary>
    [Fact]
    public async Task Composition_conformance_only_never_dispatches_the_adversarial_lens()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(
            store, ["reviewed"], cts.Token, ReviewStageComposition.ConformanceOnly);

        ScriptedExecutor executor = new("Every acceptance criterion is met.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().ContainSingle("only the conformance lens ever opens — one pass, not two");
        executor.Spawns[0].Prompt.Should().Contain(
            "What the diff is supposed to do", "the one pass dispatched is the conformance lens, never adversarial");
    }

    /// <summary>
    /// Skip-final-pass keeps both tracks through an ordinary fix-and-reverify cycle, but waives
    /// Decisions Log #92's mandatory fresh-context FinalFullPass immediately before merge — the
    /// mirror of <see cref="Either_lens_finding_defects_produces_one_verdict_and_one_fix_session_over_the_merged_findings"/>,
    /// whose own identical scenario under the default FullPipeline composition pays for that extra
    /// pass (two more spawns, one more review cycle). The mandatory build/test gate itself still
    /// runs full before the run may settle — this composition only ever touches the reviewer pass,
    /// never the gate (independent pre-PR review, cycle 1, adversarial finding: only None and
    /// AdversarialOnly had engine tests before this).
    /// </summary>
    [Fact]
    public async Task Composition_skip_final_pass_settles_without_the_mandatory_final_full_pass()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(
            store, ["reviewed"], cts.Token, ReviewStageComposition.SkipFinalPass);

        const string conformanceFinding = "1. `Auth.cs:42` — the limiter never resets. Scenario: the second request always 429s.";
        const string adversarialFinding = "1. `WorkItemContext.cs:18` — task text reaches the prompt unfenced. Scenario: a crafted objective redirects the agent.";
        ScriptedExecutor executor = new(
            $"{conformanceFinding}\n\nVERDICT: needs-fixes",
            $"{adversarialFinding}\n\nVERDICT: needs-fixes",
            "Reset the limiter window and fenced the task text.\n\nRESOLUTION: fixed",
            "Verified both fixes; nothing new stands.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(
            4, "two passes → one fix → one verify pass — no mandatory final full pass for this composition");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewCycle.Should().Be(2, "only the verify cycle advances it — no extra final-pass cycle spent");
        run.LastReviewVerdict.Should().Be(ReviewVerdict.MergeReady);

        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewDispatched>().Select(e => e.Mode).Should().Equal(
            [ReviewMode.Discovery, ReviewMode.Discovery, ReviewMode.Verify],
            "no FinalFullPass mode is ever dispatched under this composition");
        events.OfType<VerificationPassed>().Should().HaveCount(
            3, "the mandatory gate still runs full immediately before settling — only the reviewer pass is waived");
    }

    /// <summary>
    /// Per-pass turns and input tokens must be readable from an ordinary production run, so both
    /// ride on <see cref="ReviewPassCompleted"/> itself rather than only on the separately-appended
    /// <see cref="TokensRecorded"/> event.
    /// </summary>
    [Fact]
    public async Task Each_review_pass_records_its_own_turns_and_input_tokens()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "Every acceptance criterion is met.\n\nVERDICT: merge-ready",
            "Hunted the trust boundaries and the lifetimes; nothing survived verification.\n\nVERDICT: merge-ready");
        await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];

        events.OfType<ReviewPassCompleted>().Should().OnlyContain(
            pass => pass.Turns == 12 && pass.InputTokens == 1_000,
            "the fake session's own stream-json result is what a real one would report");
    }

    private static string GitOutput(string workingDirectory, string arguments)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"-C \"{workingDirectory}\" {arguments}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed: {output}{error}");
        }

        return output.Trim();
    }

    private static void Git(string workingDirectory, string arguments)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"-C \"{workingDirectory}\" {arguments}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.Start();
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed: {output}");
        }
    }

    /// <summary>Like <see cref="Git"/>, but never throws — for a call a test expects to fail (a conflicting rebase).</summary>
    private static int TryGit(string workingDirectory, string arguments)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"-C \"{workingDirectory}\" {arguments}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.Start();
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>
    /// The origin a seed hands its run: <see cref="SeededGitOriginFixture.OriginPath"/> itself
    /// when the test only reads it, and a bare clone of it under this test's own home when the
    /// test pushes. Either way the seeded history — <c>main</c> carrying one <c>base.txt</c>
    /// commit — is the fixture's, built once for the class rather than rebuilt per test.
    /// </summary>
    private string OriginFor(Guid runId, bool ownOrigin)
    {
        if (!ownOrigin)
        {
            return origins.OriginPath;
        }

        string own = Path.Combine(_home, $"origin-{runId:N}.git");
        origins.CopyTo(own);
        return own;
    }

    /// <summary>
    /// Refuses the shared template by name, so a test that pushes and forgot to ask for
    /// <c>ownOrigin: true</c> fails here saying so rather than quietly rewriting the origin every
    /// later test in the class is about to clone.
    /// </summary>
    private string MutableOrigin(string originPath) =>
        originPath == origins.OriginPath
            ? throw new InvalidOperationException(
                "this is SeededGitOriginFixture's shared template origin, which every other test in "
                + "this class clones — pushing to it would rewrite their starting state. Seed with "
                + "ownOrigin: true to get a copy of it to push to instead.")
            : originPath;

    /// <summary>
    /// The lens that finds something carries the cycle: one NeedsFixes verdict, one merged
    /// finding list, one fix session for all of it (Decisions Log #59).
    /// </summary>
    [Fact]
    public async Task Either_lens_finding_defects_produces_one_verdict_and_one_fix_session_over_the_merged_findings()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        const string conformanceFinding = "1. `Auth.cs:42` — the limiter never resets. Scenario: the second request always 429s.";
        const string adversarialFinding = "1. `WorkItemContext.cs:18` — task text reaches the prompt unfenced. Scenario: a crafted objective redirects the agent.";
        ScriptedExecutor executor = new(
            $"{conformanceFinding}\n\nVERDICT: needs-fixes",
            $"{adversarialFinding}\n\nVERDICT: needs-fixes",
            "Reset the limiter window and fenced the task text.\n\nRESOLUTION: fixed",
            // Cycle 2: both tracks are still active, so this is one Verify pass standing in for
            // both (task: review cycles after the first) — not two more full passes.
            "Verified both fixes; nothing new stands.\n\nVERDICT: merge-ready",
            // Cycle 3: nothing left to review, but the loop has never yet paid for a full-rigor
            // read of the tip the fix produced, so the mandatory FinalFullPass runs both lenses
            // fresh before the run may settle.
            "Criteria met.\n\nVERDICT: merge-ready",
            "Hunted again; the boundary holds.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(
            6, "two passes → one fix → one verify pass → the mandatory final full pass (two lenses)");
        executor.Spawns[2].Prompt.Should().Contain(conformanceFinding).And.Contain(adversarialFinding,
            "one fix session addresses both lenses' findings");
        executor.Spawns[2].Prompt.Should().Contain("Conformance lens").And.Contain("Adversarial lens",
            "the fix session still sees which lens produced which finding");
        executor.Spawns[3].Prompt.Should().Contain("verify the fix", "cycle 2 is a Verify pass, not a rediscovery");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewCycle.Should().Be(3, "the verify cycle and the mandatory final full pass each advance it");
        run.LastReviewVerdict.Should().Be(ReviewVerdict.MergeReady);

        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewCompleted>().Should().HaveCount(3, "one merged verdict per cycle, not one per lens");
        events.OfType<ReviewFixDispatched>().Should().HaveCount(1, "one fix session per cycle, however many lenses spoke");
        events.OfType<VerificationPassed>().Should().HaveCount(
            3, "gates re-ran after the fix, and again — full scope, unconditionally (task: a fix cycle's "
                + "verification gate) — right before the mandatory final full pass dispatches, since the "
                + "clean Verify cycle in between concluded straight to Settling with no fix of its own to "
                + "gate");
        events.OfType<ReviewDispatched>().Select(e => e.Mode).Should().Equal(
            [ReviewMode.Discovery, ReviewMode.Discovery, ReviewMode.Verify, ReviewMode.FinalFullPass, ReviewMode.FinalFullPass],
            "the mode each cycle actually ran under is on the stream");
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
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "1. `Auth.cs:42` — the limiter never resets.\n\nVERDICT: needs-fixes",
            "Nothing survived verification.\n\nVERDICT: merge-ready",
            "Reset the limiter window.\n\nRESOLUTION: fixed",
            // Cycle 2: only conformance is still active, so it gets one Verify pass rather than
            // a fresh full-diff dispatch (task: review cycles after the first).
            "Criteria met.\n\nVERDICT: merge-ready",
            // Cycle 3: the mandatory final full pass, both lenses fresh — it reawakens the
            // adversarial track that went dormant at cycle 1 to give it one more look.
            "Confirmed clean.\n\nVERDICT: merge-ready",
            "Confirmed clean too.\n\nVERDICT: merge-ready");
        await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        executor.Spawns.Should().HaveCount(
            6, "two passes → one fix → one verify pass over the surviving track → the mandatory " +
                "final full pass, which reawakens the dormant adversarial track for one more look");
        List<AgentSpawnRequest> passes = [executor.Spawns[0], executor.Spawns[1]];

        passes.Select(SettingsArgument).Should().OnlyHaveUniqueItems(
            "a session that owns its settings file has no writer but itself");
        passes.Should().OnlyContain(
            pass => pass.Environment.ContainsKey("GIT_OPTIONAL_LOCKS") && pass.Environment["GIT_OPTIONAL_LOCKS"] == "0",
            "read-only git must not contend for .git/index.lock with the sibling pass");

        executor.Spawns[2].Environment.Should().BeEmpty(
            "the fix session runs alone and commits — it needs git's locks");
    }

    /// <summary>
    /// The one behavior-bearing change a per-run session cap makes (Decisions Log #111, Brian's
    /// ruling 2026-08-30): at a cap of 1, the second lens is not spawned until the first lens's
    /// own result has already been recorded on the stream — proven here by the literal order
    /// <see cref="ReviewDispatched"/> and <see cref="ReviewPassCompleted"/> land in, which is
    /// interleaved at a cap of 1 and back-to-back-then-back-to-back at today's default.
    /// </summary>
    [Fact]
    public async Task A_session_cap_of_one_serializes_the_two_lenses_one_completes_before_the_other_spawns()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "Every acceptance criterion is met.\n\nVERDICT: merge-ready",
            "Hunted the trust boundaries and the lifetimes; nothing survived verification.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor, new DaemonOptions { SessionCapPerRun = 1 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue("the cap throttles the burn RATE, never whether the run converges");
        executor.Spawns.Should().HaveCount(2, "the same two lenses run either way — the cap spreads them over more wall clock, it does not skip one");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        List<string> shape = [.. events
            .Select(e => e switch
            {
                ReviewDispatched dispatched => $"Dispatched:{dispatched.Lens?.Slug}",
                ReviewPassCompleted completed => $"Completed:{completed.Lens?.Slug}",
                _ => null,
            })
            .OfType<string>()];

        shape.Should().Equal(
            [
                $"Dispatched:{ReviewLens.Conformance.Slug}",
                $"Completed:{ReviewLens.Conformance.Slug}",
                $"Dispatched:{ReviewLens.Adversarial.Slug}",
                $"Completed:{ReviewLens.Adversarial.Slug}",
            ],
            "a cap of 1 dispatches the second lens only once the first lens's own result is recorded — "
            + "one lens completes before the other spawns, rather than both spawning together the way "
            + "a cap of 2 or higher does");
    }

    /// <summary>
    /// A task's own <c>h9k task set-session-cap</c> override wins over the node's global default
    /// (Decisions Log #111) — proven the same way as the node-default test above, but with the
    /// node left at a default that would NOT serialize and only the task overridden to 1.
    /// </summary>
    [Fact]
    public async Task A_tasks_own_session_cap_override_serializes_the_lenses_even_when_the_nodes_default_would_not()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.OverrideSessionCap(task, 1, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor executor = new(
            "Every acceptance criterion is met.\n\nVERDICT: merge-ready",
            "Hunted the trust boundaries and the lifetimes; nothing survived verification.\n\nVERDICT: merge-ready");
        // The node's own default is left at today's default (3) — only the task's own override is 1.
        bool mergeReady = await NewEngine(store, executor, new DaemonOptions())
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        List<string> shape = [.. events
            .Select(e => e switch
            {
                ReviewDispatched dispatched => $"Dispatched:{dispatched.Lens?.Slug}",
                ReviewPassCompleted completed => $"Completed:{completed.Lens?.Slug}",
                _ => null,
            })
            .OfType<string>()];

        shape.Should().Equal(
            [
                $"Dispatched:{ReviewLens.Conformance.Slug}",
                $"Completed:{ReviewLens.Conformance.Slug}",
                $"Dispatched:{ReviewLens.Adversarial.Slug}",
                $"Completed:{ReviewLens.Adversarial.Slug}",
            ],
            "the task's own override wins over the node's global default, exactly like a task's model override");
    }

    /// <summary>
    /// Findings merge, severity disposition, the fix session, and the cycle progression all read
    /// identically to <see cref="Either_lens_finding_defects_produces_one_verdict_and_one_fix_session_over_the_merged_findings"/>
    /// under a cap of 1 — the acceptance bar for the serialization path (task: the serialization
    /// path is the only behavior-bearing change and deserves focused tests on this exact shape).
    /// </summary>
    [Fact]
    public async Task A_session_cap_of_one_still_merges_findings_and_dispositions_exactly_as_a_parallel_pass_does()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        const string conformanceFinding = "1. `Auth.cs:42` — the limiter never resets. Scenario: the second request always 429s.";
        const string adversarialFinding = "1. `WorkItemContext.cs:18` — task text reaches the prompt unfenced. Scenario: a crafted objective redirects the agent.";
        ScriptedExecutor executor = new(
            $"{conformanceFinding}\n\nVERDICT: needs-fixes",
            $"{adversarialFinding}\n\nVERDICT: needs-fixes",
            "Reset the limiter window and fenced the task text.\n\nRESOLUTION: fixed",
            // Cycle 2: both tracks are still active, so this is one Verify pass — Verify dispatches
            // a single stand-in session regardless of the session cap, exactly as it does at any cap.
            "Verified both fixes; nothing new stands.\n\nVERDICT: merge-ready",
            // Cycle 3: the mandatory FinalFullPass, both lenses again — serialized by the cap the
            // same way cycle 1 was.
            "Criteria met.\n\nVERDICT: merge-ready",
            "Hunted again; the boundary holds.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor, new DaemonOptions { SessionCapPerRun = 1 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(
            6, "two passes → one fix → one verify pass → the mandatory final full pass (two lenses) — "
                + "identical to the parallel pass, since the cap changes when a lens spawns, never how many spawn");
        executor.Spawns[2].Prompt.Should().Contain(conformanceFinding).And.Contain(adversarialFinding,
            "one fix session still addresses both lenses' findings, identical to a parallel pass");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewCycle.Should().Be(3, "the verify cycle and the mandatory final full pass each advance it, exactly as under a parallel pass");
        run.LastReviewVerdict.Should().Be(ReviewVerdict.MergeReady);

        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewCompleted>().Should().HaveCount(3, "one merged verdict per cycle, not one per lens — cap counting is unaffected");
        events.OfType<ReviewFixDispatched>().Should().HaveCount(1, "one fix session per cycle, however many lenses spoke");
        events.OfType<VerificationPassed>().Should().HaveCount(3, "the gate re-runs are unaffected by how the lenses were spread out");
    }

    /// <summary>
    /// The Settling branch's own mandatory full gate is skipped when the immediately preceding
    /// gate already ran full over the identical tip (task: a fix cycle's verification gate,
    /// cycle-3 finding). The common trigger is a nominally-scoped Verify reverify whose own gate
    /// fell back to full because the fix's commit touched something <see cref="TestScopeResolver"/>
    /// cannot map to a test class — a doc file, here — so re-running the full suite a second time
    /// over the identical commits would buy nothing. Only the redundant GATE call is skipped: the
    /// review pass immediately after still runs, since <c>MaySettleReason</c>'s own "another fresh-context
    /// read is still owed" rule for a Verify-mode cycle is a separate question this does not touch.
    /// </summary>
    [Fact]
    public async Task A_verify_cycles_reverify_gate_that_fell_back_to_full_skips_the_redundant_settling_gate()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, string worktreePath, _) = await SeedVerifiedRunWithTestGateAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "1. `Auth.cs:42` — the limiter never resets.\n\nVERDICT: needs-fixes",
            "Nothing survived verification.\n\nVERDICT: merge-ready",
            "Reset the limiter window.\n\nRESOLUTION: fixed",
            // Cycle 2: only conformance is still active, so it gets one Verify pass.
            "Criteria met.\n\nVERDICT: merge-ready",
            // Cycle 3: the mandatory final full pass, both lenses fresh.
            "Confirmed clean.\n\nVERDICT: merge-ready",
            "Confirmed clean too.\n\nVERDICT: merge-ready");
        // The fix session (the third spawn, index 2) is scripted rather than real — it has to
        // actually touch the worktree the way a real fix would, but only a doc file, so
        // TestScopeResolver cannot map it to any test class and the reverify gate right after
        // falls back to full even though this is nominally a "Verify" cycle.
        executor.OnSpawnByIndex[2] = () => CommitDocOnlyChange(worktreePath);

        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(
            6, "the gate skip changes nothing about how many review passes and fix sessions run — "
                + "only a redundant gate call");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        List<VerificationPassed> passes = [.. events.OfType<VerificationPassed>()];
        passes.Should().HaveCount(
            2, "the run's own first gate pass, plus the cycle-2 reverify gate that fell back to full over "
                + "the doc-only fix — Settling recognizes that full pass already covered this exact tip and "
                + "does not pay for an identical third run");
        passes[^1].RanFullScope.Should().BeTrue(
            "the reverify's own scoped attempt fell back to full because TestScopeResolver could not map "
                + "the fix's doc-only commit to any test class");
    }

    /// <summary>
    /// The end-to-end proof for this task (a headless build, fix, or recovery session never ends
    /// its turn while a gate it started is still running in the background): a fix session ends
    /// its turn with a modified-but-uncommitted tracked file still sitting in the worktree and a
    /// final message naming a pending background task, with a descendant process still alive
    /// behind it — the exact shape all three origin incidents took (2026-09-07/08). Driven through
    /// <see cref="ReviewEngine"/>, the supervisor of the fix/reverify loop, this asserts all three
    /// halves of the fix at once: the named outcome (not "(undeclared)"), the automatic recovery
    /// dispatched rather than the run failing before its gates, and the fix session's own
    /// lingering process torn down before the reverify gate — the recovery session that commits
    /// the stranded file — ever touches the same worktree.
    /// </summary>
    [Fact]
    public async Task A_fix_session_ending_on_a_dirty_tree_and_a_pending_background_task_names_the_outcome_and_recovers()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, string worktreePath, _) = await SeedVerifiedRunWithTestGateAsync(store, cts.Token);

        // A tracked file the fix session can leave modified-but-uncommitted, the same shape
        // VerificationRunnerTests' own recovery coverage uses.
        File.WriteAllText(Path.Combine(worktreePath, "half-done.cs"), "class HalfDone { }\n");
        Git(worktreePath, "add -A");
        Git(worktreePath, "-c user.name=Test -c user.email=test@test commit -q -m half-done");

        ScriptedExecutor executor = new(
            "1. `Auth.cs:42` — the limiter never resets.\n\nVERDICT: needs-fixes",
            "Nothing survived verification.\n\nVERDICT: merge-ready",
            // Spawn 2: the fix session. No RESOLUTION marker at all — it ends naming a
            // background task instead, exactly as all three origin incidents' sessions did.
            "The full dotnet test run is still running in the background.",
            // Spawn 3: the automatic uncommitted-work recovery this leaves stranded, dispatched
            // before the reverify gate is allowed to fail the run on it.
            "Committed the stranded file.",
            // Cycle 2: only conformance is still active, so it gets one Verify pass.
            "Criteria met.\n\nVERDICT: merge-ready",
            // Cycle 3: the mandatory final full pass, both lenses fresh.
            "Confirmed clean.\n\nVERDICT: merge-ready",
            "Confirmed clean too.\n\nVERDICT: merge-ready");
        executor.OnSpawnByIndex[2] = () =>
            File.WriteAllText(Path.Combine(worktreePath, "half-done.cs"), "left behind, uncommitted\n");
        executor.OnSpawnByIndex[3] = () =>
        {
            Git(worktreePath, "add -A");
            Git(worktreePath, "-c user.name=Test -c user.email=test@test commit -q -m recovered");
        };

        // The fix session's own process (pid 6002, the third spawn) left a background dotnet test
        // still running behind it — a descendant process still alive when its final message
        // arrived (task: the daemon terminates a completed session's process tree before it
        // starts any gate or another session in the same worktree). Its own root is kept alive
        // for slightly more than one poll (task: a leg's recorded token usage), the shape a real
        // session takes, so SnapshotDescendants gets the chance to see the descendant before the
        // root's own death reparents it out of reach.
        const int fixSessionProcessId = 6_002;
        const int lingeringDescendantProcessId = 6_999;
        executor.Processes.MarkDescendant(fixSessionProcessId, lingeringDescendantProcessId);
        executor.SpawnIndexesThatStayAliveBriefly.Add(2);

        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue("the recovery session committed the stranded file, so the gates and the rest of the loop proceed normally");
        executor.Spawns.Should().HaveCount(
            7, "the cycle's two passes, the fix session, the automatic recovery it earned, the cycle-2 "
                + "verify pass, and the final full pass's two fresh reads");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;

        // The named outcome: h9k task show and the run log say the session ended waiting on a
        // background gate rather than reporting the fix as undeclared.
        run.LastFixEndedWaitingOnBackgroundGate.Should().BeTrue();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewFixCompleted>().Should().ContainSingle().Which.Outcome.Should().Be(
            ReviewFixOutcome.WaitingOnBackgroundGate, "not the generic Unknown every other undeclared ending shares");

        // The recovery dispatch: the fix leg earned its own automatic recovery rather than the
        // run failing before its gates.
        run.UncommittedWorkRecoveries.Should().ContainSingle().Which.Leg.Should().Be(RunSessionLeg.Fix);
        run.UncommittedWorkRecoveries.Single().RecoveredCleanly.Should().BeTrue(
            "the scripted recovery session actually committed the stranded file");
        run.State.Should().NotBe(RunState.Failed, "the automatic recovery resolved the dirty tree before any gate could fail on it");

        // The process-tree cleanup: the fix session's own lingering descendant is gone before the
        // reverify gate — which the recovery session that commits the stranded file runs ahead
        // of — ever touches the same worktree. By the time completion is confirmed, the fix
        // session's own root is already dead (SessionResultWaiter only finalizes off a dead
        // process — discovery cc9b7aec), so TerminateTree itself finds nothing left to walk from;
        // the descendant is instead terminated individually off the pre-death SnapshotDescendants
        // view SessionResultWaiter.TerminateLingering keeps (task: a leg's recorded token usage).
        executor.Processes.Terminations.Should().Contain(
            termination => termination.ProcessId == lingeringDescendantProcessId,
            "the fix session's own backgrounded test run is torn down the instant its terminal result arrives");
        executor.Processes.IsAlive(lingeringDescendantProcessId, Now).Should().BeFalse(
            "a lingering child gone before the next gate starts is exactly what this task requires");
    }

    /// <summary>
    /// Copilot review, PR #62: a HEAD match alone cannot tell "the same gates ran" from "a human
    /// changed the project's verify commands between the last full gate and now" — the tip stays
    /// put, but the gates about to be trusted at Settling never themselves ran at full scope.
    /// Seeds the identical skip-eligible shape the sibling test above exercises, then changes the
    /// project's verify commands in the same window right after the reverify gate has already
    /// recorded its pass against the ORIGINAL commands and before the cycle's Verify-mode reviewer
    /// spawns, which is the same window a project setting change mid-run would land in. The
    /// Settling branch's own mandatory gate must run anyway rather than trust a full pass recorded
    /// against gates that no longer exist.
    /// </summary>
    [Fact]
    public async Task A_verify_commands_change_after_the_reverify_gate_still_runs_the_settling_gate()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, string worktreePath, Guid projectId) =
            await SeedVerifiedRunWithTestGateAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "1. `Auth.cs:42` — the limiter never resets.\n\nVERDICT: needs-fixes",
            "Nothing survived verification.\n\nVERDICT: merge-ready",
            "Reset the limiter window.\n\nRESOLUTION: fixed",
            // Cycle 2: only conformance is still active, so it gets one Verify pass.
            "Criteria met.\n\nVERDICT: merge-ready",
            // Cycle 3: the mandatory final full pass, both lenses fresh.
            "Confirmed clean.\n\nVERDICT: merge-ready",
            "Confirmed clean too.\n\nVERDICT: merge-ready");
        executor.OnSpawnByIndex[2] = () => CommitDocOnlyChange(worktreePath);
        executor.OnSpawnByIndex[3] = () => ChangeVerifyCommandsAsync(store, projectId, cts.Token).GetAwaiter().GetResult();

        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        List<VerificationPassed> passes = [.. events.OfType<VerificationPassed>()];
        passes.Should().HaveCount(
            3, "the run's own first gate pass, the cycle-2 reverify gate that fell back to full, and the "
                + "Settling branch's own mandatory gate — run again because the verify commands changed "
                + "since the reverify gate ran even though HEAD never moved");
        passes[^1].RanFullScope.Should().BeTrue("the mandatory Settling gate always runs full-scope");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, adversarial lens: the sibling test above changes verify
    /// commands during a cycle-2 Verify reverify, where <c>NeedsFullGateBeforeSettling</c> is
    /// already true on its own (the mode check alone forces entry). It never exercises the plain
    /// cycle-1 Discovery path — both lenses clean on their first look, no fix ever dispatched, no
    /// human involved — which is the ONE Settling entry neither the mode/fix check nor
    /// <see cref="RunAggregate.HumanEndedTheLoop"/> ever visits, so it is the one place a verify
    /// commands change would previously go unseen entirely: the old code defaulted
    /// <c>gateAlreadyRanFullOverCurrentHead</c> to a bare <c>true</c> on this path without ever
    /// comparing anything, and fell straight through to <c>SettleAsync</c>. The seed's own initial
    /// gate pass records a genuinely comparable full scope (real HEAD, real fingerprint) so this
    /// change is the only thing that moves. The mandatory gate still runs, but the run settles
    /// straight after it rather than paying for a whole second <see cref="ReviewMode.FinalFullPass"/>
    /// round over a tip both lenses already read clean this very cycle (independent pre-PR review,
    /// cycle 3, adversarial lens: a fingerprint-only trigger is not a moved HEAD or a dispatched fix,
    /// so it never earns another reviewer pass — <c>NeedsFullGateBeforeSettling</c> is what decides
    /// that, and it is false on this clean, fix-free path).
    /// </summary>
    [Fact]
    public async Task A_clean_discovery_only_convergence_still_runs_the_settling_gate_over_the_current_verify_commands()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _, Guid projectId) = await SeedVerifiedRunWithTestGateAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "Every acceptance criterion is met.\n\nVERDICT: merge-ready",
            "Nothing survived verification.\n\nVERDICT: merge-ready");
        // Fires as the cycle's first pass spawns — the same window a human's own out-of-band
        // `h9k project set --verify` would land in, since nothing else touches the worktree or
        // the run stream between the seeded gate and Settling on this clean, fix-free path.
        executor.OnSpawnByIndex[0] = () => ChangeVerifyCommandsAsync(store, projectId, cts.Token).GetAwaiter().GetResult();

        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(
            2, "the verify-commands change forces the mandatory Settling gate, but the diff itself "
                + "already converged clean under this cycle's own two-lens read, so the run settles "
                + "right after the gate instead of paying for a second FinalFullPass over an unchanged tip");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        List<VerificationPassed> passes = [.. events.OfType<VerificationPassed>()];
        passes.Should().HaveCount(
            2, "the run's own first (seeded) gate pass, plus the Settling branch's own mandatory gate — "
                + "run again because the verify commands changed after that seeded gate ran, even though "
                + "this clean, human-free, fix-free path never asked the mode/fix or human check about it");
        passes[^1].RanFullScope.Should().BeTrue("the mandatory Settling gate always runs full-scope");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 2, adversarial lens: the Reverify branch's own
    /// idempotent-resume guard (task: interactive mode becomes a recorded property of the task)
    /// used to compare HEAD alone, unlike its sibling <see cref="GateAlreadyRanFullOverCurrentHeadAsync"/>
    /// guard for Settling. An interactive-mode run parks at the "fix to re-review" boundary once its
    /// reverify gate passes, and that park can sit for "days" by design — HEAD never moves while it
    /// does. An operator changing the project's verify commands during that window is invisible to a
    /// HEAD-only comparison: on <c>h9k review proceed</c>, the resumed Reverify case saw the identical
    /// HEAD it last gated and skipped the gate outright, dispatching the next review cycle over a tip
    /// the new verify commands had never actually run against.
    /// </summary>
    [Fact]
    public async Task A_verify_commands_change_while_parked_at_the_reverify_interactive_gate_still_runs_the_gate_on_resume()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, string worktreePath, Guid projectId) = await SeedVerifiedRunWithTestGateAsync(
            store, cts.Token, interactiveMode: true, reviewStageComposition: ReviewStageComposition.AdversarialOnly);

        // Interactive mode's own "build done to review" boundary parks before cycle 1 ever dispatches.
        bool step1 = await NewEngine(store, new ScriptedExecutor()).ReviewAsync(runId, taskId, cts.Token);
        step1.Should().BeFalse("interactive mode parks before cycle 1's review ever dispatches");

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewBoundaryApproved(runId, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        // Cycle 1: the sole (adversarial) lens finds something, parking at "review verdict to fix".
        ScriptedExecutor discoveryExecutor = new(
            "FINDING: severity=medium; scope=in-scope; at=Widget.cs:1\n"
            + "Defect: the widget never initializes.\n\nVERDICT: needs-fixes");
        bool step2 = await NewEngine(store, discoveryExecutor).ReviewAsync(runId, taskId, cts.Token);
        step2.Should().BeFalse("interactive mode parks again before the fix session ever dispatches");
        discoveryExecutor.Spawns.Should().HaveCount(1);

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewBoundaryApproved(runId, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        // The fix session lands a real commit — the reverify gate that follows sees a moved HEAD,
        // exactly the shape a genuine fix leaves behind, and then parks at "fix to re-review".
        ScriptedExecutor fixExecutor = new("Initialized the widget.\n\nRESOLUTION: fixed");
        fixExecutor.OnSpawnByIndex[0] = () => CommitDocOnlyChange(worktreePath);
        bool step3 = await NewEngine(store, fixExecutor).ReviewAsync(runId, taskId, cts.Token);
        step3.Should().BeFalse("interactive mode parks again at the fix-to-re-review boundary");
        fixExecutor.Spawns.Should().HaveCount(1);

        await using IQuerySession verifyQueryAfterFix = store.QuerySession();
        List<VerificationPassed> passesAfterFix = [.. (await verifyQueryAfterFix.Events.FetchStreamAsync(runId, token: cts.Token))
            .Select(e => e.Data).OfType<VerificationPassed>()];
        passesAfterFix.Should().HaveCount(2, "the seeded initial gate, plus the reverify gate the fix's own moved HEAD earned");
        string gatedHeadSha = passesAfterFix[^1].HeadSha!;

        // While parked here — a park the design explicitly allows to sit for days — an operator
        // changes the project's verify commands. HEAD does not move.
        await ChangeVerifyCommandsAsync(store, projectId, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewBoundaryApproved(runId, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        // Another needs-fixes verdict, deliberately: a merge-ready verdict here would let the
        // track conclude and carry the run on into Settling, whose OWN fingerprint check would
        // then mask exactly the defect this test exists to catch (that check already ran the
        // gate correctly regardless of this fix). Needs-fixes instead parks straight back at
        // "review verdict to fix" with no further gate involved, so every VerificationPassed
        // recorded in this step is attributable to the Reverify branch's own guard alone.
        ScriptedExecutor resumeExecutor = new(
            "FINDING: severity=medium; scope=in-scope; at=Widget.cs:1\n"
            + "Defect: still not initialized correctly.\n\nVERDICT: needs-fixes");
        bool step4 = await NewEngine(store, resumeExecutor).ReviewAsync(runId, taskId, cts.Token);
        step4.Should().BeFalse("interactive mode parks again at the review-verdict-to-fix boundary");
        resumeExecutor.Spawns.Should().HaveCount(1, "only the cycle-2 review pass itself, no fix session yet");

        await using IQuerySession verifyQueryAfterResume = store.QuerySession();
        List<VerificationPassed> passesAfterResume = [.. (await verifyQueryAfterResume.Events.FetchStreamAsync(runId, token: cts.Token))
            .Select(e => e.Data).OfType<VerificationPassed>()];
        passesAfterResume.Should().HaveCount(
            passesAfterFix.Count + 1,
            "HEAD never moved while parked, but the project's verify commands changed underneath the park — "
                + "the Reverify branch's own idempotent-resume guard must not mistake an unchanged HEAD for "
                + "an unchanged gate, the same guarantee GateAlreadyRanFullOverCurrentHeadAsync already gives Settling. "
                + "This step never reaches Settling (a needs-fixes verdict keeps the track active), so this count "
                + "isolates the Reverify branch's own gate from Settling's already-correct one");
        VerificationPassed reverifyGateOnResume = passesAfterResume[^1];
        reverifyGateOnResume.HeadSha.Should().Be(gatedHeadSha, "no commit landed between the park and the resume");
        reverifyGateOnResume.VerifyCommandsFingerprint.Should().NotBe(
            passesAfterFix[^1].VerifyCommandsFingerprint,
            "the resumed gate must run under the NEW verify commands, not silently reuse the stale pass recorded before the change");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, adversarial lens: <c>EnsureInteractiveProceedAsync</c>
    /// used to read <c>InteractiveModeEnabled</c> off <see cref="ReviewEngine"/>'s own
    /// <c>ReviewContext.Task</c>, loaded once at the top of <c>DriveAsync</c> and held fixed for
    /// the run's whole review phase — which can span real wall-clock minutes to hours. An operator
    /// running <c>h9k task revise --clear-interactive-mode</c> while a cycle's review pass and fix
    /// session are still in flight would not be seen until this run's NEXT top-level
    /// <c>ReviewAsync</c> entry, so the very next phase boundary inside the SAME call would still
    /// park on the stale snapshot even though the flag was already off. <c>OnSpawnByIndex[0]</c>
    /// clears it mid-flight, synchronously, the moment the cycle-1 review pass spawns — before the
    /// "review verdict to fix" boundary check that follows moments later in this same call — so
    /// the fix session dispatches instead of parking, and the whole run settles in one call with no
    /// second <see cref="ReviewParked"/> ever recorded. The fix session's own dispatched prompt is
    /// exposed to the identical staleness (independent pre-PR review, cycle 1, adversarial lens on
    /// <c>DispatchFixSessionAsync</c> itself): built from the same stale <c>ReviewContext.Task</c>
    /// snapshot, it would still tell the agent to report outbound milestones and assert a
    /// phase-boundary park that the clear already made untrue, so this test's own assertion on the
    /// fix session's prompt content covers that half too.
    /// </summary>
    [Fact]
    public async Task Clearing_interactive_mode_mid_flight_is_seen_at_the_very_next_boundary_in_the_same_call()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, string worktreePath, _) = await SeedVerifiedRunWithTestGateAsync(
            store, cts.Token, interactiveMode: true, reviewStageComposition: ReviewStageComposition.AdversarialOnly);

        // Interactive mode's own "build done to review" boundary parks before cycle 1 ever dispatches.
        bool step1 = await NewEngine(store, new ScriptedExecutor()).ReviewAsync(runId, taskId, cts.Token);
        step1.Should().BeFalse("interactive mode parks before cycle 1's review ever dispatches");

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewBoundaryApproved(runId, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor executor = new(
            // Cycle 1: the sole (adversarial) lens finds something, calling for a fix.
            "FINDING: severity=medium; scope=in-scope; at=Widget.cs:1\n"
            + "Defect: the widget never initializes.\n\nVERDICT: needs-fixes",
            "Initialized the widget.\n\nRESOLUTION: fixed",
            // Cycle 2: only the adversarial track is active, so it gets one Verify pass.
            "The widget initializes now.\n\nVERDICT: merge-ready",
            // The mandatory final full pass, fresh, over the sole lens this composition ever runs.
            "Criteria still met.\n\nVERDICT: merge-ready");
        executor.OnSpawnByIndex[0] = () => ClearInteractiveModeAsync(store, taskId, cts.Token).GetAwaiter().GetResult();
        executor.OnSpawnByIndex[1] = () => CommitDocOnlyChange(worktreePath);

        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue(
            "once the flag is cleared mid-flight, no further boundary in this same call should park — " +
            "the whole cycle should run through to a settled merge-ready verdict");
        executor.Spawns.Should().HaveCount(
            4, "review, fix, the cycle-2 verify pass, and the mandatory final full pass — none of the " +
                "three later boundaries paused for a proceed that a stale InteractiveModeEnabled read would have asked for");
        executor.Spawns[1].Prompt.Should().NotContain(
            "## Reporting to the human (interactive mode)",
            "the fix session (spawn index 1) dispatches after the flag was cleared mid-flight at spawn index 0 — " +
                "its own prompt must read the flag fresh rather than off ReviewContext.Task, which was loaded " +
                "before the clear and would otherwise still say this task reports outbound and parks for a proceed " +
                "that will not happen (independent pre-PR review, cycle 1, adversarial lens)");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewParked>().Should().ContainSingle(
            "only the very first boundary — parked before the flag was ever cleared — should have parked; " +
            "a stale read would have produced a second ReviewParked at the review-verdict-to-fix boundary");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 3, adversarial lens: <c>VerifyCommandsFingerprintMatchesAsync</c>
    /// must read a never-recorded <see cref="RunAggregate.LastGateVerifyCommandsFingerprint"/> — a
    /// stream written before that field existed — as "unknown", not as "the gates changed". Seeds
    /// the same genuinely-comparable-full-scope shape (real <c>RanFullScope</c>, real
    /// <c>HeadSha</c>) the sibling tests above rely on, but with no fingerprint ever recorded on
    /// that seeded pass, and nothing else touches the worktree, the project, or the run stream. A
    /// clean cycle-1 Discovery convergence must settle without paying for a redundant Settling gate
    /// or an extra review round: the fingerprint question is moot on a stream that never observed
    /// one, exactly as it already is when <c>RanFullScope</c>/<c>HeadSha</c> themselves are missing.
    /// </summary>
    [Fact]
    public async Task A_never_recorded_verify_commands_fingerprint_settles_without_a_redundant_gate()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _, _) = await SeedVerifiedRunWithTestGateAsync(
            store, cts.Token, recordVerifyCommandsFingerprint: false);

        ScriptedExecutor executor = new(
            "Every acceptance criterion is met.\n\nVERDICT: merge-ready",
            "Nothing survived verification.\n\nVERDICT: merge-ready");

        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(
            2, "an unrecorded fingerprint is unknown, not a detected change — it must never force the "
                + "mandatory Settling gate or an extra review round on its own");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<VerificationPassed>().Should().ContainSingle(
            "only the run's own first (seeded) gate pass — the Settling branch never re-gates over a "
                + "fingerprint that was simply never observed");
    }

    /// <summary>
    /// Task: a lap reviews only what it changed. A ReviewFeedback or FailingChecks follow-up's own
    /// opening Discovery cycle seeds its diff instruction from the pull request head the previous
    /// run pushed — recorded on this run's own RunDispatched — so both lenses read the lap's own
    /// change, not the whole branch, on cycle 1. Critically, a clean verdict there must NOT settle
    /// the run the way an ordinary full-scope Discovery cycle's clean verdict does: the mandatory
    /// FinalFullPass still has to run, at full scope, because the scoped opening cycle never
    /// actually read the whole branch — "nothing merges on scoped green alone" holds for a scoped
    /// lap too, and this is the regression the MaySettleReason exclusion clause exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_follow_ups_opening_discovery_cycle_scopes_to_the_seeded_head_and_still_pays_the_final_full_pass()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, string worktreePath, _) = await SeedVerifiedRunWithTestGateAsync(
            store, cts.Token, seedOpeningReviewSinceSha: true);
        string seedSha = GitOutput(worktreePath, "rev-parse HEAD");

        ScriptedExecutor executor = new(
            "Nothing survived verification.\n\nVERDICT: merge-ready",
            "Nothing survived verification.\n\nVERDICT: merge-ready",
            "Confirmed clean.\n\nVERDICT: merge-ready",
            "Confirmed clean too.\n\nVERDICT: merge-ready");

        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(
            4, "the scoped opening cycle's two lenses plus the mandatory FinalFullPass's two lenses — "
                + "a scoped opening lap converging clean must still pay for the mandatory final pass");
        executor.Spawns[0].Prompt.Should().Contain(
            $"git diff {seedSha}..HEAD", "the opening cycle's conformance lens reads only the lap's own change");
        executor.Spawns[1].Prompt.Should().Contain(
            $"git diff {seedSha}..HEAD", "the opening cycle's adversarial lens gets the identical scoped boundary");
        executor.Spawns[2].Prompt.Should().Contain(
            "git diff origin/main...HEAD",
            "the opening cycle never latched a full-scope boundary, so the mandatory final pass reads the whole branch");
        executor.Spawns[3].Prompt.Should().Contain("git diff origin/main...HEAD");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewDispatched>().Select(e => (e.Cycle, e.Mode, e.SinceSha)).Should().Equal(
        [
            (1, ReviewMode.Discovery, seedSha),
            (1, ReviewMode.Discovery, seedSha),
            (2, ReviewMode.FinalFullPass, null),
            (2, ReviewMode.FinalFullPass, null),
        ]);

        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.OpeningReviewSinceShaApplied.Should().Be(
            seedSha, "the opening cycle's own scope actually applied, so h9k task show renders the observed "
                + "boundary rather than only the dispatch-time seed");
    }

    /// <summary>
    /// Task: the mandatory FinalFullPass rereads only the commits no full-scope pass has already
    /// read (Decisions Log #115). The Discovery cycle's own real HEAD, captured before any fix
    /// lands, is what the mandatory final pass must scope its own diff instruction to — not the
    /// whole branch again — once a fix has moved HEAD in between.
    /// </summary>
    [Fact]
    public async Task Final_full_pass_scopes_its_diff_instruction_to_the_discovery_cycles_own_head()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, string worktreePath, _) = await SeedVerifiedRunWithTestGateAsync(store, cts.Token);
        string discoveryHeadSha = GitOutput(worktreePath, "rev-parse HEAD");

        ScriptedExecutor executor = new(
            "1. `Auth.cs:42` — the limiter never resets.\n\nVERDICT: needs-fixes",
            "Nothing survived verification.\n\nVERDICT: merge-ready",
            "Reset the limiter window.\n\nRESOLUTION: fixed",
            // Cycle 2: only conformance is still active, so it gets one Verify pass.
            "Criteria met.\n\nVERDICT: merge-ready",
            // Cycle 3: the mandatory final full pass, both lenses fresh.
            "Confirmed clean.\n\nVERDICT: merge-ready",
            "Confirmed clean too.\n\nVERDICT: merge-ready");
        executor.OnSpawnByIndex[2] = () => CommitDocOnlyChange(worktreePath);

        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(6);
        executor.Spawns[4].Prompt.Should().Contain(
            $"git diff {discoveryHeadSha}..HEAD",
            "the final pass's own conformance lens must read only the commits since the Discovery "
                + "cycle's own full-scope read, not the whole branch again");
        executor.Spawns[5].Prompt.Should().Contain(
            $"git diff {discoveryHeadSha}..HEAD",
            "the final pass's own adversarial lens gets the identical scoped boundary");
    }

    /// <summary>
    /// The degrade-rather-than-guess fallback (task: the mandatory FinalFullPass rereads only the
    /// commits no full-scope pass has already read, Decisions Log #115): a recorded full-scope
    /// boundary is verified against the worktree's current HEAD before it is trusted, not merely
    /// checked for null. Simulates the gap that verification exists for — a history rewrite between
    /// cycles (a fix session's own commit-plan autosquash, or a rebase-onto-main follow-up) that
    /// strands the Discovery cycle's own recorded head outside HEAD's ancestry entirely — and
    /// confirms the mandatory final pass falls back to the full base-branch diff rather than handing
    /// the reviewer a boundary that no longer resolves.
    /// </summary>
    [Fact]
    public async Task Final_full_pass_falls_back_to_the_full_diff_when_the_recorded_boundary_no_longer_resolves()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, string worktreePath, _) = await SeedVerifiedRunWithTestGateAsync(store, cts.Token);
        string discoveryHeadSha = GitOutput(worktreePath, "rev-parse HEAD");

        ScriptedExecutor executor = new(
            "1. `Auth.cs:42` — the limiter never resets.\n\nVERDICT: needs-fixes",
            "Nothing survived verification.\n\nVERDICT: merge-ready",
            "Reset the limiter window.\n\nRESOLUTION: fixed",
            "Criteria met.\n\nVERDICT: merge-ready",
            "Confirmed clean.\n\nVERDICT: merge-ready",
            "Confirmed clean too.\n\nVERDICT: merge-ready");
        executor.OnSpawnByIndex[2] = () => CommitDocOnlyChange(worktreePath);
        // Fires as the cycle-2 Verify pass spawns — after that cycle's own reverify gate already
        // ran, so only the boundary resolution ahead of the mandatory final pass sees the rewrite.
        executor.OnSpawnByIndex[3] = () => RewriteHistoryDroppingAncestor(worktreePath, "task/review-me");

        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(6);
        executor.Spawns[4].Prompt.Should().Contain(
            "git diff origin/main...HEAD",
            "the recorded boundary no longer resolves against HEAD after the rewrite, so the mandatory "
                + "final pass must fall back to the full diff instruction rather than trust a stale sha");
        executor.Spawns[4].Prompt.Should().NotContain($"git diff {discoveryHeadSha}..HEAD");
    }

    /// <summary>
    /// Task: a human at the wheel takes the fix role herself — the lever's whole shape in one
    /// pass. At the review-verdict-to-fix park the human commits her own fix and runs
    /// <c>h9k review fixed</c>; the loop re-enters at the existing fix-to-re-review boundary, so
    /// the gates run over her commits and the next review pass is scoped to the parked cycle's own
    /// head — byte-identically to what a fix session's commits would have got — and no fix session
    /// is ever dispatched. The budget half of the same criterion rides along here rather than in a
    /// test of its own, because it is only checkable against a run that actually took this path:
    /// <see cref="RunAggregate.ReviewFixRuns"/> (the automatic count) stays at zero while
    /// <see cref="RunAggregate.HumanFixRounds"/> reaches one, and the cycle her fix opens is
    /// counted exactly as one a fix session opens is — same <see cref="RunAggregate.ReviewCycle"/>
    /// increment, same per-track cap arithmetic, same <see cref="RunAggregate.FixDispatchedThisCycle"/>
    /// owed to a fresh-context reader before the run may settle.
    /// </summary>
    [Fact]
    public async Task A_human_fix_re_enters_review_at_the_fix_to_re_review_boundary_with_no_fix_session()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, string worktreePath, _) = await SeedVerifiedRunWithTestGateAsync(
            store, cts.Token, interactiveMode: true, reviewStageComposition: ReviewStageComposition.AdversarialOnly);

        // Interactive mode's own "build done to review" boundary, then cycle 1's own verdict.
        (await NewEngine(store, new ScriptedExecutor()).ReviewAsync(runId, taskId, cts.Token)).Should().BeFalse();
        await ApproveBoundaryAsync(store, runId, cts.Token);

        ScriptedExecutor discovery = new(
            "FINDING: severity=high; scope=in-scope; at=Widget.cs:1\n"
            + "Defect: the widget never initializes.\n\nVERDICT: needs-fixes");
        (await NewEngine(store, discovery).ReviewAsync(runId, taskId, cts.Token)).Should().BeFalse(
            "the needs-fixes verdict parks at the review-verdict-to-fix boundary");
        discovery.Spawns.Should().HaveCount(1, "the review pass only — no fix session yet");

        await using (IQuerySession parked = store.QuerySession())
        {
            RunDetails details = (await parked.LoadAsync<RunDetails>(runId, cts.Token))!;
            details.ParkedIsInteractiveGate.Should().BeTrue();
            details.ParkedReason.Should().Contain(
                $"h9k review fixed {taskId}",
                "the park text names all four choices at this boundary, the human's own fix among them");
        }

        string parkedTip = GitOutput(worktreePath, "rev-parse HEAD");

        // She fixes it herself and commits, then hands the branch back with one verb.
        CommitDocOnlyChange(worktreePath);
        (await RunReviewFixedAsync(store, taskId, noChange: null, cts.Token)).Should().Be(ExitCodes.Ok);

        // Re-entered at the fix-to-re-review boundary, which is a boundary of its own: her fix
        // answered the review-verdict-to-fix question, so the gates run over her commits and then
        // this next boundary asks its own — exactly the shape a fix session's completion leaves
        // behind, where the proceed that bought the fix is likewise already spent.
        ScriptedExecutor reverify = new();
        (await NewEngine(store, reverify).ReviewAsync(runId, taskId, cts.Token)).Should().BeFalse(
            "the fix-to-re-review boundary holds for her own go, as it does after a fix session");
        reverify.Spawns.Should().BeEmpty("the gates are not a session, and no fix agent was ever needed");
        await ApproveBoundaryAsync(store, runId, cts.Token);

        // Cycle 2: one Verify pass reads what she committed.
        ScriptedExecutor afterFix = new(
            "FINDING: severity=high; scope=in-scope; at=Widget.cs:1\n"
            + "Defect: still not initialized.\n\nVERDICT: needs-fixes");
        (await NewEngine(store, afterFix).ReviewAsync(runId, taskId, cts.Token)).Should().BeFalse(
            "the cycle her fix opened comes back needs-fixes and parks at the same boundary again");
        afterFix.Spawns.Should().HaveCount(
            1, "one review pass and nothing else — a human fix dispatches no fix session of its own");
        afterFix.Spawns[0].Prompt.Should().Contain(
            "Independent review",
            "the one session dispatched after her fix is a reviewer, never a fix agent");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewFixDispatched>().Should().BeEmpty(
            "no fix session was ever dispatched on this run — that is the whole point of the lever");

        ReviewHumanFixApplied humanFix = events.OfType<ReviewHumanFixApplied>().Should().ContainSingle().Subject;
        humanFix.Cycle.Should().Be(1, "the cycle whose findings she fixed");
        humanFix.HeadSha.Should().Be(GitOutput(worktreePath, "rev-parse HEAD"));
        humanFix.NoChangeReason.Should().BeNull("commits landed, so there is nothing to explain");
        humanFix.PushedToRemote.Should().BeFalse("no pull request is open yet, so nothing needed publishing");

        List<ReviewDispatched> dispatches = [.. events.OfType<ReviewDispatched>()];
        dispatches.Should().HaveCount(2);
        dispatches[1].Cycle.Should().Be(2, "her fix opens a review cycle exactly as a fix session's would");
        dispatches[1].Mode.Should().Be(
            ReviewMode.Verify, "the same mode the fix-to-re-review boundary hands a fix session's own commits");
        dispatches[1].SinceSha.Should().Be(
            parkedTip,
            "the next pass reads her commits since the parked tip — the identical boundary a fix "
                + "session's commits would have been scoped against (RunAggregate.CycleHeadSha)");

        // The reverify gate ran over her commit rather than being skipped as an idempotent resume.
        List<VerificationPassed> gates = [.. events.OfType<VerificationPassed>()];
        gates.Should().HaveCount(2, "the seeded opening gate, plus the reverify gate her commit earned");
        gates[^1].HeadSha.Should().Be(humanFix.HeadSha);

        RunAggregate run = (await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cts.Token))!;
        run.ReviewFixRuns.Should().Be(
            0, "a human fix counts against no automatic fix budget and no cap a fix session consumes");
        run.HumanFixRounds.Should().Be(1, "counted separately, so neither number misreports who did the work");
        run.ReviewCycle.Should().Be(2, "the review cycle it opened counts exactly as one a fix session opens does");

        RunDetails view = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        view.HumanFixes.Should().ContainSingle().Which.Cycle.Should().Be(1);
        view.HumanFixes[0].NoChangeReason.Should().BeNull();
    }

    /// <summary>
    /// Task: a human at the wheel takes the fix role herself, second criterion — the
    /// uncommitted-files refusal, naming them. Nothing is recorded and the run stays parked, so
    /// the lever is still there once she commits.
    /// </summary>
    [Fact]
    public async Task A_human_fix_is_refused_over_an_uncommitted_worktree_and_names_the_files()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, string worktreePath, _) =
            await SeedRunParkedAtTheReviewVerdictToFixBoundaryAsync(store, cts.Token);

        // A tracked file edited but not committed, and an untracked file under src/ — the daemon's
        // own pre-gate check fails a run over either, so both block here (WorktreeGitStatus).
        File.WriteAllText(Path.Combine(worktreePath, "Widget.cs"), "class Widget { int x; }\n");
        Directory.CreateDirectory(Path.Combine(worktreePath, "src"));
        File.WriteAllText(Path.Combine(worktreePath, "src", "Stranded.cs"), "class Stranded { }\n");

        Func<Task> act = () => RunReviewFixedAsync(store, taskId, noChange: null, cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>())
            .WithMessage("*still holds uncommitted work*")
            .WithMessage("*Widget.cs*")
            .WithMessage("*src/Stranded.cs*");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewHumanFixApplied>().Should().BeEmpty("a refusal records nothing");
        RunDetails view = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        view.State.Should().Be(RunState.ReviewParked, "the run is still parked, so the lever is still there");
        view.ParkedIsInteractiveGate.Should().BeTrue();
    }

    /// <summary>
    /// The same guard <c>h9k task deliver</c> holds, on this lever too (round-one self-review, the
    /// blast-radius class sweep): a worktree left checked out somewhere else passes every other
    /// check here — the tree reads clean and HEAD reads moved, because it is a different commit —
    /// while the push, the reverify gate and the next review pass would each read something
    /// different from the others.
    /// </summary>
    [Fact]
    public async Task A_human_fix_is_refused_when_the_worktree_is_not_on_the_claim_branch()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, string worktreePath, _) =
            await SeedRunParkedAtTheReviewVerdictToFixBoundaryAsync(store, cts.Token);

        // A commit on another branch: clean tree, moved HEAD, wrong branch — exactly the shape
        // every other check here reads as fine.
        Git(worktreePath, "checkout -q -b somewhere-else");
        CommitDocOnlyChange(worktreePath);

        Func<Task> act = () => RunReviewFixedAsync(store, taskId, noChange: null, cts.Token);
        (await act.Should().ThrowAsync<DomainConflictException>())
            .WithMessage("*checked out to 'somewhere-else'*")
            .WithMessage("*not its claim branch 'task/review-me'*");

        await using IQuerySession query = store.QuerySession();
        (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)
            .OfType<ReviewHumanFixApplied>().Should().BeEmpty("a refusal records nothing");
    }

    /// <summary>
    /// Task: a human at the wheel takes the fix role herself, second criterion — the unmoved-tip
    /// refusal. Without commits the next review pass would be handed an empty diff, so the plain
    /// verb is refused; <c>--no-change "&lt;why&gt;"</c> is the deliberate override, and its reason
    /// is recorded and carried into the next review pass the way a resolve reason is.
    /// </summary>
    [Fact]
    public async Task A_human_fix_with_an_unmoved_tip_is_refused_until_no_change_states_why()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, string worktreePath, _) =
            await SeedRunParkedAtTheReviewVerdictToFixBoundaryAsync(store, cts.Token);
        string parkedTip = GitOutput(worktreePath, "rev-parse HEAD");

        Func<Task> act = () => RunReviewFixedAsync(store, taskId, noChange: null, cts.Token);
        (await act.Should().ThrowAsync<DomainConflictException>())
            .WithMessage("*the same tip review cycle 1 was parked at*")
            .WithMessage("*--no-change*");

        (await RunReviewFixedAsync(
            store, taskId, noChange: "  The limiter already resets in the retry sweep - confirmed by reading it.  ",
            cts.Token)).Should().Be(ExitCodes.Ok, "--no-change is the deliberate override");

        await using IQuerySession query = store.QuerySession();
        ReviewHumanFixApplied humanFix = (await query.Events.FetchStreamAsync(runId, token: cts.Token))
            .Select(e => e.Data).OfType<ReviewHumanFixApplied>()
            .Should().ContainSingle().Subject;
        humanFix.NoChangeReason.Should().Be(
            "The limiter already resets in the retry sweep - confirmed by reading it.",
            "trimmed the way an h9k review resolve reason is, so a blank never reads as 'none recorded'");
        humanFix.HeadSha.Should().Be(parkedTip, "the honest observation: the tip did not move");

        // Carried into the next review pass through the settled-rulings surface, exactly the way a
        // resolve reason is, and read as a dismissal rather than an order. Two engine calls with
        // her go in between, because the fix-to-re-review boundary she now sits at is its own.
        await NewEngine(store, new ScriptedExecutor()).ReviewAsync(runId, taskId, cts.Token);
        await ApproveBoundaryAsync(store, runId, cts.Token);
        ScriptedExecutor afterNoChange = new("Nothing new here.\n\nVERDICT: merge-ready");
        await NewEngine(store, afterNoChange).ReviewAsync(runId, taskId, cts.Token);
        afterNoChange.Spawns.Should().NotBeEmpty();
        afterNoChange.Spawns[0].Prompt.Should().Contain("## Fixes a human applied by hand on this task");
        afterNoChange.Spawns[0].Prompt.Should().Contain(
            "The limiter already resets in the retry sweep - confirmed by reading it.");
        afterNoChange.Spawns[0].Prompt.Should().Contain(
            "**a fix recorded as no-change** is a dismissal",
            "the reviewer is told how to read it, not merely handed the text");
    }

    /// <summary>
    /// The same refusal in the other direction (independent pre-PR review, cycle 1, both lenses):
    /// <c>--no-change</c> over a tip that DID move is refused rather than recorded. The two are
    /// mutually exclusive answers to the same cycle, and accepting both told every later
    /// fresh-context pass “the finding was read, nothing was deliberately changed” about a cycle
    /// whose human-authored commits are sitting in the very diff that pass is reading.
    /// </summary>
    [Fact]
    public async Task A_human_fix_refuses_no_change_over_a_tip_that_did_move()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, string worktreePath, _) =
            await SeedRunParkedAtTheReviewVerdictToFixBoundaryAsync(store, cts.Token);
        string parkedTip = GitOutput(worktreePath, "rev-parse HEAD");

        // Her fix for one finding landed; the flag is the natural-but-wrong way to say the other
        // finding needed nothing.
        CommitDocOnlyChange(worktreePath);
        Func<Task> act = () => RunReviewFixedAsync(
            store, taskId, noChange: "The other finding is already handled by the retry sweep.", cts.Token);
        (await act.Should().ThrowAsync<DomainConflictException>())
            .WithMessage("*has moved to*")
            .WithMessage("*--no-change*");

        await using (IQuerySession refused = store.QuerySession())
        {
            (await refused.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)
                .OfType<ReviewHumanFixApplied>().Should().BeEmpty("a refusal records nothing");
        }

        // The plain verb over the very same commits is what the branch actually did, and it is
        // recorded as the ordinary fix it is - no dismissal riding along with it.
        (await RunReviewFixedAsync(store, taskId, noChange: null, cts.Token)).Should().Be(ExitCodes.Ok);

        await using IQuerySession query = store.QuerySession();
        ReviewHumanFixApplied humanFix = (await query.Events.FetchStreamAsync(runId, token: cts.Token))
            .Select(e => e.Data).OfType<ReviewHumanFixApplied>()
            .Should().ContainSingle().Subject;
        humanFix.NoChangeReason.Should().BeNull("the commits are the answer, so nothing was dismissed");
        humanFix.HeadSha.Should().NotBe(parkedTip, "the honest observation: the tip moved");

        RunDetails view = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        view.HumanFixes.Should().ContainSingle().Which.NoChangeReason.Should().BeNull();
    }

    /// <summary>
    /// Task: a human at the wheel takes the fix role herself, sixth criterion — the same lever at a
    /// closeout-side fix park, on a follow-up reopened by a human's changes-requested review. The
    /// branch is already published here, so her fix has to reach origin before the reviewers and
    /// the pull request's own checks read it.
    /// </summary>
    [Fact]
    public Task A_human_fix_works_at_a_closeout_side_fix_park_after_a_changes_requested_review() =>
        AssertHumanFixWorksOnFollowUpAsync(FollowUpKind.ReviewFeedback);

    /// <summary>
    /// The same, on a follow-up reopened by failing checks (sixth criterion's other half). The
    /// lever keys on the park, not on why the pull request reopened, so this is the class sweep
    /// over the other reopen cause rather than a different code path.
    /// </summary>
    [Fact]
    public Task A_human_fix_works_at_a_closeout_side_fix_park_after_failing_checks() =>
        AssertHumanFixWorksOnFollowUpAsync(FollowUpKind.FailingChecks);

    private async Task AssertHumanFixWorksOnFollowUpAsync(FollowUpKind kind)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, string worktreePath, string originPath) =
            await SeedInteractiveFollowUpRunWithOriginAsync(store, kind, cts.Token);

        (await NewEngine(store, new ScriptedExecutor()).ReviewAsync(runId, taskId, cts.Token)).Should().BeFalse(
            "the follow-up's own build-done-to-review boundary parks first");
        await ApproveBoundaryAsync(store, runId, cts.Token);

        ScriptedExecutor discovery = new(
            "FINDING: severity=high; scope=in-scope; at=Widget.cs:1\n"
            + "Defect: the lap left the widget broken.\n\nVERDICT: needs-fixes");
        (await NewEngine(store, discovery).ReviewAsync(runId, taskId, cts.Token)).Should().BeFalse(
            "this follow-up's review verdict parks at the same review-verdict-to-fix boundary");

        string parkedTip = GitOutput(worktreePath, "rev-parse HEAD");
        CommitDocOnlyChange(worktreePath);
        (await RunReviewFixedAsync(store, taskId, noChange: null, cts.Token)).Should().Be(ExitCodes.Ok);

        // Pushed, because this branch is already published behind an open pull request: origin's
        // own copy of the branch has to hold her commit, not just the local worktree.
        TryGit(originPath, "show task/review-me:NOTES.md").Should().Be(
            0, "her fix must reach origin before the pull request's own readers see it");

        // The same two-step re-entry the pre-pull-request side takes: gates, then the
        // fix-to-re-review boundary's own go, then the re-review.
        (await NewEngine(store, new ScriptedExecutor()).ReviewAsync(runId, taskId, cts.Token)).Should().BeFalse();
        await ApproveBoundaryAsync(store, runId, cts.Token);

        ScriptedExecutor afterFix = new(
            "FINDING: severity=high; scope=in-scope; at=Widget.cs:1\n"
            + "Defect: still broken.\n\nVERDICT: needs-fixes");
        (await NewEngine(store, afterFix).ReviewAsync(runId, taskId, cts.Token)).Should().BeFalse();
        afterFix.Spawns.Should().HaveCount(1, "one reviewer, no fix session");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewFixDispatched>().Should().BeEmpty();
        events.OfType<ReviewHumanFixApplied>().Should().ContainSingle()
            .Which.PushedToRemote.Should().BeTrue("the pull request was already open");
        List<ReviewDispatched> dispatches = [.. events.OfType<ReviewDispatched>()];
        dispatches.Should().HaveCount(2);
        dispatches[1].Mode.Should().Be(
            ReviewMode.Verify, "the same fix-to-re-review shape the pre-pull-request side re-enters at");
        dispatches[1].SinceSha.Should().Be(parkedTip);
    }

    /// <summary>Interactive mode's own bare proceed, as <c>h9k review proceed</c> appends it.</summary>
    private async Task ApproveBoundaryAsync(DocumentStore store, Guid runId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new ReviewBoundaryApproved(runId, Now, DomainId.New()));
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// <c>h9k review fixed</c> through the command's own rule set rather than by appending the
    /// event directly, so every refusal and every git read this task added is what the test
    /// actually exercises. The command rings the doorbell, which resolves its connection off
    /// <c>HALL9K_CONNECTION_STRING</c> rather than this fixture, so it is pointed at the fixture
    /// for the duration of the call and put back afterwards — the same process-wide dance
    /// <c>ReviewLapTests</c> does, which is why both live in the serialized
    /// <c>Hall9kHome</c> collection.
    /// </summary>
    private async Task<int> RunReviewFixedAsync(
        DocumentStore store, Guid taskId, string? noChange, CancellationToken cancellationToken)
    {
        string? previous = Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, postgres.ConnectionString);
        try
        {
            await using IDocumentSession session = store.LightweightSession();
            return await ReviewFixedCommand.RecordAsync(
                session, taskId, new ReviewFixedCommand.Settings { NoChange = noChange }, cancellationToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previous);
        }
    }

    /// <summary>
    /// The state the refusal tests need and nothing more: an interactive-mode run whose cycle-1
    /// review filed a finding, parked at the review-verdict-to-fix boundary with its worktree
    /// exactly as the reviewers left it.
    /// </summary>
    private async Task<(Guid TaskId, Guid RunId, string WorktreePath, Guid ProjectId)>
        SeedRunParkedAtTheReviewVerdictToFixBoundaryAsync(DocumentStore store, CancellationToken cancellationToken)
    {
        (Guid taskId, Guid runId, string worktreePath, Guid projectId) = await SeedVerifiedRunWithTestGateAsync(
            store, cancellationToken, interactiveMode: true,
            reviewStageComposition: ReviewStageComposition.AdversarialOnly);

        await NewEngine(store, new ScriptedExecutor()).ReviewAsync(runId, taskId, cancellationToken);
        await ApproveBoundaryAsync(store, runId, cancellationToken);
        ScriptedExecutor discovery = new(
            "FINDING: severity=high; scope=in-scope; at=Widget.cs:1\n"
            + "Defect: the widget never initializes.\n\nVERDICT: needs-fixes");
        await NewEngine(store, discovery).ReviewAsync(runId, taskId, cancellationToken);
        return (taskId, runId, worktreePath, projectId);
    }

    /// <summary>
    /// A closeout-side follow-up run under interactive mode (task: a human at the wheel takes the
    /// fix role herself, sixth criterion): a task that already reached Done behind a real pull
    /// request, reopened for <paramref name="kind"/> and reclaimed at generation 2, on a branch
    /// that is genuinely published to a real bare origin — which is what lets the push half of
    /// <c>h9k review fixed</c> be observed rather than asserted. Real verify commands too, so the
    /// reverify gate her fix earns actually runs.
    /// <para>
    /// <c>OpeningReviewSinceSha</c> is deliberately left unset, so this follow-up's opening cycle
    /// is a full Discovery read: the scoped-opening-lap interaction is its own feature's coverage,
    /// and seeding it here would only put a second variable inside a test about the fix lever.
    /// </para>
    /// </summary>
    private async Task<(Guid TaskId, Guid RunId, string WorktreePath, string OriginPath)>
        SeedInteractiveFollowUpRunWithOriginAsync(
            DocumentStore store, FollowUpKind kind, CancellationToken cancellationToken)
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cancellationToken);

        Guid taskId = DomainId.New();
        Guid firstRunId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        string worktreePath = Path.Combine(_home, $"wt-{runId:N}");

        // This seed pushes the follow-up's own branch below, so it always needs an origin of its
        // own rather than the class's shared template.
        string originPath = OriginFor(runId, ownOrigin: true);
        Directory.CreateDirectory(_home);
        Git(_home, $"clone -q \"{originPath}\" \"{worktreePath}\"");
        Git(worktreePath, "checkout -q -b task/review-me");
        File.WriteAllText(Path.Combine(worktreePath, "Widget.cs"), "class Widget { }\n");
        Git(worktreePath, "add -A");
        Git(worktreePath, "-c user.name=Test -c user.email=test@test commit -q -m widget");
        Git(worktreePath, "push -q -u origin task/review-me");
        string headSha = GitOutput(worktreePath, "rev-parse HEAD");

        await using IDocumentSession session = store.LightweightSession();

        Hall9k.Domain.Features.Project.ProjectAggregate project = new();
        var registered = Hall9k.Domain.Features.Project.Handlers.ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), $"followup-{taskId:N}", worktreePath, null, "main", Now);
        project.Apply(registered);
        IReadOnlyList<Hall9k.Domain.Features.Project.VerifyCommand> verifyCommands =
            [new Hall9k.Domain.Features.Project.VerifyCommand("test", "dotnet test --help")];
        session.Events.StartStream<Hall9k.Domain.Features.Project.ProjectAggregate>(
            projectId, registered,
            Hall9k.Domain.Features.Project.Handlers.ProjectDecider.ChangeSettings(
                project,
                verifyCommands: Optional<IReadOnlyList<Hall9k.Domain.Features.Project.VerifyCommand>>.Of(verifyCommands),
                skipPermissions: Optional<bool>.None,
                contextLinks: Optional<IReadOnlyList<Hall9k.Domain.Features.Project.ContextLink>>.None,
                Now, node.OwnerId));

        TaskAggregate task = new();
        (task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(taskId, projectId, "Fix the closeout lap by hand", ["reviewed"],
                TaskType.Chore, null, null, null, Now, node.OwnerId),
            node.OwnerId, Now);
        var firstClaim = TaskDecider.Claim(task, node.NodeId, node.OwnerId, firstRunId, Now);
        task.Apply(firstClaim);
        var completed = TaskDecider.Complete(task, firstRunId, "https://github.com/o/r/pull/9", Now);
        task.Apply(completed);
        var reopened = TaskDecider.Reopen(
            task, firstRunId, "task/review-me", "The pull request came back.", kind,
            automatic: false, Now, node.OwnerId);
        task.Apply(reopened);
        var followUpClaim = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now)
            with { InteractiveMode = true };
        task.Apply(followUpClaim);
        session.Events.StartStream<TaskAggregate>(
            taskId, [.. lifecycle, firstClaim, completed, reopened, followUpClaim]);
        session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 2, HeartbeatAt = Now });

        session.Events.StartStream<RunAggregate>(runId,
            new RunDispatched(runId, taskId, node.NodeId, node.OwnerId, 2, DomainId.New(),
                worktreePath, "task/review-me", ExecutorMode.Subscription, Now,
                ReviewStageComposition: ReviewStageComposition.AdversarialOnly),
            new AgentSessionCompleted(runId, Now),
            new VerificationPassed(
                runId, Now, RanFullScope: true, HeadSha: headSha,
                VerifyCommandsFingerprint: Hall9k.Domain.Features.Project.VerifyCommand.Fingerprint(verifyCommands)));
        await session.SaveChangesAsync(cancellationToken);

        return (taskId, runId, worktreePath, originPath);
    }

    /// <summary>
    /// Rewrites this worktree's history so <paramref name="branch"/>'s tip no longer descends from
    /// whatever it did before — a fresh, unrelated root commit over the identical working-tree
    /// contents, standing in for a fix session's own commit-plan autosquash or a rebase-onto-main
    /// follow-up. The original commits still exist as unreferenced objects (git does not garbage
    /// collect them synchronously), so this exercises "no longer an ancestor of HEAD," not "the
    /// object is gone."
    /// </summary>
    private static void RewriteHistoryDroppingAncestor(string worktreePath, string branch)
    {
        Git(worktreePath, "checkout -q --orphan history-rewrite-tmp");
        Git(worktreePath, "add -A");
        Git(worktreePath, "-c user.name=Test -c user.email=test@test commit -q -m rewritten-history");
        string newTip = GitOutput(worktreePath, "rev-parse HEAD");
        Git(worktreePath, $"branch -q -f {branch} {newTip}");
        Git(worktreePath, $"checkout -q {branch}");
        Git(worktreePath, "branch -q -D history-rewrite-tmp");
    }

    private async Task ChangeVerifyCommandsAsync(DocumentStore store, Guid projectId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        Hall9k.Domain.Features.Project.ProjectAggregate? project =
            await session.Events.AggregateStreamAsync<Hall9k.Domain.Features.Project.ProjectAggregate>(
                projectId, token: cancellationToken);
        session.Events.Append(projectId, Hall9k.Domain.Features.Project.Handlers.ProjectDecider.ChangeSettings(
            project!,
            verifyCommands: Optional<IReadOnlyList<Hall9k.Domain.Features.Project.VerifyCommand>>.Of(
                [new Hall9k.Domain.Features.Project.VerifyCommand("test", "dotnet test --help --verbosity quiet")]),
            skipPermissions: Optional<bool>.None,
            contextLinks: Optional<IReadOnlyList<Hall9k.Domain.Features.Project.ContextLink>>.None,
            Now, project!.OwnerId));
        await session.SaveChangesAsync(cancellationToken);
    }

    private static void CommitDocOnlyChange(string worktreePath)
    {
        File.WriteAllText(Path.Combine(worktreePath, "NOTES.md"), "fix notes\n");
        Git(worktreePath, "add -A");
        Git(worktreePath, "-c user.name=Test -c user.email=test@test commit -q -m fix-notes");
    }

    /// <summary>The mid-run mutation <c>h9k task revise --clear-interactive-mode</c> makes, for tests that need it to land while a single <see cref="ReviewEngine.ReviewAsync"/> call is still in flight (the same "mutate between spawns" shape <see cref="ChangeVerifyCommandsAsync"/> already gives the verify-commands fingerprint tests).</summary>
    private async Task ClearInteractiveModeAsync(DocumentStore store, Guid taskId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken);
        session.Events.Append(taskId, new TaskRevised(
            taskId,
            Optional<string>.None,
            Optional<IReadOnlyList<string>>.None,
            Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None,
            Optional<TaskType>.None,
            Optional<AgentModel>.None,
            Now, task!.AddedByOwnerId,
            ClearInteractiveMode: true));
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Like <see cref="SeedVerifiedRunAsync(DocumentStore, CancellationToken)"/>, but a real git worktree and a real `dotnet test`-shaped gate, for tests that need <see cref="VerificationRunner"/>'s own scoping to run for real rather than short-circuit on "no gates configured".</summary>
    private async Task<(Guid TaskId, Guid RunId, string WorktreePath, Guid ProjectId)> SeedVerifiedRunWithTestGateAsync(
        DocumentStore store, CancellationToken cancellationToken, bool recordVerifyCommandsFingerprint = true,
        bool interactiveMode = false, ReviewStageComposition? reviewStageComposition = null,
        bool seedOpeningReviewSinceSha = false)
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cancellationToken);

        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid mainSessionId = DomainId.New();
        string worktreePath = Path.Combine(_home, $"wt-{runId:N}");
        Directory.CreateDirectory(worktreePath);
        Git(worktreePath, "init -q -b main");
        File.WriteAllText(Path.Combine(worktreePath, "base.txt"), "base\n");
        Git(worktreePath, "add -A");
        Git(worktreePath, "-c user.name=Test -c user.email=test@test commit -q -m init");
        // The task branch, one real commit ahead of main — the "already-reviewed" state a
        // Discovery cycle's own head is captured against, so VerificationRunner's own no-commit
        // pre-gate check (a branch with nothing beyond its base fails before any gate runs) does
        // not fire, and so the fix's later doc-only commit has a real diff to be scoped against.
        Git(worktreePath, "checkout -q -b task/review-me");
        File.WriteAllText(Path.Combine(worktreePath, "Widget.cs"), "class Widget { }\n");
        Git(worktreePath, "add -A");
        Git(worktreePath, "-c user.name=Test -c user.email=test@test commit -q -m widget");
        string headSha = GitOutput(worktreePath, "rev-parse HEAD");

        await using IDocumentSession session = store.LightweightSession();

        Hall9k.Domain.Features.Project.ProjectAggregate project = new();
        var registered = Hall9k.Domain.Features.Project.Handlers.ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), $"review-{taskId:N}", worktreePath, null, "main", Now);
        project.Apply(registered);
        IReadOnlyList<Hall9k.Domain.Features.Project.VerifyCommand> verifyCommands =
            [new Hall9k.Domain.Features.Project.VerifyCommand("test", "dotnet test --help")];
        session.Events.StartStream<Hall9k.Domain.Features.Project.ProjectAggregate>(
            projectId, registered,
            Hall9k.Domain.Features.Project.Handlers.ProjectDecider.ChangeSettings(
                project,
                verifyCommands: Optional<IReadOnlyList<Hall9k.Domain.Features.Project.VerifyCommand>>.Of(verifyCommands),
                skipPermissions: Optional<bool>.None,
                contextLinks: Optional<IReadOnlyList<Hall9k.Domain.Features.Project.ContextLink>>.None,
                Now, node.OwnerId));

        TaskAggregate task = new();
        (task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(taskId, projectId, "Review me before the PR", ["reviewed"],
                TaskType.Chore, null, null, null, Now, node.OwnerId),
            node.OwnerId, Now);
        var claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now);
        if (interactiveMode)
        {
            claimed = claimed with { InteractiveMode = true };
        }

        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
        session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = Now });

        // RanFullScope, HeadSha and VerifyCommandsFingerprint set to what a real first gate pass
        // (VerificationRunner's own always-full initial gate) would actually record, rather than
        // the fields' own conservative defaults — several Settling-gate tests below rely on this
        // seed representing a genuinely comparable prior full gate. recordVerifyCommandsFingerprint
        // lets a caller instead seed the shape a stream written before that field existed has: a
        // real RanFullScope/HeadSha pair with no fingerprint ever recorded (independent pre-PR
        // review, cycle 3, adversarial lens).
        session.Events.StartStream<RunAggregate>(runId,
            new RunDispatched(runId, taskId, node.NodeId, node.OwnerId, 1, mainSessionId,
                worktreePath, "task/review-me", ExecutorMode.Subscription, Now,
                ReviewStageComposition: reviewStageComposition,
                OpeningReviewSinceSha: seedOpeningReviewSinceSha ? headSha : null),
            new AgentSessionCompleted(runId, Now),
            new VerificationPassed(
                runId, Now, RanFullScope: true, HeadSha: headSha,
                VerifyCommandsFingerprint: recordVerifyCommandsFingerprint
                    ? Hall9k.Domain.Features.Project.VerifyCommand.Fingerprint(verifyCommands)
                    : null));
        await session.SaveChangesAsync(cancellationToken);

        return (taskId, runId, worktreePath, projectId);
    }

    /// <summary>
    /// Like <see cref="SeedVerifiedRunAsync(DocumentStore, CancellationToken)"/>, but a real git
    /// worktree cloned from a real bare "origin" repository, with the project's base branch set
    /// to "main" (task: a run rebases its branch onto the current base branch) — for tests
    /// exercising the pre-final-pass rebase check, which needs a genuine <c>origin/main</c> to
    /// fetch and compare against. Real git, never a fake — the same convention
    /// <c>CloseoutEngineTests</c>' own mechanical-rebase coverage already follows, so a genuinely
    /// conflicting (or genuinely clean) history is what decides the outcome.
    /// <para>
    /// The seeded origin itself is <see cref="SeededGitOriginFixture"/>'s, built once for the
    /// class. <paramref name="ownOrigin"/> defaults to true — a copy of the template, this run's
    /// to push to — because all but one caller here does push to it; a caller that only reads
    /// <c>origin/main</c> passes false and gets the template directly. Defaulting the other way
    /// would make forgetting the parameter silently corrupt every later test in the class, so the
    /// default is the safe one and <see cref="MutableOrigin"/> catches the mistake anyway.
    /// </para>
    /// </summary>
    private async Task<(Guid TaskId, Guid RunId, string WorktreePath, string OriginPath)> SeedVerifiedRunWithOriginAsync(
        DocumentStore store, CancellationToken cancellationToken, string? pullRequestUrl = null,
        string baseBranch = "", bool ownOrigin = true)
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cancellationToken);

        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid mainSessionId = DomainId.New();
        string worktreePath = Path.Combine(_home, $"wt-{runId:N}");
        string originPath = OriginFor(runId, ownOrigin);
        Directory.CreateDirectory(_home);
        Git(_home, $"clone -q \"{originPath}\" \"{worktreePath}\"");
        Git(worktreePath, "checkout -q -b task/review-me");
        File.WriteAllText(Path.Combine(worktreePath, "Widget.cs"), "class Widget { }\n");
        Git(worktreePath, "add -A");
        Git(worktreePath, "-c user.name=Test -c user.email=test@test commit -q -m widget");

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

        // A follow-up run's own task already carries the pull request its predecessor opened
        // (task: a run rebases its branch onto the current base branch — the retargeted-base
        // guard only ever has anything to check once a pull request already exists).
        if (!string.IsNullOrWhiteSpace(pullRequestUrl))
        {
            session.Events.Append(taskId, new TaskCompleted(taskId, DomainId.New(), pullRequestUrl, Now));
        }

        session.Events.StartStream<RunAggregate>(runId,
            new RunDispatched(runId, taskId, node.NodeId, node.OwnerId, 1, mainSessionId,
                worktreePath, "task/review-me", ExecutorMode.Subscription, Now,
                // Blank for every ordinary run, which is what "the project's own base branch"
                // means; a parent branch name for a stacked child (task: a stacked pull-request
                // edge exists as an explicit opt-in dependency).
                BaseBranch: baseBranch),
            new AgentSessionCompleted(runId, Now),
            new VerificationPassed(runId, Now));
        await session.SaveChangesAsync(cancellationToken);

        return (taskId, runId, worktreePath, originPath);
    }

    /// <summary>
    /// Like <see cref="SeedVerifiedRunWithOriginAsync"/>, but the project also carries
    /// <paramref name="verifyCommands"/> — for the Settling-gate repair lap's own tests (task: a
    /// pre-final-pass rebase that applies cleanly but breaks the mandatory gate gets a repair lap
    /// inside the same run instead of failing it), which need a real, controllable mandatory gate
    /// on top of the real origin every pre-final-pass rebase test already needs.
    /// </summary>
    private async Task<(Guid TaskId, Guid RunId, string WorktreePath, string OriginPath)> SeedVerifiedRunWithOriginAndGateAsync(
        DocumentStore store, IReadOnlyList<Hall9k.Domain.Features.Project.VerifyCommand> verifyCommands,
        CancellationToken cancellationToken)
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cancellationToken);

        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid mainSessionId = DomainId.New();
        string worktreePath = Path.Combine(_home, $"wt-{runId:N}");
        string originPath = OriginFor(runId, ownOrigin: true);
        Directory.CreateDirectory(_home);
        Git(_home, $"clone -q \"{originPath}\" \"{worktreePath}\"");
        Git(worktreePath, "checkout -q -b task/review-me");
        File.WriteAllText(Path.Combine(worktreePath, "Widget.cs"), "class Widget { }\n");
        Git(worktreePath, "add -A");
        Git(worktreePath, "-c user.name=Test -c user.email=test@test commit -q -m widget");

        await using IDocumentSession session = store.LightweightSession();

        Hall9k.Domain.Features.Project.ProjectAggregate project = new();
        var registered = Hall9k.Domain.Features.Project.Handlers.ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), $"review-{taskId:N}", worktreePath, null, "main", Now);
        project.Apply(registered);
        session.Events.StartStream<Hall9k.Domain.Features.Project.ProjectAggregate>(
            projectId, registered,
            Hall9k.Domain.Features.Project.Handlers.ProjectDecider.ChangeSettings(
                project,
                verifyCommands: Optional<IReadOnlyList<Hall9k.Domain.Features.Project.VerifyCommand>>.Of(verifyCommands),
                skipPermissions: Optional<bool>.None,
                contextLinks: Optional<IReadOnlyList<Hall9k.Domain.Features.Project.ContextLink>>.None,
                Now, node.OwnerId));

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

        return (taskId, runId, worktreePath, originPath);
    }

    /// <summary>Clones <paramref name="originPath"/> into a fresh temp directory and pushes one commit to <c>main</c> — "another engineer's work merging in" while this run's own worktree sits untouched.</summary>
    private void PushToOrigin(string originPath, string fileName, string content, string message)
    {
        string otherClone = Path.Combine(_home, $"other-{Guid.NewGuid():N}");
        Git(_home, $"clone -q \"{MutableOrigin(originPath)}\" \"{otherClone}\"");
        File.WriteAllText(Path.Combine(otherClone, fileName), content);
        Git(otherClone, "add -A");
        Git(otherClone, $"-c user.name=Other -c user.email=other@test commit -q -m \"{message}\"");
        Git(otherClone, "push -q origin main");
    }

    /// <summary>
    /// The same, onto a branch of its own cut from <c>main</c> — a stacked child's parent branch,
    /// carrying a commit the child's branch does not have, so a rebase onto it would visibly land
    /// that file in the run's worktree if one ever ran.
    /// </summary>
    private void PushBranchToOrigin(string originPath, string branch, string fileName, string content)
    {
        string otherClone = Path.Combine(_home, $"parent-{Guid.NewGuid():N}");
        Git(_home, $"clone -q \"{MutableOrigin(originPath)}\" \"{otherClone}\"");
        Git(otherClone, $"checkout -q -b {branch}");
        File.WriteAllText(Path.Combine(otherClone, fileName), content);
        Git(otherClone, "add -A");
        Git(otherClone, "-c user.name=Parent -c user.email=parent@test commit -q -m \"parent slice\"");
        Git(otherClone, $"push -q origin {branch}");
    }

    /// <summary>
    /// Task: a run rebases its branch onto the current base branch. When origin's base branch
    /// has not moved past what the branch already contains, the pre-final-pass rebase is a
    /// recorded no-op that costs nothing else — same cycle count, same spawn count as the
    /// mandatory-final-pass shape without this feature.
    /// </summary>
    [Fact]
    public async Task Pre_final_pass_rebase_is_a_recorded_no_op_when_the_base_has_not_moved()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        // This is the one origin-backed test here that never pushes — the base has not moved is
        // the whole scenario — so it reads the class's shared origin rather than taking a copy.
        (Guid taskId, Guid runId, _, _) =
            await SeedVerifiedRunWithOriginAsync(store, cts.Token, ownOrigin: false);

        ScriptedExecutor executor = new(
            "Criteria met at cycle 1.\n\nVERDICT: merge-ready",
            "FINDING: severity=high; scope=in-scope; at=Widget.cs:1\nDefect: needs work.\n\nVERDICT: needs-fixes",
            "Fixed it.\n\nRESOLUTION: fixed",
            "The fix holds.\n\nVERDICT: merge-ready",
            "FINDING: severity=low; scope=in-scope; at=Widget.cs:1\nDefect: minor.\n\nVERDICT: merge-ready",
            "Still holds.\n\nVERDICT: merge-ready");

        bool mergeReady = await NewEngine(store, executor, new DaemonOptions { MaxComplianceReviewCycles = 3 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(6, "a no-op rebase check dispatches no session of its own");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        // Two, not one: the rebase check now runs on every Settling entry (task: a run rebases its
        // branch onto the current base branch — the ordinary settle path must never open a stale
        // pull request just because an earlier entry into Settling already checked). This run
        // enters Settling once after cycle 1's fix (needsFullGateBeforeSettling) and again after the
        // mandatory final pass concludes on the severity bar — both no-ops, since origin/main never
        // moved either time.
        events.OfType<RunRebasedOntoBase>().Should().HaveCount(2)
            .And.OnlyContain(e => e.WasNoOp, "origin/main never moved past this branch's own merge base");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, both lenses: a placeholder Decisions Log entry must
    /// still get its real number even when origin's base has not moved — the no-op path used to
    /// return before ever calling the renumberer, which is exactly the shape a branch that needs
    /// no rebase (and a branch whose conflict a recovery session already resolved by hand before
    /// the loop re-entered here) both take, so it was also the shape most likely to merge its own
    /// placeholder into main unrenumbered.
    /// </summary>
    [Fact]
    public async Task Pre_final_pass_rebase_no_op_still_assigns_the_placeholders_real_number()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, string worktreePath, _) = await SeedVerifiedRunWithOriginAsync(store, cts.Token);

        string taskShortId = DomainId.Short(taskId);
        File.WriteAllText(Path.Combine(worktreePath, "PLAN.md"), string.Join('\n',
        [
            "# Fixture Plan",
            "",
            "## 16. v0 Decisions Log",
            "",
            $"PLACEHOLDER-{taskShortId}. **A test decision.** Placeholder body.",
            "",
            "---",
            "",
            "## 17. Reference Materials",
            "",
        ]));
        Git(worktreePath, "add -A");
        Git(worktreePath, "-c user.name=Test -c user.email=test@test commit -q -m \"decisions log entry\"");

        ScriptedExecutor executor = new(
            "Every acceptance criterion is met.\n\nVERDICT: merge-ready",
            "Hunted the trust boundaries and the lifetimes; nothing survived verification.\n\nVERDICT: merge-ready");

        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();

        string plan = await File.ReadAllTextAsync(Path.Combine(worktreePath, "PLAN.md"));
        plan.Should().Contain("1. **A test decision.**",
            "the no-op rebase path must still renumber the tail placeholder rather than let it merge unrenumbered");
        plan.Should().NotContain(
            $"#PLACEHOLDER-{taskShortId}", "no citation of the placeholder may survive once the mandatory final pass has run");
        plan.Should().Contain(
            "Renumbering placement note:", "the placement note deliberately keeps naming the old placeholder as history");

        string log = GitOutput(worktreePath, "log --oneline -5");
        log.Should().Contain(
            "chore: assign Decisions Log #1 to placeholder", "the renumbering step commits mechanically, with no agent in the loop");

        // Independent pre-PR review, cycle 3, conformance lens: a renumbering commit moves HEAD
        // past whatever this run's tip was last gated at, exactly like a real rebase does, so the
        // outcome recorded for it must still raise RunAggregate.PreFinalPassRebaseAwaitingGate.
        // Cycle 5's adversarial lens found that forcing this by lying about WasNoOp
        // (`wasNoOp: !renumberCommitted`) broke RunAggregate's own trailing-no-op guard on the
        // post-recovery re-entry — origin genuinely never moved, so WasNoOp stays true, and
        // DecisionsLogRenumbered is the honest, separate signal that still earns the gate.
        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<RunRebasedOntoBase>().Should().Contain(
            e => e.WasNoOp && e.DecisionsLogRenumbered,
            "origin never moved, but the renumbering commit that landed must still raise the gate flag");
    }

    /// <summary>
    /// Task: a run rebases its branch onto the current base branch (independent pre-PR review,
    /// cycle 1, both lenses). A follow-up run reuses whatever pull request the task already has
    /// open, and that pull request's base can have been retargeted away from the project's own
    /// base branch on GitHub itself (the stacked-PR shape AGENTS.md documents as current
    /// practice) — CloseoutEngine's own mechanical rebase already refuses the identical mismatch
    /// for the identical reason, and this check must too, rather than silently rewriting the
    /// branch onto a base it was never meant to be on.
    /// </summary>
    [Fact]
    public async Task A_pre_final_pass_rebase_skips_when_the_pull_request_was_retargeted_off_the_project_base()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, string worktreePath, string originPath) = await SeedVerifiedRunWithOriginAsync(
            store, cts.Token, pullRequestUrl: "https://github.com/acme/widgets/pull/42");

        PushToOrigin(originPath, "unrelated.txt", "merged while this run was building\n", "unrelated merge");

        ScriptedExecutor executor = new(
            "Nothing to fix.\n\nVERDICT: merge-ready",
            "Nothing to fix either.\n\nVERDICT: merge-ready");

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding(
            """{"number":42,"title":"x","body":null,"state":"OPEN","url":"https://github.com/acme/widgets/pull/42","baseRefName":"release/1.0"}""");

        bool mergeReady = await NewEngine(
                store, executor, new DaemonOptions { MaxComplianceReviewCycles = 3 }, gh.Runner)
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        File.Exists(Path.Combine(worktreePath, "unrelated.txt")).Should().BeFalse(
            "the pull request's actual base (release/1.0) is not this project's own base branch (main) — " +
            "rebasing onto main would silently rewrite the branch onto a base it was never meant to be on");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<RunRebasedOntoBase>().Should().BeEmpty("the mismatch is caught before any git fetch or rebase is attempted");
    }

    /// <summary>
    /// Task: a stacked pull-request edge exists as an explicit opt-in dependency (independent
    /// pre-PR review, cycle 1, adversarial lens). A stacked child's recorded base IS its parent's
    /// branch, and a plain merge-base <c>git rebase origin/&lt;parent&gt;</c> is the one operation
    /// this feature's own design proves wrong against it: a parent force-pushed mid-run collapses
    /// that merge base below the child's fork point, and the rebase then replays the child's copies
    /// of the parent's commits against the parent's new ones.
    /// <para>
    /// This run records such a base while its task declares no stacked edge at all — a pull request
    /// a human retargeted by hand, for reasons this platform knows nothing about — so there is no
    /// parent to watch, and the honest answer is to leave the branch alone rather than rebase it
    /// onto a branch nobody declared. The checkpoint replay that answers a real declared parent is
    /// covered by the stacked-checkpoint tests below.
    /// </para>
    /// <para>
    /// Deliberately seeded with no pull request: the retargeted-base guard beside this one is
    /// skipped entirely when there is nothing to retarget, so a fresh run recording a foreign base
    /// is the case only this arm can catch.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_pre_final_pass_rebase_leaves_a_foreign_base_alone_when_no_stacked_edge_is_declared()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, string worktreePath, string originPath) = await SeedVerifiedRunWithOriginAsync(
            store, cts.Token, baseBranch: "task/parent-slice");

        PushBranchToOrigin(originPath, "task/parent-slice", "parent.txt", "the parent's own work\n");

        ScriptedExecutor executor = new(
            "Nothing to fix.\n\nVERDICT: merge-ready",
            "Nothing to fix either.\n\nVERDICT: merge-ready");

        bool mergeReady = await NewEngine(
                store, executor, new DaemonOptions { MaxComplianceReviewCycles = 3 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        File.Exists(Path.Combine(worktreePath, "parent.txt")).Should().BeFalse(
            "no rebase of any kind belongs here: a plain merge-base rebase onto a parent branch replays this "
            + "branch's copies of the parent's commits, and the checkpoint replay that would be right needs a "
            + "declared parent this task does not have");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<RunRebasedOntoBase>().Should().BeEmpty(
            "nothing moved, and an audit record naming no commits is worse than none at all");
    }

    /// <summary>
    /// Task: a stacked child absorbs its parent's post-delivery churn safely — the first
    /// checkpoint, and the one the reviewers depend on. The parent takes an ordinary
    /// post-delivery lap while this child is building (its own closeout reopening it for a
    /// failing check, say), so by the time the child's first review cycle is ready to dispatch its
    /// recorded base is stale. The checkpoint replays this branch's own commit from its recorded
    /// fork point onto the parent's new head <em>before</em> the first review pass spawns, which is
    /// what makes the delta those reviewers read this child's own work rather than the parent's
    /// already-reviewed work.
    /// </summary>
    [Fact]
    public async Task A_stacked_child_is_replayed_onto_its_parents_head_before_its_first_review_cycle()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        StackedChildFixture fixture = await SeedStackedChildRunAsync(store, cts.Token);

        PushToOriginBranch(
            fixture.OriginPath, fixture.ParentBranch, "parent-lap.txt", "the parent's own review lap\n");

        ScriptedExecutor executor = new(
            "Nothing to fix.\n\nVERDICT: merge-ready",
            "Nothing to fix either.\n\nVERDICT: merge-ready");
        bool parentsLapPresentWhenTheFirstReviewerSpawned = false;
        executor.OnSpawnByIndex[0] = () => parentsLapPresentWhenTheFirstReviewerSpawned =
            File.Exists(Path.Combine(fixture.WorktreePath, "parent-lap.txt"));

        bool mergeReady = await NewEngine(store, executor, new DaemonOptions { MaxComplianceReviewCycles = 3 })
            .ReviewAsync(fixture.RunId, fixture.TaskId, cts.Token);

        mergeReady.Should().BeTrue();
        parentsLapPresentWhenTheFirstReviewerSpawned.Should().BeTrue(
            "the checkpoint runs before the first review pass dispatches — a reviewer that read the branch "
            + "against a stale parent head would grade the parent's own delta as this child's work");
        File.Exists(Path.Combine(fixture.WorktreePath, "Widget.cs")).Should().BeTrue(
            "the replay carries this branch's own commits forward, it does not drop them");
        executor.Spawns.Should().HaveCount(2,
            "the replay is mechanical: both lenses of cycle 1 and nothing else — no recovery session, no extra "
            + "cycle, no lens earned by the rebase itself");

        await using IQuerySession query = store.QuerySession();
        List<object> runEvents = [.. (await query.Events.FetchStreamAsync(fixture.RunId, token: cts.Token)).Select(e => e.Data)];
        runEvents.OfType<RunRebasedOntoBase>().Should().ContainSingle(
            e => !e.WasNoOp && !e.RecoveredByAgentSession,
            "one replay, mechanical — and the second checkpoint before the final pass then finds nothing left "
            + "to do, which records nothing at all");
        runEvents.OfType<VerificationPassed>().Should().HaveCount(2,
            "a checkpoint rebase is replay plus gates: the moved tip is gated before cycle 1's reviewers read it");

        List<object> taskEvents = [.. (await query.Events.FetchStreamAsync(fixture.TaskId, token: cts.Token)).Select(e => e.Data)];
        taskEvents.OfType<StackedCheckpointRebased>().Should().ContainSingle()
            .Which.Checkpoint.Should().Be(StackedCheckpoint.BeforeFirstReviewCycle);
        TaskAggregate? child = await query.Events.AggregateStreamAsync<TaskAggregate>(fixture.TaskId, token: cts.Token);
        child!.StackReplaysDispatched.Should().Be(1,
            "the checkpoint spends the same rebase budget closeout's own replay follow-ups spend");
    }

    /// <summary>
    /// Task: a stacked child absorbs its parent's post-delivery churn safely — the half of the
    /// first checkpoint that only the dispatched prompt can prove (independent pre-PR review,
    /// cycle 1, adversarial lens). Landing the replay is not enough: the fork point every reviewer
    /// is told to read and scope against comes from the run's own record, which the replay moves,
    /// and the review loop's context snapshot is loaded once at entry. A pass dispatched from that
    /// stale snapshot names a commit the branch no longer contains — and a three-dot range from
    /// there collapses to the project's base, handing the reviewer the parent's whole
    /// already-reviewed delta as this child's work, which is the exact duplication this checkpoint
    /// exists to prevent.
    /// </summary>
    [Fact]
    public async Task Cycle_1_reviewers_are_told_the_fork_point_the_checkpoint_replay_landed_on()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        StackedChildFixture fixture = await SeedStackedChildRunAsync(store, cts.Token);

        string parentsNewHead = PushToOriginBranch(
            fixture.OriginPath, fixture.ParentBranch, "parent-lap.txt", "the parent's own review lap\n");

        ScriptedExecutor executor = new(
            "Nothing to fix.\n\nVERDICT: merge-ready",
            "Nothing to fix either.\n\nVERDICT: merge-ready");

        bool mergeReady = await NewEngine(store, executor, new DaemonOptions { MaxComplianceReviewCycles = 3 })
            .ReviewAsync(fixture.RunId, fixture.TaskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(2, "both of cycle 1's lenses, dispatched after the replay landed");
        foreach (AgentSpawnRequest pass in executor.Spawns)
        {
            pass.Prompt.Should().Contain(parentsNewHead,
                "the boundary of every range this pass reads is the commit the replay actually landed on");
            pass.Prompt.Should().NotContain(fixture.ParentHeadCommit,
                "and never the fork point recorded at dispatch, which this branch no longer contains");
        }

        await using IQuerySession query = store.QuerySession();
        RunDetails? run = await query.LoadAsync<RunDetails>(fixture.RunId, cts.Token);
        run!.BaseCommit.Should().Be(parentsNewHead,
            "the prompts read this record, so it is the record the replay has to have moved");
    }

    /// <summary>
    /// Task: a stacked child absorbs its parent's post-delivery churn safely — the second
    /// checkpoint, and the one that keeps the promise nothing merges on a stale base. The parent
    /// moves <em>during</em> the child's first review cycle, so the first checkpoint saw nothing:
    /// the mandatory pre-final-pass checkpoint is what catches it, and the branch is on the
    /// parent's current head before this run may settle.
    /// </summary>
    [Fact]
    public async Task A_stacked_child_whose_parent_moves_mid_review_is_replayed_before_the_final_pass()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        StackedChildFixture fixture = await SeedStackedChildRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "Nothing to fix.\n\nVERDICT: merge-ready",
            "Nothing to fix either.\n\nVERDICT: merge-ready");
        executor.OnSpawnByIndex[0] = () => PushToOriginBranch(
            fixture.OriginPath, fixture.ParentBranch, "parent-lap.txt", "the parent's own review lap\n");

        bool mergeReady = await NewEngine(store, executor, new DaemonOptions { MaxComplianceReviewCycles = 3 })
            .ReviewAsync(fixture.RunId, fixture.TaskId, cts.Token);

        mergeReady.Should().BeTrue();
        File.Exists(Path.Combine(fixture.WorktreePath, "parent-lap.txt")).Should().BeTrue(
            "the run does not settle on a branch built on a parent head that has moved");

        await using IQuerySession query = store.QuerySession();
        List<object> taskEvents = [.. (await query.Events.FetchStreamAsync(fixture.TaskId, token: cts.Token)).Select(e => e.Data)];
        taskEvents.OfType<StackedCheckpointRebased>().Should().ContainSingle()
            .Which.Checkpoint.Should().Be(StackedCheckpoint.BeforeFinalPass,
                "the parent moved after the first checkpoint had already looked, so this is the second one's work");
    }

    /// <summary>
    /// Task: a stacked child absorbs its parent's post-delivery churn safely. A parent that has not
    /// moved costs the child nothing at all — no rebase, no budget, and no audit record claiming
    /// commits nothing observed. This is the other half of "at defined checkpoints": the
    /// checkpoints ask, and they only ever act on an answer that says the parent moved.
    /// </summary>
    [Fact]
    public async Task A_stacked_child_whose_parent_stood_still_spends_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        StackedChildFixture fixture = await SeedStackedChildRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "Nothing to fix.\n\nVERDICT: merge-ready",
            "Nothing to fix either.\n\nVERDICT: merge-ready");

        bool mergeReady = await NewEngine(store, executor, new DaemonOptions { MaxComplianceReviewCycles = 3 })
            .ReviewAsync(fixture.RunId, fixture.TaskId, cts.Token);

        mergeReady.Should().BeTrue();

        await using IQuerySession query = store.QuerySession();
        List<object> runEvents = [.. (await query.Events.FetchStreamAsync(fixture.RunId, token: cts.Token)).Select(e => e.Data)];
        runEvents.OfType<RunRebasedOntoBase>().Should().BeEmpty("the parent's head is what this branch is already on");
        List<object> taskEvents = [.. (await query.Events.FetchStreamAsync(fixture.TaskId, token: cts.Token)).Select(e => e.Data)];
        taskEvents.OfType<StackedCheckpointRebased>().Should().BeEmpty("nothing was rebased, so nothing was spent");
    }

    /// <summary>
    /// Task: a stacked child absorbs its parent's post-delivery churn safely — a parent that dies
    /// terminally. The parent's own pull request was closed without merging, which leaves it Done
    /// forever, carrying the pull-request URL it always had, and unable to reach even Delivered.
    /// The child parks with the situation named rather than replaying onto a base nothing further
    /// arrives on.
    /// </summary>
    [Fact]
    public async Task A_stacked_child_parks_for_a_human_when_its_parent_can_no_longer_deliver()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        StackedChildFixture fixture = await SeedStackedChildRunAsync(
            store, cts.Token, parentPullRequestClosedUnmerged: true);

        PushToOriginBranch(
            fixture.OriginPath, fixture.ParentBranch, "parent-lap.txt", "a lap nobody will ever merge\n");

        ScriptedExecutor executor = new(
            "Nothing to fix.\n\nVERDICT: merge-ready",
            "Nothing to fix either.\n\nVERDICT: merge-ready");

        bool mergeReady = await NewEngine(store, executor, new DaemonOptions { MaxComplianceReviewCycles = 3 })
            .ReviewAsync(fixture.RunId, fixture.TaskId, cts.Token);

        mergeReady.Should().BeFalse("a dead base is not something to review your way past");
        executor.Spawns.Should().BeEmpty("the park lands before this run's first review cycle ever dispatches");
        File.Exists(Path.Combine(fixture.WorktreePath, "parent-lap.txt")).Should().BeFalse(
            "nothing is replayed onto a parent that can no longer deliver");

        await using IQuerySession query = store.QuerySession();
        List<object> runEvents = [.. (await query.Events.FetchStreamAsync(fixture.RunId, token: cts.Token)).Select(e => e.Data)];
        ReviewParked parked = runEvents.OfType<ReviewParked>().Should().ContainSingle().Subject;
        parked.Reason.Should().Contain(fixture.ParentBranch, "the park names the base this branch is stuck on");
        parked.Reason.Should().Contain("never delivered a pull request that can merge");
        RunDetails? run = await query.LoadAsync<RunDetails>(fixture.RunId, cts.Token);
        run!.State.Should().Be(RunState.ReviewParked);
    }

    /// <summary>
    /// Task: a stacked child absorbs its parent's post-delivery churn safely. The parent's branch
    /// is gone from origin and its run recorded no pull request either, so there is no head for
    /// this branch to be brought onto and nothing that changes that on its own. Closeout can shrug
    /// at an unobservable sweep and ask again next time; a checkpoint is the last look before the
    /// thing it precedes runs, so it parks instead.
    /// </summary>
    [Fact]
    public async Task A_stacked_child_parks_when_its_parents_head_cannot_be_resolved_at_all()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        StackedChildFixture fixture = await SeedStackedChildRunAsync(
            store, cts.Token, parentPullRequestOpened: false);

        // The parent's closeout deleting its branch, with no pull-request head ref to fall back to.
        Git(fixture.WorktreePath, $"push -q origin --delete {fixture.ParentBranch}");

        ScriptedExecutor executor = new("Nothing to fix.\n\nVERDICT: merge-ready");

        bool mergeReady = await NewEngine(store, executor, new DaemonOptions { MaxComplianceReviewCycles = 3 })
            .ReviewAsync(fixture.RunId, fixture.TaskId, cts.Token);

        mergeReady.Should().BeFalse();
        await using IQuerySession query = store.QuerySession();
        List<object> runEvents = [.. (await query.Events.FetchStreamAsync(fixture.RunId, token: cts.Token)).Select(e => e.Data)];
        ReviewParked parked = runEvents.OfType<ReviewParked>().Should().ContainSingle().Subject;
        parked.Reason.Should().Contain("no parent head for this branch to be brought onto");
        parked.Reason.Should().Contain("h9k review resolve");
    }

    /// <summary>
    /// Task: a stacked child absorbs its parent's post-delivery churn safely — the guardrail past
    /// the cap. The rebase budget is the child's own and is shared with closeout's replay
    /// follow-ups, so a checkpoint past it parks with the branch untouched, leaving a coherent
    /// stack rather than a half-followed parent.
    /// </summary>
    [Fact]
    public async Task A_stacked_child_past_its_rebase_budget_parks_rather_than_following_its_parent_again()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        StackedChildFixture fixture = await SeedStackedChildRunAsync(store, cts.Token, priorCheckpointRebases: 1);

        PushToOriginBranch(
            fixture.OriginPath, fixture.ParentBranch, "parent-lap.txt", "the parent moving one more time\n");

        ScriptedExecutor executor = new("Nothing to fix.\n\nVERDICT: merge-ready");

        bool mergeReady = await NewEngine(
                store, executor, new DaemonOptions { MaxComplianceReviewCycles = 3, MaxStackReplayRuns = 1 })
            .ReviewAsync(fixture.RunId, fixture.TaskId, cts.Token);

        mergeReady.Should().BeFalse();
        File.Exists(Path.Combine(fixture.WorktreePath, "parent-lap.txt")).Should().BeFalse(
            "past the cap the branch is left exactly as it was");

        await using IQuerySession query = store.QuerySession();
        List<object> runEvents = [.. (await query.Events.FetchStreamAsync(fixture.RunId, token: cts.Token)).Select(e => e.Data)];
        ReviewParked parked = runEvents.OfType<ReviewParked>().Should().ContainSingle().Subject;
        parked.Reason.Should().Contain("1/1 rebase(s)",
            "the prior checkpoint's own recorded spend is what this cap counted");
    }

    /// <summary>
    /// Task: a stacked child absorbs its parent's post-delivery churn safely. A checkpoint rebase
    /// is mechanical by contract — replay plus gates, no review cycle — so a conflict is not
    /// something it resolves: it restores the branch and parks. The recovery session the unstacked
    /// path dispatches would also land this run on <c>ReviewPhase.Settling</c>, carrying it straight
    /// past the review cycles this checkpoint exists to precede.
    /// </summary>
    [Fact]
    public async Task A_stacked_checkpoint_replay_that_conflicts_parks_with_the_branch_restored()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        StackedChildFixture fixture = await SeedStackedChildRunAsync(store, cts.Token);

        // The parent's lap touches the very file this child added, differently: the replay of the
        // child's own commit onto that head cannot apply.
        PushToOriginBranch(fixture.OriginPath, fixture.ParentBranch, "Widget.cs", "class Widget { int parent; }\n");

        ScriptedExecutor executor = new("Nothing to fix.\n\nVERDICT: merge-ready");

        bool mergeReady = await NewEngine(store, executor, new DaemonOptions { MaxComplianceReviewCycles = 3 })
            .ReviewAsync(fixture.RunId, fixture.TaskId, cts.Token);

        mergeReady.Should().BeFalse();
        executor.Spawns.Should().BeEmpty("no session of any kind is dispatched for a checkpoint conflict");
        GitOutput(fixture.WorktreePath, "rev-parse HEAD").Should().Be(fixture.ChildHeadCommit,
            "the worktree is restored to this branch's own tip, so the human inherits it unchanged");
        File.ReadAllText(Path.Combine(fixture.WorktreePath, "Widget.cs")).Should().NotContain("int parent",
            "and with its own content, not half of the parent's lap");

        await using IQuerySession query = store.QuerySession();
        List<object> runEvents = [.. (await query.Events.FetchStreamAsync(fixture.RunId, token: cts.Token)).Select(e => e.Data)];
        runEvents.OfType<RunRebasedOntoBase>().Should().BeEmpty("nothing landed, so nothing is recorded as landed");
        List<object> taskEvents = [.. (await query.Events.FetchStreamAsync(fixture.TaskId, token: cts.Token)).Select(e => e.Data)];
        taskEvents.OfType<StackedCheckpointRebased>().Should().BeEmpty("a conflict spends no budget");
        ReviewParked parked = runEvents.OfType<ReviewParked>().Should().ContainSingle().Subject;
        parked.Reason.Should().Contain("conflicted");
        parked.Reason.Should().Contain("git rebase --onto", "the park hands over the exact command it tried");
    }

    /// <summary>
    /// A stacked child mid-run: a real origin, a parent branch pushed and Delivered, this child's
    /// branch cut from the parent's head with that head recorded as its fork point, and the run
    /// sitting exactly where the review loop is entered (gates passed, no pull request yet). One
    /// clone serves as both the project's repository and the run's worktree, the same shape
    /// <see cref="SeedVerifiedRunWithOriginAsync"/> uses — the watch reads refs and the replay
    /// reads objects, and both are in there.
    /// </summary>
    private async Task<StackedChildFixture> SeedStackedChildRunAsync(
        DocumentStore store, CancellationToken cancellationToken, bool parentPullRequestOpened = true,
        bool parentPullRequestClosedUnmerged = false, int priorCheckpointRebases = 0)
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cancellationToken);

        Guid parentTaskId = DomainId.New();
        Guid parentRunId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        string parentBranch = $"task/{parentTaskId:N}"[..20];
        string worktreePath = Path.Combine(_home, $"wt-{runId:N}");

        // This seed pushes main and the parent's branch below, so it always needs an origin of its
        // own rather than the class's shared template.
        string originPath = OriginFor(runId, ownOrigin: true);
        Directory.CreateDirectory(_home);
        Git(_home, $"clone -q \"{originPath}\" \"{worktreePath}\"");

        // The parent's branch, cut from main and pushed — Delivered.
        Git(worktreePath, $"checkout -q -b {parentBranch}");
        File.WriteAllText(Path.Combine(worktreePath, "parent.txt"), "the parent's slice\n");
        Git(worktreePath, "add -A");
        Git(worktreePath, "-c user.name=Test -c user.email=test@test commit -q -m \"parent slice\"");
        Git(worktreePath, $"push -q origin {parentBranch}");
        string parentHeadCommit = GitOutput(worktreePath, "rev-parse HEAD");

        // The child's branch, cut from the PARENT's head rather than main — which is the whole
        // point: its own commit sits on top of the parent's.
        Git(worktreePath, "checkout -q -b task/review-me");
        File.WriteAllText(Path.Combine(worktreePath, "Widget.cs"), "class Widget { }\n");
        Git(worktreePath, "add -A");
        Git(worktreePath, "-c user.name=Test -c user.email=test@test commit -q -m widget");
        string childHeadCommit = GitOutput(worktreePath, "rev-parse HEAD");

        await using IDocumentSession session = store.LightweightSession();

        var registered = Hall9k.Domain.Features.Project.Handlers.ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), $"stacked-{taskId:N}", worktreePath, null, "main", Now);
        session.Events.StartStream<Hall9k.Domain.Features.Project.ProjectAggregate>(registered.Id, registered);

        const string parentPullRequestUrl = "https://github.com/acme/widgets/pull/41";
        TaskAggregate parentTask = new();
        (parentTask, object[] parentLifecycle) = TaskSeed.Start(
            TaskDecider.Add(parentTaskId, projectId, "Parent slice", ["it works"],
                TaskType.Feature, null, null, null, Now, node.OwnerId),
            node.OwnerId, Now);
        TaskClaimed parentClaimed = TaskDecider.Claim(parentTask, node.NodeId, node.OwnerId, parentRunId, Now);
        parentTask.Apply(parentClaimed);
        TaskCompleted parentCompleted = TaskDecider.Complete(parentTask, parentRunId, parentPullRequestUrl, Now);
        session.Events.StartStream<TaskAggregate>(
            parentTaskId, [.. parentLifecycle, parentClaimed, parentCompleted]);

        List<object> parentRunEvents =
        [
            new RunDispatched(parentRunId, parentTaskId, node.NodeId, node.OwnerId, 1, DomainId.New(),
                worktreePath, parentBranch, ExecutorMode.Subscription, Now),
            new AgentSessionCompleted(parentRunId, Now),
            new VerificationPassed(parentRunId, Now),
        ];
        if (parentPullRequestOpened)
        {
            parentRunEvents.Add(new PullRequestOpened(parentRunId, parentPullRequestUrl, 41, Now));
        }

        if (parentPullRequestClosedUnmerged)
        {
            parentRunEvents.Add(new PullRequestClosed(parentRunId, Now, Now));
        }

        session.Events.StartStream<RunAggregate>(parentRunId, [.. parentRunEvents]);

        // The graph the child's own assignment saw: a Delivered parent, which is the bar a stacked
        // edge starts at. A parent that dies does so after this point, which is the real sequence.
        TaskDependencyGraph graph = new([
            new TaskDependency(
                parentTaskId, "Parent slice", TaskState.Done, IsClosedOut: false, RunState.AwaitingReview,
                parentPullRequestUrl, TaskType.Feature, [], ProjectId: projectId),
        ]);
        TaskAggregate task = new();
        (task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(taskId, projectId, "Child slice, stacked on its parent", ["reviewed"],
                TaskType.Feature, null, null, null, Now, node.OwnerId,
                blockedBy: [parentTaskId], stackedOnTaskId: parentTaskId),
            node.OwnerId, Now, graph);
        TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now);
        task.Apply(claimed);
        List<object> taskEvents = [.. lifecycle, claimed];

        // Rebase budget already spent, as real recorded checkpoint rebases rather than a doctored
        // counter — the cap has to count the same events the checkpoint appends.
        for (int i = 0; i < priorCheckpointRebases; i++)
        {
            taskEvents.Add(new StackedCheckpointRebased(
                taskId, runId, StackedCheckpoint.BeforeFirstReviewCycle, parentBranch,
                $"deadbee{i}", $"cafe111{i}", Now));
        }

        session.Events.StartStream<TaskAggregate>(taskId, [.. taskEvents]);
        session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = Now });

        session.Events.StartStream<RunAggregate>(runId,
            new RunDispatched(runId, taskId, node.NodeId, node.OwnerId, 1, DomainId.New(),
                worktreePath, "task/review-me", ExecutorMode.Subscription, Now,
                BaseBranch: parentBranch, BaseCommit: parentHeadCommit),
            new AgentSessionCompleted(runId, Now),
            new VerificationPassed(runId, Now));
        await session.SaveChangesAsync(cancellationToken);

        return new StackedChildFixture(
            taskId, runId, parentTaskId, worktreePath, originPath, parentBranch, parentHeadCommit,
            childHeadCommit);
    }

    /// <summary>What a stacked-checkpoint test needs in hand: the child's run, its worktree, and the parent's branch.</summary>
    private sealed record StackedChildFixture(
        Guid TaskId, Guid RunId, Guid ParentTaskId, string WorktreePath, string OriginPath, string ParentBranch,
        string ParentHeadCommit, string ChildHeadCommit);

    /// <summary>
    /// One more commit on a branch origin already has — the parent taking a post-delivery lap while
    /// its child is in flight. Unlike <see cref="PushBranchToOrigin"/>, which creates the branch,
    /// this checks the existing one out so the push is a fast-forward rather than a rejection.
    /// Returns the commit the branch now points at, which is the head a checkpoint replay lands on.
    /// </summary>
    private string PushToOriginBranch(string originPath, string branch, string fileName, string content)
    {
        string otherClone = Path.Combine(_home, $"lap-{Guid.NewGuid():N}");
        Git(_home, $"clone -q \"{MutableOrigin(originPath)}\" \"{otherClone}\"");
        Git(otherClone, $"checkout -q {branch}");
        File.WriteAllText(Path.Combine(otherClone, fileName), content);
        Git(otherClone, "add -A");
        Git(otherClone, "-c user.name=Parent -c user.email=parent@test commit -q -m \"parent lap\"");
        Git(otherClone, $"push -q origin {branch}");
        return GitOutput(otherClone, "rev-parse HEAD");
    }

    /// <summary>
    /// Task: a run rebases its branch onto the current base branch. A clean rebase (no
    /// conflict) is folded into the mandatory final pass with no extra Discovery cycle, lens,
    /// or fix session — Brian's 2026-09-04 ruling: git applying every commit cleanly is itself
    /// the evidence that no judgment was exercised.
    /// </summary>
    [Fact]
    public async Task A_clean_pre_final_pass_rebase_reads_the_rebased_tree_and_costs_no_extra_session()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, string worktreePath, string originPath) =
            await SeedVerifiedRunWithOriginAsync(store, cts.Token);

        PushToOrigin(originPath, "unrelated.txt", "merged while this run was building\n", "unrelated merge");

        ScriptedExecutor executor = new(
            "Criteria met at cycle 1.\n\nVERDICT: merge-ready",
            "FINDING: severity=high; scope=in-scope; at=Widget.cs:1\nDefect: needs work.\n\nVERDICT: needs-fixes",
            "Fixed it.\n\nRESOLUTION: fixed",
            "The fix holds.\n\nVERDICT: merge-ready",
            "FINDING: severity=low; scope=in-scope; at=Widget.cs:1\nDefect: minor.\n\nVERDICT: merge-ready",
            "Still holds.\n\nVERDICT: merge-ready");

        bool mergeReady = await NewEngine(store, executor, new DaemonOptions { MaxComplianceReviewCycles = 3 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(6, "a clean rebase costs no review of its own");
        File.Exists(Path.Combine(worktreePath, "unrelated.txt")).Should().BeTrue(
            "the branch is now rebased onto the merge that landed while this run was building");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<RunRebasedOntoBase>().Should().ContainSingle(
            e => !e.WasNoOp && !e.RecoveredByAgentSession, "git applied every commit without a conflict");

        // The mandatory final pass this real rebase earns re-enters Settling afterward, whose own
        // rebase check runs again and finds nothing left to do — that trailing no-op must not
        // clobber the read model's own record of the real rebase (independent pre-PR review,
        // cycle 1, both lenses).
        RunDetails? run = await query.LoadAsync<RunDetails>(runId, cts.Token);
        run.Should().NotBeNull();
        run!.LastPreFinalPassRebaseWasNoOp.Should().BeFalse(
            "h9k task show must report the real rebase, not the trailing no-op re-check that followed it");
    }

    /// <summary>
    /// Task: a run rebases its branch onto the current base branch (independent pre-PR review,
    /// cycle 1, both lenses). A run whose review converges merge-ready at cycle 1 with no fix ever
    /// dispatched settles through the "nothing owed" path, which never triggers the mandatory
    /// final gate — but the rebase must still run there: it is the single most common way a run
    /// ends, and the one with the longest window for the base to have moved underneath it.
    /// </summary>
    [Fact]
    public async Task A_clean_cycle_one_settle_with_nothing_owed_still_rebases_onto_a_moved_base()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, string worktreePath, string originPath) =
            await SeedVerifiedRunWithOriginAsync(store, cts.Token);

        PushToOrigin(originPath, "unrelated.txt", "merged while this run was building\n", "unrelated merge");

        ScriptedExecutor executor = new(
            "Nothing to fix.\n\nVERDICT: merge-ready",
            "Nothing to fix either.\n\nVERDICT: merge-ready");

        bool mergeReady = await NewEngine(store, executor, new DaemonOptions { MaxComplianceReviewCycles = 3 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(2, "both lenses converged clean at cycle 1 — nothing owed a fix session");
        File.Exists(Path.Combine(worktreePath, "unrelated.txt")).Should().BeTrue(
            "the branch is rebased onto the merge that landed while this run was building, even though no fix or mandatory gate was ever owed");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<RunRebasedOntoBase>().Should().ContainSingle(
            e => !e.WasNoOp && !e.RecoveredByAgentSession, "git applied every commit without a conflict");
    }

    /// <summary>
    /// Task: a run rebases its branch onto the current base branch. A conflicting rebase is
    /// handed to a narrow recovery session, dispatched inside this same run — no new run, no
    /// task reopen — and a resolved conflict lets the loop proceed to the mandatory final pass
    /// exactly as a clean rebase would have.
    /// </summary>
    [Fact]
    public async Task A_conflicting_pre_final_pass_rebase_is_resolved_by_a_narrow_recovery_session_inside_the_same_run()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, string worktreePath, string originPath) =
            await SeedVerifiedRunWithOriginAsync(store, cts.Token);

        // Both sides add Widget.cs, with different content — a genuine add/add conflict when
        // this branch's own commit replays onto the moved base.
        PushToOrigin(originPath, "Widget.cs", "class Widget { /* from main */ }\n", "add Widget from main");

        ScriptedExecutor executor = new(
            "Criteria met at cycle 1.\n\nVERDICT: merge-ready",
            "FINDING: severity=high; scope=in-scope; at=Widget.cs:1\nDefect: needs work.\n\nVERDICT: needs-fixes",
            "Fixed it.\n\nRESOLUTION: fixed",
            "The fix holds.\n\nVERDICT: merge-ready",
            "Resolved the conflict by keeping both intents.\n\nRESOLUTION: fixed",
            "FINDING: severity=low; scope=in-scope; at=Widget.cs:1\nDefect: minor.\n\nVERDICT: merge-ready",
            "Still holds.\n\nVERDICT: merge-ready");
        executor.OnSpawnByIndex[4] = () =>
        {
            Git(worktreePath, "fetch -q origin");
            TryGit(worktreePath, "rebase origin/main").Should().NotBe(0, "both sides added Widget.cs differently");
            File.WriteAllText(Path.Combine(worktreePath, "Widget.cs"), "class Widget { /* resolved */ }\n");
            Git(worktreePath, "add -A");
            Git(
                worktreePath,
                "-c user.name=Test -c user.email=test@test -c core.editor=true -c commit.gpgsign=false "
                + "rebase --continue");
        };

        bool mergeReady = await NewEngine(store, executor, new DaemonOptions { MaxComplianceReviewCycles = 3 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(
            7, "the conflict earns exactly one narrow recovery session, not an extra review cycle");
        File.ReadAllText(Path.Combine(worktreePath, "Widget.cs")).Should().Contain("resolved");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<PreFinalPassRebaseRecoveryDispatched>().Should().ContainSingle();
        events.OfType<PreFinalPassRebaseRecoveryCompleted>().Should().ContainSingle(
            e => e.Outcome == ReviewFixOutcome.Fixed);
        events.OfType<RunRebasedOntoBase>().Should().ContainSingle(
            e => e.RecoveredByAgentSession && !e.WasNoOp);

        // The loop returns straight to Settling once the recovery completes, whose own rebase
        // check runs again immediately and finds nothing left to do — that trailing no-op must not
        // overwrite "recovered by a narrow session" before any human ever sees it (independent
        // pre-PR review, cycle 1, adversarial lens).
        RunDetails? run = await query.LoadAsync<RunDetails>(runId, cts.Token);
        run.Should().NotBeNull();
        run!.LastPreFinalPassRebaseRecovered.Should().BeTrue(
            "the operator investigating why the branch's history was rewritten must still see the recovery, not a trailing no-op");
        run.LastPreFinalPassRebaseWasNoOp.Should().BeFalse();
    }

    /// <summary>
    /// Task: a run rebases its branch onto the current base branch, independent pre-PR review,
    /// cycle 1, conformance lens finding: a recovery session resolves a real conflict with
    /// judgment, not a mechanical apply — unlike a clean rebase, this must NOT be allowed to reach
    /// the pull request through the ordinary "nothing owed" settle path unread by any fresh-context
    /// reviewer. Before the fix, a run that converges merge-ready at Discovery cycle 1 with both
    /// lenses clean and no fix ever dispatched settled straight through here even when the
    /// mandatory pre-final-pass rebase needed a recovery session to resolve a genuine conflict.
    /// </summary>
    [Fact]
    public async Task A_recovered_pre_final_pass_rebase_on_the_nothing_owed_settle_path_still_earns_a_final_pass()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, string worktreePath, string originPath) =
            await SeedVerifiedRunWithOriginAsync(store, cts.Token);

        // Both sides add Widget.cs, with different content — a genuine add/add conflict when
        // this branch's own commit replays onto the moved base.
        PushToOrigin(originPath, "Widget.cs", "class Widget { /* from main */ }\n", "add Widget from main");

        ScriptedExecutor executor = new(
            "Nothing to fix.\n\nVERDICT: merge-ready",
            "Nothing to fix either.\n\nVERDICT: merge-ready",
            "Resolved the conflict by keeping both intents.\n\nRESOLUTION: fixed",
            "Nothing new to flag.\n\nVERDICT: merge-ready",
            "Still holds.\n\nVERDICT: merge-ready");
        executor.OnSpawnByIndex[2] = () =>
        {
            Git(worktreePath, "fetch -q origin");
            TryGit(worktreePath, "rebase origin/main").Should().NotBe(0, "both sides added Widget.cs differently");
            File.WriteAllText(Path.Combine(worktreePath, "Widget.cs"), "class Widget { /* resolved */ }\n");
            Git(worktreePath, "add -A");
            Git(
                worktreePath,
                "-c user.name=Test -c user.email=test@test -c core.editor=true -c commit.gpgsign=false "
                + "rebase --continue");
        };

        bool mergeReady = await NewEngine(store, executor, new DaemonOptions { MaxComplianceReviewCycles = 3 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(
            5, "both lenses converged clean at cycle 1 (nothing owed), plus the recovery session, " +
            "plus the mandatory final pass the recovered conflict now earns even on that path");
        File.ReadAllText(Path.Combine(worktreePath, "Widget.cs")).Should().Contain("resolved");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<PreFinalPassRebaseRecoveryDispatched>().Should().ContainSingle();
        events.OfType<RunRebasedOntoBase>().Should().Contain(e => e.RecoveredByAgentSession && !e.WasNoOp);
        events.OfType<ReviewDispatched>().Should().Contain(
            e => e.Mode == ReviewMode.FinalFullPass,
            "the recovered conflict forces the mandatory final pass rather than settling unread");
    }

    /// <summary>
    /// Task: a run rebases its branch onto the current base branch, independent pre-PR review,
    /// cycle 3, both lenses finding: <c>MaySettleReason</c>'s <c>Bar</c> clause (a final full pass
    /// whose verdict is merge-ready with only below-bar findings) was gated on
    /// <see cref="RunAggregate.PreFinalPassRebaseAwaitingReview"/> only in its own doc's shadow —
    /// the actual conjunct landed solely on the <c>NothingOwed</c> clause. A recovered rebase
    /// conflict discovered on the very Settling entry that would otherwise settle through Bar must
    /// force one more fresh-context final pass instead, exactly as it already does for NothingOwed.
    /// </summary>
    [Fact]
    public async Task A_recovered_pre_final_pass_rebase_on_the_severity_bar_settle_path_still_earns_another_final_pass()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, string worktreePath, string originPath) =
            await SeedVerifiedRunWithOriginAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "Criteria met at cycle 1.\n\nVERDICT: merge-ready",
            "FINDING: severity=high; scope=in-scope; at=Widget.cs:1\nDefect: needs work.\n\nVERDICT: needs-fixes",
            "Fixed it.\n\nRESOLUTION: fixed",
            "The fix holds.\n\nVERDICT: merge-ready",
            "Nothing new to flag.\n\nVERDICT: merge-ready",
            "FINDING: severity=low; scope=in-scope; at=Widget.cs:1\nDefect: minor.\n\nVERDICT: merge-ready",
            "Resolved the conflict by keeping both intents.\n\nRESOLUTION: fixed",
            "Still nothing to flag.\n\nVERDICT: merge-ready",
            "Still just the minor nit.\n\nVERDICT: merge-ready");

        // Main moves only once the genuine mandatory final pass (index 4-5) is already under way,
        // so the conflict is discovered on the very Settling entry that reads that pass's own
        // merge-ready-with-a-ride-along verdict — the exact ordering the finding names, rather
        // than a conflict discovered before the final pass ever ran (already covered by
        // A_conflicting_pre_final_pass_rebase_is_resolved_by_a_narrow_recovery_session_inside_the_same_run).
        executor.OnSpawnByIndex[5] = () =>
            PushToOrigin(originPath, "Widget.cs", "class Widget { /* from main */ }\n", "add Widget from main");
        executor.OnSpawnByIndex[6] = () =>
        {
            Git(worktreePath, "fetch -q origin");
            TryGit(worktreePath, "rebase origin/main").Should().NotBe(0, "both sides added Widget.cs differently");
            File.WriteAllText(Path.Combine(worktreePath, "Widget.cs"), "class Widget { /* resolved */ }\n");
            Git(worktreePath, "add -A");
            Git(
                worktreePath,
                "-c user.name=Test -c user.email=test@test -c core.editor=true -c commit.gpgsign=false "
                + "rebase --continue");
        };

        bool mergeReady = await NewEngine(store, executor, new DaemonOptions { MaxComplianceReviewCycles = 3 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(
            9, "the first final pass's own merge-ready-with-a-ride-along verdict must not settle " +
            "through the severity bar while a just-recovered rebase conflict has never been read by " +
            "a fresh-context reviewer — a second final pass (indexes 7-8) is owed, on top of the " +
            "recovery session (index 6)");
        File.ReadAllText(Path.Combine(worktreePath, "Widget.cs")).Should().Contain("resolved");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<PreFinalPassRebaseRecoveryDispatched>().Should().ContainSingle();
        events.OfType<RunRebasedOntoBase>().Should().Contain(e => e.RecoveredByAgentSession && !e.WasNoOp);
        events.OfType<ReviewDispatched>().Count(e => e.Mode == ReviewMode.FinalFullPass).Should().Be(
            4, "two lenses for the genuine final pass the conflict interrupted, plus two more for " +
            "the fresh-context final pass the recovered conflict forces before the run may settle");
    }

    /// <summary>
    /// Task: a run rebases its branch onto the current base branch. When the recovery session
    /// cannot honestly resolve the conflict, the run parks for a human — the same shape a
    /// disputed rebase park takes today — rather than failing the run or reopening the task.
    /// </summary>
    [Fact]
    public async Task A_disputed_pre_final_pass_rebase_conflict_parks_the_run_for_a_human()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, string worktreePath, string originPath) =
            await SeedVerifiedRunWithOriginAsync(store, cts.Token);

        PushToOrigin(originPath, "Widget.cs", "class Widget { /* from main, disputed */ }\n", "add Widget from main");

        ScriptedExecutor executor = new(
            "Criteria met at cycle 1.\n\nVERDICT: merge-ready",
            "FINDING: severity=high; scope=in-scope; at=Widget.cs:1\nDefect: needs work.\n\nVERDICT: needs-fixes",
            "Fixed it.\n\nRESOLUTION: fixed",
            "The fix holds.\n\nVERDICT: merge-ready",
            "Both sides change Widget.cs's own behavior — I cannot honestly pick.\n\nRESOLUTION: disputed");
        executor.OnSpawnByIndex[4] = () =>
        {
            Git(worktreePath, "fetch -q origin");
            TryGit(worktreePath, "rebase origin/main").Should().NotBe(0, "both sides added Widget.cs differently");
            Git(worktreePath, "rebase --abort");
        };

        bool mergeReady = await NewEngine(store, executor, new DaemonOptions { MaxComplianceReviewCycles = 3 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("a disputed conflict parks the run rather than settling");
        executor.Spawns.Should().HaveCount(5, "the loop stops at the recovery session's own dispute");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked);
        run.ParkedReason.Should().Contain("pre-flight rebase conflicted");
        run.ParkedOnRebaseRecoveryDispute.Should().BeTrue(
            "the attention pane's lever must offer --needs-fixes, never --merge-ready, for this park");

        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<PreFinalPassRebaseRecoveryCompleted>().Should().ContainSingle(
            e => e.Outcome == ReviewFixOutcome.Disputed);
        events.OfType<RunRebasedOntoBase>().Should().BeEmpty("nothing was actually resolved");

        File.ReadAllText(RunPaths.PreFinalPassRebaseDisputeFile(RunPaths.GlobalDirectory(runId)))
            .Should().Contain("Both sides change Widget.cs");
    }

    /// <summary>
    /// Task: a run rebases its branch onto the current base branch (independent pre-PR review,
    /// cycle 1, both lenses). A recovery session that repeatedly claims the conflict is resolved
    /// without ever actually rebasing sends <c>EnsureRebasedBeforeFinalPassAsync</c> straight back
    /// to the identical conflict every time Settling is re-entered — bounded here so the loop parks
    /// for a human once it has spent its round cap, rather than spawning a fresh agent session
    /// forever with nothing to stop it.
    /// </summary>
    [Fact]
    public async Task A_rebase_recovery_session_that_never_actually_resolves_parks_once_the_round_cap_is_spent()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _, string originPath) = await SeedVerifiedRunWithOriginAsync(store, cts.Token);

        // A genuine add/add conflict that never resolves itself: every fresh `git rebase
        // origin/main` attempt reconflicts identically, because nothing about the branch's own
        // Widget.cs ever changes between attempts here.
        PushToOrigin(originPath, "Widget.cs", "class Widget { /* from main */ }\n", "add Widget from main");

        ScriptedExecutor executor = new(
            "Nothing to fix.\n\nVERDICT: merge-ready",
            "Nothing to fix either.\n\nVERDICT: merge-ready",
            "Claiming this is resolved without touching the worktree.\n\nRESOLUTION: fixed",
            "Claiming this is resolved without touching the worktree.\n\nRESOLUTION: fixed",
            "Claiming this is resolved without touching the worktree.\n\nRESOLUTION: fixed");

        bool mergeReady = await NewEngine(store, executor, new DaemonOptions { MaxComplianceReviewCycles = 3 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("the round cap parks the run rather than dispatching a fourth recovery session");
        executor.Spawns.Should().HaveCount(
            5, "two lenses converging clean, plus exactly the round cap's worth of recovery sessions and never a fourth");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked);
        run.ParkedReason.Should().Contain("3 time(s) in a row");

        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<PreFinalPassRebaseRecoveryDispatched>().Should().HaveCount(
            3, "the cap stops the fourth dispatch before it ever spawns");
    }

    /// <summary>
    /// Task: a run rebases its branch onto the current base branch. A human's needs-fixes
    /// resolution on a disputed pre-final-pass rebase conflict redispatches the recovery session
    /// with their guidance folded into its prompt, and a clean resolution this time lets the run
    /// proceed to the mandatory final pass exactly as an unparked conflict would have.
    /// </summary>
    [Fact]
    public async Task A_human_resolving_a_disputed_pre_final_pass_rebase_conflict_redispatches_with_their_guidance()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, string worktreePath, string originPath) =
            await SeedVerifiedRunWithOriginAsync(store, cts.Token);

        PushToOrigin(originPath, "Widget.cs", "class Widget { /* from main, disputed */ }\n", "add Widget from main");

        ScriptedExecutor firstAttempt = new(
            "Criteria met at cycle 1.\n\nVERDICT: merge-ready",
            "FINDING: severity=high; scope=in-scope; at=Widget.cs:1\nDefect: needs work.\n\nVERDICT: needs-fixes",
            "Fixed it.\n\nRESOLUTION: fixed",
            "The fix holds.\n\nVERDICT: merge-ready",
            "Both sides change Widget.cs's own behavior — I cannot honestly pick.\n\nRESOLUTION: disputed");
        firstAttempt.OnSpawnByIndex[4] = () =>
        {
            Git(worktreePath, "fetch -q origin");
            TryGit(worktreePath, "rebase origin/main").Should().NotBe(0, "both sides added Widget.cs differently");
            Git(worktreePath, "rebase --abort");
        };
        bool firstMergeReady = await NewEngine(store, firstAttempt, new DaemonOptions { MaxComplianceReviewCycles = 3 })
            .ReviewAsync(runId, taskId, cts.Token);
        firstMergeReady.Should().BeFalse("the first attempt disputes and parks");

        const string humanResolution = "Keep main's Widget — the task branch's own version is superseded.";
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.NeedsFixes, humanResolution, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor retry = new(
            "Applied the human's decision and rebased cleanly.\n\nRESOLUTION: fixed",
            "FINDING: severity=low; scope=in-scope; at=Widget.cs:1\nDefect: minor.\n\nVERDICT: merge-ready",
            "Still holds.\n\nVERDICT: merge-ready");
        retry.OnSpawnByIndex[0] = () =>
        {
            Git(worktreePath, "fetch -q origin");
            TryGit(worktreePath, "rebase origin/main").Should().NotBe(0, "the conflict is still there for the retry to resolve");
            File.WriteAllText(Path.Combine(worktreePath, "Widget.cs"), "class Widget { /* kept main's version */ }\n");
            Git(worktreePath, "add -A");
            Git(
                worktreePath,
                "-c user.name=Test -c user.email=test@test -c core.editor=true -c commit.gpgsign=false "
                + "rebase --continue");
        };

        bool mergeReady = await NewEngine(store, retry, new DaemonOptions { MaxComplianceReviewCycles = 3 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        retry.Spawns.Should().HaveCount(3, "the redispatched recovery session plus the mandatory final pass");
        retry.Spawns[0].Prompt.Should().Contain(humanResolution);
        File.ReadAllText(Path.Combine(worktreePath, "Widget.cs")).Should().Contain("kept main's version");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.UnderReview);
        run.ParkedOnRebaseRecoveryDispute.Should().BeFalse("the resolved park no longer applies");

        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<PreFinalPassRebaseRecoveryCompleted>().Should().HaveCount(2);
        events.OfType<PreFinalPassRebaseRecoveryCompleted>().Should().Contain(e => e.Outcome == ReviewFixOutcome.Disputed);
        events.OfType<PreFinalPassRebaseRecoveryCompleted>().Should().Contain(e => e.Outcome == ReviewFixOutcome.Fixed);
        events.OfType<RunRebasedOntoBase>().Should().ContainSingle(e => e.RecoveredByAgentSession);
    }

    /// <summary>
    /// Task: a pre-final-pass rebase that applies cleanly but breaks the mandatory gate gets a
    /// repair lap inside the same run instead of failing it. A clean rebase's own mandatory gate
    /// failure earns exactly one narrow repair session, dispatched inside this same run — no
    /// RunFailed, no task reopen — and a passing re-run of the gate afterward lets the loop
    /// proceed to settle. It does not settle straight off that passing gate, though (independent
    /// pre-PR review, cycle 1, adversarial lens): a repair session's own fix is exactly the same
    /// kind of unreviewed judgment call a rebase-recovery session's conflict resolution is, so it
    /// earns the identical mandatory final pass before the run may settle, the same way a
    /// recovered rebase already does on the ordinary nothing-owed path.
    /// </summary>
    [Fact]
    public async Task A_settling_gate_failure_after_a_real_rebase_gets_one_repair_session_then_passes()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        string markerPath = Path.Combine(_home, $"repair-marker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_home);
        IReadOnlyList<Hall9k.Domain.Features.Project.VerifyCommand> verifyCommands =
        [
            new Hall9k.Domain.Features.Project.VerifyCommand("build", GateScript.New()
                .BranchOnFile(markerPath,
                    whenPresent: GateScript.New().Print("build ok").Exit(0),
                    whenMissing: GateScript.New().Print("BUILD BROKEN: CS0246 'Widget' could not be found").Exit(1))
                .Command),
        ];
        (Guid taskId, Guid runId, _, string originPath) =
            await SeedVerifiedRunWithOriginAndGateAsync(store, verifyCommands, cts.Token);

        PushToOrigin(originPath, "unrelated.txt", "merged while this run was building\n", "unrelated merge");

        ScriptedExecutor executor = new(
            "Nothing to fix.\n\nVERDICT: merge-ready",
            "Nothing to fix either.\n\nVERDICT: merge-ready",
            "Found what the rebase left broken and fixed it.",
            "Nothing new to flag.\n\nVERDICT: merge-ready",
            "Still holds.\n\nVERDICT: merge-ready");
        executor.OnSpawnByIndex[2] = () => File.WriteAllText(markerPath, "fixed\n");

        bool mergeReady = await NewEngine(store, executor, new DaemonOptions { MaxComplianceReviewCycles = 3 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(
            5, "two review passes converge clean, one repair session, and the mandatory final pass the " +
            "repair earns even on the nothing-owed path");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<SettlingGateRepairDispatched>().Should().ContainSingle();
        events.OfType<SettlingGateRepairCompleted>().Should().ContainSingle();
        events.OfType<Hall9k.Domain.Features.Run.Events.RunFailed>().Should().BeEmpty(
            "a repair-eligible gate failure dispatches a repair session instead of failing the run");
    }

    /// <summary>
    /// Task: a pre-final-pass rebase that applies cleanly but breaks the mandatory gate gets a
    /// repair lap inside the same run instead of failing it. A repair session that never actually
    /// fixes the gate sends the loop straight back to the identical failure every time Settling is
    /// re-entered — bounded here so the loop parks for a human once it has spent its round cap
    /// (one, by default), naming both commits the triggering rebase ran between and the gate's own
    /// output, rather than spawning a fresh session forever.
    /// </summary>
    [Fact]
    public async Task A_settling_gate_repair_that_never_fixes_it_parks_once_the_round_cap_is_spent()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        IReadOnlyList<Hall9k.Domain.Features.Project.VerifyCommand> verifyCommands =
        [
            new Hall9k.Domain.Features.Project.VerifyCommand(
                "build", GateScript.New().Print("BUILD BROKEN: CS0246 'Widget' could not be found").Exit(1).Command),
        ];
        (Guid taskId, Guid runId, _, string originPath) =
            await SeedVerifiedRunWithOriginAndGateAsync(store, verifyCommands, cts.Token);

        PushToOrigin(originPath, "unrelated.txt", "merged while this run was building\n", "unrelated merge");

        ScriptedExecutor executor = new(
            "Nothing to fix.\n\nVERDICT: merge-ready",
            "Nothing to fix either.\n\nVERDICT: merge-ready",
            "Looked, but could not find the cause.");

        bool mergeReady = await NewEngine(store, executor, new DaemonOptions { MaxComplianceReviewCycles = 3 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("the round cap (one, by default) parks the run rather than dispatching a second repair session");
        executor.Spawns.Should().HaveCount(3, "two review passes converge clean, plus exactly the round cap's worth of repair sessions");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked);
        run.ParkedReason.Should().Contain("1 repair session(s) in a row")
            .And.Contain(run.LastPreFinalPassRebaseFromCommit![..10])
            .And.Contain(run.LastPreFinalPassRebaseOntoCommit![..10])
            .And.Contain("BUILD BROKEN");

        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<SettlingGateRepairDispatched>().Should().ContainSingle(
            "the cap stops the second dispatch before it ever spawns");
        events.OfType<SettlingGateRepairCapReached>().Should().ContainSingle();
    }

    /// <summary>
    /// Task: a pre-final-pass rebase that applies cleanly but breaks the mandatory gate gets a
    /// repair lap inside the same run instead of failing it. A human's needs-fixes resolve on a
    /// spent repair cap buys exactly one more repair round, carrying their guidance into its
    /// prompt — and, unlike the rebase-recovery cap's own inherited resolve, deliberately does not
    /// reset the round counter: a cap raised to two rounds proves it, since a reset would let the
    /// bought round's own failure earn a second automatic dispatch instead of re-parking immediately.
    /// </summary>
    [Fact]
    public async Task A_human_resolving_a_spent_settling_gate_repair_cap_buys_one_round_without_resetting_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        IReadOnlyList<Hall9k.Domain.Features.Project.VerifyCommand> verifyCommands =
        [
            new Hall9k.Domain.Features.Project.VerifyCommand(
                "build", GateScript.New().Print("BUILD BROKEN: CS0246 'Widget' could not be found").Exit(1).Command),
        ];
        (Guid taskId, Guid runId, _, string originPath) =
            await SeedVerifiedRunWithOriginAndGateAsync(store, verifyCommands, cts.Token);

        PushToOrigin(originPath, "unrelated.txt", "merged while this run was building\n", "unrelated merge");

        DaemonOptions options = new() { MaxComplianceReviewCycles = 3, MaxSettlingGateRepairRounds = 2 };

        ScriptedExecutor firstAttempt = new(
            "Nothing to fix.\n\nVERDICT: merge-ready",
            "Nothing to fix either.\n\nVERDICT: merge-ready",
            "Looked, but could not find the cause.",
            "Still could not find the cause.");
        bool firstMergeReady = await NewEngine(store, firstAttempt, options).ReviewAsync(runId, taskId, cts.Token);
        firstMergeReady.Should().BeFalse("both repair rounds the cap allows fail their own gate re-run");
        firstAttempt.Spawns.Should().HaveCount(4, "two review passes plus the round cap's own two repair sessions");

        const string humanGuidance = "Check Widget.cs's own namespace — the rebase likely dropped a using directive.";
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.NeedsFixes, humanGuidance, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor retry = new("Still could not find the cause, even with the human's guidance.");
        bool retryMergeReady = await NewEngine(store, retry, options).ReviewAsync(runId, taskId, cts.Token);

        retryMergeReady.Should().BeFalse(
            "the bought round also fails its own gate re-run, and the counter was never reset, so the very next automatic check is already back at the cap");
        retry.Spawns.Should().HaveCount(1, "the human's resolve buys exactly one bought round, not a second fresh cap's worth");
        retry.Spawns[0].Prompt.Should().Contain(humanGuidance);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked);

        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<SettlingGateRepairDispatched>().Should().HaveCount(
            3, "two ordinary rounds before the first park, plus exactly the one bought round — a reset would have " +
            "let the bought round's own failure earn a second automatic dispatch instead of re-parking immediately");
        events.OfType<SettlingGateRepairCapReached>().Should().HaveCount(2, "parked once at the original cap, once again right after the bought round also failed");
    }

    /// <summary>
    /// Task: a pre-final-pass rebase that applies cleanly but breaks the mandatory gate gets a
    /// repair lap inside the same run instead of failing it. A rebase that needed the recovery
    /// session's own judgment to resolve a real conflict is just as repair-eligible as a clean one
    /// — and the repair session's own prompt says so, naming the recovery rather than describing a
    /// mechanical apply that never happened.
    /// </summary>
    [Fact]
    public async Task A_settling_gate_repair_after_a_recovered_rebase_names_the_recovery_in_its_prompt()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        string markerPath = Path.Combine(_home, $"repair-marker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_home);
        IReadOnlyList<Hall9k.Domain.Features.Project.VerifyCommand> verifyCommands =
        [
            new Hall9k.Domain.Features.Project.VerifyCommand("build", GateScript.New()
                .BranchOnFile(markerPath,
                    whenPresent: GateScript.New().Print("build ok").Exit(0),
                    whenMissing: GateScript.New().Print("BUILD BROKEN: CS0246 'Widget' could not be found").Exit(1))
                .Command),
        ];
        (Guid taskId, Guid runId, string worktreePath, string originPath) =
            await SeedVerifiedRunWithOriginAndGateAsync(store, verifyCommands, cts.Token);

        // A genuine add/add conflict, the same shape every rebase-recovery test above uses.
        PushToOrigin(originPath, "Widget.cs", "class Widget { /* from main */ }\n", "add Widget from main");

        ScriptedExecutor executor = new(
            "Nothing to fix.\n\nVERDICT: merge-ready",
            "Nothing to fix either.\n\nVERDICT: merge-ready",
            "Resolved the conflict by keeping both intents.\n\nRESOLUTION: fixed",
            "Found what the rebase left broken and fixed it.",
            "Nothing new to flag.\n\nVERDICT: merge-ready",
            "Still holds.\n\nVERDICT: merge-ready");
        executor.OnSpawnByIndex[2] = () =>
        {
            Git(worktreePath, "fetch -q origin");
            TryGit(worktreePath, "rebase origin/main").Should().NotBe(0, "both sides added Widget.cs differently");
            File.WriteAllText(Path.Combine(worktreePath, "Widget.cs"), "class Widget { /* resolved */ }\n");
            Git(worktreePath, "add -A");
            Git(
                worktreePath,
                "-c user.name=Test -c user.email=test@test -c core.editor=true -c commit.gpgsign=false "
                + "rebase --continue");
        };
        executor.OnSpawnByIndex[3] = () => File.WriteAllText(markerPath, "fixed\n");

        bool mergeReady = await NewEngine(store, executor, new DaemonOptions { MaxComplianceReviewCycles = 3 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(
            6, "two review passes, the rebase-recovery session for the conflict, the Settling-gate repair " +
            "session, and the mandatory final pass the recovered conflict earns even on the nothing-owed path");
        executor.Spawns[3].Prompt.Should().Contain(
            "needed a narrow recovery session's own judgment to resolve a real conflict",
            "the repair prompt must say a recovery rebase preceded it, not a clean one");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<PreFinalPassRebaseRecoveryDispatched>().Should().ContainSingle();
        events.OfType<SettlingGateRepairDispatched>().Should().ContainSingle();
    }

    /// <summary>
    /// Task: a pre-final-pass rebase that applies cleanly but breaks the mandatory gate gets a
    /// repair lap inside the same run instead of failing it. A mandatory-gate failure with no real
    /// rebase behind it — here, no origin ever reachable, so the pre-final-pass rebase check never
    /// records a landing at all — keeps today's fail-hard contract unchanged: the run fails, and no
    /// repair session is ever dispatched.
    /// </summary>
    [Fact]
    public async Task A_settling_gate_failure_with_no_real_rebase_behind_it_fails_the_run_with_no_repair_session()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _, Guid projectId) = await SeedVerifiedRunWithTestGateAsync(store, cts.Token);

        // A mid-run verify-commands change is what this test borrows to force the mandatory
        // Settling gate on an otherwise "nothing owed" cycle-1 convergence, the identical technique
        // A_clean_discovery_only_convergence_still_runs_the_settling_gate_over_the_current_verify_commands
        // above already proves forces that gate with no fix, no human, and — here — no origin at
        // all, so the pre-final-pass rebase check never records a landing either.
        IReadOnlyList<Hall9k.Domain.Features.Project.VerifyCommand> failingVerifyCommands =
        [
            new Hall9k.Domain.Features.Project.VerifyCommand(
                "build", GateScript.New().Print("BUILD BROKEN: CS0246 'Widget' could not be found").Exit(1).Command),
        ];
        async Task ChangeToFailingGateAsync()
        {
            await using IDocumentSession session = store.LightweightSession();
            Hall9k.Domain.Features.Project.ProjectAggregate? project =
                await session.Events.AggregateStreamAsync<Hall9k.Domain.Features.Project.ProjectAggregate>(
                    projectId, token: cts.Token);
            session.Events.Append(projectId, Hall9k.Domain.Features.Project.Handlers.ProjectDecider.ChangeSettings(
                project!,
                verifyCommands: Optional<IReadOnlyList<Hall9k.Domain.Features.Project.VerifyCommand>>.Of(failingVerifyCommands),
                skipPermissions: Optional<bool>.None,
                contextLinks: Optional<IReadOnlyList<Hall9k.Domain.Features.Project.ContextLink>>.None,
                Now, project!.OwnerId));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor executor = new(
            "Every acceptance criterion is met.\n\nVERDICT: merge-ready",
            "Nothing survived verification.\n\nVERDICT: merge-ready");
        executor.OnSpawnByIndex[0] = () => ChangeToFailingGateAsync().GetAwaiter().GetResult();

        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("the gate fails with no real rebase behind it, so today's fail-hard contract is unchanged");
        executor.Spawns.Should().HaveCount(
            2, "both lenses converge clean at cycle 1 — the verify-commands change forces the mandatory " +
            "Settling gate, but nothing here ever earns a repair session");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.Failed);

        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<RunRebasedOntoBase>().Should().BeEmpty("origin was never reachable, so no rebase was ever recorded");
        events.OfType<SettlingGateRepairDispatched>().Should().BeEmpty("no real rebase behind the failure means no repair lap");
        events.OfType<Hall9k.Domain.Features.Run.Events.RunFailed>().Should().ContainSingle();
    }

    /// <summary>
    /// Task: a pre-final-pass rebase that applies cleanly but breaks the mandatory gate gets a
    /// repair lap inside the same run instead of failing it — criterion 4's own no-op half. Once
    /// an earlier real rebase's own full-scope gate has already passed, a LATER Settling entry's
    /// no-op re-check must not leave the run repair-eligible forever:
    /// <see cref="RunAggregate.LastPreFinalPassRebaseWasNoOp"/> alone never goes back to true once
    /// a real rebase has landed on a run (the trailing-no-op guard in
    /// <see cref="RunAggregate.Apply(RunRebasedOntoBase)"/> deliberately ignores a later no-op's
    /// own claim), so <c>ReviewEngine.EligibleForSettlingGateRepair</c> also has to require
    /// <see cref="RunAggregate.PreFinalPassRebaseAwaitingGate"/>, which the earlier passing gate
    /// already cleared. A mandatory-gate failure at that second, later entry — forced here by a
    /// mid-run verify-commands change, unrelated to either rebase — keeps today's fail-hard
    /// contract exactly as <see cref="A_settling_gate_failure_with_no_real_rebase_behind_it_fails_the_run_with_no_repair_session"/>
    /// already proves for the no-rebase-at-all shape: the run fails, and no repair session is ever
    /// dispatched, even though a real rebase sits earlier on this same run's own stream.
    /// </summary>
    [Fact]
    public async Task A_gate_failure_after_a_later_no_op_rebase_fails_the_run_with_no_repair_session_once_an_earlier_real_rebase_already_passed()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        IReadOnlyList<Hall9k.Domain.Features.Project.VerifyCommand> verifyCommands =
        [
            new Hall9k.Domain.Features.Project.VerifyCommand("build", GateScript.New().Print("build ok").Exit(0).Command),
        ];
        (Guid taskId, Guid runId, _, string originPath) =
            await SeedVerifiedRunWithOriginAndGateAsync(store, verifyCommands, cts.Token);

        // The one real rebase this test's own earlier gate pass lands onto — pushed before the
        // run ever starts reviewing, so EnsureRebasedBeforeFinalPassAsync's own first call (at the
        // first Settling entry, reached below only after the fix-and-verify cycle converges) is
        // the one that lands it.
        PushToOrigin(originPath, "unrelated.txt", "merged while this run was building\n", "unrelated merge");

        await using IQuerySession projectLookup = store.QuerySession();
        Guid projectId = (await projectLookup.LoadAsync<TaskDetails>(taskId, cts.Token))!.ProjectId;

        IReadOnlyList<Hall9k.Domain.Features.Project.VerifyCommand> failingVerifyCommands =
        [
            new Hall9k.Domain.Features.Project.VerifyCommand(
                "build", GateScript.New().Print("BUILD BROKEN: CS0246 'Widget' could not be found").Exit(1).Command),
        ];
        async Task ChangeToFailingGateAsync()
        {
            await using IDocumentSession session = store.LightweightSession();
            Hall9k.Domain.Features.Project.ProjectAggregate? project =
                await session.Events.AggregateStreamAsync<Hall9k.Domain.Features.Project.ProjectAggregate>(
                    projectId, token: cts.Token);
            session.Events.Append(projectId, Hall9k.Domain.Features.Project.Handlers.ProjectDecider.ChangeSettings(
                project!,
                verifyCommands: Optional<IReadOnlyList<Hall9k.Domain.Features.Project.VerifyCommand>>.Of(failingVerifyCommands),
                skipPermissions: Optional<bool>.None,
                contextLinks: Optional<IReadOnlyList<Hall9k.Domain.Features.Project.ContextLink>>.None,
                Now, project!.OwnerId));
            await session.SaveChangesAsync(cts.Token);
        }

        const string conformanceFinding = "1. `Auth.cs:42` — the limiter never resets. Scenario: the second request always 429s.";
        const string adversarialFinding = "1. `WorkItemContext.cs:18` — task text reaches the prompt unfenced. Scenario: a crafted objective redirects the agent.";
        ScriptedExecutor executor = new(
            $"{conformanceFinding}\n\nVERDICT: needs-fixes",
            $"{adversarialFinding}\n\nVERDICT: needs-fixes",
            "Reset the limiter window and fenced the task text.\n\nRESOLUTION: fixed",
            // Cycle 2 is a scoped Verify pass — CurrentCycleMode stays Verify once this converges
            // clean, which is what forces the mandatory gate at the first Settling entry just
            // below, exactly the way NeedsFullGateBeforeSettling's own doc describes.
            "Verified both fixes; nothing new stands.\n\nVERDICT: merge-ready",
            // Cycle 3, the mandatory FinalFullPass: the earlier real rebase's own gate already
            // passed at the first Settling entry, immediately before this cycle dispatched — this
            // cycle's own two lenses are the fresh-context read that gate pass earns. The
            // verify-commands change lands on this cycle's very last spawn, so the LATER, no-op
            // rebase re-check the second Settling entry runs right after this cycle concludes
            // finds the gate newly broken for a reason that has nothing to do with either rebase.
            "Criteria met.\n\nVERDICT: merge-ready",
            "Hunted again; the boundary holds.\n\nVERDICT: merge-ready");
        executor.OnSpawnByIndex[5] = () => ChangeToFailingGateAsync().GetAwaiter().GetResult();

        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse(
            "the second Settling entry's own gate failure follows the later no-op rebase, not the earlier " +
            "real one whose own gate already passed clean");
        executor.Spawns.Should().HaveCount(
            6, "two discovery passes, one fix, one verify pass, and the mandatory final full pass (two " +
            "lenses) — the verify-commands change fails the gate on the very last spawn, with no session " +
            "ever earned over it");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.Failed);

        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        List<RunRebasedOntoBase> rebases = [.. events.OfType<RunRebasedOntoBase>()];
        rebases.Should().HaveCount(2, "the earlier real rebase, then the later no-op re-check that found nothing left to do");
        rebases[0].WasNoOp.Should().BeFalse("the earlier rebase is the real one this test's own gate pass lands onto");
        rebases[1].WasNoOp.Should().BeTrue("the later re-check finds origin unmoved since the earlier rebase");
        events.OfType<VerificationPassed>().Should().Contain(pass => pass.RanFullScope,
            "the earlier real rebase's own gate ran, and passed, at full scope, clearing " +
            "PreFinalPassRebaseAwaitingGate before the later, unrelated failure ever ran — cycle 2's own " +
            "scoped reverify can also legitimately fall back to full scope with nothing yet committed to " +
            "narrow it against, so this only asserts that the rebase's own full-scope pass is among them");
        events.OfType<SettlingGateRepairDispatched>().Should().BeEmpty(
            "the failure follows a no-op rebase with its own real rebase already gated clean, so it must " +
            "fail hard exactly as it would with no rebase at all, not spend a repair round on an unrelated break");
        events.OfType<Hall9k.Domain.Features.Run.Events.RunFailed>().Should().ContainSingle();
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
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "Criteria met, doctrine followed.\n\nVERDICT: merge-ready",
            "FINDING: severity=high; scope=in-scope; at=Spawner.cs:60\n"
            + "Defect: the child process is never reaped. Scenario: a failed run leaks a claude process.\n\n"
            + "VERDICT: needs-fixes",
            "Reaped the child on the failure path.\n\nRESOLUTION: fixed",
            // Cycle 2: only the adversarial track is still active, so it gets one Verify pass.
            "The lifetime holds now.\n\nVERDICT: merge-ready",
            // Cycle 3: the mandatory final full pass, both lenses fresh — it reawakens the
            // conformance track that went dormant at cycle 1 for one more look.
            "Criteria still met.\n\nVERDICT: merge-ready",
            "The lifetime still holds.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue("the adversarial track found it, the fix resolved it, and both lenses read clean");
        executor.Spawns.Should().HaveCount(
            6, "two passes → one fix → one verify pass over the surviving track → the mandatory " +
                "final full pass, which reawakens the dormant conformance track");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];

        events.OfType<ReviewDispatched>().Select(e => e.Lens).Should().Equal(
            [ReviewLens.Conformance, ReviewLens.Adversarial, ReviewLens.Verify,
                ReviewLens.Conformance, ReviewLens.Adversarial],
            "cycle 2 merges into one Verify pass over the surviving track, and the mandatory final "
                + "full pass reawakens both");
        events.OfType<ReviewPassCompleted>().Select(e => (e.Cycle, e.Lens, e.Verdict)).Should().Equal(
        [
            (1, ReviewLens.Conformance, ReviewVerdict.MergeReady),
            (1, ReviewLens.Adversarial, ReviewVerdict.NeedsFixes),
            (2, ReviewLens.Verify, ReviewVerdict.MergeReady),
            (3, ReviewLens.Conformance, ReviewVerdict.MergeReady),
            (3, ReviewLens.Adversarial, ReviewVerdict.MergeReady),
        ], "which track found the defect is a fact on the stream, not an impression");
        events.OfType<ReviewTrackConcluded>().Select(e => (e.Lens, e.Cycle, e.Settlement)).Should().Equal(
            [
                (ReviewLens.Conformance, 1, ReviewSettlement.Clean),
                (ReviewLens.Adversarial, 2, ReviewSettlement.Clean),
                (ReviewLens.Conformance, 3, ReviewSettlement.Clean),
                (ReviewLens.Adversarial, 3, ReviewSettlement.Clean),
            ], "the mandatory final full pass reconfirms both tracks clean at cycle 3, on the record");
        events.OfType<ReviewCompleted>().Select(e => (e.Cycle, e.Verdict)).Should().Equal(
            [(1, ReviewVerdict.NeedsFixes), (2, ReviewVerdict.MergeReady), (3, ReviewVerdict.MergeReady)],
            "the cycle's verdict is the merge of the tracks that were live for it");
        events.OfType<ReviewPassCompleted>().Select(e => e.Mode).Should().Equal(
            [ReviewMode.Discovery, ReviewMode.Discovery, ReviewMode.Verify,
                ReviewMode.FinalFullPass, ReviewMode.FinalFullPass],
            "the mode each pass ran under is on the stream");
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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

        // This seed never records a ReviewDispatched event, so RunAggregate.ReviewCycle reads 0
        // even though a cycle-1 fix already ran on the stream — the same "no review pass has ever
        // actually run" shape a pre-gate dispute resume leaves behind, so the engine's next dispatch
        // is Discovery over both lenses (task: review cycles after the first), reusing the label
        // "cycle 1" the seed's own events already used.
        ScriptedExecutor executor = new(
            // Conformance reads clean again.
            "Nothing new survived verification.\n\nVERDICT: merge-ready",
            // The next cycle's reviewer reports the untouched legacy line again, and this time
            // the routing succeeds — the retry the failed disposition exists to allow.
            $"{preExisting}\n\nVERDICT: needs-fixes");
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
        DocumentStore store = postgres.Store;
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
            // Cycle 2: both tracks are still active (the placeholder finding forced conformance
            // to continue, and the pre-gate rule kept adversarial alive over its own routed
            // finding), so one Verify pass stands in for both.
            "Every acceptance criterion is met now, and nothing survived verification.\n\nVERDICT: merge-ready",
            // Cycle 3: the mandatory final full pass, both lenses fresh.
            "Confirmed clean.\n\nVERDICT: merge-ready",
            "Confirmed clean too.\n\nVERDICT: merge-ready");
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
    /// cycle 2 is gated here, so its mediums are fixed without forcing a review pass of their own.
    /// The cycle-2 conclusion still records the fix as FixedUnreviewed — nobody has re-read it yet
    /// at that point — but this run's mandatory <see cref="ReviewMode.FinalFullPass"/> (cycle 3)
    /// goes on to read that exact fix fresh and finds nothing, which is the re-read the residual
    /// was waiting on: the settlement is Clean, not Settled (cycle-3 cap-park finding — a run whose
    /// final full pass comes back clean settles Clean instead of forever reporting the gate's own
    /// now-superseded residual).
    /// </summary>
    [Fact]
    public async Task Past_the_gate_a_medium_is_fixed_and_ships_without_another_review_pass()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "Criteria met.\n\nVERDICT: merge-ready",
            "FINDING: severity=medium; scope=in-scope; at=Auth.cs:42\nDefect: the limiter never resets.\n\n"
            + "VERDICT: needs-fixes",
            "Reset the limiter.\n\nRESOLUTION: fixed",
            // Cycle 2: only adversarial is active, so one Verify pass stands in for it. Past the
            // gate, these mediums are fixed without forcing another cycle of their own — the
            // track concludes at this same cycle, alongside its own terminal fix.
            "FINDING: severity=medium; scope=in-scope; at=Auth.cs:44\nDefect: the window is off by one.\n\n"
            + "FINDING: severity=low; scope=in-scope; at=Auth.cs:9\nDefect: the name reads badly.\n\n"
            + "VERDICT: needs-fixes",
            "Narrowed the window and renamed it.\n\nRESOLUTION: fixed",
            // Cycle 3: nothing is left to review, but this run's every cycle past the first has
            // been a narrow Verify pass, so the mandatory final full pass runs both lenses fresh
            // before the run may settle — reawakening the conformance track dormant since cycle 1.
            "Criteria still met.\n\nVERDICT: merge-ready",
            "Nothing else survives.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(
            store, executor, new DaemonOptions { AdversarialSeverityGateFromCycle = 2 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue("the gate ended the loop rather than parking a converging run");
        executor.Spawns.Should().HaveCount(
            7, "two passes, a fix, one verify pass, the terminal fix, and the mandatory final full pass");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.LastReviewVerdict.Should().Be(ReviewVerdict.MergeReady, "the terminal verdict is MergeReady either way");
        run.ReviewSettlement.Should().Be(
            ReviewSettlement.Clean,
            "the mandatory final full pass read the exact fix fresh and found nothing — the re-read the "
                + "cycle-2 residual was waiting on, so it is superseded rather than reported forever");
        run.ReviewResidualsFixed.Should().Be(
            0, "the medium and the low were fixed, and the final full pass went on to confirm the fix clean");
        run.ReviewResidualsRouted.Should().Be(0);

        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewSettled>().Should().ContainSingle().Which.Settlement
            .Should().Be(ReviewSettlement.Clean);
        events.FindLastIndex(recorded => recorded is VerificationPassed).Should().BeGreaterThan(
            events.FindIndex(recorded => recorded is ReviewFixCompleted fix && fix.Cycle == 2),
            "what a settled ending ships unreviewed is the reviewers' reading of the terminal fix, "
            + "never the build and the tests — the gates run over its commits before the pull request opens");
        events.FindIndex(recorded => recorded is ReviewSettled).Should().BeGreaterThan(
            events.FindLastIndex(recorded => recorded is VerificationPassed),
            "the loop settles only after those gates have passed");
        events.OfType<ReviewTrackConcluded>()
            .Single(track => track.Lens == ReviewLens.Adversarial && track.Cycle == 2)
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
        DocumentStore store = postgres.Store;
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
    /// The other direction of Decisions Log #87's reclassification: a lens that answers
    /// <c>VERDICT: merge-ready</c> but still attaches a Fix-dispositioned finding (an out-of-scope
    /// High, here — the shape both independent reviewers actually hit) is not taken at its word.
    /// Before this reclassification checked Disposition in both directions, a pass like this one
    /// slipped past untouched: the fix it owed was never dispatched, and the run settled clean
    /// over a defect nobody read again.
    /// </summary>
    [Fact]
    public async Task A_merge_ready_verdict_carrying_a_fix_disposed_finding_is_not_taken_at_its_word()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "Criteria met.\n\nVERDICT: merge-ready",
            "FINDING: severity=high; scope=out-of-scope; at=src/Legacy.cs:40\n"
            + "Defect: a pre-existing null dereference.\n\nVERDICT: merge-ready",
            "Guarded the null case.\n\nRESOLUTION: fixed",
            // Cycle 2: only the adversarial track is still active, so one Verify pass stands in for it.
            "Clean now.\n\nVERDICT: merge-ready",
            // Cycle 3: the mandatory final full pass, both lenses fresh.
            "Still clean.\n\nVERDICT: merge-ready",
            "Still clean too.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(
            6, "the lens's own merge-ready line does not excuse the fix its attached finding owes — "
                + "two passes, one fix, one verify pass, and the mandatory final full pass");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewFixDispatched>().Should().ContainSingle(
            "the out-of-scope high is Fix-dispositioned whatever the reviewer's own VERDICT line said");
        ReviewPassCompleted adversarialPass = events.OfType<ReviewPassCompleted>()
            .Single(pass => pass.Lens == ReviewLens.Adversarial && pass.Cycle == 1);
        adversarialPass.Verdict.Should().Be(
            ReviewVerdict.NeedsFixes, "the attached finding overrides the lens's literal merge-ready line");
        adversarialPass.Findings.Should().ContainSingle()
            .Which.Disposition.Should().Be(ReviewFindingDisposition.Fix);
        executor.Spawns[2].Prompt.Should().Contain(
            "Legacy.cs:40", "the fix session reads the very finding the merge-ready line tried to skip past");

        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.LastReviewVerdict.Should().Be(ReviewVerdict.MergeReady);
        run.ReviewSettlement.Should().Be(
            ReviewSettlement.Clean, "a fresh adversarial pass read the fix and found nothing left");
        run.ReviewResidualsFixed.Should().Be(0, "the fix was re-reviewed, not shipped unread");
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
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "Criteria met.\n\nVERDICT: merge-ready",
            "FINDING: severity=high; scope=in-scope; at=Spawner.cs:60\nDefect: the child is never reaped.\n\n"
            + "FINDING: severity=medium; scope=out-of-scope; at=Legacy.cs:12\n"
            + "Defect: the retry duplicates the effect. Scenario: a transient failure charges twice.\n\n"
            + "VERDICT: needs-fixes",
            "Reaped the child.\n\nRESOLUTION: fixed",
            // Cycle 2: only the adversarial track is still active, so one Verify pass stands in for it.
            "Clean now.\n\nVERDICT: merge-ready",
            // Cycle 3: the mandatory final full pass, both lenses fresh.
            "Still clean.\n\nVERDICT: merge-ready",
            "Still clean too.\n\nVERDICT: merge-ready");
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
        merged.Should().Contain("Do NOT fix here").And.Contain(routed.DraftTaskId!.Value.ToString());
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
    /// The severity gate the standing sweep adds (Decisions Log #117): a Medium out-of-scope
    /// finding still mints its own dedicated draft exactly as before, while a Low one folds into
    /// the project's one standing sweep draft instead of costing a build-gate-review pipeline of
    /// its own — so a serious pre-existing defect can never be buried in a polish pile, and the
    /// board shows one extra draft this cycle, not two.
    /// </summary>
    [Fact]
    public async Task An_out_of_scope_low_folds_into_the_projects_standing_sweep_while_a_medium_still_gets_its_own_draft()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "Criteria met.\n\nVERDICT: merge-ready",
            "FINDING: severity=medium; scope=out-of-scope; at=Legacy.cs:12\n"
            + "Defect: the retry duplicates the effect.\n\n"
            + "FINDING: severity=low; scope=out-of-scope; at=Cosmetic.cs:4\n"
            + "Defect: a stale comment misleads the next reader.\n\nVERDICT: needs-fixes");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue("nothing in either finding is this branch's own work");
        executor.Spawns.Should().HaveCount(2, "there was nothing in this branch to fix, so no fix session ran");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        List<ReviewFindingRouted> routedEvents = [.. events.OfType<ReviewFindingRouted>()];
        routedEvents.Should().HaveCount(2);

        ReviewFindingRouted mediumRouted =
            routedEvents.Should().ContainSingle(entry => entry.Severity == ReviewSeverity.Medium).Subject;
        ReviewFindingRouted lowRouted =
            routedEvents.Should().ContainSingle(entry => entry.Severity == ReviewSeverity.Low).Subject;
        mediumRouted.DraftTaskId.Should().NotBeNull();
        lowRouted.DraftTaskId.Should().NotBeNull();
        (mediumRouted.DraftTaskId == lowRouted.DraftTaskId).Should().BeFalse("the medium still mints its own draft");

        TaskDetails mediumDraft = (await query.LoadAsync<TaskDetails>(mediumRouted.DraftTaskId!.Value, cts.Token))!;
        mediumDraft.Type.Should().Be(TaskType.Bugfix, "a medium out-of-scope finding routes exactly as it did before");
        mediumDraft.Objective.Should().Contain("Legacy.cs:12");

        TaskDetails sweep = (await query.LoadAsync<TaskDetails>(lowRouted.DraftTaskId!.Value, cts.Token))!;
        sweep.State.Should().Be(TaskState.Draft, "the sweep stays a draft — the platform never publishes it");
        sweep.Type.Should().Be(TaskType.Chore);
        sweep.Objective.Should().Be(SweepDraftTask.Objective);
        sweep.AgentContext.Should().Contain("Cosmetic.cs:4")
            .And.Contain("Severity: Low")
            .And.Contain("a stale comment misleads")
            .And.Contain(RunPaths.ReviewFindingsFile(RunPaths.GlobalDirectory(runId), 1),
                "the evidence path points at the run's own findings file, so grooming needs no archaeology")
            .And.Contain("Assign it alone", "the wide-footprint, run-alone warning is in the generated body");

        List<TaskListItem> sweepRows = [.. await query.Query<TaskListItem>()
            .Where(item => item.ProjectId == sweep.ProjectId && item.Objective == SweepDraftTask.Objective)
            .ToListAsync(cts.Token)];
        sweepRows.Should().ContainSingle("h9k task list shows one sweep draft, not a row per auto-filed finding");

        string merged = File.ReadAllText(RunPaths.ReviewFindingsFile(RunPaths.GlobalDirectory(runId), 1));
        merged.Should().Contain($"folded into the standing sweep draft {lowRouted.DraftTaskId}");
    }

    /// <summary>
    /// Two lenses can disagree on the grade of the exact same pre-existing line in one cycle.
    /// Both_tracks_reporting_one_place_in_one_cycle_export_it_once_and_say_which_cycle already
    /// covers that one place is exported once; this covers which artifact it lands in when the
    /// two stated grades differ — the more severe one must decide, never whichever lens's finding
    /// the routing happened to process first. Before this fix, the lens iteration order
    /// (Conformance, then Adversarial) meant a Low reported by Conformance claimed the place and
    /// blocked Adversarial's Medium at the identical line from ever earning its own draft — it was
    /// silently folded into the sweep instead (adversarial review, cycle 4).
    /// </summary>
    [Fact]
    public async Task A_medium_and_a_low_disagreeing_on_the_same_place_in_one_cycle_still_mint_the_mediums_own_draft()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "FINDING: severity=low; scope=out-of-scope; at=Legacy.cs:40\n"
            + "Defect: the retry duplicates the effect.\n\nVERDICT: needs-fixes",
            "FINDING: severity=medium; scope=out-of-scope; at=Legacy.cs:40\n"
            + "Defect: the retry duplicates the effect.\n\nVERDICT: needs-fixes");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue("there was nothing in this branch's own work to fix");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        ReviewFindingRouted routedEvent = events.OfType<ReviewFindingRouted>().Should().ContainSingle(
            "one place named by both lenses in one cycle is one exported defect").Subject;
        routedEvent.Severity.Should().Be(
            ReviewSeverity.Medium, "the more severe stated grade decides the place's destination");

        TaskDetails draft = (await query.LoadAsync<TaskDetails>(routedEvent.DraftTaskId!.Value, cts.Token))!;
        draft.Type.Should().Be(
            TaskType.Bugfix, "a Medium at this place must mint its own draft, never fold into the sweep");
        draft.Objective.Should().Contain("Legacy.cs:40");

        List<TaskListItem> sweepRows = [.. await query.Query<TaskListItem>()
            .Where(item => item.ProjectId == draft.ProjectId && item.Objective == SweepDraftTask.Objective)
            .ToListAsync(cts.Token)];
        sweepRows.Should().BeEmpty(
            "the Medium must not be buried in the polish sweep just because a Low at the same place also reported it");
    }

    /// <summary>
    /// The already-routed guard has to pick the strongest prior routing at a place, not
    /// whichever happened to land first: a Low that folded into the sweep in an earlier cycle
    /// must not keep gating a Medium that later earns its own draft at the identical place, and
    /// once that Medium is routed, a second report of the same place — from the other track, in
    /// the same cycle — must recognize the Medium as already routed rather than minting a second
    /// draft for the one defect (adversarial review, cycle 5).
    /// </summary>
    [Fact]
    public async Task A_medium_that_outranks_an_earlier_swept_low_at_the_same_place_is_still_routed_only_once()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            // Cycle 1: both tracks find something in-scope, so both stay active into cycle 2;
            // adversarial also reports a genuinely out-of-scope Low, which folds into the sweep.
            "FINDING: severity=medium; scope=in-scope; at=A.cs:1\nDefect: the criterion is not met.\n\n"
            + "VERDICT: needs-fixes",
            "FINDING: severity=medium; scope=in-scope; at=B.cs:2\nDefect: still present.\n\n"
            + "FINDING: severity=low; scope=out-of-scope; at=Legacy.cs:40\n"
            + "Defect: the retry duplicates the effect.\n\nVERDICT: needs-fixes",
            "Fixed both.\n\nRESOLUTION: fixed",
            // Cycle 2: one Verify pass stands in for both still-active tracks, and both now grade
            // the identical pre-existing line a Medium — the exact shape that used to mint two
            // drafts, because the guard matched the earlier swept Low instead of the stronger
            // routing this same cycle had already minted for the first of the two.
            "FINDING: severity=medium; scope=out-of-scope; track=conformance; at=Legacy.cs:40\n"
            + "Defect: the retry duplicates the effect.\n\n"
            + "FINDING: severity=medium; scope=out-of-scope; track=adversarial; at=Legacy.cs:40\n"
            + "Defect: the retry duplicates the effect.\n\nVERDICT: needs-fixes",
            // Nothing was left to fix, so both tracks force-conclude right there (Decisions Log
            // #63's empty terminal case) — but the mandatory final full pass still runs both
            // lenses fresh before the run may settle.
            "The routed line is still someone else's; nothing else stands.\n\nVERDICT: merge-ready",
            "The routed line is still someone else's; nothing else stands.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue("nothing out-of-scope is this branch's own work to fix");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        List<ReviewFindingRouted> mediumRoutings = [.. events.OfType<ReviewFindingRouted>()
            .Where(e => e.Location == "Legacy.cs:40" && e.Severity == ReviewSeverity.Medium)];

        mediumRoutings.Should().ContainSingle(
            "the same defect graded Medium by both tracks in cycle 2 is one routed defect, not two, even " +
            "though an earlier cycle already swept it as a Low");
    }

    /// <summary>
    /// The idempotency the sweep exists to provide: a second run's review, on a different task
    /// against the same project, reports the exact same pre-existing Low defect a first run's
    /// review already folded into the sweep. It updates that item's evidence list rather than
    /// minting a second sweep or a second item — "eight one-line fixes cost one pipeline" only
    /// holds if a defect two different branches both notice does not itself get double-booked.
    /// </summary>
    [Fact]
    public async Task A_low_finding_reported_by_a_second_run_updates_the_sweep_items_evidence_instead_of_duplicating_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid firstTaskId, Guid firstRunId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        await using IQuerySession firstQuery = store.QuerySession();
        TaskDetails firstTask = (await firstQuery.LoadAsync<TaskDetails>(firstTaskId, cts.Token))!;

        const string sameFinding = "FINDING: severity=low; scope=out-of-scope; at=Cosmetic.cs:4\n"
            + "Defect: a stale comment misleads the next reader.\n\nVERDICT: needs-fixes";
        ScriptedExecutor firstExecutor = new("Criteria met.\n\nVERDICT: merge-ready", sameFinding);
        (await NewEngine(store, firstExecutor).ReviewAsync(firstRunId, firstTaskId, cts.Token))
            .Should().BeTrue();

        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        string secondWorktree = Path.Combine(_home, $"wt-{DomainId.New():N}");
        Directory.CreateDirectory(secondWorktree);
        (Guid secondTaskId, Guid secondRunId, _) = await SeedVerifiedRunInProjectAsync(
            store, firstTask.ProjectId, node, secondWorktree, cts.Token);

        ScriptedExecutor secondExecutor = new("Criteria met.\n\nVERDICT: merge-ready", sameFinding);
        (await NewEngine(store, secondExecutor).ReviewAsync(secondRunId, secondTaskId, cts.Token))
            .Should().BeTrue();

        await using IQuerySession query = store.QuerySession();
        List<TaskListItem> sweepRows = [.. await query.Query<TaskListItem>()
            .Where(item => item.ProjectId == firstTask.ProjectId && item.Objective == SweepDraftTask.Objective)
            .ToListAsync(cts.Token)];
        sweepRows.Should().ContainSingle("the second run's re-raise updates the open sweep rather than starting a second one");

        TaskDetails sweep = (await query.LoadAsync<TaskDetails>(sweepRows[0].Id, cts.Token))!;
        Regex.Matches(sweep.AgentContext!, "### Cosmetic.cs:4").Count.Should().Be(
            1, "the same file and defect updates one item rather than adding a second");
        sweep.AgentContext.Should().Contain(firstRunId.ToString())
            .And.Contain(secondRunId.ToString(), "both runs' evidence lands on the one item");
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
        DocumentStore store = postgres.Store;
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
    /// Independent pre-PR review, cycle 2, conformance finding #4: a single <see cref="ReviewMode.Verify"/>
    /// pass stands in for both still-active tracks, so <c>SplitForTrack</c> hands the identical
    /// out-of-scope finding to both tracks' plans when it names no <c>track=</c> tag — exactly the
    /// same conservative reading that already applies to a Fix finding. Unlike a Fix finding,
    /// nothing downstream is meant to route the same statement twice: one reviewer naming one
    /// pre-existing defect, with no line to place it on, must still become one draft bug task.
    /// </summary>
    [Fact]
    public async Task A_verify_pass_shared_unplaced_out_of_scope_finding_routes_once_not_twice()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            // Cycle 1: both tracks find something in-scope, so both stay active into cycle 2.
            "FINDING: severity=medium; scope=in-scope; at=A.cs:1\nDefect: the criterion is not met.\n\n"
            + "VERDICT: needs-fixes",
            "FINDING: severity=medium; scope=in-scope; at=B.cs:2\nDefect: still present.\n\n"
            + "VERDICT: needs-fixes",
            "Fixed both.\n\nRESOLUTION: fixed",
            // Cycle 2: one Verify pass stands in for both tracks, and reports one pre-existing,
            // out-of-scope defect it names by file but could not pin to a line — no `at=` tag, so
            // the parsed Location stays blank, and no `track=` tag either.
            "FINDING: severity=medium; scope=out-of-scope\n"
            + "Defect: Legacy.cs carries a pre-existing issue, but no single line accounts for it.\n\n"
            + "VERDICT: needs-fixes",
            // Cycle 3: the routing-only cycle needed no fix session, but a Verify cycle still
            // never paid the mandatory final full pass, so it runs before the run may settle —
            // both lenses fresh, both clean.
            "Criteria still met.\n\nVERDICT: merge-ready",
            "Still clean.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue("routing away the one out-of-scope defect leaves nothing left to fix");
        executor.Spawns.Should().HaveCount(6, "the routing-only cycle needed no fix session, but still paid the mandatory final pass");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewFindingRouted>().Should().ContainSingle(
            "one reviewer statement, reached once per track it stands in for, is still one defect");

        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewResidualsRouted.Should().Be(1);
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
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "`Program.cs` — the third acceptance criterion is not met.\n\nVERDICT: needs-fixes",
            "FINDING: severity=medium; scope=out-of-scope; at=Legacy.cs:12\nDefect: pre-existing, and real.\n\n"
            + "VERDICT: needs-fixes",
            "Met the criterion; left the pre-existing one alone.\n\nRESOLUTION: fixed",
            // Cycle 2: both tracks are still active, so one Verify pass stands in for both.
            "Criteria met now, and the fix commits carry nothing new.\n\nVERDICT: merge-ready",
            // Cycle 3: the mandatory final full pass, both lenses fresh.
            "Criteria still met.\n\nVERDICT: merge-ready",
            "Read the fix commits too; nothing survived verification.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewDispatched>().Select(dispatched => (dispatched.Cycle, dispatched.Lens)).Should().Equal(
            [
                (1, ReviewLens.Conformance),
                (1, ReviewLens.Adversarial),
                (2, ReviewLens.Verify),
                (3, ReviewLens.Conformance),
                (3, ReviewLens.Adversarial),
            ], "the adversarial track had routed, not finished — cycle 2 merges into one Verify pass, "
                + "and the mandatory final full pass reads the rewritten tip fresh");
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
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        const string preExisting = "FINDING: severity=medium; scope=out-of-scope; at=Legacy.cs:12\n"
            + "Defect: the retry duplicates the effect. Scenario: a transient failure charges twice.";
        ScriptedExecutor executor = new(
            "Criteria met.\n\nVERDICT: merge-ready",
            "FINDING: severity=high; scope=in-scope; at=Spawner.cs:60\nDefect: the child is never reaped.\n\n"
            + $"{preExisting}\n\nVERDICT: needs-fixes",
            "Reaped the child; left the pre-existing one alone.\n\nRESOLUTION: fixed",
            // Cycle 2: the high is gone, only the adversarial track is still active, and the one
            // Verify pass standing in for it reports the untouched legacy line again.
            $"{preExisting}\n\nVERDICT: needs-fixes",
            // Nothing was left to fix, so the run would otherwise settle straight from this
            // Verify cycle — but the mandatory final full pass runs first.
            "Nothing new to report.\n\nVERDICT: merge-ready",
            "The routed line is still someone else's; nothing else stands.\n\nVERDICT: merge-ready");
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
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "Criteria met.\n\nVERDICT: merge-ready",
            "FINDING: severity=high; scope=in-scope; at=Spawner.cs:60\nDefect: the child is never reaped.\n\n"
            + "FINDING: severity=medium; scope=out-of-scope; at=src/Legacy.cs:12\nDefect: the retry duplicates.\n\n"
            + "VERDICT: needs-fixes",
            "Reaped the child; left the pre-existing one alone.\n\nRESOLUTION: fixed",
            // Cycle 2: only the adversarial track is still active, so one Verify pass stands in
            // for it — the same untouched line, written the way this reviewer writes paths.
            "FINDING: severity=medium; scope=out-of-scope; at=./Legacy.cs:12\nDefect: the retry duplicates.\n\n"
            + "VERDICT: needs-fixes",
            // Nothing was left to fix (routing only), so the run would otherwise settle straight
            // from this cycle — but it was a Verify cycle, so the mandatory final full pass runs
            // first, reawakening the dormant conformance track.
            "Nothing new to report.\n\nVERDICT: merge-ready",
            "The routed line is still someone else's; nothing else stands.\n\nVERDICT: merge-ready");
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
    /// Independent pre-PR review, cycle 2, adversarial finding #1: a Verify pass's single reviewer
    /// is recorded under the pseudo-lens <see cref="ReviewLens.Verify"/>, which covers both real
    /// lenses, so the adversarial cap-park reason must attribute each of that pass's findings to
    /// the track its own `track=` tag names rather than crediting all of them to adversarial just
    /// because the pass covers it. A conformance-tagged High must not be read as an adversarial
    /// High when adversarial's own cap parks the run.
    /// </summary>
    [Fact]
    public async Task An_adversarial_cap_park_attributes_a_verify_pass_findings_by_their_own_track_tag()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            // Cycle 1: both tracks find something medium, so both stay active into cycle 2.
            "FINDING: severity=medium; scope=in-scope; at=A.cs:1\nDefect: the criterion is not met.\n\n"
            + "VERDICT: needs-fixes",
            "FINDING: severity=medium; scope=in-scope; at=B.cs:2\nDefect: still present.\n\n"
            + "VERDICT: needs-fixes",
            "Tried.\n\nRESOLUTION: fixed",
            // Cycle 2: one Verify pass stands in for both tracks. Conformance's own finding is
            // now graded high; adversarial's own finding stays medium. Only adversarial is
            // capped this cycle, so its park reason must not borrow conformance's high.
            "FINDING: severity=high; scope=in-scope; track=conformance; at=A.cs:1\nDefect: still not met, worse than thought.\n\n"
            + "FINDING: severity=medium; scope=in-scope; track=adversarial; at=B.cs:2\nDefect: still present.\n\n"
            + "VERDICT: needs-fixes");
        bool mergeReady = await NewEngine(
            store, executor,
            new DaemonOptions { MaxComplianceReviewCycles = 3, MaxAdversarialReviewCycles = 2 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("adversarial is at its two-cycle cap while conformance is not");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked);
        run.ParkedReason.Should()
            .NotContain("still returning high-severity findings",
                "the high finding belongs to conformance, not the adversarial track that capped")
            .And.Contain("none of cycle 2's findings is graded high",
                "adversarial's own finding this cycle was a medium");
    }

    /// <summary>
    /// This track's findings are plain prose with no structured `FINDING:` header, so nothing
    /// survives to grade: the needs-fixes placeholder that stands in for an unstructured pass
    /// (Decisions Log #86) is always Fix, never a ride-along, regardless of grade (Decisions Log
    /// #87). Still returning findings at its cap parks the run, and the reason says why: nothing
    /// automated is left to try.
    /// </summary>
    [Fact]
    public async Task A_conformance_track_still_finding_things_at_its_cap_parks_the_run()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
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

    /// <summary>
    /// The takeover lever (task: the review cycle caps become settable at three levels, Brian's
    /// ruling 2026-08-29): a task-level cap set at or below a track's current cycle count parks
    /// the run at the very next cap check, with no new state or command beyond the setting
    /// itself. The node's own MaxComplianceReviewCycles stays at its generous default (3) —
    /// without the task override this run would still have two more cycles of room.
    /// </summary>
    [Fact]
    public async Task A_task_level_cap_at_or_below_the_current_cycle_count_parks_the_run_as_a_takeover()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            TaskReviewCapsOverridden overridden = TaskDecider.OverrideReviewCaps(
                task, Optional<int?>.Of(1), Optional<int?>.None, Optional<int?>.None, Optional<int?>.None,
                Now, DomainId.New());
            session.Events.Append(taskId, overridden);
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor executor = new(
            "FINDING: severity=medium; scope=in-scope; at=A.cs:1\nDefect: the criterion is not met.\n\n"
            + "VERDICT: needs-fixes",
            "Nothing of my own.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor, new DaemonOptions { MaxComplianceReviewCycles = 3 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse(
            "the task override capped conformance at 1, even though the node's own cap of 3 has two cycles left");
        executor.Spawns.Should().HaveCount(2, "the run parks before ever dispatching a fix session");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked);
        run.ReviewCycle.Should().Be(1);
        run.ParkedReason.Should().Contain("its cap of 1").And.Contain("a task override");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 2, adversarial finding: a task-level takeover cap can
    /// legally floor at 0 (only the task level does), and 0 parks every single cycle immediately —
    /// before a granted round's fix session ever gets to dispatch. Unlike a real cap, a human's
    /// <c>--needs-fixes</c> grant here buys no progress at all: <c>TrackBudgetBaseCycle</c> resets
    /// to the current cycle, the very next check reads 0 &gt;= 0, and the run re-parks with the
    /// identical reason before a fix session runs — the same defect commit 53bd0998 already fixed
    /// for the lifetime-budget park. The park text must not offer that lever here, and must point
    /// at raising or clearing the override instead.
    /// </summary>
    [Fact]
    public async Task A_task_level_cap_of_zero_parks_immediately_without_offering_needs_fixes_as_a_lever()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            TaskReviewCapsOverridden overridden = TaskDecider.OverrideReviewCaps(
                task, Optional<int?>.Of(0), Optional<int?>.None, Optional<int?>.None, Optional<int?>.None,
                Now, DomainId.New());
            session.Events.Append(taskId, overridden);
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor executor = new(
            "FINDING: severity=medium; scope=in-scope; at=A.cs:1\nDefect: the criterion is not met.\n\n"
            + "VERDICT: needs-fixes",
            "Nothing of my own.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor, new DaemonOptions { MaxComplianceReviewCycles = 3 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("a task-level cap of 0 parks the very first cycle");
        executor.Spawns.Should().HaveCount(2, "the run parks before ever dispatching a fix session");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked);
        run.ReviewCycle.Should().Be(1);
        run.ParkedReason.Should()
            .Contain("cap is 0")
            .And.Contain("a task override")
            .And.Contain("h9k task set-review-caps")
            .And.NotContain("grant a fresh round with --needs-fixes",
                "a cap this low parks every cycle before a granted round's fix session could ever run");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 3, adversarial finding: the cap-0 takeover park is not a
    /// task-only case. <c>h9k config set</c> refuses a value below 1, but nothing stops a
    /// hand-edited config file or an environment variable from binding <see
    /// cref="DaemonOptions.MaxComplianceReviewCycles"/> straight to 0, and <see
    /// cref="ReviewCapResolver.Resolve"/> then resolves it as a Node-level cap exactly like any
    /// other node value. The park text must say the level it actually resolved and offer a lever
    /// that actually clears it.
    /// <para>
    /// Cycle 4, adversarial finding: the node lever originally asserted the low value reached "the
    /// config file" specifically and that <c>h9k config set</c> always overwrites it — an unobserved
    /// provenance claim that is also provably wrong when an environment variable supplied the value,
    /// since the env source outranks the config file (<see cref="PlatformConfigFileSource"/>) and
    /// the identical park would recur after the operator followed that exact advice. The fixed text
    /// no longer guesses the source; it points at <c>h9k config show</c> (which does know it) and
    /// offers the task-level override as the lever that clears the park either way.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_node_level_cap_of_zero_names_the_node_level_and_a_lever_that_always_works()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "FINDING: severity=medium; scope=in-scope; at=A.cs:1\nDefect: the criterion is not met.\n\n"
            + "VERDICT: needs-fixes",
            "Nothing of my own.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor, new DaemonOptions { MaxComplianceReviewCycles = 0 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("a node-level cap of 0 parks the very first cycle exactly like a task override does");
        executor.Spawns.Should().HaveCount(2, "the run parks before ever dispatching a fix session");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked);
        run.ParkedReason.Should()
            .Contain("cap is 0")
            .And.Contain("this node's configured value")
            .And.Contain("h9k config show")
            .And.Contain("h9k task set-review-caps",
                "the task override always clears a node-level park, whether an environment variable or a hand-edited config file supplied the low value")
            .And.NotContain("hand edit", "the message no longer guesses which ungated source supplied the value")
            .And.NotContain("grant a fresh round with --needs-fixes",
                "a cap this low parks every cycle before a granted round's fix session could ever run");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 3, adversarial finding: <c>MaxFinalFullPassRounds</c> is
    /// the third task-level per-run cap <c>RefuseNegativeCap</c> lets floor at 0, exactly like the
    /// two per-track caps above, but <c>FinalFullPassCapParkReason</c>'s ordinary wording asserts
    /// <c>run.FinalFullPassRounds</c> repetitions of a pass that never actually ran when the cap
    /// itself is what stopped it from ever dispatching. This reaches the FinalFullPassCapReached
    /// check with the mandatory pass never having run — a Verify-mode cycle 2 (which forces
    /// <c>NeedsFullGateBeforeSettling</c> without ever entering <c>ReviewMode.FinalFullPass</c>) —
    /// and asserts the park text does not claim the mandatory pass ran zero times "without ever
    /// reaching a clean settle", and instead gets the same takeover wording and lever the two
    /// per-track caps already got.
    /// </summary>
    [Fact]
    public async Task A_task_level_final_full_pass_cap_of_zero_parks_before_the_mandatory_pass_ever_runs()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            TaskReviewCapsOverridden overridden = TaskDecider.OverrideReviewCaps(
                task, Optional<int?>.None, Optional<int?>.None, Optional<int?>.Of(0), Optional<int?>.None,
                Now, DomainId.New());
            session.Events.Append(taskId, overridden);
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor executor = new(
            // Cycle 1: conformance clean and dormant; adversarial needs a fix.
            "Criteria met.\n\nVERDICT: merge-ready",
            "FINDING: severity=medium; scope=in-scope; at=Retry.cs:12\nDefect: the retry loop never backs off.\n\n"
            + "VERDICT: needs-fixes",
            "Added the backoff.\n\nRESOLUTION: fixed",
            // Cycle 2: only adversarial is active, so a Verify pass stands in — Verify mode alone
            // forces NeedsFullGateBeforeSettling, reaching FinalFullPassCapReached without this
            // run ever having dispatched a ReviewMode.FinalFullPass cycle.
            "Nothing else survives.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("a task-level final-full-pass cap of 0 parks before the mandatory pass ever runs");
        executor.Spawns.Should().HaveCount(4, "the run parks right after the Verify cycle, before a mandatory final full pass ever dispatches");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked);
        run.ParkedReason.Should()
            .Contain("cap is 0")
            .And.Contain("a task override")
            .And.Contain("h9k task set-review-caps")
            .And.Contain("mandatory pass ever ran")
            .And.NotContain("consecutive time(s) without ever reaching a clean settle",
                "the mandatory pass never ran even once, so the ordinary repetition-count wording would assert something that never happened")
            .And.NotContain("grant a fresh round with --needs-fixes",
                "a cap this low parks every cycle before the mandatory pass could ever run");

        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewDispatched>().Should().NotContain(
            e => e.Mode == ReviewMode.FinalFullPass,
            "the cap must stop the loop before the mandatory pass ever dispatches, not after");
    }

    /// <summary>
    /// The fourth setting, the task-lifetime review-cycle budget (task: the review cycle caps
    /// become settable at three levels): it parks the run at a settle point even when this
    /// particular run would otherwise settle cleanly — the pathology it exists to catch already
    /// happened by the time the count is this high, so it is worth a human's look regardless of
    /// how cleanly this cycle converged. The per-run caps (MaxComplianceReviewCycles,
    /// MaxAdversarialReviewCycles) are left at their generous defaults and never trip on their
    /// own; only the lifetime budget, set low here, ends the loop.
    /// </summary>
    [Fact]
    public async Task A_lifetime_review_cycle_budget_parks_a_run_that_would_otherwise_settle_cleanly()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            TaskReviewCapsOverridden overridden = TaskDecider.OverrideReviewCaps(
                task, Optional<int?>.None, Optional<int?>.None, Optional<int?>.None, Optional<int?>.Of(2),
                Now, DomainId.New());
            session.Events.Append(taskId, overridden);
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor executor = new(
            // Cycle 1: conformance needs a fix; adversarial is clean and goes dormant.
            "FINDING: severity=medium; scope=in-scope; at=A.cs:1\nDefect: the criterion is not met.\n\n"
            + "VERDICT: needs-fixes",
            "Nothing of my own.\n\nVERDICT: merge-ready",
            "Tried.\n\nRESOLUTION: fixed",
            // Cycle 2: one Verify pass over conformance alone — it confirms clean, so both tracks
            // have now concluded, but a Verify cycle is never itself a settle-worthy fresh read.
            "Confirmed fixed.\n\nVERDICT: merge-ready",
            // Cycle 3: the mandatory final full pass, both lenses fresh and clean — the run would
            // ordinarily settle right here (three cycles total) were it not for the budget of 2.
            "Still clean.\n\nVERDICT: merge-ready",
            "Still clean too.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor, new DaemonOptions())
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("this task has spent 3 review cycles, past its lifetime budget of 2");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked);
        run.ReviewCycle.Should().Be(3, "the run had genuinely converged clean at the mandatory final pass");
        run.ParkedReason.Should()
            .Contain("3 review cycle(s)")
            .And.Contain("lifetime review-cycle budget of 2")
            .And.Contain("a task override");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 3, adversarial finding #2: the lifetime-budget park text
    /// must not claim a clean convergence when the settling cycle actually recorded findings —
    /// only demoted to ride-alongs by the severity bar, not absent. Before this, the "converged
    /// cleanly" wording keyed off <c>SettleReason.Bar</c> alone, which only ever fires for a
    /// <c>FinalFullPass</c> cycle; a plain <c>Discovery</c> cycle settling <c>NothingOwed</c> with
    /// a low-only ride-along on the books (the same shape as
    /// <see cref="A_pass_whose_only_finding_is_graded_low_is_demoted_to_merge_ready_with_a_ride_along_and_no_fix_session"/>)
    /// hit that same "converged cleanly" wording even though a real finding sits right there in
    /// the findings file the very next sentence points to.
    /// </summary>
    [Fact]
    public async Task A_lifetime_budget_park_after_a_discovery_cycle_with_a_ride_along_names_the_finding_not_a_clean_convergence()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            TaskReviewCapsOverridden overridden = TaskDecider.OverrideReviewCaps(
                task, Optional<int?>.None, Optional<int?>.None, Optional<int?>.None, Optional<int?>.Of(1),
                Now, DomainId.New());
            session.Events.Append(taskId, overridden);
            // An earlier generation of this same task (a stranding salvage, a retry — the exact
            // history the lifetime budget exists to remember) already spent one review cycle of
            // its own. Seeded as a plain document, the same way TaskLease is seeded above, since
            // LifetimeReviewCycleCountAsync reads RunDetails.ReviewCycle straight from the store
            // rather than replaying that earlier run's events.
            session.Store(new RunDetails { Id = Guid.NewGuid(), TaskId = taskId, ReviewCycle = 1 });
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor executor = new(
            "FINDING: severity=low; scope=in-scope; at=Docs.md:3\nDefect: the comment is stale.\n\n"
            + "VERDICT: needs-fixes",
            "Criteria met.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse(
            "this run's own cycle 1 plus the earlier generation's already puts the task at 2, past a budget of 1");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked);
        run.ParkedReason.Should()
            .Contain("recorded below the fix bar")
            .And.NotContain("converged cleanly");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, adversarial finding: the lifetime-budget park text must
    /// not describe a settling cycle's own Routed finding as merely "recorded below the fix bar"
    /// when that finding was in fact a real defect graded medium — routed to a draft bug task
    /// because it is out of this pull request's scope, not because it failed to clear the bar. A
    /// Medium is the only severity <see cref="ReviewFindingDisposition.Route"/> can ever carry
    /// (<see cref="ReviewFinding.Disposition"/> requires a stated grade below High to route at
    /// all, and a routed Low never sets <see cref="ReviewSeverity.MeetsFixBar"/>), so this is the
    /// one reachable shape of the "routed away, not below the bar" branch.
    /// </summary>
    [Fact]
    public async Task A_lifetime_budget_park_after_a_routed_medium_finding_names_it_rather_than_below_the_fix_bar()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            TaskReviewCapsOverridden overridden = TaskDecider.OverrideReviewCaps(
                task, Optional<int?>.None, Optional<int?>.None, Optional<int?>.None, Optional<int?>.Of(1),
                Now, DomainId.New());
            session.Events.Append(taskId, overridden);
            session.Store(new RunDetails { Id = Guid.NewGuid(), TaskId = taskId, ReviewCycle = 1 });
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor executor = new(
            "FINDING: severity=medium; scope=out-of-scope; at=Legacy.cs:12\nDefect: pre-existing, and real.\n\n"
            + "VERDICT: needs-fixes",
            "Criteria met.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse(
            "this run's own cycle 1 plus the earlier generation's already puts the task at 2, past a budget of 1");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked);
        run.ParkedReason.Should()
            .Contain("routed to a draft bug task")
            .And.NotContain("recorded below the fix bar")
            .And.NotContain("converged cleanly");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 2, conformance finding #1: the mandatory FinalFullPass
    /// can reawaken a track that went dormant cycles ago, and the earliest that pass can possibly
    /// land is cycle 3 — already <c>MaxComplianceReviewCycles</c>' own absolute count measured
    /// from cycle 0. Without a per-track budget base, the reactivated track would be capped and
    /// parked on the very cycle that reawakened it, before ever earning a fix session for the
    /// defect that mandatory pass exists to catch.
    /// </summary>
    [Fact]
    public async Task A_track_the_mandatory_final_pass_reawakens_gets_a_genuine_cycle_to_fix_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        const string reawakenedFinding =
            "FINDING: severity=high; scope=in-scope; at=Auth.cs:9\n"
            + "Defect: the mandatory final pass found a real regression the earlier cycles missed.\n\n";
        ScriptedExecutor executor = new(
            // Cycle 1: conformance clean and goes dormant; adversarial finds something.
            "Criteria met at cycle 1.\n\nVERDICT: merge-ready",
            "FINDING: severity=high; scope=in-scope; at=Spawner.cs:60\nDefect: the child process is never reaped.\n\n"
            + "VERDICT: needs-fixes",
            "Reaped the child.\n\nRESOLUTION: fixed",
            // Cycle 2: only adversarial is still active — one Verify pass, and it concludes.
            "The lifetime holds now.\n\nVERDICT: merge-ready",
            // Cycle 3: the mandatory final full pass, both lenses fresh. Conformance — dormant
            // since cycle 1 — finds a genuine new defect this time, reawakening it at cycle 3,
            // which already equals MaxComplianceReviewCycles measured from cycle 0.
            reawakenedFinding + "VERDICT: needs-fixes",
            "The lifetime still holds.\n\nVERDICT: merge-ready",
            "Fixed the regression the final pass caught.\n\nRESOLUTION: fixed",
            // Cycle 4: one Verify pass over the reawakened conformance track alone.
            "Confirmed fixed.\n\nVERDICT: merge-ready",
            // Cycle 5: a fix landed since the last full pass, so one more mandatory final pass
            // runs before the run may settle (finding #2) — both lenses fresh, both clean.
            "Still clean.\n\nVERDICT: merge-ready",
            "Still clean too.\n\nVERDICT: merge-ready");

        bool mergeReady = await NewEngine(
            store, executor, new DaemonOptions { MaxComplianceReviewCycles = 3 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue(
            "the reawakened conformance track earned its own fix cycle instead of parking on a budget it never spent");
        executor.Spawns.Should().HaveCount(10);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.UnderReview);
        run.ReviewCycle.Should().Be(5);

        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewTrackReactivated>().Should().ContainSingle(
            reactivated => reactivated.Lens == ReviewLens.Conformance && reactivated.Cycle == 3);
    }

    /// <summary>
    /// Independent pre-PR review, cycle 2, conformance finding #2: a fix session can still
    /// dispatch on the very cycle the mandatory FinalFullPass ran (a fresh High that reactivates
    /// its dormant track) — and the fix it produces must itself get a fresh-context read before
    /// the run may settle, or the pull request ships commits the mandatory final pass never
    /// actually saw. A Medium here would ride along instead of forcing a fix session at all
    /// (task: a mandatory FinalFullPass records merge-ready when every finding it attaches is
    /// below High), so this scenario now needs a High to reach the fix-and-reverify path at all.
    /// </summary>
    [Fact]
    public async Task A_fix_dispatched_from_the_mandatory_final_pass_gets_one_more_pass_before_settling()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            // Cycle 1: conformance clean and dormant; adversarial needs a fix (pre-gate, any
            // grade forces the next cycle) — a Medium here is unaffected by this task, since the
            // narrower bar is FinalFullPass-only.
            "Criteria met.\n\nVERDICT: merge-ready",
            "FINDING: severity=medium; scope=in-scope; at=Retry.cs:12\nDefect: the retry duplicates the effect.\n\n"
            + "VERDICT: needs-fixes",
            "Tightened the retry guard.\n\nRESOLUTION: fixed",
            // Cycle 2: still pre-gate (< AdversarialSeverityGateFromCycle, default 4) — a second
            // minor issue still forces another cycle regardless of its grade.
            "FINDING: severity=medium; scope=in-scope; at=Retry.cs:20\nDefect: a related edge is still off.\n\n"
            + "VERDICT: needs-fixes",
            "Closed the edge case too.\n\nRESOLUTION: fixed",
            // Cycle 3: clean — adversarial concludes, both tracks now dormant.
            "Clean now.\n\nVERDICT: merge-ready",
            // Cycle 4: the mandatory final full pass. Both tracks already concluded, so it is
            // dispatched at cycle 4. Adversarial reports a fresh High, reactivating the dormant
            // track and forcing a fix session regardless of the gate.
            "Criteria still met.\n\nVERDICT: merge-ready",
            "FINDING: severity=high; scope=in-scope; at=Retry.cs:31\n"
            + "Defect: the final pass caught a fresh regression.\n\nVERDICT: needs-fixes",
            "Fixed the regression the final pass found.\n\nRESOLUTION: fixed",
            // Cycle 5: one Verify pass over the reactivated track, confirming the fix — clean, so
            // ActiveReviewLenses empties out and a Verify-mode cycle can never settle on its own
            // (MaySettleReason's own doc), forcing the loop back to the mandatory final pass.
            "Confirmed fixed.\n\nVERDICT: merge-ready",
            // Cycle 6: nothing may settle over that fix unread, so one more mandatory final pass
            // runs — both lenses fresh, both clean this time.
            "Still clean.\n\nVERDICT: merge-ready",
            "Still clean too.\n\nVERDICT: merge-ready");

        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(
            12, "the reactivated track's fix earns its own extra final pass before the run may " +
                "settle, rather than shipping unreviewed");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewCycle.Should().Be(6, "a fix landed on top of the mandatory final pass, so a confirming Verify pass and one more mandatory pass ran first");

        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewDispatched>().Count(e => e.Mode == ReviewMode.FinalFullPass).Should().Be(
            4, "the mandatory final pass ran twice — once before the last fix, once to read it");
    }

    /// <summary>
    /// Cycle-3 finding: a track the mandatory final pass keeps reawakening never trips its own
    /// per-track cap, because <c>RunAggregate.TrackBudgetBaseCycle</c> deliberately measures that
    /// cap from the cycle it was last reactivated at (the prior test's own scenario). Left alone,
    /// a fix session that keeps introducing one fresh regression the mandatory pass catches would
    /// let FinalFullPass → reactivate → fix → verify recur without end. This scripts exactly that
    /// — the same fresh HIGH finding on every mandatory pass (task: a mandatory FinalFullPass
    /// records merge-ready when every finding it attaches is below High — a Medium here would
    /// only ride along and settle on the spot, never reaching this loop at all) — with
    /// <c>MaxFinalFullPassRounds</c> set low, and asserts the run parks instead of looping a
    /// third time.
    /// </summary>
    [Fact]
    public async Task A_track_the_final_pass_keeps_reawakening_parks_once_the_final_pass_round_cap_is_hit()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        string freshHigh(string at) =>
            $"FINDING: severity=high; scope=in-scope; at={at}\nDefect: the final pass caught a fresh regression.\n\n"
            + "VERDICT: needs-fixes";

        ScriptedExecutor executor = new(
            // Cycle 1: conformance clean and dormant; adversarial needs a fix (pre-gate, any
            // grade forces the next cycle) — a Medium here is unaffected by this task, since the
            // narrower FinalFullPass-only bar does not apply to a Discovery cycle.
            "Criteria met.\n\nVERDICT: merge-ready",
            "FINDING: severity=medium; scope=in-scope; at=Retry.cs:12\nDefect: the retry duplicates the effect.\n\n"
            + "VERDICT: needs-fixes",
            "Tightened the retry guard.\n\nRESOLUTION: fixed",
            // Cycle 2: still pre-gate — a second minor issue still forces another cycle.
            "FINDING: severity=medium; scope=in-scope; at=Retry.cs:20\nDefect: a related edge is still off.\n\n"
            + "VERDICT: needs-fixes",
            "Closed the edge case too.\n\nRESOLUTION: fixed",
            // Cycle 3: clean — adversarial concludes, both tracks now dormant.
            "Clean now.\n\nVERDICT: merge-ready",
            // Cycle 4: mandatory final pass, round 1. A fresh High reactivates the dormant track
            // and forces a fix session, whatever the gate says.
            "Criteria still met.\n\nVERDICT: merge-ready",
            freshHigh("Retry.cs:31"),
            "Fixed the regression the final pass found.\n\nRESOLUTION: fixed",
            // Cycle 5: one Verify pass over the reactivated track alone, confirming the fix —
            // ActiveReviewLenses empties out again once it reports clean, and a Verify-mode cycle
            // can never settle on its own (MaySettleReason's own doc), so the loop is forced back
            // to the mandatory final pass rather than concluding here.
            "Confirmed fixed.\n\nVERDICT: merge-ready",
            // Cycle 6: mandatory final pass, round 2 — the same shape as round 1, a fresh High.
            "Criteria still met.\n\nVERDICT: merge-ready",
            freshHigh("Retry.cs:40"),
            "Fixed that regression too.\n\nRESOLUTION: fixed",
            // Cycle 7: the confirming Verify pass for round 2's fix.
            "Confirmed fixed again.\n\nVERDICT: merge-ready");
        // A third round would be owed next — reactivation keeps resetting the track's own cap
        // (RunAggregate.TrackBudgetBaseCycle), so nothing about ITS cap would ever stop this.
        // MaxFinalFullPassRounds is the independent bound that does, set to 2 so this test hits
        // it on the very next round rather than scripting a long, unbounded-looking sequence.

        bool mergeReady = await NewEngine(
            store, executor, new DaemonOptions { MaxFinalFullPassRounds = 2 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse(
            "the mandatory final pass round cap must stop the loop rather than let it recur forever");
        executor.Spawns.Should().HaveCount(14, "the run parks before a third final-pass round ever dispatches");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked);
        run.ReviewCycle.Should().Be(7, "the park happens deciding cycle 8, before it ever dispatches");
        run.ParkedReason.Should().Contain("dispatched the mandatory final full review pass")
            .And.Contain("2 consecutive time(s)")
            .And.Contain("h9k review resolve --merge-ready")
            .And.Contain(
                "reawakened",
                "a fresh High reactivates the dormant track on both final-pass rounds, so the park text " +
                "must say a track kept being reawakened rather than the ordinary still-active wording");

        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewDispatched>().Count(e => e.Mode == ReviewMode.FinalFullPass).Should().Be(
            4, "exactly two final-pass rounds ran (cycles 4 and 6) before the cap parked the third");
        events.OfType<ReviewTrackReactivated>().Should().HaveCount(
            2, "the fresh High reactivates the dormant adversarial track on both final-pass rounds");
    }

    /// <summary>
    /// Task: a final full pass whose verdict is merge-ready and whose findings are all below the
    /// fix bar counts as a clean settle. Origin (2026-08-29): runs 514ffa6c and 430decdb parked at
    /// the mandatory final-full-pass oscillation cap even though their last pass came back
    /// merge-ready with nothing but a severity=low finding — the bar (Decisions Log #87) already
    /// treats that as done. This scripts the narrowest shape of that claim: the mandatory final
    /// pass reawakens a track dormant since cycle 1 (the same setup
    /// <see cref="A_track_the_mandatory_final_pass_reawakens_gets_a_genuine_cycle_to_fix_it"/>
    /// uses), but with a Low, in-scope finding rather than a High one, so the pass's own verdict
    /// reclassifies to merge-ready (Decisions Log #87 — every finding attached is RideAlong) rather
    /// than needs-fixes. The run must settle right there — no reactivation event, no extra
    /// fix-and-reverify cycle, no cap consumed — with the finding recorded as a residual and the
    /// daemon log naming the bar as the rule that concluded it.
    /// </summary>
    [Fact]
    public async Task A_final_pass_that_concludes_merge_ready_with_only_below_bar_findings_settles_by_the_bar()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            // Cycle 1: conformance clean and goes dormant; adversarial finds something real.
            "Criteria met at cycle 1.\n\nVERDICT: merge-ready",
            "FINDING: severity=high; scope=in-scope; at=Spawner.cs:60\nDefect: the child process is never reaped.\n\n"
            + "VERDICT: needs-fixes",
            "Reaped the child.\n\nRESOLUTION: fixed",
            // Cycle 2: only adversarial is still active — one Verify pass, and it concludes.
            "The lifetime holds now.\n\nVERDICT: merge-ready",
            // Cycle 3: the mandatory final full pass, both lenses fresh. Conformance — dormant
            // since cycle 1 — reports one Low, in-scope finding this time: below the fix bar, so
            // the pass's own verdict reclassifies to merge-ready and the cycle's merged verdict
            // does too, rather than the High that reawakens the track in the sibling test.
            "FINDING: severity=low; scope=in-scope; at=Auth.cs:9\nDefect: a log message could be clearer.\n\n"
            + "VERDICT: merge-ready",
            "The lifetime still holds.\n\nVERDICT: merge-ready");

        ListLogger<ReviewEngine> logger = new();
        ReviewEngine engine = new(store, executor, executor.Processes,
            new VerificationRunner(
                store, Options.Create(new DaemonOptions()), NullLogger<VerificationRunner>.Instance,
                new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance), executor, executor.Processes),
            Options.Create(new DaemonOptions { MaxComplianceReviewCycles = 3 }), logger,
            new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance), RecordingProcessRunner.NeverInvoked(),
            NewStackedParentWatch());

        bool mergeReady = await engine.ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue("a final pass that comes back merge-ready with only below-bar findings is done");
        executor.Spawns.Should().HaveCount(
            6, "the mandatory final pass settles on the spot — no reactivation, no extra fix-and-reverify cycle");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.UnderReview);
        run.ReviewCycle.Should().Be(3, "nothing after the mandatory final pass owed another cycle");
        run.ReviewSettlement.Should().Be(
            ReviewSettlement.Settled, "the Low finding is a real residual, not a reviewer who found nothing");
        run.ReviewResidualsRideAlong.Should().Be(1, "the below-bar finding is recorded, never fixed, never re-read");

        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewTrackReactivated>().Should().BeEmpty(
            "a below-bar finding never sets Continues: true off the merge-ready branch, so nothing here reactivates");

        logger.Lines.Should().Contain(line =>
            line.Contains("settling at cycle 3")
            && line.Contains("bar settle")
            && line.Contains("Decisions Log #87"),
            "the settle log must say the bar concluded it, not just that the run settled");
    }

    /// <summary>
    /// Task: a mandatory FinalFullPass records merge-ready when every finding it attaches is below
    /// High (Decisions Log #119). The sibling test above scripts a Low; this scripts the case the
    /// narrowing actually exists for — an in-scope Medium, which an ordinary cycle would fix but a
    /// FinalFullPass rides along instead. Exercises the full path end to end: mode threaded into
    /// <c>RecordReviewPassAsync</c>'s reclassification, the empty-terminal conclusion of every
    /// active track, <see cref="RunDetails.ReviewResidualsRideAlong"/>, and the
    /// <c>SettleReason.Bar</c> log line — not just the disposition split a unit test can already
    /// cover alone.
    /// </summary>
    [Fact]
    public async Task A_final_pass_that_concludes_merge_ready_with_an_in_scope_medium_finding_settles_by_the_bar()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            // Cycle 1: conformance clean and goes dormant; adversarial finds something real.
            "Criteria met at cycle 1.\n\nVERDICT: merge-ready",
            "FINDING: severity=high; scope=in-scope; at=Spawner.cs:60\nDefect: the child process is never reaped.\n\n"
            + "VERDICT: needs-fixes",
            "Reaped the child.\n\nRESOLUTION: fixed",
            // Cycle 2: only adversarial is still active — one Verify pass, and it concludes.
            "The lifetime holds now.\n\nVERDICT: merge-ready",
            // Cycle 3: the mandatory final full pass, both lenses fresh. Conformance — dormant
            // since cycle 1 — reports one Medium, in-scope finding this time: an ordinary cycle
            // would fix it, but the FinalFullPass bar is High alone, so it rides along and the
            // pass's own verdict reclassifies to merge-ready rather than needs-fixes.
            "FINDING: severity=medium; scope=in-scope; at=Auth.cs:9\nDefect: an error message swallows the cause.\n\n"
            + "VERDICT: merge-ready",
            "The lifetime still holds.\n\nVERDICT: merge-ready");

        ListLogger<ReviewEngine> logger = new();
        ReviewEngine engine = new(store, executor, executor.Processes,
            new VerificationRunner(
                store, Options.Create(new DaemonOptions()), NullLogger<VerificationRunner>.Instance,
                new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance), executor, executor.Processes),
            Options.Create(new DaemonOptions { MaxComplianceReviewCycles = 3 }), logger,
            new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance), RecordingProcessRunner.NeverInvoked(),
            NewStackedParentWatch());

        bool mergeReady = await engine.ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue(
            "a final pass that comes back merge-ready with only below-High findings is done");
        executor.Spawns.Should().HaveCount(
            6, "the mandatory final pass settles on the spot — no reactivation, no extra fix-and-reverify cycle");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.UnderReview);
        run.ReviewCycle.Should().Be(3, "nothing after the mandatory final pass owed another cycle");
        run.ReviewSettlement.Should().Be(
            ReviewSettlement.Settled, "the Medium finding is a real residual, not a reviewer who found nothing");
        run.ReviewResidualsRideAlong.Should().Be(
            1, "the below-High finding is recorded as a ride-along, never fixed, never re-read");

        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewTrackReactivated>().Should().BeEmpty(
            "a below-High finding never sets Continues: true off the merge-ready branch, so nothing here reactivates");

        logger.Lines.Should().Contain(line =>
            line.Contains("settling at cycle 3")
            && line.Contains("bar settle")
            && line.Contains("Decisions Log #87")
            && line.Contains("#119"),
            "the settle log must say the FinalFullPass bar concluded it, not just that the run settled");
    }

    /// <summary>
    /// Cycle-4 finding (both lenses): once a run parks on <c>MaxFinalFullPassRounds</c>, a human's
    /// <c>h9k review resolve --needs-fixes</c> must be a genuine fresh grant, the same way it
    /// already is for the per-track cycle caps (<see cref="RunAggregate.ReviewBudgetBaseCycle"/>).
    /// Left unfixed, <see cref="RunAggregate.FinalFullPassRounds"/> is a lifetime counter nothing
    /// ever lowers, so the human's fix session dispatches, the run reaches the Reverify branch, and
    /// <c>FinalFullPassCapReached</c> is still true — the run re-parks immediately with the
    /// identical reason, having spent a fix session and never dispatched the review pass asked for.
    /// This resolves the park, provides one fix, and asserts the mandatory pass actually runs and
    /// the run settles, rather than re-parking on the very next check.
    /// </summary>
    [Fact]
    public async Task A_needs_fixes_park_resolution_grants_a_fresh_final_full_pass_round_instead_of_reparking()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        // A fresh HIGH on every mandatory pass (task: a mandatory FinalFullPass records
        // merge-ready when every finding it attaches is below High — a Medium here would only
        // ride along and settle immediately, never reaching this park at all), same shape as
        // the sibling cap-park test.
        string freshHigh(string at) =>
            $"FINDING: severity=high; scope=in-scope; at={at}\nDefect: the final pass caught a fresh regression.\n\n"
            + "VERDICT: needs-fixes";

        ScriptedExecutor executor = new(
            // Cycle 1: conformance clean and dormant; adversarial needs a fix (pre-gate) — a
            // Medium here is unaffected by this task, since the narrower bar is FinalFullPass-only.
            "Criteria met.\n\nVERDICT: merge-ready",
            "FINDING: severity=medium; scope=in-scope; at=Retry.cs:12\nDefect: the retry duplicates the effect.\n\n"
            + "VERDICT: needs-fixes",
            "Tightened the retry guard.\n\nRESOLUTION: fixed",
            // Cycle 2: still pre-gate — a second minor issue still forces another cycle.
            "FINDING: severity=medium; scope=in-scope; at=Retry.cs:20\nDefect: a related edge is still off.\n\n"
            + "VERDICT: needs-fixes",
            "Closed the edge case too.\n\nRESOLUTION: fixed",
            // Cycle 3: clean — adversarial concludes, both tracks now dormant.
            "Clean now.\n\nVERDICT: merge-ready",
            // Cycle 4: mandatory final pass, round 1 — a fresh High reactivates the track.
            "Criteria still met.\n\nVERDICT: merge-ready",
            freshHigh("Retry.cs:31"),
            "Fixed the regression the final pass found.\n\nRESOLUTION: fixed",
            // Cycle 5: the confirming Verify pass over the reactivated track — clean, so
            // ActiveReviewLenses empties out and the loop is forced back to another mandatory pass.
            "Confirmed fixed.\n\nVERDICT: merge-ready",
            // Cycle 6: mandatory final pass, round 2 — MaxFinalFullPassRounds (set to 2 below) is
            // reached here, so the run parks deciding cycle 8 rather than dispatching a third round.
            "Criteria still met.\n\nVERDICT: merge-ready",
            freshHigh("Retry.cs:40"),
            "Fixed that regression too.\n\nRESOLUTION: fixed",
            // Cycle 7: the confirming Verify pass for round 2's fix.
            "Confirmed fixed again.\n\nVERDICT: merge-ready");

        bool mergeReady = await NewEngine(
            store, executor, new DaemonOptions { MaxFinalFullPassRounds = 2 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("the run parks on the final-pass round cap before cycle 8 ever dispatches");

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.NeedsFixes, "look one more time", Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor resumeExecutor = new(
            // The human's own fix session, dispatched directly over their reason.
            "Looked again.\n\nRESOLUTION: fixed",
            // A genuine third final-pass round — round 1 all over again if the fresh grant did
            // not reset FinalFullPassRounds, this is where the bug would instead re-park.
            "Clean this time.\n\nVERDICT: merge-ready",
            "Clean this time too.\n\nVERDICT: merge-ready");

        mergeReady = await NewEngine(
            store, resumeExecutor, new DaemonOptions { MaxFinalFullPassRounds = 2 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue(
            "the human's needs-fixes resolution is a fresh grant for the final-pass round cap too, " +
                "not just the per-track caps — the run must dispatch the pass asked for, not re-park on it");
        resumeExecutor.Spawns.Should().HaveCount(
            3, "the fix session and a genuine third final-pass round, not an immediate re-park");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.UnderReview);
        run.ReviewCycle.Should().Be(8, "a genuine third final-pass round ran and settled");
    }

    /// <summary>
    /// Adversarial cycle-2 review finding: a track still saying Continues: true when it hits its
    /// own cycle cap parks the run without ever reaching the concluding branch that turns a
    /// ride-along into a residual (that branch only runs for a plan whose own convergence rule
    /// says Continues: false). If the human then resolves the park with merge-ready, the run
    /// settles straight from here — SettleAsync force-concludes the still-active track, and has
    /// to read its last completed pass for a ride-along it never otherwise gets the chance to
    /// record, or the finding disappears from the tally as if it had never been reported.
    /// </summary>
    [Fact]
    public async Task A_ride_along_on_a_track_still_capped_when_the_run_settles_is_recorded_as_a_residual()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        const string mixedFindings =
            "FINDING: severity=medium; scope=in-scope; at=A.cs:1\n"
            + "Defect: the criterion is not met.\n\n"
            + "FINDING: severity=low; scope=in-scope; at=B.cs:2\n"
            + "Defect: a nit nobody asked for.\n\nVERDICT: needs-fixes";
        ScriptedExecutor executor = new(
            mixedFindings,
            "Nothing of my own.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(
            store, executor, new DaemonOptions { MaxComplianceReviewCycles = 1 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("conformance is still continuing but already at its one-cycle cap");

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.MergeReady, null, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor resumeExecutor = new();
        mergeReady = await NewEngine(store, resumeExecutor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        resumeExecutor.Spawns.Should().BeEmpty("no further session second-guesses the human");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewResidualsRideAlong.Should().Be(
            1, "the low finding at B.cs:2 rode along on the capped conformance track and was " +
                "never fixed or re-reviewed — settling must not drop it silently");
        run.ReviewResidualsUnfixed.Should().Be(
            1, "the medium finding at A.cs:1 was Fix-dispositioned — the platform had already " +
                "decided it had to be fixed here — but the cap parked before any fix session ever ran");
        run.ReviewUnfixedFindings.Should().ContainSingle()
            .Which.Should().Match<ReviewUnfixedFinding>(
                finding => finding.Severity == ReviewSeverity.Medium && finding.Location == "A.cs:1");
    }

    /// <summary>
    /// The exact shape the finding routed to this task describes (adversarial review, the routed
    /// finding that opened this task): cycle N's own pass returns needs-fixes with an in-scope
    /// High (Fix) and an in-scope Low (RideAlong). The track is capped, so the run parks in
    /// <c>ReviewPhase.FixNeeded</c> without ever dispatching a fix session over either finding.
    /// A human resolves the park with <c>h9k review resolve --merge-ready</c>, and the settled
    /// line must not report the High as though it had simply vanished: before this fix,
    /// <c>SettleAsync</c>'s forced-residual loop only ever swept up RideAlong-dispositioned
    /// findings, so the High at Api.cs:7 was silently dropped from every tally.
    /// </summary>
    [Fact]
    public async Task A_capped_fix_finding_the_run_settles_over_is_recorded_as_unfixed_not_dropped()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        const string mixedFindings =
            "FINDING: severity=high; scope=in-scope; at=Api.cs:7\n"
            + "Defect: envelope type differs from spec.\n\n"
            + "FINDING: severity=low; scope=in-scope; at=Shared.cs:9\n"
            + "Defect: a nit nobody asked for.\n\nVERDICT: needs-fixes";
        ScriptedExecutor executor = new(
            mixedFindings,
            "Nothing of my own.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(
            store, executor, new DaemonOptions { MaxComplianceReviewCycles = 1 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("conformance is still continuing but already at its one-cycle cap");

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.MergeReady, null, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor resumeExecutor = new();
        mergeReady = await NewEngine(store, resumeExecutor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        resumeExecutor.Spawns.Should().BeEmpty("no further session second-guesses the human");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewResidualsUnfixed.Should().Be(
            1, "the high finding at Api.cs:7 was decided fix-here but never handed to a fix session");
        run.ReviewUnfixedFindings.Should().ContainSingle()
            .Which.Should().Match<ReviewUnfixedFinding>(
                finding => finding.Severity == ReviewSeverity.High && finding.Location == "Api.cs:7");
        run.ReviewResidualsRideAlong.Should().Be(1, "the low finding at Shared.cs:9 still rides along as before");
        run.ReviewResidualsFixed.Should().Be(0, "no fix session ever ran over either finding");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, both lenses: a still-active track's forced-Unfixed
    /// sweep must not re-record a Fix finding a SIBLING track already concluded on this same
    /// cycle. Cycle 2 is one Verify pass covering both tracks; the severity gate applies from
    /// this cycle, so adversarial's own in-scope medium at A.cs:1 no longer forces another
    /// cycle and the track concludes here, normally, via <c>ReviewTrackPolicy.Decide</c> — which
    /// always records a concluding track's own Fix findings as <c>FixedUnreviewed</c>, whether or
    /// not a fix session ever reads them. Conformance's own finding at B.cs:2 still forces it to
    /// continue, but it is already at its own two-cycle cap, so the run parks without a fix
    /// session ever dispatching over either finding. Before this fix, <c>SettleAsync</c>'s own
    /// dedup checked only the disposition this cycle's own forced sweep would use
    /// (<c>Unfixed</c>, since no fix session ran) and missed the sibling's already-recorded
    /// <c>FixedUnreviewed</c> residual at the identical location, so A.cs:1 was forced in a
    /// second time as <c>Unfixed</c> — one defect counted as both fixed and left unfixed.
    /// </summary>
    [Fact]
    public async Task A_track_concluding_this_cycle_suppresses_its_shared_fix_finding_from_a_sibling_forced_sweep()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            // Cycle 1: both tracks find something medium, so both stay active into cycle 2.
            "FINDING: severity=medium; scope=in-scope; at=A.cs:1\nDefect: the criterion is not met.\n\n"
            + "VERDICT: needs-fixes",
            "FINDING: severity=medium; scope=in-scope; at=B.cs:2\nDefect: still present.\n\n"
            + "VERDICT: needs-fixes",
            "Tried.\n\nRESOLUTION: fixed",
            // Cycle 2: one Verify pass stands in for both tracks. The gate applies from this
            // cycle, so adversarial's own in-scope medium concludes the track right here instead
            // of forcing a third cycle. Conformance's own finding still forces it to continue,
            // but it is already at its cap, so no fix session ever dispatches over either one.
            "FINDING: severity=medium; scope=in-scope; track=adversarial; at=A.cs:1\nDefect: still present.\n\n"
            + "FINDING: severity=medium; scope=in-scope; track=conformance; at=B.cs:2\nDefect: still not met.\n\n"
            + "VERDICT: needs-fixes");
        bool mergeReady = await NewEngine(
            store, executor,
            new DaemonOptions
            {
                MaxComplianceReviewCycles = 2,
                MaxAdversarialReviewCycles = 5,
                AdversarialSeverityGateFromCycle = 2,
            })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("conformance is still continuing but already at its two-cycle cap");

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.MergeReady, null, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor resumeExecutor = new();
        mergeReady = await NewEngine(store, resumeExecutor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        resumeExecutor.Spawns.Should().BeEmpty("no further session second-guesses the human");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewResidualsFixed.Should().Be(
            1, "adversarial's own medium at A.cs:1 concluded normally this cycle and is recorded fixed-unreviewed");
        run.ReviewResidualsUnfixed.Should().Be(
            1, "conformance's own medium at B.cs:2 is still-active and genuinely never reached a fix session — "
                + "A.cs:1 must not count here too, since it already has its own fixed-unreviewed residual");
        run.ReviewUnfixedFindings.Should().ContainSingle(
                "A.cs:1 already has its own residual; the forced sweep must recognize that and skip it")
            .Which.Should().Match<ReviewUnfixedFinding>(
                finding => finding.Severity == ReviewSeverity.Medium && finding.Location == "B.cs:2");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 3, conformance finding #2: the sibling-suppression dedup
    /// above was cycle-scoped to catch a reactivated track's own stale residual, but that same
    /// scoping also blocked the legitimate cross-cycle match it still owes when the forced
    /// disposition really is <see cref="ReviewResidualDisposition.FixedUnreviewed"/>. Adversarial
    /// concludes normally at cycle 1 on a Medium at A.cs:10 (the gate is active from cycle 1),
    /// recording <c>FixedUnreviewed</c>. Conformance's own separate issue keeps it going into
    /// cycle 2, where its own Verify pass reports the IDENTICAL location — a fix session dispatches
    /// over it this same cycle and disputes it, so the run parks with the fix session's own
    /// dispatch cycle equal to the settle cycle, exactly the shape that forces
    /// <c>FixedUnreviewed</c> rather than <c>Unfixed</c>. The human resolves with merge-ready, and
    /// <c>SettleAsync</c> must recognize A.cs:10 already has a fixed-unreviewed residual from
    /// adversarial's own cycle-1 conclusion — a whole cycle earlier — rather than force a second
    /// one in under conformance, which would count one defect as fixed-unreviewed twice.
    /// </summary>
    [Fact]
    public async Task A_disputed_fix_session_at_a_previously_fixed_unreviewed_location_does_not_double_count()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            // Cycle 1: conformance's own separate issue keeps it going; adversarial's medium
            // concludes the track right here (the gate is active from cycle 1), recording
            // FixedUnreviewed at A.cs:10.
            "FINDING: severity=medium; scope=in-scope; at=Conform.cs:5\nDefect: the criterion is not met.\n\n"
            + "VERDICT: needs-fixes",
            "FINDING: severity=medium; scope=in-scope; at=A.cs:10\nDefect: the criterion is not met.\n\n"
            + "VERDICT: needs-fixes",
            // Cycle 1's fix session resolves conformance's own Conform.cs:5 finding cleanly —
            // adversarial's A.cs:10 is already consumed by its own conclusion above, so it is
            // never in this merged document at all.
            "Fixed.\n\nRESOLUTION: fixed",
            // Cycle 2: only conformance is still active — one Verify pass, and it reports the
            // SAME location as adversarial's own cycle-1 conclusion.
            "FINDING: severity=medium; scope=in-scope; at=A.cs:10\n"
            + "Defect: still present, same spot adversarial flagged a cycle ago.\n\nVERDICT: needs-fixes",
            // Cycle 2's fix session dispatches over that same-cycle finding and disputes it, so
            // the run parks with no cycle 3 ever starting: the dispatch cycle and the settle
            // cycle are the same, forcing FixedUnreviewed.
            "That is the intended behavior per spec.\n\nRESOLUTION: disputed");
        bool mergeReady = await NewEngine(
            store, executor,
            new DaemonOptions { AdversarialSeverityGateFromCycle = 1 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("the fix session disputed conformance's own finding");

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.MergeReady, null, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor resumeExecutor = new();
        mergeReady = await NewEngine(store, resumeExecutor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        resumeExecutor.Spawns.Should().BeEmpty("no further session second-guesses the human");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewResidualsFixed.Should().Be(
            1, "A.cs:10 is one defect, already recorded fixed-unreviewed by adversarial's own cycle-1 " +
                "conclusion — conformance's own cycle-2 restatement of the identical place must collapse " +
                "into it, not add a second fixed-unreviewed count for the same defect");
        run.ReviewResidualsUnfixed.Should().Be(0, "nothing here was left unhanded to a fix session");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 3, conformance finding #2: the finding itself named the
    /// identical defect in <c>alreadyOnStreamRideAlong</c>'s own FixedUnreviewed branch, alongside
    /// <c>alreadyOnStreamFix</c> above — the sibling test's shape, but for a ride-along instead of
    /// a Fix finding. Adversarial concludes normally at cycle 1 on a Low ride-along at L.cs:20 (the
    /// gate is active from cycle 1) — a fix session dispatches THIS cycle over conformance's own
    /// separate Medium, so adversarial's ride-along, handed to that same merged document, records
    /// FixedUnreviewed rather than plain RideAlong. Conformance's own issue keeps it going into
    /// cycle 2, where its Verify pass restates the IDENTICAL L.cs:20 location as a ride-along again
    /// alongside its own still-open Medium; its own cycle-2 fix session disputes the Medium, so the
    /// run parks with the dispatch cycle equal to the settle cycle — forcing FixedUnreviewed again.
    /// The human resolves with merge-ready, and <c>SettleAsync</c> must recognize L.cs:20 already
    /// has a fixed-unreviewed residual from adversarial's own cycle-1 conclusion — a whole cycle
    /// earlier — rather than force a second one in under conformance.
    /// </summary>
    [Fact]
    public async Task A_disputed_fix_session_at_a_previously_fixed_unreviewed_ride_along_location_does_not_double_count()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            // Cycle 1: conformance's own separate issue keeps it going; adversarial's own low
            // ride-along concludes the track right here (the gate is active from cycle 1). A fix
            // session WILL dispatch this cycle over conformance's own finding, so adversarial's
            // ride-along is handed to that same merged document and records FixedUnreviewed at
            // L.cs:20 rather than plain RideAlong.
            "FINDING: severity=medium; scope=in-scope; at=Conform.cs:5\nDefect: the criterion is not met.\n\n"
            + "VERDICT: needs-fixes",
            "FINDING: severity=low; scope=in-scope; at=L.cs:20\nDefect: a nit nobody asked for.\n\n"
            + "VERDICT: needs-fixes",
            // Cycle 1's fix session resolves conformance's own Conform.cs:5 finding cleanly —
            // adversarial's L.cs:20 is already consumed by its own conclusion above.
            "Fixed.\n\nRESOLUTION: fixed",
            // Cycle 2: only conformance is still active — one Verify pass, restating both its own
            // Medium and a ride-along at the SAME location as adversarial's own cycle-1 conclusion.
            "FINDING: severity=medium; scope=in-scope; at=Conform.cs:5\n"
            + "Defect: still not met.\n\n"
            + "FINDING: severity=low; scope=in-scope; at=L.cs:20\n"
            + "Defect: still a nit, same spot adversarial flagged a cycle ago.\n\nVERDICT: needs-fixes",
            // Cycle 2's fix session dispatches over that same-cycle finding and disputes it, so
            // the run parks with no cycle 3 ever starting: the dispatch cycle and the settle
            // cycle are the same, forcing FixedUnreviewed for the ride-along too.
            "That is the intended behavior per spec.\n\nRESOLUTION: disputed");
        bool mergeReady = await NewEngine(
            store, executor,
            new DaemonOptions { AdversarialSeverityGateFromCycle = 1 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("the fix session disputed conformance's own finding");

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.MergeReady, null, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor resumeExecutor = new();
        mergeReady = await NewEngine(store, resumeExecutor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        resumeExecutor.Spawns.Should().BeEmpty("no further session second-guesses the human");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewResidualsFixed.Should().Be(
            2, "Conform.cs:5 (conformance's own genuinely new fixed-unreviewed location this cycle) " +
                "and L.cs:20 (adversarial's own cycle-1 conclusion, one defect, not two) together");
        run.ReviewResidualsRideAlong.Should().Be(
            0, "L.cs:20 already has its own fixed-unreviewed residual from cycle 1 — it must not also " +
                "land here as an unclaimed ride-along, and must not be force-recorded a second time either");
        run.ReviewResidualsUnfixed.Should().Be(0, "nothing here was left unhanded to a fix session");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 5, both lenses' high finding: the cross-cycle
    /// already-on-stream match above (widened in the prior fix to catch a sibling track's own
    /// EARLIER-cycle conclusion) reads <c>run.ReviewResiduals</c> raw, but the tally it is meant
    /// to anticipate (<c>RunAggregate.DeriveResidualTally</c> → <c>PerDefect</c>) excludes any
    /// FixedUnreviewed residual a later clean re-read on its own lens has since superseded
    /// (<c>RunAggregate.IsSupersededByCleanReread</c>). Adversarial concludes at cycle 1 with a
    /// gated Medium at A.cs:10, recording <c>FixedUnreviewed</c>; conformance keeps its own
    /// separate issue going and concludes clean at cycle 2, so both tracks are dormant. The
    /// mandatory <see cref="ReviewMode.FinalFullPass"/> at cycle 3 reads adversarial clean —
    /// superseding its cycle-1 residual — while conformance reports a fresh in-scope High at the
    /// IDENTICAL A.cs:10, reactivating it. The fix session that cycle disputes, so the run parks
    /// and settles at cycle 3 with a fix session having run THIS cycle, forcing
    /// <c>FixedUnreviewed</c>. Before this fix, the stale — now superseded — cycle-1 residual
    /// still matched on disposition alone and silently swallowed conformance's genuinely new
    /// High, dropping it from the settle entirely instead of forcing its own residual.
    /// </summary>
    [Fact]
    public async Task A_reactivated_tracks_new_finding_is_not_swallowed_by_a_sibling_s_superseded_residual()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            // Cycle 1: conformance's own separate issue keeps it going; adversarial's medium
            // concludes the track right here (the gate is active from cycle 1), recording
            // FixedUnreviewed at A.cs:10.
            "FINDING: severity=medium; scope=in-scope; at=Conform.cs:5\nDefect: the criterion is not met.\n\n"
            + "VERDICT: needs-fixes",
            "FINDING: severity=medium; scope=in-scope; at=A.cs:10\nDefect: the criterion is not met.\n\n"
            + "VERDICT: needs-fixes",
            "Fixed.\n\nRESOLUTION: fixed",
            // Cycle 2: only conformance is still active — one Verify pass, and it concludes clean.
            // Both tracks are now dormant, so the very next cycle is the mandatory final pass.
            "Clean now.\n\nVERDICT: merge-ready",
            // Cycle 3: the mandatory final full pass, both lenses fresh. Conformance — dormant
            // since cycle 2 — reports a genuinely new High at the SAME location adversarial's own
            // cycle-1 conclusion already recorded FixedUnreviewed, reawakening it. Adversarial
            // reads clean this time, superseding its own cycle-1 residual.
            "FINDING: severity=high; scope=in-scope; at=A.cs:10\n"
            + "Defect: the final pass found a real regression at the exact spot adversarial fixed a cycle ago.\n\n"
            + "VERDICT: needs-fixes",
            "Still clean.\n\nVERDICT: merge-ready",
            // Cycle 3's fix session dispatches over conformance's reactivated High and disputes
            // it, so the run parks with the dispatch cycle equal to the settle cycle — forcing
            // FixedUnreviewed on whatever the settle sweep still owes conformance.
            "That is the intended behavior per spec.\n\nRESOLUTION: disputed");
        bool mergeReady = await NewEngine(
            store, executor,
            new DaemonOptions { AdversarialSeverityGateFromCycle = 1 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("the fix session disputed conformance's reactivated finding");

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.MergeReady, null, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor resumeExecutor = new();
        mergeReady = await NewEngine(store, resumeExecutor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        resumeExecutor.Spawns.Should().BeEmpty("no further session second-guesses the human");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewResidualsFixed.Should().Be(
            1, "A.cs:10 is one still-outstanding defect — adversarial's own cycle-1 residual there " +
                "is superseded by its clean cycle-3 re-read, so conformance's genuinely new High must " +
                "be the one and only fixed-unreviewed residual recorded for it, not zero and not two");
        run.ReviewResidualsRideAlong.Should().Be(0, "nothing here ever fell below the fix bar");
        run.ReviewResidualsUnfixed.Should().Be(0, "a fix session did run this cycle, so this is FixedUnreviewed, not Unfixed");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, both lenses' finding #1: the sibling-suppression dedup
    /// above matched a Fix-shaped residual at the same place from ANY cycle, not just this one, so
    /// a track the mandatory <see cref="ReviewMode.FinalFullPass"/> reawakens with a genuinely
    /// different, still-unresolved finding at the SAME location its own earlier conclusion already
    /// recorded fixed-unreviewed would have that new finding silently swallowed by the stale
    /// record. Adversarial concludes normally at cycle 1 (the gate is active from cycle 1 here) on
    /// a Medium at A.cs:10, recording <c>FixedUnreviewed</c>, while conformance's own separate
    /// issue keeps it — and the run — going for two more cycles until it too is confirmed clean.
    /// Both tracks now dormant, the mandatory final pass dispatches and adversarial reports a fresh
    /// High at the IDENTICAL location — reactivating the track — and immediately caps out (its
    /// per-track budget base resets to the reactivation cycle, and the cap here is zero), so no fix
    /// session ever reads it. The human resolves the resulting park with merge-ready, and the
    /// settled line must show BOTH the stale fixed-unreviewed record and the genuinely new unfixed
    /// one, under DIFFERENT dispositions (so, unlike the dispute-shaped variant of this scenario,
    /// <see cref="RunAggregate.DeriveResidualTally"/>'s own same-disposition collapse never hides
    /// the difference) — not let the first silently absorb the second.
    /// </summary>
    [Fact]
    public async Task A_reawakened_track_s_new_finding_at_a_previously_fixed_location_is_not_swallowed()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            // Cycle 1: conformance's own separate issue keeps it continuing; adversarial's medium
            // concludes the track right here (the gate is active from cycle 1), recording
            // FixedUnreviewed at A.cs:10. The fix session dispatched this cycle is conformance's
            // own — adversarial already concluded and needs nothing further from it.
            "FINDING: severity=medium; scope=in-scope; at=Conform.cs:1\nDefect: the criterion is not met.\n\n"
            + "VERDICT: needs-fixes",
            "FINDING: severity=medium; scope=in-scope; at=A.cs:10\nDefect: the criterion is not met.\n\n"
            + "VERDICT: needs-fixes",
            "Addressed the conformance issue.\n\nRESOLUTION: fixed",
            // Cycle 2: only conformance is still active — one Verify pass, and it confirms clean.
            "Confirmed fixed.\n\nVERDICT: merge-ready",
            // Cycle 3: both tracks are now dormant, so the mandatory final full pass dispatches.
            // Conformance stays clean; adversarial reports a fresh High at the SAME location as its
            // own cycle-1 conclusion — reactivating the track — and its cap (zero, measured from
            // the reactivation cycle itself) is already reached, so no fix session ever dispatches.
            "Still clean.\n\nVERDICT: merge-ready",
            "FINDING: severity=high; scope=in-scope; at=A.cs:10\n"
            + "Defect: still broken, worse than the earlier fix believed.\n\nVERDICT: needs-fixes");
        bool mergeReady = await NewEngine(
            store, executor,
            new DaemonOptions
            {
                MaxAdversarialReviewCycles = 0,
                AdversarialSeverityGateFromCycle = 1,
            })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("the reawakened track is already at its (zero) cap the moment it reactivates");

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.MergeReady, null, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor resumeExecutor = new();
        mergeReady = await NewEngine(store, resumeExecutor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        resumeExecutor.Spawns.Should().BeEmpty("no further session second-guesses the human");

        List<object> events = [.. (await store.QuerySession().Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewTrackReactivated>().Should().ContainSingle(
            reactivated => reactivated.Lens == ReviewLens.Adversarial && reactivated.Cycle == 3);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewResidualsFixed.Should().Be(
            1, "cycle 1's own conclusion at A.cs:10 is still on the stream, recorded fixed-unreviewed");
        run.ReviewResidualsUnfixed.Should().Be(
            1, "the reactivated track's genuinely new High at the identical location must not be " +
                "swallowed by the stale fixed-unreviewed record from cycle 1");
        run.ReviewUnfixedFindings.Should().ContainSingle()
            .Which.Should().Match<ReviewUnfixedFinding>(
                finding => finding.Severity == ReviewSeverity.High && finding.Location == "A.cs:10");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, adversarial finding #2: a Verify pass's own finding
    /// tagged for a track that has ALREADY concluded this exact cycle cannot be deduplicated by
    /// place — <see cref="ReviewFindingLocations.SamePlace"/> deliberately treats two blank
    /// locations as different defects — so the sibling-suppression dedup above cannot catch it
    /// when the reviewer stated no location at all. Adversarial concludes normally this cycle on
    /// an UNPLACED Medium (recording <c>FixedUnreviewed</c> with an empty location); conformance's
    /// own separate, placed Medium keeps it active, and it is already at its two-cycle cap, so the
    /// run parks without a fix session ever dispatching. On <c>h9k review resolve --merge-ready</c>,
    /// <c>SettleAsync</c>'s force-conclude loop reaches the SAME Verify pass again while iterating
    /// conformance (the only still-active lens) and must recognize that the unplaced finding's own
    /// <c>track=adversarial</c> tag names an already-concluded lens — skipping it outright — rather
    /// than falling back to attribute it to conformance and force it in a second time.
    /// </summary>
    [Fact]
    public async Task A_verify_pass_s_unplaced_finding_tagged_for_an_already_concluded_track_is_not_recorded_twice()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            // Cycle 1: both tracks find something medium, so both stay active into cycle 2.
            "FINDING: severity=medium; scope=in-scope; at=Conform.cs:1\nDefect: the criterion is not met.\n\n"
            + "VERDICT: needs-fixes",
            "FINDING: severity=medium; scope=in-scope; at=Adv.cs:2\nDefect: still present.\n\n"
            + "VERDICT: needs-fixes",
            "Tried.\n\nRESOLUTION: fixed",
            // Cycle 2: one Verify pass stands in for both tracks. The gate applies from this
            // cycle, so adversarial's own in-scope medium — stated with no location at all —
            // concludes the track right here instead of forcing a third cycle. Conformance's own
            // finding still forces it to continue, but it is already at its cap, so no fix session
            // ever dispatches over either one.
            "FINDING: severity=medium; scope=in-scope; track=adversarial\nDefect: still present, exact line moved.\n\n"
            + "FINDING: severity=medium; scope=in-scope; track=conformance; at=Conform.cs:1\n"
            + "Defect: still not met.\n\nVERDICT: needs-fixes");
        bool mergeReady = await NewEngine(
            store, executor,
            new DaemonOptions
            {
                MaxComplianceReviewCycles = 2,
                MaxAdversarialReviewCycles = 5,
                AdversarialSeverityGateFromCycle = 2,
            })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("conformance is still continuing but already at its two-cycle cap");

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.MergeReady, null, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor resumeExecutor = new();
        mergeReady = await NewEngine(store, resumeExecutor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        resumeExecutor.Spawns.Should().BeEmpty("no further session second-guesses the human");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewResidualsFixed.Should().Be(
            1, "adversarial's own unplaced medium concluded normally this cycle, fixed-unreviewed");
        run.ReviewResidualsUnfixed.Should().Be(
            1, "conformance's own medium at Conform.cs:1 is still-active and genuinely never reached " +
                "a fix session — the adversarial-tagged unplaced finding must not be force-recorded a " +
                "second time under conformance just because SamePlace cannot match two blank locations");
        run.ReviewUnfixedFindings.Should().ContainSingle(
                "the adversarial-tagged unplaced finding already has its own residual under adversarial; " +
                "it must not also appear here, mis-attributed to conformance")
            .Which.Should().Match<ReviewUnfixedFinding>(
                finding => finding.Severity == ReviewSeverity.Medium && finding.Location == "Conform.cs:1");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 3, both lenses' finding #1: the sibling-tag skip above
    /// only ever needs to catch a tag naming a track that concluded THIS cycle (the one case
    /// where a residual for it genuinely already sits on the stream) — not one that concluded
    /// cycles ago. Adversarial concludes clean at cycle 1 with nothing to say at all; conformance
    /// keeps its own separate Medium going for two more cycles until it hits its cap. The cap
    /// cycle's own Verify pass — the only pass covering conformance, the one active lens — also
    /// restates an old <c>track=adversarial</c> finding at a placed location, exactly the shape
    /// <c>AgentPromptBuilder.AppendVerifyTrackTagContract</c> asks a Verify reviewer for. Before
    /// this fix, the skip fired unconditionally on "not currently active" and dropped it outright;
    /// it must instead fall back to conformance, the only lens still being force-concluded, the
    /// same way an untagged finding already does.
    /// </summary>
    [Fact]
    public async Task A_verify_pass_finding_tagged_for_a_track_that_concluded_cycles_ago_is_credited_not_dropped()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            // Cycle 1: conformance's own issue keeps it going; adversarial finds nothing at all
            // and concludes clean right here — no reactivation anywhere in this scenario.
            "FINDING: severity=medium; scope=in-scope; at=Conform.cs:1\nDefect: the criterion is not met.\n\n"
            + "VERDICT: needs-fixes",
            "VERDICT: merge-ready",
            "Tried.\n\nRESOLUTION: fixed",
            // Cycle 2: only conformance is still active, so one Verify pass stands in for it.
            // Conformance's own restated finding keeps it going, but this is already its
            // two-cycle cap. The same pass also restates an old adversarial-tagged finding at a
            // DIFFERENT, placed location — adversarial concluded a whole cycle ago, with no
            // residual of its own for this place, so this is not the same-cycle sibling
            // conclusion the guard above exists to catch.
            "FINDING: severity=medium; scope=in-scope; track=conformance; at=Conform.cs:1\n"
            + "Defect: still not met.\n\n"
            + "FINDING: severity=medium; scope=in-scope; track=adversarial; at=Adv.cs:2\n"
            + "Defect: restated from an earlier cycle.\n\nVERDICT: needs-fixes");
        bool mergeReady = await NewEngine(
            store, executor,
            new DaemonOptions
            {
                MaxComplianceReviewCycles = 2,
                MaxAdversarialReviewCycles = 5,
            })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("conformance is still continuing but already at its two-cycle cap");

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.MergeReady, null, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor resumeExecutor = new();
        mergeReady = await NewEngine(store, resumeExecutor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        resumeExecutor.Spawns.Should().BeEmpty("no further session second-guesses the human");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewResidualsUnfixed.Should().Be(
            2, "both conformance's own restated finding and the adversarial-tagged restatement must " +
                "be credited under conformance — the still-active lens — rather than dropped just " +
                "because adversarial's own conclusion is a whole cycle stale");
        run.ReviewUnfixedFindings.Should().HaveCount(2)
            .And.Contain(finding => finding.Severity == ReviewSeverity.Medium && finding.Location == "Conform.cs:1")
            .And.Contain(finding => finding.Severity == ReviewSeverity.Medium && finding.Location == "Adv.cs:2");
    }

    /// <summary>
    /// Cycle-3 cap-park finding: both tracks can be forced-concluded together at the same
    /// settlement (both capped at cycle 1 here), and each can independently report the same
    /// nit. SettleAsync's forced ride-along has to collapse that per distinct location exactly as
    /// <see cref="RunAggregate.DeriveResidualTally"/>'s own <c>PerDefect</c> does everywhere else
    /// in the tally, or two lenses reporting one nit inflates the residual count to two.
    /// </summary>
    [Fact]
    public async Task Two_lenses_forced_concluding_together_collapse_a_shared_ride_along_to_one_residual()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        const string sharedNit =
            "FINDING: severity=low; scope=in-scope; at=Shared.cs:9\n"
            + "Defect: a nit both lenses happened to notice.\n\n";
        ScriptedExecutor executor = new(
            "FINDING: severity=medium; scope=in-scope; at=A.cs:1\n"
            + "Defect: the criterion is not met.\n\n" + sharedNit + "VERDICT: needs-fixes",
            "FINDING: severity=high; scope=in-scope; at=B.cs:2\n"
            + "Defect: a real correctness bug.\n\n" + sharedNit + "VERDICT: needs-fixes");
        bool mergeReady = await NewEngine(
            store, executor,
            new DaemonOptions { MaxComplianceReviewCycles = 1, MaxAdversarialReviewCycles = 1 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("both tracks are still continuing but already at their one-cycle cap");

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.MergeReady, null, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor resumeExecutor = new();
        mergeReady = await NewEngine(store, resumeExecutor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        resumeExecutor.Spawns.Should().BeEmpty("no further session second-guesses the human");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewResidualsRideAlong.Should().Be(
            1, "both lenses reported the same nit at Shared.cs:9 — one defect reported twice is " +
                "still one defect, not two");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 2, adversarial finding: unlike the two-independent-passes
    /// case above, a single <see cref="ReviewMode.Verify"/> pass stands in for BOTH still-active
    /// tracks at once, so <c>SettleAsync</c>'s force-conclude loop reaches the very same
    /// <see cref="ReviewFindingRecord"/> instance once per lens that pass covers. An unplaced
    /// ride-along (no location the reviewer stated) cannot be collapsed by place — that is
    /// deliberate, so two genuinely different unplaced findings never get merged into one — so
    /// this has to be caught before the place-based dedup ever runs, or one reviewer's one
    /// statement becomes two residuals.
    /// </summary>
    [Fact]
    public async Task A_verify_pass_shared_unplaced_ride_along_settles_as_one_residual_not_two()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        const string unplacedRideAlong =
            "FINDING: severity=low; scope=in-scope\n"
            + "Defect: a nit neither reviewer bothered to place on a line.\n\n";
        ScriptedExecutor executor = new(
            // Cycle 1: both tracks find something, so both stay active into cycle 2.
            "FINDING: severity=medium; scope=in-scope; at=A.cs:1\nDefect: the criterion is not met.\n\n"
            + "VERDICT: needs-fixes",
            "FINDING: severity=medium; scope=in-scope; at=B.cs:2\nDefect: still present.\n\n"
            + "VERDICT: needs-fixes",
            "Tried.\n\nRESOLUTION: fixed",
            // Cycle 2: one Verify pass stands in for both tracks. Both findings still stand
            // (each keeps its own track alive), plus one ride-along neither lens placed or
            // tagged, so it counts against every track this pass stands in for.
            "FINDING: severity=medium; scope=in-scope; track=conformance; at=A.cs:1\nDefect: still not met.\n\n"
            + "FINDING: severity=medium; scope=in-scope; track=adversarial; at=B.cs:2\nDefect: still present.\n\n"
            + unplacedRideAlong + "VERDICT: needs-fixes");
        bool mergeReady = await NewEngine(
            store, executor,
            new DaemonOptions { MaxComplianceReviewCycles = 2, MaxAdversarialReviewCycles = 2 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("both tracks are still continuing but already at their two-cycle cap");

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.MergeReady, null, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor resumeExecutor = new();
        mergeReady = await NewEngine(store, resumeExecutor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewResidualsRideAlong.Should().Be(
            1, "the Verify pass's one unplaced ride-along was reached once per track it stands in " +
                "for, but it is still one reviewer statement, not two");
    }

    /// <summary>
    /// Cycle-4 adversarial finding: unlike the two prior tests above, both tracks here CONCLUDE
    /// inside <c>RecordReviewPassAsync</c>'s own loop — the Verify pass's shared ride-along is the
    /// only thing either track reports, so neither is left "still active" for <c>SettleAsync</c>'s
    /// own reference-identity guard to catch. That guard exists only there; this asserts the same
    /// hazard is closed at the concluding-plan loop too, or one reviewer statement shared by two
    /// concluding tracks becomes two residuals.
    /// </summary>
    [Fact]
    public async Task A_verify_pass_shared_unplaced_ride_along_concluding_both_tracks_settles_as_one_residual_not_two()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            // Cycle 1: both tracks find something, so both stay active into cycle 2.
            "FINDING: severity=medium; scope=in-scope; at=A.cs:1\nDefect: the criterion is not met.\n\n"
            + "VERDICT: needs-fixes",
            "FINDING: severity=medium; scope=in-scope; at=B.cs:2\nDefect: still present.\n\n"
            + "VERDICT: needs-fixes",
            "Tried.\n\nRESOLUTION: fixed",
            // Cycle 2: one Verify pass stands in for both tracks, and reports only a shared,
            // unplaced, untagged low — nothing keeps either track continuing, so both conclude
            // right here, inside the concluding-plan loop, each carrying the SAME finding
            // instance in its own RideAlong list.
            "FINDING: severity=low; scope=in-scope\n"
            + "Defect: a nit neither reviewer bothered to place on a line.\n\nVERDICT: needs-fixes",
            // Cycle 3: both tracks concluded, so the mandatory final full pass runs before the
            // run may settle — both lenses fresh, both clean.
            "Still clean.\n\nVERDICT: merge-ready",
            "Still clean too.\n\nVERDICT: merge-ready");

        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewResidualsRideAlong.Should().Be(
            1, "the Verify pass's one unplaced ride-along was attributed to both concluding tracks' " +
                "own plans, but it is still one reviewer statement, not two");
    }

    /// <summary>
    /// Cycle-3 conformance finding: <c>SettleAsync</c>'s force-conclude loop iterates
    /// <c>ActiveReviewLenses</c> in order — Conformance, then Adversarial — and used to always
    /// credit whichever of those it reached first with a Verify pass's shared ride-along,
    /// regardless of which track the reviewer's own `track=` tag actually named. This forces both
    /// tracks to their cap with a Verify pass whose ride-along is explicitly tagged
    /// `track=adversarial`, and asserts the residual lands on the adversarial track's own
    /// conclusion, never conformance's, even though conformance is reached first in the loop.
    /// </summary>
    [Fact]
    public async Task A_verify_pass_tagged_ride_along_settles_under_the_track_its_own_tag_names()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            // Cycle 1: both tracks find something, so both stay active into cycle 2.
            "FINDING: severity=medium; scope=in-scope; at=A.cs:1\nDefect: the criterion is not met.\n\n"
            + "VERDICT: needs-fixes",
            "FINDING: severity=medium; scope=in-scope; at=B.cs:2\nDefect: still present.\n\n"
            + "VERDICT: needs-fixes",
            "Tried.\n\nRESOLUTION: fixed",
            // Cycle 2: one Verify pass stands in for both tracks. Both prior findings still
            // stand (each keeps its own track alive at the two-cycle cap), plus a ride-along
            // explicitly tagged adversarial — not conformance, the track SettleAsync's loop
            // reaches first.
            "FINDING: severity=medium; scope=in-scope; track=conformance; at=A.cs:1\nDefect: still not met.\n\n"
            + "FINDING: severity=medium; scope=in-scope; track=adversarial; at=B.cs:2\nDefect: still present.\n\n"
            + "FINDING: severity=low; scope=in-scope; track=adversarial; at=C.cs:5\n"
            + "Defect: a nit only the adversarial track flagged.\n\nVERDICT: needs-fixes");
        bool mergeReady = await NewEngine(
            store, executor,
            new DaemonOptions { MaxComplianceReviewCycles = 2, MaxAdversarialReviewCycles = 2 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("both tracks are still continuing but already at their two-cycle cap");

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.MergeReady, null, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor resumeExecutor = new();
        mergeReady = await NewEngine(store, resumeExecutor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        List<ReviewTrackConcluded> concluded = [.. events.OfType<ReviewTrackConcluded>()
            .Where(e => e.Settlement == ReviewSettlement.Settled && e.Residuals.Count > 0)];

        concluded.Should().ContainSingle(e => e.Lens == ReviewLens.Adversarial && e.Residuals.Any(r => r.Location == "C.cs:5"),
            "the ride-along's own track= tag named adversarial, not whichever lens the settle loop reached first");
        concluded.Should().NotContain(e => e.Lens == ReviewLens.Conformance && e.Residuals.Any(r => r.Location == "C.cs:5"),
            "conformance is reached first in SettleAsync's loop, but the tag did not name it");
    }

    /// <summary>
    /// Cycle-3 cap-park finding: a still-active track's ride-along can be force-concluded after a
    /// fix session actually ran this same cycle (dispatched over the Fix finding that kept the
    /// track continuing, disputed, and the human then ended the loop with merge-ready). That
    /// fix session already read the ride-along too — <c>WriteMergedFindingsAsync</c> writes every
    /// active lens's ride-alongs into the one merged document a dispatched fix session reads,
    /// concluding or not — so it must record fixed-unreviewed, the same distinction
    /// <c>RecordReviewPassAsync</c>'s own <c>fixSessionWillDispatch</c> already draws for a
    /// normally-concluding track, rather than ride-along, which would claim nobody ever looked.
    /// The disputed Fix finding itself (Api.cs:7) gets the identical treatment, for the identical
    /// reason (adversarial review, the routed finding that opened this task): it too was handed to
    /// this same fix session, so it records fixed-unreviewed rather than vanishing from the tally
    /// the way it once did.
    /// </summary>
    [Fact]
    public async Task A_ride_along_handed_to_a_disputed_fix_session_settles_as_fixed_unreviewed()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "FINDING: severity=high; scope=in-scope; at=Api.cs:7\n"
            + "Defect: envelope type differs from spec.\n\n"
            + "FINDING: severity=low; scope=in-scope; at=Shared.cs:9\n"
            + "Defect: a nit nobody asked for.\n\nVERDICT: needs-fixes",
            "No defects of my own.\n\nVERDICT: merge-ready",
            "That envelope change is the task's stated design; changing it back is a scope decision.\n\nRESOLUTION: disputed");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("the fix session disputed the conformance finding");

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.MergeReady, null, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor resumeExecutor = new();
        mergeReady = await NewEngine(store, resumeExecutor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        resumeExecutor.Spawns.Should().BeEmpty("no further session second-guesses the human");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewResidualsFixed.Should().Be(
            2, "both the disputed high finding at Api.cs:7 and the low finding at Shared.cs:9 " +
                "were already inside the merged document the disputed fix session read this same " +
                "cycle, so both shipped fixed-unreviewed rather than one of them vanishing");
        run.ReviewResidualsRideAlong.Should().Be(
            0, "the low finding must not also be counted as an unclaimed ride-along");
        run.ReviewResidualsUnfixed.Should().Be(
            0, "the high finding was handed to the fix session this cycle, not left unhanded");
    }

    /// <summary>
    /// AC3's own named gap (task: a headless build, fix, or recovery session never ends its turn
    /// while a gate it started is still running in the background — origin incident, task
    /// 6df5f975, run 01a08028, 2026-09-08 09:53 EDT): a human-resolved fix session
    /// (<c>h9k review resolve --needs-fixes</c>, <see cref="DispatchFixSessionAsync"/>'s
    /// <c>humanFindings.IsNotBlank()</c> branch) ends its turn with a modified-but-uncommitted
    /// tracked file still sitting in the worktree, and gets the identical automatic recovery an
    /// ordinary review-fix leg already earns — the production gap was exactly this leg getting
    /// none, and the run failing before its gates instead of a human retry ever being needed.
    /// </summary>
    [Fact]
    public async Task A_human_resolved_fix_session_ending_on_a_dirty_tree_earns_its_own_automatic_recovery()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, string worktreePath, _) = await SeedVerifiedRunWithTestGateAsync(store, cts.Token);

        File.WriteAllText(Path.Combine(worktreePath, "half-done.cs"), "class HalfDone { }\n");
        Git(worktreePath, "add -A");
        Git(worktreePath, "-c user.name=Test -c user.email=test@test commit -q -m half-done");

        // No review cycle runs first: an operator's own `h9k review resolve --needs-fixes` reaches
        // this leg on a freshly-claimed run exactly as readily as it does on a parked one — the
        // event is what puts the run into ReviewPhase.FixNeeded, regardless of what came before it.
        const string humanFindings = "The limiter finding is real; fix it as the reviewer described.";
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.NeedsFixes, humanFindings, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor fixExecutor = new(
            // The human-resolved fix session: no RESOLUTION marker, ends naming a background task.
            "The full dotnet test run is still running in the background.",
            // The automatic uncommitted-work recovery this leaves stranded.
            "Committed the stranded file.",
            // Whatever review cycle follows (a fresh Discovery pass over both tracks, since this
            // run never dispatched one before the human resolved it) — this test's own point is
            // already proven by the two spawns above; these just let the loop reach a real end
            // rather than the scripted queue running dry underneath it.
            "Clean.\n\nVERDICT: merge-ready",
            "Clean too.\n\nVERDICT: merge-ready");
        fixExecutor.OnSpawnByIndex[0] = () =>
            File.WriteAllText(Path.Combine(worktreePath, "half-done.cs"), "left behind, uncommitted\n");
        fixExecutor.OnSpawnByIndex[1] = () =>
        {
            Git(worktreePath, "add -A");
            Git(worktreePath, "-c user.name=Test -c user.email=test@test commit -q -m recovered");
        };

        await NewEngine(store, fixExecutor).ReviewAsync(runId, taskId, cts.Token);

        fixExecutor.Spawns.Should().HaveCountGreaterThanOrEqualTo(
            2, "the human-resolved fix session, plus the automatic recovery its own dirty ending earned");
        fixExecutor.Spawns[0].Prompt.Should().Contain(
            humanFindings, "the human-resolved leg reads the operator's own reason, not a merged findings document");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.LastFixEndedWaitingOnBackgroundGate.Should().BeTrue(
            "h9k task show and the run log must say this leg ended waiting on a background gate, not undeclared");
        run.UncommittedWorkRecoveries.Should().ContainSingle().Which.Leg.Should().Be(
            RunSessionLeg.HumanResolvedFix,
            "this round dispatched over the operator's own --needs-fixes reason, its own leg " +
            "distinct from the ordinary review-fix leg — so an earlier review-fix leg's own " +
            "spent recovery on this same run never blocks this leg's own attempt");
        run.UncommittedWorkRecoveries.Single().RecoveredCleanly.Should().BeTrue(
            "the scripted recovery session actually committed the stranded file");
        run.State.Should().NotBe(
            RunState.Failed, "the automatic recovery resolved the dirty tree before any gate could fail the run on it — "
                + "the production gap this task closes had this leg failing before its gates instead");
    }

    /// <summary>
    /// Adversarial review finding (cycle 1): a fix session dispatched over a human's own
    /// <c>h9k review resolve --needs-fixes</c> reason reads only that text
    /// (<see cref="DispatchFixSessionAsync"/>'s <c>humanFindings.IsNotBlank()</c> branch) — it
    /// never opens the cycle's merged findings document, so it never sees a ride-along the same
    /// cycle's completed passes attached. Unlike the sibling test above (a fix session dispatched
    /// from an ordinary needs-fixes verdict, which DOES read the merged document), this round
    /// must not settle the ride-along as fixed-unreviewed: nobody ever showed it to anyone.
    /// </summary>
    [Fact]
    public async Task A_ride_along_never_shown_to_a_human_resolved_fix_session_settles_as_a_ride_along()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        const string mixedFindings =
            "FINDING: severity=medium; scope=in-scope; at=A.cs:1\n"
            + "Defect: the criterion is not met.\n\n"
            + "FINDING: severity=low; scope=in-scope; at=Shared.cs:9\n"
            + "Defect: a nit nobody asked for.\n\nVERDICT: needs-fixes";
        ScriptedExecutor executor = new(
            mixedFindings,
            "Nothing of my own.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(
            store, executor, new DaemonOptions { MaxComplianceReviewCycles = 1 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("conformance is still continuing but already at its one-cycle cap");

        const string humanFindings = "The medium finding at A.cs:1 is real; fix it as the reviewer described.";
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.NeedsFixes, humanFindings, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor disputeExecutor = new(
            "That criterion reading is wrong; the code already meets it.\n\nRESOLUTION: disputed");
        mergeReady = await NewEngine(store, disputeExecutor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("the fix session disputed the human's finding");
        disputeExecutor.Spawns.Should().ContainSingle().Which.Prompt
            .Should().Contain(humanFindings, "the dispatch reads the human's own reason")
            .And.NotContain("Shared.cs:9", "the human-findings round never opens the merged " +
                "findings document, so it never shows this fix session the ride-along at all");

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.MergeReady, null, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor resumeExecutor = new();
        mergeReady = await NewEngine(store, resumeExecutor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        resumeExecutor.Spawns.Should().BeEmpty("no further session second-guesses the human");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewResidualsRideAlong.Should().Be(
            1, "the low finding at Shared.cs:9 was never inside anything the human-findings fix " +
                "session read, so it must not settle as fixed-unreviewed");
        run.ReviewResidualsFixed.Should().Be(
            0, "no fix session ever saw this finding — claiming fixed-unreviewed would assert " +
                "one did on no evidence");
    }

    /// <summary>
    /// Decisions Log #163: a fix session refreshes the pull request's own summary
    /// when its fixes changed what a reviewer of the whole change needs to know, and the daemon
    /// takes that block off the same result it already reads the resolution line from.
    /// </summary>
    [Fact]
    public async Task A_fix_result_carrying_a_pr_summary_block_refreshes_the_run_directorys_copy()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "1. `Api.cs:7` — envelope type differs from spec. Scenario: clients break.\n\nVERDICT: needs-fixes",
            "No defects of my own.\n\nVERDICT: merge-ready",
            "Fixed it.\n\nPR SUMMARY:\nTitle: A refreshed title\n\nThe envelope now matches the spec.\n\nRESOLUTION: disputed");
        await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        string artifact = RunPaths.PrSummaryFile(RunPaths.GlobalDirectory(runId));
        File.ReadAllText(artifact).Should().Contain("Title: A refreshed title")
            .And.Contain("The envelope now matches the spec.")
            .And.NotContain("RESOLUTION:", "the resolution line belongs to its own parser, not to the pull request");
    }

    /// <summary>
    /// The other half of the same rule: silence leaves the build session's own summary standing.
    /// Overwriting with nothing would throw away the one description of the change a reviewer has,
    /// on the strength of a fix session having had nothing to add.
    /// </summary>
    [Fact]
    public async Task A_fix_result_with_no_block_leaves_the_build_sessions_summary_alone()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        string artifact = RunPaths.PrSummaryFile(RunPaths.GlobalDirectory(runId));
        Directory.CreateDirectory(RunPaths.GlobalDirectory(runId));
        File.WriteAllText(artifact, "Title: What the build session wrote\n\nIts own body.");

        ScriptedExecutor executor = new(
            "1. `Api.cs:7` — envelope type differs from spec. Scenario: clients break.\n\nVERDICT: needs-fixes",
            "No defects of my own.\n\nVERDICT: merge-ready",
            "That envelope change is the task's stated design.\n\nRESOLUTION: disputed");
        await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        File.ReadAllText(artifact).Should().Be("Title: What the build session wrote\n\nIts own body.");
    }

    [Fact]
    public async Task A_disputed_finding_parks_with_both_positions_recorded()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _, _) = await SeedReviewThreadDisputeParkedRunAsync(store, cts.Token);

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

    /// <summary>
    /// Task: a lap reviews only what it changed. A ReviewFeedback follow-up's own fix session can
    /// dispute a review thread before any Discovery review ever ran (Decisions Log #62, ReviewCycle
    /// still 0). Once a human resolves it with needs-fixes and the fix session actually resolves the
    /// thread, the Discovery cycle that follows is still this run's own OPENING cycle (cycle 1) —
    /// reached through the Reverify branch's own cycle-0 dispute-resume path rather than the ordinary
    /// dispute-free ReviewPhase.None one — so it must seed its diff instruction from the recorded
    /// pull request head exactly the same way. This is the reverify branch's own mirror of the
    /// ReviewPhase.None fix (ReviewEngine.cs's reverifySinceSha), a second gap the same blast-radius
    /// sweep found alongside the first.
    /// </summary>
    [Fact]
    public async Task A_resolved_review_thread_dispute_still_scopes_the_opening_discovery_cycle_to_the_seeded_head()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _, string worktreePath) = await SeedReviewThreadDisputeParkedRunAsync(
            store, cts.Token, seedOpeningReviewSinceSha: true);
        string seedSha = GitOutput(worktreePath, "rev-parse HEAD");

        const string humanResolution = "Reply that this is intentional and resolve the thread.";
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.NeedsFixes, humanResolution, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor executor = new(
            "Replied to the thread with the human's own explanation.\n\nRESOLUTION: fixed",
            "Nothing survived verification.\n\nVERDICT: merge-ready",
            "Nothing survived verification.\n\nVERDICT: merge-ready",
            // Cycle 1's own scoped Discovery converges clean, but that alone must not settle the
            // run: the mandatory FinalFullPass still has to run at full scope (task: a lap reviews
            // only what it changed) — the same guarantee the ReviewPhase.None path's own test covers.
            "Confirmed clean.\n\nVERDICT: merge-ready",
            "Confirmed clean too.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(
            5, "the resumed fix session, this run's own scoped opening Discovery cycle, then the mandatory FinalFullPass");
        executor.Spawns[1].Prompt.Should().Contain(
            $"git diff {seedSha}..HEAD",
            "the opening Discovery cycle reached through the dispute-resume path is scoped exactly "
                + "like the dispute-free ReviewPhase.None path");
        executor.Spawns[2].Prompt.Should().Contain($"git diff {seedSha}..HEAD");
        executor.Spawns[3].Prompt.Should().Contain(
            "git diff origin/main...HEAD",
            "the scoped opening cycle never latched a full-scope boundary, so the mandatory final pass "
                + "reads the whole branch");
        executor.Spawns[4].Prompt.Should().Contain("git diff origin/main...HEAD");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewDispatched>().Where(e => e.Mode == ReviewMode.Discovery)
            .Select(e => e.SinceSha).Should().Equal([seedSha, seedSha]);
    }

    [Fact]
    public async Task A_verdict_less_pass_is_reprompted_once_in_the_same_session_and_may_still_conclude()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "Criteria met.\n\nVERDICT: merge-ready",
            "VERDICT: needs-fixes",
            "On reflection: `Auth.cs:42` — the limiter never resets.\n\nVERDICT: needs-fixes",
            "Reset the limiter.\n\nRESOLUTION: fixed",
            // Cycle 2: only the adversarial track is still active, so one Verify pass stands in for it.
            "Hunted again; the boundary holds.\n\nVERDICT: merge-ready",
            // Cycle 3: the mandatory final full pass, both lenses fresh.
            "Criteria still met.\n\nVERDICT: merge-ready",
            "Still holds.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue("the adversarial lens named a real finding on its one re-prompt");
        executor.Spawns.Should().HaveCount(
            7, "two passes, one re-prompt, one fix, one verify pass over the surviving track, "
                + "and the mandatory final full pass");
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
        DocumentStore store = postgres.Store;
        const string criterion = "Auth.cs:42 no longer drops the token";
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, [criterion], cts.Token);

        ScriptedExecutor executor = new(
            "Criteria met.\n\nVERDICT: merge-ready",
            $"{criterion}.\n\nVERDICT: needs-fixes",
            "Reset the limiter.\n\nRESOLUTION: fixed",
            // Cycle 2: only the adversarial track is still active, so one Verify pass stands in for it.
            "Hunted again; the boundary holds.\n\nVERDICT: merge-ready",
            // Cycle 3: the mandatory final full pass, both lenses fresh.
            "Hunted once more; nothing stands.\n\nVERDICT: merge-ready",
            "The boundary still holds.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue(
            "the adversarial finding survives its own cycle and the fix session clears it");
        executor.Spawns.Should().HaveCount(6, "two passes, one fix, one verify pass over the surviving track, "
            + "and the mandatory final full pass — no re-prompt, because the adversarial pass's finding was never stripped");

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
        DocumentStore store = postgres.Store;
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

    /// <summary>
    /// Independent pre-PR review, cycle 1, adversarial lens: <c>NeedsFullGateBeforeSettling</c>'s
    /// mode/fix-dispatch check alone said nothing about a tip that moved without ever dispatching a
    /// fix session — a cycle-1 Discovery park resolved merge-ready after a same-session worktree
    /// commit reached <c>SettleAsync</c> straight from the Settling phase with no full-scope gate
    /// ever run over the commit about to ship, because <c>FixDispatchedThisCycle</c> stays false
    /// when the park's own verdict was simply unreadable rather than needs-fixes. Seeds the exact
    /// shape <see cref="A_park_resolved_merge_ready_proceeds_straight_to_the_pull_request"/> does
    /// and asserts the mandatory full gate now runs before settling, the same property the
    /// Verify-mode sibling test below checks for that other mode.
    /// </summary>
    [Fact]
    public async Task A_human_merge_ready_after_a_discovery_mode_park_still_runs_the_full_gate_before_settling()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
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
        executor.Spawns.Should().BeEmpty("no further review session second-guesses the human");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<VerificationPassed>().Should().HaveCount(2,
            "the run's own first gate pass, plus the mandatory full-scope gate the Settling phase must still "
                + "run before a human's merge-ready may settle a tip a Discovery-mode cycle-1 park never gated "
                + "at all — no fix session was ever dispatched, so the mode/fix-dispatch check alone missed it");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1 finding: <c>MaySettleReason</c>'s human exemption was written
    /// for "no reviewer needs to read this diff again," not "the suite ran" — but before this
    /// task's own fix, a human's merge-ready resolution on a park that followed a Verify-mode
    /// cycle's own (possibly scoped) gate reached <c>SettleAsync</c> straight from the Settling
    /// phase, skipping the mandatory full-scope gate entirely. A run parked mid-<see
    /// cref="ReviewMode.Verify"/> is exactly that shape: its own gate pass may have been scoped to
    /// the fix's own touched files, and no reviewer or full gate has looked at the whole tree
    /// since. This seeds that shape directly (a Verify-mode cycle 2 that parked on an
    /// unreadable verdict) rather than driving a full cycle through the scripted executor, since
    /// the property under test is the Settling phase's own gate decision, not how the park was
    /// reached.
    /// </summary>
    [Fact]
    public async Task A_human_merge_ready_after_a_verify_mode_park_still_runs_the_full_gate_before_settling()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId,
                new ReviewDispatched(runId, DomainId.New(), 2, 6_001, Now, Now, null, ReviewLens.Verify, ReviewMode.Verify),
                new ReviewPassCompleted(runId, 2, ReviewLens.Verify, ReviewVerdict.Unknown, Now),
                new ReviewCompleted(runId, 2, ReviewVerdict.Unknown, Now),
                new ReviewParked(runId, "No parseable verdict, even after a re-prompt.", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.MergeReady, null, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor executor = new();
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue("the human's verdict stands in for the review");
        executor.Spawns.Should().BeEmpty(
            "the human already looked, or deliberately chose not to; no fresh reviewer second-guesses that");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<VerificationPassed>().Should().HaveCount(2,
            "the run's own first gate pass, plus the mandatory full-scope gate the Settling phase must still run "
                + "before a human's merge-ready may settle a tip a Verify-mode cycle last gated, possibly scoped");
    }

    [Fact]
    public async Task A_park_resolved_needs_fixes_dispatches_a_fix_session_over_the_human_findings()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
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
            // Cycle 2: both tracks are still active (this run's first cycle never concluded a
            // track, since its verdicts were unreadable at the park), so one Verify pass stands
            // in for both.
            "Criteria met, and nothing stands.\n\nVERDICT: merge-ready",
            // Cycle 3: the mandatory final full pass, both lenses fresh.
            "Criteria still met.\n\nVERDICT: merge-ready",
            "Still nothing stands.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(
            4, "fix over the human findings, then one verify pass, then the mandatory final full pass");
        executor.Spawns[0].Prompt.Should().Contain(humanFindings, "the human's reason is the fix session's findings")
            .And.Contain("Human review verdict");
    }

    /// <summary>
    /// Task: review prompts carry prior rulings. The human's needs-fixes resolve above is not
    /// only the fix session's own findings text (the test above) — it also has to reach the
    /// FRESH review passes the fix triggers, since a fresh-context reviewer that never saw the
    /// park would otherwise re-raise the same question the human already settled (origin
    /// incidents: the config.json survival ruling re-litigated across a task's twelve cycles, and
    /// a finding dismissed with evidence re-raised verbatim by the next fresh-context reviewer).
    /// </summary>
    [Fact]
    public async Task A_fresh_review_pass_after_a_human_resolve_is_told_it_as_a_settled_ruling()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
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
            // Cycle 2: both tracks are still active, so one Verify pass stands in for both.
            "Criteria met, and nothing stands.\n\nVERDICT: merge-ready",
            // Cycle 3: the mandatory final full pass, both lenses fresh.
            "Criteria still met.\n\nVERDICT: merge-ready",
            "Still nothing stands.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(
            4, "fix over the human findings, then one verify pass, then the mandatory final full pass");
        executor.Spawns[1].Prompt.Should().Contain("Settled rulings on this task")
            .And.Contain(humanFindings, "the fresh pass sees the human's own resolution, not just the fix session")
            .And.Contain("Cycle 1, resolved", "the ruling names which cycle it was decided at");
        executor.Spawns[2].Prompt.Should().Contain("Settled rulings on this task",
            "the mandatory final full pass is told the settled ruling too, not just the verify pass "
                + "that first re-raised it");
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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

        // Adversarial review, cycle 1 finding (Decisions Log #115): this reverify's own dispatch
        // must resolve as Discovery, not carry a boundary its own prompt (a full base-branch read,
        // per ReviewDispatched.SinceSha's own doc) was never scoped to.
        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewDispatched>().Where(e => e.Cycle == 1).Should().HaveCount(2)
            .And.OnlyContain(e => e.Mode == ReviewMode.Discovery, "no review pass has ever run on this branch")
            .And.OnlyContain(e => e.SinceSha == null, "a Discovery pass always reads the full diff");
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
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRebaseFollowUpRunAsync(store, cts.Token);

        const string conformanceFinding = "1. `Auth.cs:42` — the limiter never resets.";
        ScriptedExecutor executor = new(
            $"{conformanceFinding}\n\nVERDICT: needs-fixes",
            "Nothing of my own.\n\nVERDICT: merge-ready",
            "Reset the limiter.\n\nRESOLUTION: fixed",
            // Cycle 2: only the conformance track is still active, so one Verify pass stands in for it.
            "Criteria met.\n\nVERDICT: merge-ready",
            // Cycle 3: the mandatory final full pass, both lenses fresh.
            "Criteria still met.\n\nVERDICT: merge-ready",
            "Still nothing of my own.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(
            6, "two passes, one fix, one verify pass over the surviving track, and the mandatory final full pass");
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
        DocumentStore store = postgres.Store;
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
            // Cycle 2: only the conformance track is still active, so one Verify pass stands in for it.
            "Criteria met.\n\nVERDICT: merge-ready",
            // Cycle 3: the mandatory final full pass, both lenses fresh.
            "Criteria still met.\n\nVERDICT: merge-ready",
            "Still nothing of my own.\n\nVERDICT: merge-ready");
        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(
            7, "the resumed rebase, two passes, one ordinary fix, one verify pass over the "
                + "surviving track, and the mandatory final full pass");
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
        DocumentStore store = postgres.Store;
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
            // Cycle 2: only the conformance track is still active, so one Verify pass stands in for it.
            "Criteria met.\n\nVERDICT: merge-ready",
            // Cycle 3: the mandatory final full pass, both lenses fresh.
            "Criteria still met.\n\nVERDICT: merge-ready",
            "Still nothing of my own.\n\nVERDICT: merge-ready");

        bool mergeReady = await NewEngine(store, executor, options).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Select(spawn => spawn.Model.Value).Should().Equal(
            ["sonnet", "sonnet", "haiku", "sonnet", "sonnet", "sonnet"],
            "each leg resolves the chain for its own role");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewDispatched>().Select(e => e.Model!.Value).Should().Equal(
            ["sonnet", "sonnet", "sonnet", "sonnet", "sonnet"], "every pass of every cycle records its model");
        events.OfType<ReviewFixDispatched>().Select(e => e.Model!.Value).Should().Equal(["haiku"]);

        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewModel.Should().Be(AgentModel.Sonnet, "the projection shows the latest review leg's model");
    }

    /// <summary>
    /// A Verify pass resolves the configured verify-review model (Brian's ruling, 2026-08-29) while
    /// Discovery and FinalFullPass keep resolving the plain Review model — and a fix round
    /// escalated by a repeat finding still resolves the plain Review model too, never the Verify
    /// knob, even when the repeated finding was itself reported by a Verify pass. Escalation
    /// (Decisions Log #90) compares the Review and Fix roles exactly as before; the Verify knob is
    /// a pass-shape override, not a participant in that comparison.
    /// </summary>
    [Fact]
    public async Task A_verify_pass_resolves_its_own_knob_while_discovery_finalfullpass_and_escalation_stay_on_review()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        DaemonOptions options = new()
        {
            DefaultModel = "claude-opus-5",
            ModelByRole = new RoleModelDefaults { Review = "sonnet", ReviewVerify = "fable", Fix = "haiku" },
            MaxComplianceReviewCycles = 10,
        };
        ScriptedExecutor executor = new(
            // Cycle 1 (Discovery, both lenses): conformance finds a defect; adversarial goes dormant.
            "FINDING: severity=high; scope=in-scope; at=src/Auth.cs:42\n"
                + "Defect: the limiter never resets.\nScenario: the second request always 429s.\n\n"
                + "VERDICT: needs-fixes",
            "Nothing of my own.\n\nVERDICT: merge-ready",
            // Fix round 1 over src/Auth.cs:42 — the first round, nothing to repeat yet.
            "Reset the limiter.\n\nRESOLUTION: fixed",
            // Cycle 2 (Verify, standing in for the surviving conformance track alone): the SAME location again.
            "FINDING: severity=high; scope=in-scope; at=src/Auth.cs:42\n"
                + "Defect: still never resets.\nScenario: still 429s.\n\nVERDICT: needs-fixes",
            // Fix round 2 — a repeat of round 1's own location, dispatched over a Verify pass's own finding.
            "Reset it for real this time.\n\nRESOLUTION: fixed",
            // Cycle 3 (Verify again): reads clean.
            "Fixed for real.\n\nVERDICT: merge-ready",
            // Cycle 4: the mandatory final full pass, both lenses fresh.
            "Still clean.\n\nVERDICT: merge-ready",
            "Still nothing of my own.\n\nVERDICT: merge-ready");

        bool mergeReady = await NewEngine(store, executor, options).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Select(spawn => spawn.Model.Value).Should().Equal(
            ["sonnet", "sonnet", "haiku", "fable", "sonnet", "fable", "sonnet", "sonnet"],
            "Discovery (indices 0-1) and the FinalFullPass (indices 6-7) resolve Review's plain model; "
                + "the Verify passes (indices 3 and 5) resolve the separate ReviewVerify knob; the first "
                + "fix round (index 2) resolves the ordinary Fix model; and the second fix round (index 4), "
                + "escalated by the Verify pass's own repeat finding, resolves Review's model rather than "
                + "either the Fix model it would otherwise have run on or the Verify pass's own ReviewVerify model");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewDispatched>().Where(e => e.Mode == ReviewMode.Verify).Select(e => e.Model!.Value)
            .Should().OnlyContain(model => model == "fable", "every recorded Verify pass carries the ReviewVerify model");
        events.OfType<ReviewDispatched>().Where(e => e.Mode != ReviewMode.Verify).Select(e => e.Model!.Value)
            .Should().OnlyContain(model => model == "sonnet", "Discovery and FinalFullPass keep recording the plain Review model");

        List<ReviewFixDispatched> fixDispatches = [.. events.OfType<ReviewFixDispatched>()];
        fixDispatches.Should().HaveCount(2);
        fixDispatches[0].Escalated.Should().BeFalse();
        fixDispatches[1].Escalated.Should().BeTrue(
            "the second fix round repeats the first round's own location, exactly as Decisions Log #90 already escalates");
        fixDispatches[1].EscalationReason.Should().NotBeNull().And.Contain("src/Auth.cs:42");
    }

    /// <summary>
    /// The mandatory FinalFullPass resolves the configured finalpass-review model (task: completing
    /// the per-stage model set #105 started for Verify) while Discovery and the confirming Verify
    /// passes keep resolving the plain Review model — and a fix round escalated by a repeat finding
    /// still resolves the plain Review model too, never the FinalFullPass knob, even when the round
    /// it repeats was itself dispatched from a FinalFullPass finding. Escalation (Decisions Log #90)
    /// compares the Review and Fix roles exactly as before; the FinalFullPass knob is a pass-shape
    /// override, not a participant in that comparison — the identical carve-out #105 already
    /// established for Verify.
    /// </summary>
    [Fact]
    public async Task A_finalfullpass_resolves_its_own_knob_while_discovery_verify_and_escalation_stay_on_review()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        DaemonOptions options = new()
        {
            DefaultModel = "claude-opus-5",
            ModelByRole = new RoleModelDefaults { Review = "sonnet", ReviewFinalFullPass = "fable", Fix = "haiku" },
            MaxComplianceReviewCycles = 10,
        };
        ScriptedExecutor executor = new(
            // Cycle 1 (Discovery, both lenses): conformance finds a defect; adversarial goes dormant.
            // A run that converged clean at cycle 1 would pay no FinalFullPass at all (task: "a run
            // that converges clean at cycle 1 pays no extra pass"), so this must actually need a fix
            // to ever reach the mandatory final pass this test exists to exercise.
            "FINDING: severity=high; scope=in-scope; at=Retry.cs:31\n"
                + "Defect: the retry duplicates the effect.\nScenario: every retry doubles up.\n\n"
                + "VERDICT: needs-fixes",
            "Nothing of my own.\n\nVERDICT: merge-ready",
            // Fix round 1 over Retry.cs:31 — the first round, nothing to repeat yet.
            "Tightened the retry guard.\n\nRESOLUTION: fixed",
            // Cycle 2: one Verify pass over the surviving conformance track alone — the SAME
            // location again, still broken.
            "FINDING: severity=high; scope=in-scope; at=Retry.cs:31\n"
                + "Defect: still duplicates.\nScenario: still broken.\n\nVERDICT: needs-fixes",
            // Fix round 2 — a repeat of round 1's own location.
            "Fixed it for real this time.\n\nRESOLUTION: fixed",
            // Cycle 3: the confirming Verify pass for round 2's fix — clean, so ActiveReviewLenses
            // empties out and a Verify-mode cycle can never settle on its own, forcing the loop into
            // the mandatory final pass.
            "Confirmed fixed.\n\nVERDICT: merge-ready",
            // Cycle 4: the mandatory final pass, both lenses fresh — both clean.
            "Still clean.\n\nVERDICT: merge-ready",
            "Still clean too.\n\nVERDICT: merge-ready");

        bool mergeReady = await NewEngine(store, executor, options).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Select(spawn => spawn.Model.Value).Should().Equal(
            ["sonnet", "sonnet", "haiku", "sonnet", "sonnet", "sonnet", "fable", "fable"],
            "Discovery (indices 0-1) resolves Review's plain model; the first fix round (index 2) "
                + "resolves the ordinary Fix model; the confirming Verify passes (indices 3 and 5) fall "
                + "through to Review's plain model since ReviewVerify is unset here; the second fix round "
                + "(index 4), escalated by the repeat finding, resolves Review's model rather than either "
                + "the Fix model it would otherwise have run on or the ReviewFinalFullPass model; and the "
                + "mandatory final pass (indices 6-7) resolves the separate ReviewFinalFullPass knob");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewDispatched>().Where(e => e.Mode == ReviewMode.FinalFullPass).Select(e => e.Model!.Value)
            .Should().OnlyContain(
                model => model == "fable", "every recorded mandatory final pass carries the ReviewFinalFullPass model");
        events.OfType<ReviewDispatched>().Where(e => e.Mode != ReviewMode.FinalFullPass).Select(e => e.Model!.Value)
            .Should().OnlyContain(model => model == "sonnet", "Discovery and Verify keep recording the plain Review model");

        List<ReviewFixDispatched> fixDispatches = [.. events.OfType<ReviewFixDispatched>()];
        fixDispatches.Should().HaveCount(2);
        fixDispatches[0].Escalated.Should().BeFalse();
        fixDispatches[1].Escalated.Should().BeTrue(
            "the second fix round repeats the first round's own location, exactly as Decisions Log #90 already escalates");
        fixDispatches[1].EscalationReason.Should().NotBeNull().And.Contain("Retry.cs:31");
    }

    /// <summary>
    /// A second fix round dispatched over the same finding location the previous fix round was
    /// already given escalates to the review role's model (task: a second fix round over the same
    /// findings, origin: task 60 generation 2's Sonnet fix session dodged a flaky-test race by
    /// restructuring the test instead of fixing it). The first round over that same defect never
    /// escalates — there is no previous round yet — and once a later round moves on to a genuinely
    /// different defect, de-escalation is automatic: nothing resets it by hand.
    /// </summary>
    [Fact]
    public async Task A_second_fix_round_over_the_same_finding_escalates_and_a_fresh_defect_de_escalates()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        DaemonOptions options = new()
        {
            DefaultModel = "claude-opus-5",
            ModelByRole = new RoleModelDefaults { Review = "sonnet", Fix = "haiku" },
            // High enough that this scenario's four conformance cycles never hit the cap — the
            // point here is the escalation trigger, not the cap.
            MaxComplianceReviewCycles = 10,
        };
        ScriptedExecutor executor = new(
            // Cycle 1: conformance finds a defect; adversarial is clean and goes dormant.
            "FINDING: severity=high; scope=in-scope; at=src/Auth.cs:42\n"
                + "Defect: the limiter never resets.\nScenario: the second request always 429s.\n\n"
                + "VERDICT: needs-fixes",
            "Nothing of my own.\n\nVERDICT: merge-ready",
            // Fix round 1 over src/Auth.cs:42 — the first round, nothing to repeat yet.
            "Reset the limiter.\n\nRESOLUTION: fixed",
            // Cycle 2: conformance (alone now) finds the SAME location again.
            "FINDING: severity=high; scope=in-scope; at=src/Auth.cs:42\n"
                + "Defect: still never resets.\nScenario: still 429s.\n\nVERDICT: needs-fixes",
            // Fix round 2 over src/Auth.cs:42 again — a repeat of round 1's own finding.
            "Reset it for real this time.\n\nRESOLUTION: fixed",
            // Cycle 3: conformance finds a genuinely different defect.
            "FINDING: severity=high; scope=in-scope; at=src/Other.cs:99\n"
                + "Defect: a descriptor leaks.\nScenario: descriptors pile up.\n\nVERDICT: needs-fixes",
            // Fix round 3 over src/Other.cs:99 — fresh, not a repeat of round 2's location.
            "Closed the descriptor.\n\nRESOLUTION: fixed",
            // Cycle 4 (verify, standing in for the surviving conformance track alone) reads clean.
            "Fixed for real.\n\nVERDICT: merge-ready",
            // Cycle 5: the mandatory final full pass, both lenses fresh.
            "Still clean.\n\nVERDICT: merge-ready",
            "Still nothing of my own.\n\nVERDICT: merge-ready");

        bool mergeReady = await NewEngine(store, executor, options).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Select(spawn => spawn.Model.Value).Should().Equal(
            ["sonnet", "sonnet", "haiku", "sonnet", "sonnet", "sonnet", "haiku", "sonnet", "sonnet", "sonnet"],
            "fix round 1 (index 2) is a first round, fix round 2 (index 4) repeats round 1's own "
                + "location and escalates, and fix round 3 (index 6) moves to a fresh defect and "
                + "de-escalates automatically — cycles 4 and 5's own passes are ordinary review work "
                + "and always resolve the review role");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        List<ReviewFixDispatched> fixDispatches = [.. events.OfType<ReviewFixDispatched>()];
        fixDispatches.Should().HaveCount(3);
        fixDispatches.Select(e => e.Escalated).Should().Equal([false, true, false]);
        fixDispatches[0].EscalationReason.Should().BeNull();
        fixDispatches[1].EscalationReason.Should().NotBeNull().And.Contain("src/Auth.cs:42");
        fixDispatches[2].EscalationReason.Should().BeNull();

        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.LastFixSessionEscalated.Should().BeFalse(
            "the run's last fix round was the de-escalated one over the fresh defect");
        run.LastFixSessionEscalationReason.Should().BeNull();
    }

    /// <summary>
    /// The default install resolves every role to the same model, and there escalation is a
    /// claim with nothing behind it: a repeat round whose review and fix roles resolve
    /// identically records Escalated: false and spawns the fix role's model as any round would,
    /// because telling the human the dodge-and-redo mitigation applied when the session ran on
    /// the model it would have run on anyway is a false record (the model-equality gate,
    /// otherwise untested — independent pre-PR review of the PR #53 follow-up, cycle 3).
    /// </summary>
    [Fact]
    public async Task A_repeat_round_with_identical_role_models_records_no_escalation()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        DaemonOptions options = new()
        {
            // No ModelByRole: every role falls through to the default, the shape a fresh
            // install runs with.
            DefaultModel = "claude-opus-5",
        };
        ScriptedExecutor executor = new(
            "FINDING: severity=high; scope=in-scope; at=src/Auth.cs:42\n"
                + "Defect: the limiter never resets.\nScenario: the second request always 429s.\n\n"
                + "VERDICT: needs-fixes",
            "Nothing of my own.\n\nVERDICT: merge-ready",
            "Reset the limiter.\n\nRESOLUTION: fixed",
            "FINDING: severity=high; scope=in-scope; at=src/Auth.cs:42\n"
                + "Defect: still never resets.\nScenario: still 429s.\n\nVERDICT: needs-fixes",
            "Reset it for real this time.\n\nRESOLUTION: fixed",
            "Fixed for real.\n\nVERDICT: merge-ready",
            // The mandatory final full pass, both lenses fresh.
            "Still clean.\n\nVERDICT: merge-ready",
            "Still nothing of my own.\n\nVERDICT: merge-ready");

        bool mergeReady = await NewEngine(store, executor, options).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Select(spawn => spawn.Model.Value).Should().OnlyContain(
            model => model == "claude-opus-5",
            "with no per-role binding every leg resolves to the default model");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        List<ReviewFixDispatched> fixDispatches = [.. events.OfType<ReviewFixDispatched>()];
        fixDispatches.Should().HaveCount(2);
        fixDispatches.Select(e => e.Escalated).Should().Equal([false, false],
            "a repeat round is not an escalation when there is no different model to escalate to");
        fixDispatches[1].EscalationReason.Should().BeNull();
    }

    /// <summary>
    /// A human's own needs-fixes verdict, resolving a dispute at the same cycle the disputed
    /// finding was found on, is a genuinely new round — not the mechanical redispatch a budget
    /// retry would be — and gets its own fresh escalation check rather than inheriting the
    /// disputed round's (non-escalated) decision. It escalates here because the redispatch is
    /// still over the very location the first round was already given.
    /// </summary>
    [Fact]
    public async Task A_human_resolving_a_dispute_with_needs_fixes_over_the_same_location_escalates_the_redispatch()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        DaemonOptions options = new()
        {
            DefaultModel = "claude-opus-5",
            ModelByRole = new RoleModelDefaults { Review = "sonnet", Fix = "haiku" },
        };
        ScriptedExecutor executor = new(
            "FINDING: severity=high; scope=in-scope; at=src/Api.cs:7\n"
                + "Defect: envelope type differs from spec.\nScenario: clients break.\n\nVERDICT: needs-fixes",
            "No defects of my own.\n\nVERDICT: merge-ready",
            // Fix round 1 disputes rather than fixing — the first round, so no escalation applies.
            "That envelope change is the task's stated design; changing it back is a scope decision.\n\n"
                + "RESOLUTION: disputed",
            // Fix round 2 — the human's redispatch, still over src/Api.cs:7.
            "Fixed it as originally reported.\n\nRESOLUTION: fixed",
            // Cycle 2 (verify, standing in for the surviving conformance track alone) reads clean.
            "Fixed for real.\n\nVERDICT: merge-ready",
            // Cycle 3: the mandatory final full pass, both lenses fresh.
            "Still clean.\n\nVERDICT: merge-ready",
            "Still nothing of my own.\n\nVERDICT: merge-ready");

        bool disputedPass = await NewEngine(store, executor, options).ReviewAsync(runId, taskId, cts.Token);
        disputedPass.Should().BeFalse("the disputed finding parks for the human");

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.NeedsFixes,
                "Still a real bug in src/Api.cs:7 — fix it as originally reported.", Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        bool mergeReady = await NewEngine(store, executor, options).ReviewAsync(runId, taskId, cts.Token);
        mergeReady.Should().BeTrue();

        executor.Spawns.Select(spawn => spawn.Model.Value).Should().Equal(
            ["sonnet", "sonnet", "haiku", "sonnet", "sonnet", "sonnet", "sonnet"],
            "the disputed round (index 2) never escalates, but the human's own redispatch (index 3) "
                + "is a fresh round over the same location and escalates — cycles 2 and 3's own "
                + "passes are ordinary review work and always resolve the review role");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        List<ReviewFixDispatched> fixDispatches = [.. events.OfType<ReviewFixDispatched>()];
        fixDispatches.Should().HaveCount(2);
        fixDispatches.Select(e => e.Escalated).Should().Equal([false, true]);
        fixDispatches[1].EscalationReason.Should().NotBeNull().And.Contain("src/Api.cs:7");

        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.LastFixSessionEscalated.Should().BeTrue("the last fix round dispatched was the escalated one");
    }

    /// <summary>
    /// A human resolving a dispute with a needs-fixes reason that redirects the work to a
    /// genuinely different concern must not escalate, even though the dispute round's own
    /// (disputed) location is still sitting in <c>CurrentCycleFixFindingLocations</c> — the dispute
    /// resolution never starts a new review cycle, so that automated set is frozen to whatever the
    /// disputed round was itself dispatched over and is not what this round is dispatched over. The
    /// human-restatement scan, not that stale automated set, is the only signal that may fire here.
    /// </summary>
    [Fact]
    public async Task A_human_resolving_a_dispute_with_a_genuinely_different_reason_does_not_escalate()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        DaemonOptions options = new()
        {
            DefaultModel = "claude-opus-5",
            ModelByRole = new RoleModelDefaults { Review = "sonnet", Fix = "haiku" },
        };
        ScriptedExecutor executor = new(
            "FINDING: severity=high; scope=in-scope; at=src/Auth.cs:42\n"
                + "Defect: envelope type differs from spec.\nScenario: clients break.\n\nVERDICT: needs-fixes",
            "No defects of my own.\n\nVERDICT: merge-ready",
            // Fix round 1 disputes rather than fixing — the first round, so no escalation applies.
            "That envelope change is the task's stated design; changing it back is a scope decision.\n\n"
                + "RESOLUTION: disputed",
            // Fix round 2 — the human's redispatch, over a different concern entirely.
            "Added the missing cancellation token.\n\nRESOLUTION: fixed",
            // Cycle 2 (verify, standing in for the surviving conformance track alone) reads clean.
            "Fixed for real.\n\nVERDICT: merge-ready",
            // Cycle 3: the mandatory final full pass, both lenses fresh.
            "Still clean.\n\nVERDICT: merge-ready",
            "Still nothing of my own.\n\nVERDICT: merge-ready");

        bool disputedPass = await NewEngine(store, executor, options).ReviewAsync(runId, taskId, cts.Token);
        disputedPass.Should().BeFalse("the disputed finding parks for the human");

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewParkResolved(
                runId, ReviewVerdict.NeedsFixes,
                "The Auth.cs finding is wrong — leave it. The real gap is that the new helper at "
                    + "src/Retry.cs:10 takes no CancellationToken.",
                Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        bool mergeReady = await NewEngine(store, executor, options).ReviewAsync(runId, taskId, cts.Token);
        mergeReady.Should().BeTrue();

        executor.Spawns.Select(spawn => spawn.Model.Value).Should().Equal(
            ["sonnet", "sonnet", "haiku", "haiku", "sonnet", "sonnet", "sonnet"],
            "the human redirected the work to a fresh concern, so the redispatch (index 3) must not "
                + "escalate even though the disputed round's own location is still in "
                + "CurrentCycleFixFindingLocations — cycles 2 and 3's own passes are ordinary review "
                + "work and always resolve the review role");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        List<ReviewFixDispatched> fixDispatches = [.. events.OfType<ReviewFixDispatched>()];
        fixDispatches.Should().HaveCount(2);
        fixDispatches.Select(e => e.Escalated).Should().Equal([false, false]);
        fixDispatches[1].EscalationReason.Should().BeNull();

        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.LastFixSessionEscalated.Should().BeFalse();
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
    /// The core case (task: a session that reports an error result is retried once in place,
    /// measured 2026-09-05: bursty across only 18 distinct hours, the shape of a provider-side
    /// burst): the conformance pass reports a generic error while the adversarial pass reads
    /// clean at the same time. The errored lens is redispatched fresh after a short backoff and
    /// the sibling is never touched on that account — its verdict is kept exactly as if nothing
    /// had happened.
    /// </summary>
    [Fact]
    public async Task A_review_session_reporting_an_error_result_is_retried_once_leaving_its_sibling_untouched()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "Hit a transient snag.",
            "Nothing of my own.\n\nVERDICT: merge-ready",
            "Clean on the retry.\n\nVERDICT: merge-ready")
        {
            ErrorAtSpawnIndex = { 0 },
        };
        DaemonOptions options = new() { SessionErrorRetryBackoff = TimeSpan.FromMilliseconds(1) };
        bool mergeReady = await NewEngine(store, executor, options).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue("the transient error is retried once, not failed");
        executor.Spawns.Should().HaveCount(3, "conformance's error costs one extra spawn; adversarial dispatches once");

        // A completed session's process tree is torn down once SessionResultWaiter confirms the
        // root itself has exited (discovery cc9b7aec) — every session here is a synchronous,
        // already-completed scripted spawn that ScriptedExecutor's own doc says is never observed
        // alive, so IProcessManager.TerminateTree correctly finds nothing left to clean up for any
        // of the three (its own documented contract: empty once the root is already gone). What
        // this test actually guards survives on the assertions below instead: pid 6001, the
        // sibling adversarial pass, keeps its own clean verdict untouched by the other lens's
        // error, and only one retry event is ever recorded for the errored conformance lens.
        executor.Processes.Terminations.Should().BeEmpty(
            "none of these scripted sessions are ever observed alive for TerminateTree to find");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        RunSessionErrorRetried retry = events.OfType<RunSessionErrorRetried>().Single();
        retry.Leg.Should().Be(RunSessionLeg.ReviewPass);
        retry.Lens.Should().Be(ReviewLens.Conformance);
        retry.Cycle.Should().Be(1);
        events.OfType<TokensRecorded>().Should().HaveCount(
            3, "the errored pass's own tokens are recorded before the retry, exactly like the two clean passes' own — "
            + "a full diff read that ends in an error is not simply dropped from the run's totals");

        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.SessionErrorRetries.Should().ContainSingle();
    }

    /// <summary>
    /// The shape that used to fall through entirely (independent pre-PR review, cycle 1, both
    /// lenses): an error result whose "result" field is missing (or non-string) parses to a
    /// null Summary, which the old "IsError: true, Summary: { }" pattern match did not treat as
    /// an error at all — it fell straight through into RecordReviewPassAsync as though the
    /// session had actually produced a verdict, parsed to ReviewVerdict.Unknown, and burned the
    /// cycle's re-prompt on a session that never said anything. A null-summary error must be
    /// retried exactly like one that carries a message.
    /// </summary>
    [Fact]
    public async Task A_review_session_erroring_with_no_result_message_is_retried_exactly_like_one_that_has_one()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "unused — overridden to a null result by NullSummaryErrorAtSpawnIndex",
            "Nothing of my own.\n\nVERDICT: merge-ready",
            "Clean on the retry.\n\nVERDICT: merge-ready")
        {
            NullSummaryErrorAtSpawnIndex = { 0 },
        };
        DaemonOptions options = new() { SessionErrorRetryBackoff = TimeSpan.FromMilliseconds(1) };
        bool mergeReady = await NewEngine(store, executor, options).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue("the null-summary error is retried once, not read as a real (empty) verdict");
        executor.Spawns.Should().HaveCount(3, "conformance's error costs one extra spawn; adversarial dispatches once");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        RunSessionErrorRetried retry = events.OfType<RunSessionErrorRetried>().Single();
        retry.Leg.Should().Be(RunSessionLeg.ReviewPass);
        retry.Lens.Should().Be(ReviewLens.Conformance);
        retry.ObservedMessage.Should().Be(
            "(no message)", "the observed error carried no text, and that absence is recorded honestly rather than guessed");
        events.OfType<ReviewVerdictReprompted>().Should().BeEmpty(
            "the null-summary error must never be mistaken for an empty verdict worth re-prompting");
    }

    /// <summary>
    /// The residue this task exists to narrow the failures down to: a SECOND consecutive error
    /// on the identical lens/cycle spends the one retry and fails the run exactly as before —
    /// same reason text as a genuinely broken session always got.
    /// </summary>
    [Fact]
    public async Task A_second_consecutive_error_on_the_same_lens_and_cycle_fails_the_run()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "First transient-looking error.",
            "Nothing of my own.\n\nVERDICT: merge-ready",
            "Second error — not transient after all.")
        {
            ErrorAtSpawnIndex = { 0, 2 },
        };
        DaemonOptions options = new() { SessionErrorRetryBackoff = TimeSpan.FromMilliseconds(1) };
        bool mergeReady = await NewEngine(store, executor, options).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("a second consecutive error on the same lens/cycle fails the run");
        executor.Spawns.Should().HaveCount(3, "one retry is spent; the second error ends the run rather than a third spawn");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<RunSessionErrorRetried>().Should().ContainSingle(
            "only the first error earns a retry; the run fails outright on the second");

        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.Failed);
        run.FailureReason.Should().Contain("reported an error result").And.Contain(
            ReviewLens.Conformance.Slug, "the failure says which pass it was, exactly as an ordinary failure already does");
        (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Should().Be(TaskState.Failed);
    }

    /// <summary>
    /// The fix-session leg of the same recovery: an error mid-fix redispatches fresh over the
    /// same cycle's findings, the identical redispatch shape <see cref="RunBudgetExhausted"/>'s
    /// own AwaitingFix clearing already relies on — but the run never parks, it just retries in
    /// place after a short backoff.
    /// </summary>
    [Fact]
    public async Task A_fix_session_reporting_an_error_result_is_retried_once_over_the_same_findings()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            "1. `Auth.cs:42` — the limiter never resets.\n\nVERDICT: needs-fixes",
            "Nothing survived verification.\n\nVERDICT: merge-ready",
            "Hit a transient snag applying the fix.",
            "Reset the limiter window.\n\nRESOLUTION: fixed",
            // Cycle 2: only conformance is still active, so it gets one Verify pass.
            "Criteria met.\n\nVERDICT: merge-ready",
            // Cycle 3: the mandatory final full pass, both lenses fresh.
            "Confirmed clean.\n\nVERDICT: merge-ready",
            "Confirmed clean too.\n\nVERDICT: merge-ready")
        {
            ErrorAtSpawnIndex = { 2 },
        };
        DaemonOptions options = new() { SessionErrorRetryBackoff = TimeSpan.FromMilliseconds(1) };
        bool mergeReady = await NewEngine(store, executor, options).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue("the fix session's transient error is retried once, not failed");
        executor.Spawns.Should().HaveCount(7, "the fix session's own error costs one extra spawn");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        RunSessionErrorRetried retry = events.OfType<RunSessionErrorRetried>().Single();
        retry.Leg.Should().Be(RunSessionLeg.Fix);
        retry.Cycle.Should().Be(1);
        events.OfType<ReviewFixDispatched>().Should().HaveCount(2, "the errored attempt plus its retry");
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
        DocumentStore store = postgres.Store;
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
        // A completed review pass's process tree is only torn down once SessionResultWaiter
        // confirms the root has exited (discovery cc9b7aec); pids 6000 and 6001 are synchronous,
        // already-completed scripted spawns that ScriptedExecutor's own doc says are never
        // observed alive, so IProcessManager.TerminateTree correctly finds nothing left to clean
        // up for either of them (its own documented contract). What this test actually guards
        // survives untouched: TerminateInFlightSessionsAsync's own crash-sweep call is a plain,
        // unconditional processManager.Terminate — distinct from TerminateTree, and never gated
        // on having observed the pid alive — so it still reaches the fix session specifically,
        // pid 6002, because the stream still shows it in flight when the crash lands, proving the
        // sweep covers the fix phase and not only review passes.
        executor.Processes.Terminations.Should().ContainSingle(
            termination => termination.ProcessId == 6_002,
            "the crash sweep terminates the fix session that was still in flight when the loop crashed");

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
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
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
            new VerificationRunner(
                store, Options.Create(new DaemonOptions()), NullLogger<VerificationRunner>.Instance,
                new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance), executor, executor.Processes),
            Options.Create(new DaemonOptions()), logger,
            new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance), RecordingProcessRunner.NeverInvoked(),
            NewStackedParentWatch());

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
        DocumentStore store = postgres.Store;

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
            new VerificationRunner(
                store, Options.Create(new DaemonOptions()), NullLogger<VerificationRunner>.Instance,
                new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance), executor, executor.Processes),
            Options.Create(new DaemonOptions()), logger,
            new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance), RecordingProcessRunner.NeverInvoked(),
            NewStackedParentWatch());

        await engine.ParkAsync(staleRunId, taskId, "No parseable verdict.", cancellationToken: cts.Token);

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


    private static ReviewEngine NewEngine(DocumentStore store, ScriptedExecutor executor) =>
        NewEngine(store, executor, new DaemonOptions());

    private static ReviewEngine NewEngine(DocumentStore store, ScriptedExecutor executor, DaemonOptions options) =>
        NewEngine(store, executor, options, RecordingProcessRunner.NeverInvoked());

    /// <summary>
    /// <paramref name="ghRunner"/> answers the pre-final-pass rebase's own retargeted-base check
    /// (task: a run rebases its branch onto the current base branch) — never gh itself, since no
    /// test here has a real pull request to ask. Every other test passes no task PullRequestUrl at
    /// all, so <see cref="RecordingProcessRunner.NeverInvoked"/> is the right default: if a future
    /// edit makes that check run unexpectedly, the test fails loudly here instead of quietly
    /// shelling out to the real gh CLI and the real network.
    /// </summary>
    private static ReviewEngine NewEngine(
        DocumentStore store, ScriptedExecutor executor, DaemonOptions options, ProcessRunner ghRunner) =>
        new(store, executor, executor.Processes,
            new VerificationRunner(
                store, Options.Create(new DaemonOptions()), NullLogger<VerificationRunner>.Instance,
                new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance), executor, executor.Processes),
            Options.Create(options),
            NullLogger<ReviewEngine>.Instance,
            new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance),
            ghRunner,
            NewStackedParentWatch());

    /// <summary>
    /// The real watch, never a fake: a stacked child's rebase checkpoints read it (task: a stacked
    /// child absorbs its parent's post-delivery churn safely), and it answers from git and the
    /// store — the same two things every other real-repository test here already seeds. An
    /// unstacked run never reaches it at all, which is what keeps every test in this file that
    /// records no base branch byte-for-byte unaffected by its presence.
    /// </summary>
    private static StackedParentWatch NewStackedParentWatch() => new(
        new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance),
        NullLogger<StackedParentWatch>.Instance);

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

    private Task<(Guid TaskId, Guid RunId, Guid MainSessionId)> SeedVerifiedRunAsync(
        DocumentStore store, IReadOnlyList<string> acceptanceCriteria, CancellationToken cancellationToken) =>
        SeedVerifiedRunAsync(store, acceptanceCriteria, cancellationToken, reviewStageComposition: null);

    /// <summary>
    /// <paramref name="reviewStageComposition"/> mirrors what RunLauncher itself resolves and
    /// records at dispatch (task: the review pipeline's stage composition becomes configuration
    /// recorded per run) — null omits it from RunDispatched entirely, the unchanged-defaults case
    /// every other test in this file exercises.
    /// </summary>
    private async Task<(Guid TaskId, Guid RunId, Guid MainSessionId)> SeedVerifiedRunAsync(
        DocumentStore store, IReadOnlyList<string> acceptanceCriteria, CancellationToken cancellationToken,
        ReviewStageComposition? reviewStageComposition)
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cancellationToken);

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
                worktreePath, "task/review-me", ExecutorMode.Subscription, Now,
                ReviewStageComposition: reviewStageComposition),
            new AgentSessionCompleted(runId, Now),
            new VerificationPassed(runId, Now));
        await session.SaveChangesAsync(cancellationToken);

        return (taskId, runId, mainSessionId);
    }

    /// <summary>
    /// Like <see cref="SeedVerifiedRunAsync(DocumentStore, CancellationToken)"/>, but reuses an
    /// already-registered project and node instead of minting a new one of each — for a test
    /// that needs two runs sharing one project, the way two different pull requests against the
    /// same repository would.
    /// </summary>
    private async Task<(Guid TaskId, Guid RunId, Guid MainSessionId)> SeedVerifiedRunInProjectAsync(
        DocumentStore store, Guid projectId, NodeContext node, string worktreePath, CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid mainSessionId = DomainId.New();

        await using IDocumentSession session = store.LightweightSession();

        TaskAggregate task = new();
        (task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(taskId, projectId, "Review me before another PR", ["reviewed"],
                TaskType.Chore, null, null, null, Now, node.OwnerId),
            node.OwnerId, Now);
        var claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now);
        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
        session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = Now });

        session.Events.StartStream<RunAggregate>(runId,
            new RunDispatched(runId, taskId, node.NodeId, node.OwnerId, 1, mainSessionId,
                worktreePath, "task/review-me-too", ExecutorMode.Subscription, Now),
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
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cancellationToken);

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
        DocumentStore store, CancellationToken cancellationToken, ReviewStageComposition? composition = null)
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cancellationToken);

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
                worktreePath, "task/review-me", ExecutorMode.Subscription, Now, IsFollowUp: true,
                ReviewStageComposition: composition),
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
    private async Task<(Guid TaskId, Guid RunId, Guid MainSessionId, string WorktreePath)> SeedReviewThreadDisputeParkedRunAsync(
        DocumentStore store, CancellationToken cancellationToken, bool seedOpeningReviewSinceSha = false)
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cancellationToken);

        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid mainSessionId = DomainId.New();
        string worktreePath = Path.Combine(_home, $"wt-{runId:N}");
        Directory.CreateDirectory(worktreePath);
        Git(worktreePath, "init -q -b main");
        File.WriteAllText(Path.Combine(worktreePath, "base.txt"), "base\n");
        Git(worktreePath, "add -A");
        Git(worktreePath, "-c user.name=Test -c user.email=test@test commit -q -m init");
        Git(worktreePath, "checkout -q -b task/review-me");
        // One real commit ahead of main, exactly like SeedVerifiedRunWithTestGateAsync's own
        // "widget" commit: VerificationRunner's no-commit pre-gate check (a branch with nothing
        // beyond its base fails before any gate runs) would otherwise fire on the fix session's own
        // reverify gate, since a scripted fix response never actually touches the worktree.
        File.WriteAllText(Path.Combine(worktreePath, "widget.txt"), "widget\n");
        Git(worktreePath, "add -A");
        Git(worktreePath, "-c user.name=Test -c user.email=test@test commit -q -m widget");
        string headSha = GitOutput(worktreePath, "rev-parse HEAD");

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
                worktreePath, "task/review-me", ExecutorMode.Subscription, Now, IsFollowUp: true,
                OpeningReviewSinceSha: seedOpeningReviewSinceSha ? headSha : null),
            new AgentSessionCompleted(runId, Now),
            new ReviewParked(runId,
                "A follow-up disputed a review thread as a design call it cannot honestly make.", Now));
        await session.SaveChangesAsync(cancellationToken);

        return (taskId, runId, mainSessionId, worktreePath);
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of a temp directory, already accepted for IOException — widened
            // to UnauthorizedAccessException because the seeds that build a real bare "origin"
            // repository leave git's own loose object files marked read-only, which
            // Directory.Delete refuses on Windows. Every test in this class that seeds an origin
            // failed on that alone, with its own assertions all passing, before this caught it.
        }
    }
}
