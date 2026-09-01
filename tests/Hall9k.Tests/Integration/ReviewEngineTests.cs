using System.Diagnostics;
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
using Hall9k.Tests.Fakes;
using JasperFx;
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

        /// <summary>Lets a test act like the spawn actually touched the worktree — a fix session's own commit, keyed by that spawn's zero-based index — since every session here is scripted rather than real.</summary>
        public Dictionary<int, Action> OnSpawnByIndex { get; } = [];

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
                ["num_turns"] = 12,
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
    /// Per-pass turns and input tokens must be readable from an ordinary production run, so both
    /// ride on <see cref="ReviewPassCompleted"/> itself rather than only on the separately-appended
    /// <see cref="TokensRecorded"/> event.
    /// </summary>
    [Fact]
    public async Task Each_review_pass_records_its_own_turns_and_input_tokens()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
            maxParallelAgents: Optional<int>.None,
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

    /// <summary>Like <see cref="SeedVerifiedRunAsync(DocumentStore, CancellationToken)"/>, but a real git worktree and a real `dotnet test`-shaped gate, for tests that need <see cref="VerificationRunner"/>'s own scoping to run for real rather than short-circuit on "no gates configured".</summary>
    private async Task<(Guid TaskId, Guid RunId, string WorktreePath, Guid ProjectId)> SeedVerifiedRunWithTestGateAsync(
        DocumentStore store, CancellationToken cancellationToken, bool recordVerifyCommandsFingerprint = true)
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
                maxParallelAgents: Optional<int>.None,
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

        // RanFullScope, HeadSha and VerifyCommandsFingerprint set to what a real first gate pass
        // (VerificationRunner's own always-full initial gate) would actually record, rather than
        // the fields' own conservative defaults — several Settling-gate tests below rely on this
        // seed representing a genuinely comparable prior full gate. recordVerifyCommandsFingerprint
        // lets a caller instead seed the shape a stream written before that field existed has: a
        // real RanFullScope/HeadSha pair with no fingerprint ever recorded (independent pre-PR
        // review, cycle 3, adversarial lens).
        session.Events.StartStream<RunAggregate>(runId,
            new RunDispatched(runId, taskId, node.NodeId, node.OwnerId, 1, mainSessionId,
                worktreePath, "task/review-me", ExecutorMode.Subscription, Now),
            new AgentSessionCompleted(runId, Now),
            new VerificationPassed(
                runId, Now, RanFullScope: true, HeadSha: headSha,
                VerifyCommandsFingerprint: recordVerifyCommandsFingerprint
                    ? Hall9k.Domain.Features.Project.VerifyCommand.Fingerprint(verifyCommands)
                    : null));
        await session.SaveChangesAsync(cancellationToken);

        return (taskId, runId, worktreePath, projectId);
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
    /// The severity gate the standing sweep adds (Decisions Log #99): a Medium out-of-scope
    /// finding still mints its own dedicated draft exactly as before, while a Low one folds into
    /// the project's one standing sweep draft instead of costing a build-gate-review pipeline of
    /// its own — so a serious pre-existing defect can never be buried in a polish pile, and the
    /// board shows one extra draft this cycle, not two.
    /// </summary>
    [Fact]
    public async Task An_out_of_scope_low_folds_into_the_projects_standing_sweep_while_a_medium_still_gets_its_own_draft()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            var overridden = TaskDecider.OverrideReviewCaps(
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
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            var overridden = TaskDecider.OverrideReviewCaps(
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
    /// other node value. The park text must say the level it actually resolved and offer that
    /// level's own lever (h9k config set) rather than the hard-coded task-level wording and
    /// h9k task set-review-caps, which the operator here has no override to clear.
    /// </summary>
    [Fact]
    public async Task A_node_level_cap_of_zero_names_the_node_level_and_its_own_lever()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
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
            .And.Contain("h9k config set")
            .And.NotContain("task override", "a node value parked this, not a task override")
            .And.NotContain("h9k task set-review-caps",
                "there is no task override here for that command to clear")
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
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            var overridden = TaskDecider.OverrideReviewCaps(
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
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            var overridden = TaskDecider.OverrideReviewCaps(
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
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            var overridden = TaskDecider.OverrideReviewCaps(
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
        using DocumentStore store = NewStore();
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
    /// dispatch on the very cycle the mandatory FinalFullPass ran (a post-severity-gate Medium
    /// that concludes its track but still owes a fix, the empty terminal case) — and the fix it
    /// produces must itself get a fresh-context read before the run may settle, or the pull
    /// request ships commits the mandatory final pass never actually saw.
    /// </summary>
    [Fact]
    public async Task A_fix_dispatched_from_the_mandatory_final_pass_gets_one_more_pass_before_settling()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        ScriptedExecutor executor = new(
            // Cycle 1: conformance clean and dormant; adversarial needs a fix (pre-gate, any
            // grade forces the next cycle).
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
            // dispatched at cycle 4 — at or past the severity gate. Adversarial reports a fresh
            // Medium: post-gate, a Medium no longer forces another cycle on its own, so the track
            // concludes right here even though a fix is still owed for it (the empty terminal
            // case) — this is the exact shape that used to ship unreviewed.
            "Criteria still met.\n\nVERDICT: merge-ready",
            "FINDING: severity=medium; scope=in-scope; at=Retry.cs:31\n"
            + "Defect: the final pass caught a fresh regression.\n\nVERDICT: needs-fixes",
            "Fixed the regression the final pass found.\n\nRESOLUTION: fixed",
            // Cycle 5: nothing may settle over that fix unread, so one more mandatory final pass
            // runs — both lenses fresh, both clean this time.
            "Still clean.\n\nVERDICT: merge-ready",
            "Still clean too.\n\nVERDICT: merge-ready");

        bool mergeReady = await NewEngine(store, executor).ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeTrue();
        executor.Spawns.Should().HaveCount(
            11, "the post-gate fix from the mandatory final pass earns its own extra final pass " +
                "before the run may settle, rather than shipping unreviewed");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewCycle.Should().Be(5, "a fix landed on top of the mandatory final pass, so one more fresh-context pass ran first");

        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewDispatched>().Count(e => e.Mode == ReviewMode.FinalFullPass).Should().Be(
            4, "the mandatory final pass ran twice — once before the last fix, once to read it");
    }

    /// <summary>
    /// Cycle-3 finding: a track the mandatory final pass keeps reawakening never trips its own
    /// per-track cap, because <c>RunAggregate.TrackBudgetBaseCycle</c> deliberately measures that
    /// cap from the cycle it was last reactivated at (the prior test's own scenario). Left alone,
    /// a fix session that keeps introducing one fresh post-gate finding per pass would let
    /// FinalFullPass → reactivate → fix → verify recur without end. This scripts exactly that —
    /// the same fresh medium finding on every mandatory pass — with <c>MaxFinalFullPassRounds</c>
    /// set low, and asserts the run parks instead of looping a third time.
    /// </summary>
    [Fact]
    public async Task A_track_the_final_pass_keeps_reawakening_parks_once_the_final_pass_round_cap_is_hit()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        string freshMedium(string at) =>
            $"FINDING: severity=medium; scope=in-scope; at={at}\nDefect: the final pass caught a fresh regression.\n\n"
            + "VERDICT: needs-fixes";

        ScriptedExecutor executor = new(
            // Cycle 1: conformance clean and dormant; adversarial needs a fix (pre-gate, any
            // grade forces the next cycle).
            "Criteria met.\n\nVERDICT: merge-ready",
            freshMedium("Retry.cs:12"),
            "Tightened the retry guard.\n\nRESOLUTION: fixed",
            // Cycle 2: still pre-gate — a second minor issue still forces another cycle.
            freshMedium("Retry.cs:20"),
            "Closed the edge case too.\n\nRESOLUTION: fixed",
            // Cycle 3: clean — adversarial concludes, both tracks now dormant.
            "Clean now.\n\nVERDICT: merge-ready",
            // Cycle 4: mandatory final pass, round 1. Post-gate, so the fresh medium concludes
            // the track right here (the empty terminal case) while still owing its fix.
            "Criteria still met.\n\nVERDICT: merge-ready",
            freshMedium("Retry.cs:31"),
            "Fixed the regression the final pass found.\n\nRESOLUTION: fixed",
            // Cycle 5: nothing may settle over that fix unread, so the mandatory pass runs
            // again — round 2 — and finds another fresh post-gate medium, same shape as round 1.
            "Criteria still met.\n\nVERDICT: merge-ready",
            freshMedium("Retry.cs:40"),
            "Fixed that regression too.\n\nRESOLUTION: fixed");
        // A third round would be owed next — reactivation keeps resetting the track's own cap
        // (RunAggregate.TrackBudgetBaseCycle), so nothing about ITS cap would ever stop this.
        // MaxFinalFullPassRounds is the independent bound that does, set to 2 so this test hits
        // it on the very next round rather than scripting a long, unbounded-looking sequence.

        bool mergeReady = await NewEngine(
            store, executor, new DaemonOptions { MaxFinalFullPassRounds = 2 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse(
            "the mandatory final pass round cap must stop the loop rather than let it recur forever");
        executor.Spawns.Should().HaveCount(12, "the run parks before a third final-pass round ever dispatches");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked);
        run.ReviewCycle.Should().Be(5, "the park happens deciding cycle 6, before it ever dispatches");
        run.ParkedReason.Should().Contain("dispatched the mandatory final full review pass")
            .And.Contain("2 consecutive time(s)")
            .And.Contain("h9k review resolve --merge-ready")
            .And.NotContain(
                "reawakened",
                "the post-gate medium concludes the track outright every time (Continues: false), so " +
                "ReviewTrackReactivated never actually fires in this scenario and the park text must not " +
                "claim it did");

        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<ReviewDispatched>().Count(e => e.Mode == ReviewMode.FinalFullPass).Should().Be(
            4, "exactly two final-pass rounds ran (cycles 4 and 5) before the cap parked the third");
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
        using DocumentStore store = NewStore();
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
            new VerificationRunner(store, Options.Create(new DaemonOptions()), NullLogger<VerificationRunner>.Instance),
            Options.Create(new DaemonOptions { MaxComplianceReviewCycles = 3 }), logger);

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
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, _) = await SeedVerifiedRunAsync(store, cts.Token);

        string freshMedium(string at) =>
            $"FINDING: severity=medium; scope=in-scope; at={at}\nDefect: the final pass caught a fresh regression.\n\n"
            + "VERDICT: needs-fixes";

        ScriptedExecutor executor = new(
            // Cycle 1: conformance clean and dormant; adversarial needs a fix (pre-gate).
            "Criteria met.\n\nVERDICT: merge-ready",
            freshMedium("Retry.cs:12"),
            "Tightened the retry guard.\n\nRESOLUTION: fixed",
            // Cycle 2: still pre-gate — a second minor issue still forces another cycle.
            freshMedium("Retry.cs:20"),
            "Closed the edge case too.\n\nRESOLUTION: fixed",
            // Cycle 3: clean — adversarial concludes, both tracks now dormant.
            "Clean now.\n\nVERDICT: merge-ready",
            // Cycle 4: mandatory final pass, round 1. Post-gate, so the fresh medium concludes
            // the track right here while still owing its fix.
            "Criteria still met.\n\nVERDICT: merge-ready",
            freshMedium("Retry.cs:31"),
            "Fixed the regression the final pass found.\n\nRESOLUTION: fixed",
            // Cycle 5: mandatory final pass, round 2 — MaxFinalFullPassRounds (set to 2 below) is
            // reached here, so the run parks deciding cycle 6 rather than dispatching a third round.
            "Criteria still met.\n\nVERDICT: merge-ready",
            freshMedium("Retry.cs:40"),
            "Fixed that regression too.\n\nRESOLUTION: fixed");

        bool mergeReady = await NewEngine(
            store, executor, new DaemonOptions { MaxFinalFullPassRounds = 2 })
            .ReviewAsync(runId, taskId, cts.Token);

        mergeReady.Should().BeFalse("the run parks on the final-pass round cap before cycle 6 ever dispatches");

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
        run.ReviewCycle.Should().Be(6, "a genuine third final-pass round ran and settled");
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
    /// </summary>
    [Fact]
    public async Task A_ride_along_handed_to_a_disputed_fix_session_settles_as_fixed_unreviewed()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
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
            1, "the low finding at Shared.cs:9 was already inside the merged document the " +
                "disputed fix session read this same cycle, so it shipped fixed-unreviewed");
        run.ReviewResidualsRideAlong.Should().Be(
            0, "it must not also be counted as an unclaimed ride-along");
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
                worktreePath, "task/review-me", ExecutorMode.Subscription, Now),
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
