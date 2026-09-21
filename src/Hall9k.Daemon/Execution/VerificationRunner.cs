using Hall9k.Domain.Infrastructure.Storage;
using System.Diagnostics;
using Hall9k.Connectors.Processes;
using System.Text.RegularExpressions;
using Hall9k.Connectors.Verification;
using Hall9k.Connectors.Worktrees;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Documents;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Run.Queries;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Deterministic gates only (PLAN.md §6.5): the project's verify commands run sequentially
/// in the run's worktree; first failure stops the line. Before any gate, two pre-gate checks
/// fail a run honestly rather than let the gates run against a tree the pull request will
/// never actually carry: a no-commit check for a branch that carries nothing at all (Research
/// tasks exempt; their deliverable is the transcript), and an uncommitted-files check
/// (backlog 57, not exempt for any task type) for a session that ended with
/// modified-but-uncommitted files, or brand-new untracked files under src/ or tests/, still
/// sitting in the worktree — a failure that names every file left behind, of either kind, so a
/// human or a retry session finds the whole feature instead of committing only the modified
/// half of it and shipping a hollow branch. The reviewer agent is Slice 3.
/// <para>
/// The uncommitted-files check is not only a failure any more (task: when a session ends with
/// finished work uncommitted, the daemon recovers on its own): a run that has not already spent
/// its one attempt gets a single bounded, commit-only recovery session spawned into the SAME
/// worktree before this method ever calls <see cref="FailBeforeGatesAsync"/> — see
/// <see cref="RecoverUncommittedWorkOrExplainAsync"/> for the whole mechanism. The detection
/// itself (<see cref="DetectStrandedWorkAsync"/>) is unchanged either way; only what happens
/// once it finds something is new.
/// </para>
/// </summary>
public sealed partial class VerificationRunner(
    IDocumentStore store,
    IOptions<DaemonOptions> options,
    ILogger<VerificationRunner> logger,
    IWorktreeManager worktrees,
    IExecutor executor,
    IProcessManager processManager)
{
    /// <summary>
    /// <see cref="VerifyForSettlingAsync"/>'s own result (task: a pre-final-pass rebase that
    /// applies cleanly but breaks the mandatory gate gets a repair lap inside the same run instead
    /// of failing it): <see cref="FailedGateName"/> names a genuine gate failure — whether
    /// <c>allowRepairInsteadOfFail</c> was true (the run stays open, for a caller to dispatch a
    /// repair session over) or false (the ordinary <see cref="RunFailed"/>/<c>TaskFailed</c> append
    /// still runs, exactly as <see cref="VerifyAsync"/> always has). <see cref="FailureOutput"/> is
    /// populated for either path once a genuine gate failure is recorded — the reason a caller has
    /// to read <see cref="FailedGateName"/>, not <see cref="Passed"/> alone, to tell a genuine gate
    /// failure apart from a pre-gate one (stranded work, a missing run or task, or an abandoned
    /// task) when deciding whether a failure is safe to dispatch a repair session over, or what to
    /// show a human when it is not (independent pre-PR review, cycle 1, both lenses): every pre-gate
    /// failure shape leaves both null, since nothing downstream of those ever reads them — the run
    /// already failed (and, for stranded work, the task did too) or was retired superseded (for an
    /// abandoned task) by the time either returns.
    /// </summary>
    public readonly record struct SettlingVerificationResult(bool Passed, string? FailedGateName, string? FailureOutput);

    /// <summary>
    /// Runs the project's gates (task: a fix cycle's verification gate). <paramref name="scopeSinceSha"/>
    /// is the reviewed cycle's own head — the boundary <see cref="TestScopeResolver"/> diffs the fix's
    /// commits against when narrowing a `dotnet test`-shaped gate — or null to run every gate at full
    /// scope regardless, the caller's own decision (the run's very first gate pass before any review
    /// cycle, or a FinalFullPass fix's own reverify: nothing merges on scoped green alone) rather than
    /// something this method second-guesses. <paramref name="scopeContext"/> is always required: it is
    /// the human-readable "why" recorded on the verification pass and logged either way.
    /// <paramref name="leg"/> names which of the run's own legs is being gated right here — the
    /// automatic uncommitted-work recovery's own eligibility is scoped to it (task: a headless
    /// build, fix, or recovery session never ends its turn while a gate it started is still running
    /// in the background), so a leg's own dirty worktree is never turned away just because a
    /// different, earlier leg on the same run already spent ITS one automatic recovery.
    /// </summary>
    public Task<bool> VerifyAsync(
        Guid runId, Guid taskId, string? scopeSinceSha, string scopeContext, RunSessionLeg leg,
        CancellationToken cancellationToken) =>
        VerifyAsync(runId, taskId, scopeSinceSha, scopeContext, leg, recentlyEndedGate: null, cancellationToken);

    /// <summary>
    /// <see cref="VerifyAsync(Guid,Guid,string?,string,RunSessionLeg,CancellationToken)"/>'s own
    /// overload for the one caller that knows something this call's own fresh
    /// <see cref="RunDetails"/> load cannot: <c>RunSupervisor.AdoptOrphansAsync</c>, resuming a run
    /// whose orphaned gate it just killed and recorded <see cref="GateEnded"/> for. That
    /// append clears <see cref="RunDetails.ActiveGate"/> before this method's own load ever runs,
    /// so without <paramref name="recentlyEndedGate"/> the log-open <see cref="IOException"/>
    /// branch in <c>RunGateAsync</c> could never name the process most likely still holding the
    /// handle — the one adoption itself just terminated (independent pre-PR review, cycle 3,
    /// adversarial lens, low). Every other caller passes null through the parameterless overload
    /// above, unchanged.
    /// </summary>
    public async Task<bool> VerifyAsync(
        Guid runId, Guid taskId, string? scopeSinceSha, string scopeContext, RunSessionLeg leg,
        ActiveGate? recentlyEndedGate, CancellationToken cancellationToken)
    {
        SettlingVerificationResult result = await VerifyCoreAsync(
            runId, taskId, scopeSinceSha, scopeContext, leg, allowRepairInsteadOfFail: false, recentlyEndedGate,
            cancellationToken);
        return result.Passed;
    }

    /// <summary>
    /// The Settling phase's own mandatory-gate call (task: a pre-final-pass rebase that applies
    /// cleanly but breaks the mandatory gate gets a repair lap inside the same run instead of
    /// failing it) — identical to <see cref="VerifyAsync"/> in every way except what a genuine gate
    /// failure does: <paramref name="allowRepairInsteadOfFail"/> true skips the ordinary
    /// <see cref="Domain.Features.Run.Events.RunFailed"/>/<c>TaskFailed</c> append for a real gate
    /// failure (never for the pre-gate stranded-work check, which stays fail-hard unconditionally
    /// — an agent's own uncommitted work is not something a rebase caused) and instead returns the
    /// gate's own output so the caller can dispatch a repair session over it. The caller — not this
    /// method — decides whether a given failure is repair-eligible at all: this always runs the
    /// gate for real, so a passing result is recorded exactly as <see cref="VerifyAsync"/> would.
    /// </summary>
    public Task<SettlingVerificationResult> VerifyForSettlingAsync(
        Guid runId, Guid taskId, string? scopeSinceSha, string scopeContext, RunSessionLeg leg,
        bool allowRepairInsteadOfFail, CancellationToken cancellationToken) =>
        VerifyCoreAsync(
            runId, taskId, scopeSinceSha, scopeContext, leg, allowRepairInsteadOfFail, recentlyEndedGate: null,
            cancellationToken);

    private async Task<SettlingVerificationResult> VerifyCoreAsync(
        Guid runId, Guid taskId, string? scopeSinceSha, string scopeContext, RunSessionLeg leg,
        bool allowRepairInsteadOfFail, ActiveGate? recentlyEndedGate, CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        RunDetails? run = await query.LoadAsync<RunDetails>(runId, cancellationToken);
        TaskDetails? task = run is null ? null : await query.LoadAsync<TaskDetails>(taskId, cancellationToken);
        ProjectDetails? project = task is null
            ? null
            : await query.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);

        if (run is null || task is null)
        {
            logger.LogError("Cannot verify run {RunId}: run or task missing", runId);
            return new SettlingVerificationResult(false, null, null);
        }

        // A task abandoned while this run was in flight (task: abandoning a task halts its
        // in-flight run entirely) — the gates below spend real process time and the
        // review/open-PR chain this feeds is `&&`-short-circuited on this call's own result, so
        // refusing here before any gate runs is what stops verification, review, and PR-opening
        // together for a run whose task nobody is coming back to. Deliberately Abandoned only,
        // not TaskState.IsTerminal: a Done task can still own a live follow-up run addressing
        // further feedback under the same task (its own objective was already met by an earlier
        // run), and that follow-up's own gates must still run.
        //
        // This refusal must also retire the run itself, not just decline to run the gates
        // (independent pre-PR review, cycle 1, both lenses): AgentSessionCompleted (appended
        // unconditionally when the agent process exits, regardless of what happened to the task
        // meanwhile) moves RunDetails back to RunState.Verifying, and this method's own false is
        // `&&`-short-circuited ahead of ReviewEngine.ReviewAsync — the only other caller in this
        // chain that would otherwise retire the run with RunSuperseded. Left unretired, the run
        // would sit in the live Verifying state forever: counted against NodeLoad's concurrency
        // slots, re-adopted by ResumeStrandedPipelinesAsync and AdoptOrphansAsync on every cycle,
        // and never archived by ProjectHomeRenderEngine, which waits on the run going non-live.
        if (task.State == TaskState.Abandoned)
        {
            logger.LogInformation(
                "Run {RunId}: task {TaskId} is Abandoned - skipping verification", runId, taskId);
            await RetireAbandonedRunAsync(runId, taskId, cancellationToken);
            return new SettlingVerificationResult(false, null, null);
        }

        // Fail fast on an agent that left work behind uncommitted, before any gate runs against
        // a tree the pull request will never actually carry — see DetectStrandedWorkAsync's own
        // doc for the two failure shapes this observes, and RecoverUncommittedWorkOrExplainAsync's
        // for what happens before either one is allowed to fail the run outright.
        if (project is not null)
        {
            StrandedWorkCheck check = await DetectStrandedWorkAsync(run, task, project, cancellationToken);
            string? failureReason = check.FailureReason;

            // Recovery-eligible only when there is something a commit-only session could
            // actually do something about (task: when a session ends with finished work
            // uncommitted, the daemon recovers on its own) — a task that produced zero commits
            // AND left nothing sitting in the worktree did not strand work, it did nothing, and
            // no commit session fixes that. At most one attempt per (run, leg): a run that
            // already carries an UncommittedWorkRecoveries entry for THIS leg spent that leg's
            // own attempt already, whatever the outcome — a different leg on the same run reads
            // its own eligibility independently (task: a headless build, fix, or recovery session
            // never ends its turn while a gate it started is still running in the background).
            if (failureReason is not null && check.StrandedFiles.Count > 0 && !run.HasUncommittedWorkRecoveryAttempt(leg))
            {
                failureReason = await RecoverUncommittedWorkOrExplainAsync(
                    run, task, project, check.StrandedFiles, failureReason, leg, cancellationToken);
            }

            if (failureReason is not null)
            {
                // Fail-hard unconditionally, even when allowRepairInsteadOfFail is true: an
                // agent's own uncommitted work is not something a rebase caused, so this pre-gate
                // check is out of scope for the Settling-gate repair lap (task: a pre-final-pass
                // rebase that applies cleanly but breaks the mandatory gate gets a repair lap
                // inside the same run instead of failing it — only a genuine gate failure below
                // is eligible).
                await FailBeforeGatesAsync(runId, taskId, failureReason, cancellationToken);
                logger.LogWarning("Run {RunId} failed before the gates: {Reason}", runId, failureReason);
                return new SettlingVerificationResult(false, null, null);
            }
        }

        IReadOnlyList<VerifyCommand> gates = project?.VerifyCommands ?? [];
        string gatesFingerprint = VerifyCommand.Fingerprint(gates);
        // project is null only when gates.Count == 0 too (gates is derived from project?.VerifyCommands),
        // so folding the null check into this same early return is what lets the compiler — and
        // every block below it — treat `project` as non-null for the rest of this method, rather
        // than a null-forgiving `!` asserting a fact AGENTS.md says never to assume unobserved.
        if (project is null || gates.Count == 0)
        {
            string? noGatesHeadSha = await GetHeadShaAsync(run.WorktreePath, cancellationToken);
            await RecordPassAsync(
                runId, "No verification gates configured for this project.", ranFullScope: true, noGatesHeadSha,
                gatesFingerprint, gateDurations: [], cancellationToken);
            logger.LogInformation("Run {RunId} verification passed: no gates configured", runId);
            return new SettlingVerificationResult(true, null, null);
        }

        // Before any gate runs, whether every path this run's branch changed against its base is
        // content the project has declared non-executable (task: a delivered diff that touches no
        // buildable or testable source skips the build and test gates — origin: ef2fefe5, a
        // two-file skill markdown fix paying roughly twelve minutes of build-and-test ceremony on
        // every pipeline entry while the actual work took four). Classified afresh at every entry
        // into the gates — first delivery, an intermediate review-cycle reverify, and every
        // follow-up lap alike — never cached from an earlier entry's own verdict, so a fix lap that
        // starts touching real source is judged on its own diff, not an earlier lap's content-only
        // one. Unobservable git (GetChangedPathsAsync returning null) runs the gates as today,
        // never guessed as content-only (AGENTS.md's "never guess at unobserved facts" rule) — the
        // same convention DetectStrandedWorkAsync's own git reads already follow.
        IReadOnlyList<string>? changedPaths = await GetChangedPathsAsync(
            run.WorktreePath, run.BaseBranchOr(project.BaseBranch), run.StackedForkPoint(project.BaseBranch),
            cancellationToken);
        if (changedPaths is { Count: > 0 })
        {
            NonExecutablePathClassifier.ClassificationResult classification =
                NonExecutablePathClassifier.Classify(changedPaths, project.EffectiveNonExecutablePaths);
            if (classification.AllMatched)
            {
                IReadOnlyList<VerificationSkippedPath> skippedPaths =
                    [.. classification.Paths.Select(path => new VerificationSkippedPath(path.Path, path.MatchedRule!))];
                await RecordSkipAsync(runId, skippedPaths, cancellationToken);
                logger.LogInformation(
                    "Run {RunId} verification skipped: every changed path ({Count}) matched the project's non-executable set",
                    runId, changedPaths.Count);
                return new SettlingVerificationResult(true, null, null);
            }
        }

        // run.RunDirectory is whatever RunDispatched recorded once, at dispatch — stale for a
        // run whose task has since crossed the tasks/_archive/ boundary (backlog 51 cycle 6):
        // gates run well after the agent's own session ends, so the render sweep has had a
        // chance to move the task's directory by the time this reads it. Resolved once, here,
        // rather than at each gate: every reader of a recorded RunDirectory funnels through
        // RunPaths.ResolveCurrentDirectory (PLAN.md §16 #84).
        string runDirectory = RunPaths.ResolveCurrentDirectory(run.RunDirectory);

        // Scoping only matters to a `dotnet test`-shaped gate — a project with no such gate (or
        // one that never reached this point, e.g. a build-only project) pays nothing extra: no
        // git diff, no test-tree scan, no note beyond what verification always recorded.
        TestGateScope? scope = null;
        if (gates.Any(gate => IsDotnetTestGate(gate.Command)))
        {
            scope = scopeSinceSha is null
                ? TestGateScope.Full(scopeContext)
                : await TestScopeResolver.ResolveAsync(run.WorktreePath, scopeSinceSha, scopeContext, cancellationToken);
            logger.LogInformation(
                "Run {RunId} test gate scope: {Mode} — {Reason}",
                runId, scope.IsScoped ? "scoped" : "full", scope.Reason);
        }

        // A host-coupled gate only ever runs at the first verification and the final full pass —
        // both unscoped (scopeSinceSha null) — and is skipped on every intermediate review-cycle
        // pass, which is always scoped to the fix's own commits (task: host-coupled tests run in
        // their own gate once per task, never in parallel with another run's copy —
        // #225). This is the identical condition the scope block above already
        // uses to decide Full vs. a resolved scope, so a host-coupled gate needs no separate
        // signal threaded down from RunSupervisor's own first-verification call or ReviewEngine's
        // own ReviewMode: unscoped IS "first or final pass", scoped IS "intermediate cycle".
        bool runHostCoupledGate = scopeSinceSha is null;

        // Counted per gate rather than OR'd into one flag (independent pre-PR review, cycle 4):
        // a project can configure more than one `dotnet test`-shaped gate, and a single shared
        // flag recorded the whole pass as full-scope the moment ANY one of them fell back, even
        // while a sibling test gate ran genuinely scoped and was never covered at full scope over
        // this HEAD — exactly the gap the mandatory pre-Settling full gate exists to close.
        // dotnetTestGateCount and dotnetTestGateFellBackCount together answer "did every
        // configured test gate actually run at full scope", never guessed from a single gate's
        // own outcome. A host-coupled gate never counts here (#225): its own
        // run/skip axis is orthogonal to fix-scope narrowing, tracked separately on
        // GateDuration.HostCoupledSkipped instead, and mixing it in would make a project with a
        // configured host-coupled gate never read as allTestGatesFellBack on a scoped pass that
        // skipped it outright rather than falling back to full.
        int dotnetTestGateCount = 0;
        int dotnetTestGateFellBackCount = 0;

        // Whether this pass skipped its configured host-coupled gate outright (adversarial
        // review, high — #225): a scoped reverify whose commits touch a non-C#
        // file falls back to TestGateScope.Full the identical way an ordinary dotnet-test gate
        // does, which used to make the pass-level ranFullScope below true even though the
        // host-coupled gate itself was still skipped (runHostCoupledGate is keyed only on
        // scopeSinceSha, never on what the scope resolver decided). RunAggregate.LastGateRanFullScope
        // then read this pass as a genuine full pass over the current HEAD, and
        // ReviewEngine.GateAlreadyRanFullOverCurrentHeadAsync waived the mandatory pre-Settling
        // full gate on the strength of it — so the host-coupled gate could reach a merge having
        // never once run against the tip that actually ships. Folded into ranFullScope below so a
        // pass that skips it can never satisfy that waiver; the run's own next full pass
        // (scopeSinceSha null) is unscoped and so always runs the host-coupled gate for real.
        bool anyHostCoupledGateSkipped = false;

        // Every gate's own wall-clock duration this pass (task: gate wall-clock duration is
        // recorded and surfaced), in the order the gates ran — GateDuration.cs's own doc comment
        // covers why a retried gate sums both attempts into one entry rather than recording two.
        List<GateDuration> gateDurations = [];

        // The one place a genuine gate failure's own recording decision is made (task: a
        // pre-final-pass rebase that applies cleanly but breaks the mandatory gate gets a repair
        // lap inside the same run instead of failing it) — shared by every failure branch below so
        // neither can drift from the other. allowRepairInsteadOfFail true skips the ordinary
        // RunFailed/TaskFailed append and returns the gate's own (clean-base-annotated) output
        // instead, for the caller to dispatch a repair session over; false records the failure
        // exactly as this method always has.
        async Task<SettlingVerificationResult> RecordGateFailureOutcomeAsync(
            VerifyCommand failedGate, string reason, bool isInfrastructureFailure)
        {
            if (allowRepairInsteadOfFail)
            {
                string reportedReason = await RecordGateFailureWithoutFailingRunAsync(
                    runId, run.NodeId, project, failedGate, reason, isInfrastructureFailure, gateDurations,
                    cancellationToken);
                return new SettlingVerificationResult(false, failedGate.Name, reportedReason);
            }

            string recordedReason = await RecordGateFailureAsync(
                runId, taskId, run.NodeId, project, failedGate, reason, isInfrastructureFailure, gateDurations,
                cancellationToken);
            return new SettlingVerificationResult(false, failedGate.Name, recordedReason);
        }

        // Consumed by the first gate this pass actually attempts (a host-coupled gate this pass
        // skips outright never attempts anything, so it never consumes it) — recentlyEndedGate
        // describes the one process adoption just killed before this pass began, and naming it
        // against a second, unrelated gate later in the same pass would misattribute a lock
        // that process was never holding.
        ActiveGate? pendingRecentlyEndedGate = recentlyEndedGate;

        foreach (VerifyCommand gate in gates)
        {
            bool gateIsDotnetTest = IsDotnetTestGate(gate.Command);
            if (gateIsDotnetTest && !gate.IsHostCoupled)
            {
                dotnetTestGateCount++;
            }

            // Skipped outright, not run at full scope and discarded (#225): an
            // intermediate review-cycle pass never pays for the categories of tests this gate's
            // own filter selects — the ones that reach outside the process (git, the process
            // table, the toolchain, Docker) — only the run's first verification and its final
            // full pass do. Recorded with a zero duration and Passed true (a skip is never a
            // failure) so h9k task show can say which passes ran this gate and which skipped it,
            // rather than the skip reading as silence the way an absent entry would.
            if (gate.IsHostCoupled && !runHostCoupledGate)
            {
                anyHostCoupledGateSkipped = true;
                gateDurations.Add(new GateDuration(
                    gate.Name, TimeSpan.Zero, Passed: true, RanFullScope: false, HostCoupledSkipped: true));
                logger.LogInformation(
                    "Run {RunId} gate '{Gate}' skipped: host-coupled, and this pass is an intermediate review cycle",
                    runId, gate.Name);
                continue;
            }

            ActiveGate? recordedActiveGate = run.ActiveGate ?? pendingRecentlyEndedGate;
            pendingRecentlyEndedGate = null;

            Stopwatch gateStopwatch = Stopwatch.StartNew();
            (bool passed, string summary, bool isInfrastructureFailure, string? excerpt, bool fellBackToFull, TimeSpan permitWaitElapsed) =
                await RunGateAsync(runId, runDirectory, run.WorktreePath, gate, scope, recordedActiveGate, cancellationToken);
            TimeSpan gateElapsed = gateStopwatch.Elapsed - permitWaitElapsed;
            bool gateFellBackToFull = fellBackToFull;
            if (passed)
            {
                if (gateIsDotnetTest && !gate.IsHostCoupled && gateFellBackToFull)
                {
                    dotnetTestGateFellBackCount++;
                }

                gateDurations.Add(new GateDuration(
                    gate.Name, gateElapsed, Passed: true, RanFullScope: GateRanFullScope(gateIsDotnetTest, scope, gateFellBackToFull)));
                logger.LogInformation("Run {RunId} gate '{Gate}' passed", runId, gate.Name);
                continue;
            }

            // A gate this run already retried once, in a prior daemon lifetime, never earns a
            // second one on adoption: the retry budget otherwise lives only in this method's
            // local state, so a daemon that died between the GateRetried commit below and this
            // gate's resolution would resume with no memory of the retry already spent
            // (backlog 53, Copilot review on PR #36 — RunSupervisor.AdoptOrphansAsync calls
            // VerifyAsync fresh). run.PendingGateRetry is the persisted record of that spend.
            if (isInfrastructureFailure && run.PendingGateRetry == gate.Name)
            {
                string adoptedReason =
                    $"Gate '{gate.Name}' failed again with an infrastructure-classified signature " +
                    $"after already spending its one retry before an earlier daemon restart. {summary}";
                gateDurations.Add(new GateDuration(
                    gate.Name, gateElapsed, Passed: false, RanFullScope: GateRanFullScope(gateIsDotnetTest, scope, gateFellBackToFull)));
                logger.LogWarning(
                    "Run {RunId} verification failed at gate '{Gate}': its one retry was already spent before adoption",
                    runId, gate.Name);
                return await RecordGateFailureOutcomeAsync(gate, adoptedReason, isInfrastructureFailure: true);
            }

            if (!isInfrastructureFailure)
            {
                gateDurations.Add(new GateDuration(
                    gate.Name, gateElapsed, Passed: false, RanFullScope: GateRanFullScope(gateIsDotnetTest, scope, gateFellBackToFull)));
                logger.LogWarning("Run {RunId} verification failed at gate '{Gate}': {Summary}", runId, gate.Name, summary);
                return await RecordGateFailureOutcomeAsync(gate, summary, isInfrastructureFailure: false);
            }

            // Infrastructure-classified: retry once, in place, before believing the agent's
            // work is broken (backlog 53's origin incident — the container, not the diff, was
            // what failed). Recorded on the stream so the record says the flake happened,
            // whichever way the retry goes, and never fails the run or spends any budget. The
            // cause carries the matching excerpt, not just the recorded summary's tail: a
            // large gate's summary is truncated to its last 400 characters and can push the
            // marker that actually triggered classification out of it (adversarial review).
            await RecordGateRetryAsync(runId, gate.Name, BuildRetryCause(summary, excerpt), cancellationToken);
            logger.LogWarning(
                "Run {RunId} gate '{Gate}' failed with an infrastructure-classified signature; retrying once: {Summary}",
                runId, gate.Name, summary);

            // A gate that failed within milliseconds of starting (the log-open IOException this
            // task's own incident classifies) can still be racing a residual OS teardown delay
            // right after adoption's own TerminateTree kill — Process.Kill(entireProcessTree:
            // true) is a request, not a confirmation of death, so the handle it held can outlive
            // the kill call by a short window. An immediate retry lands inside that window and
            // fails the same way, spending the run's one retry on a lock that would have cleared
            // moments later. Every other infrastructure classification (container startup,
            // MSB4166) is reached only after the gate has already run for minutes, so this delay
            // costs those retries nothing worth naming (independent pre-PR review, cycle 1,
            // adversarial lens).
            await Task.Delay(InfrastructureRetryDelay, cancellationToken);

            Stopwatch retryStopwatch = Stopwatch.StartNew();
            (bool retryPassed, string retrySummary, bool retryIsInfrastructureFailure, _, bool retryFellBackToFull, TimeSpan retryPermitWaitElapsed) =
                await RunGateAsync(runId, runDirectory, run.WorktreePath, gate, scope, recordedActiveGate, cancellationToken);
            TimeSpan totalGateElapsed = gateElapsed + retryStopwatch.Elapsed - retryPermitWaitElapsed;

            // The retry's own outcome replaces the first attempt's, not OR's with it (adversarial
            // review): only the attempt that actually passed is what the recorded RanFullScope fact
            // describes, and a first attempt that fell back to full before failing says nothing
            // about whether the retry — which is what's about to be recorded — also did.
            gateFellBackToFull = retryFellBackToFull;
            if (retryPassed)
            {
                if (gateIsDotnetTest && !gate.IsHostCoupled && gateFellBackToFull)
                {
                    dotnetTestGateFellBackCount++;
                }

                gateDurations.Add(new GateDuration(
                    gate.Name, totalGateElapsed, Passed: true, RanFullScope: GateRanFullScope(gateIsDotnetTest, scope, gateFellBackToFull)));
                logger.LogInformation("Run {RunId} gate '{Gate}' passed on retry", runId, gate.Name);
                continue;
            }

            // A second consecutive infrastructure failure fails the run honestly with the
            // classification in the reason, so a genuinely broken environment surfaces instead
            // of looping. A retry that instead surfaces a real failure is recorded as exactly
            // that, unclassified — the retry earned it a second look, not a second pass.
            string reason = retryIsInfrastructureFailure
                ? $"Gate '{gate.Name}' failed twice in a row with an infrastructure-classified " +
                  $"signature (an environment failure, not the agent's work). First attempt: " +
                  $"{summary} Retry attempt: {retrySummary}"
                : retrySummary;

            gateDurations.Add(new GateDuration(
                gate.Name, totalGateElapsed, Passed: false, RanFullScope: GateRanFullScope(gateIsDotnetTest, scope, gateFellBackToFull)));
            logger.LogWarning("Run {RunId} verification failed at gate '{Gate}' after retry: {Summary}", runId, gate.Name, reason);
            return await RecordGateFailureOutcomeAsync(gate, reason, isInfrastructureFailure: retryIsInfrastructureFailure);
        }

        // Whether the WHOLE pass covered every configured `dotnet test`-shaped gate at full
        // scope, never guessed from any single gate's own outcome: a project can configure more
        // than one such gate, and only when every one of them ran unscoped (either because the
        // top-level scope decision itself resolved to full, or because each one individually fell
        // back to full after its own filter intersected to nothing) does the pass as a whole earn
        // "full scope" — a sibling gate that ran genuinely scoped means the pass did not.
        bool anyTestGateFellBack = dotnetTestGateFellBackCount > 0;
        bool allTestGatesFellBack = dotnetTestGateCount > 0 && dotnetTestGateFellBackCount == dotnetTestGateCount;

        // anyHostCoupledGateSkipped is checked regardless of what the block above concluded
        // (adversarial review, high): a pass that skipped its own configured host-coupled gate
        // never covered the project's FULL configured suite, however the ordinary dotnet-test
        // gates' own scope resolved, so it can never be recorded as the "already ran full over
        // this HEAD" fact ReviewEngine's pre-Settling waiver reads.
        bool ranFullScope = (scope is null || !scope.IsScoped || allTestGatesFellBack) && !anyHostCoupledGateSkipped;

        // "No executed tests were recorded" rather than "the scoped filter matched no tests"
        // (conformance review finding): at this point only the per-gate fallback count is known,
        // not the gate's own output, so the note must not assert the filter itself was the cause
        // when a suppressed VSTest summary is equally consistent with what was actually observed.
        string? note = scope is null
            ? null
            : !scope.IsScoped
                ? $"Test gate ran full: {scope.Reason}"
                : !anyTestGateFellBack
                    ? $"Test gate scoped: {scope.Reason}"
                    : allTestGatesFellBack
                        ? $"Test gate ran full: no executed tests were recorded for the scoped run ({scope.Reason})"
                        : $"Test gate ran full for {dotnetTestGateFellBackCount} of {dotnetTestGateCount} test gate(s): " +
                          $"no executed tests were recorded for those, while the rest ran scoped ({scope.Reason})";
        string testGateModeDescription = scope is null
            ? ""
            : !scope.IsScoped || allTestGatesFellBack
                ? "full"
                : anyTestGateFellBack
                    ? "mixed"
                    : "scoped";

        string? passHeadSha = await GetHeadShaAsync(run.WorktreePath, cancellationToken);
        await RecordPassAsync(runId, note, ranFullScope, passHeadSha, gatesFingerprint, gateDurations, cancellationToken);
        logger.LogInformation(
            "Run {RunId} verification passed ({Count} gate(s)){TestGateSummary}",
            runId, gates.Count,
            scope is null ? "" : $"; test gate ran {testGateModeDescription}");
        return new SettlingVerificationResult(true, null, null);
    }

    /// <summary>
    /// The pre-gate uncommitted-work observation (backlog 57), unchanged from before this run
    /// ever gained an automatic recovery: null <see cref="FailureReason"/> means the tree is fine
    /// to gate; otherwise it names the failure exactly as <see cref="FailBeforeGatesAsync"/> would
    /// report it today. <see cref="StrandedFiles"/> is always the raw list — even when
    /// <see cref="FailureReason"/> is the no-commit variant that already folds them into its own
    /// text — so a caller deciding whether to recover never has to re-derive it from prose.
    /// <see cref="Observed"/> is false only when `git status` itself could not be read — a null
    /// <see cref="FailureReason"/> on an unobserved tree means "unknown", not "clean", so a caller
    /// recording this check's own verdict never mistakes the one for the other (independent pre-PR
    /// review, cycle 1, conformance finding).
    /// </summary>
    /// <remarks>
    /// Internal rather than private (task: a do-now session launched by h9k task start is caught
    /// within seconds): <c>RunSupervisor</c>'s own unattended-exit handling reuses this exact
    /// check — the identical clean-tree-with-commits question <c>h9k task deliver</c> already
    /// answers by hand — rather than a second, independently maintained copy of the same git
    /// status and commit-count reads.
    /// </remarks>
    internal readonly record struct StrandedWorkCheck(
        string? FailureReason, IReadOnlyList<string> StrandedFiles, bool Observed = true);

    /// <summary>
    /// Fail fast on an agent that left work behind uncommitted, before any gate runs against a
    /// tree the pull request will never actually carry (origin incident: task 08's agent
    /// completed all its work uncommitted; gates passed vacuously on the unmodified tree and the
    /// failure surfaced two stages late as "No commits between main and branch" at PR creation).
    /// Two distinct shapes of that same failure, checked separately because they need different
    /// words (backlog 57): zero commits at all, and — the shape the zero-commit check alone
    /// always missed — some commits landed but the session still ended with
    /// modified-but-uncommitted files sitting in the worktree (origin incidents, both 2026-08-26:
    /// the PR #53 follow-up's cycle-3 fix round left eight files uncommitted, caught only by the
    /// next review pass; task df277369 failed twice backgrounding its own test suite and ending
    /// the session before it finished). Either way the reason names the files, so a human or a
    /// recovery session finds the finished work instead of rediscovering it.
    /// </summary>
    internal async Task<StrandedWorkCheck> DetectStrandedWorkAsync(
        RunDetails run, TaskDetails task, ProjectDetails project, CancellationToken cancellationToken)
    {
        (IReadOnlyList<string>? modifiedFiles, IReadOnlyList<string> untrackedFiles) =
            await ListUncommittedFilesAsync(run.WorktreePath, cancellationToken);

        if (modifiedFiles is null)
        {
            // Git is unobservable here (not a repo, permission denied, `git` missing from
            // PATH); never guess — proceed and let the gates surface whatever is actually
            // broken, but say so, the same as the no-commit check's own unobservable case
            // below: an unlogged skip here would leave an operator with no record of why a
            // session's stranded work went uncaught.
            logger.LogWarning(
                "Run {RunId}: could not read the worktree's status at {WorktreePath}; skipping the uncommitted-files check",
                run.Id, run.WorktreePath);
        }

        // An untracked path under src/ or tests/ is treated as first-class strandable work,
        // not a softer signal: it is exactly the shape that stranded a feature's own core
        // files (TwgJiraExecutor.cs, JiraWriteCoordinator.cs) behind a failure message that
        // named only the modified files, so an agent that faithfully committed the named
        // list still delivered a hollow branch (origin incident, 2026-08-29, the Jira
        // compose/execute task). git status is run with `--untracked-files=all`, so a new
        // file inside a wholly untracked directory is reported by its own path rather than
        // collapsed into one directory entry (conformance review, independent pre-PR review
        // cycle 2), and .gitignore matches (bin/, obj/, artifacts/) never appear here at
        // all — git status omits an ignored path by default. That still leaves a known
        // .NET build/test byproduct that lands inside src/ or tests/ on a project whose own
        // .gitignore has not caught up with it — a `dotnet test --logger trx` run's default
        // `TestResults/` directory chief among them — which IsKnownBuildOrTestOutput excludes
        // by well-known directory name regardless of .gitignore, the same way `bin/` and
        // `obj/` are excluded whether or not a project ignores them (independent pre-PR
        // review cycle 1: without this, a fully committed session could fail before any gate
        // over its own gate's coverage output, a defect no retry can ever clear since the
        // next session's gates regenerate the same file). Anything untracked outside src/ or
        // tests/ (a coverage report at the repo root, a project home's own workspace notes)
        // is still just as likely a gate byproduct the project's .gitignore has not caught up
        // with, so it stays a warn-only signal — failing on it would be the same unclearable
        // defect.
        (IReadOnlyList<string> strandableUntrackedFiles, IReadOnlyList<string> byproductUntrackedFiles) =
            WorktreeGitStatus.SplitUntracked(untrackedFiles);

        if (byproductUntrackedFiles.Count > 0)
        {
            logger.LogWarning(
                "Run {RunId}: the worktree at {WorktreePath} has untracked file(s) not counted against the " +
                "uncommitted-files check: {Files}",
                run.Id, run.WorktreePath, SummarizeFiles(byproductUntrackedFiles));
        }

        // The failing list a resuming agent or a human sees: every untracked new file under
        // src/ or tests/, plus every modified-but-uncommitted tracked file — untracked first
        // so `SummarizeFiles`' MaxListedFiles cap, on a wide session, never drops preferentially
        // from the class this check exists to make visible (conformance review finding: a
        // session with 22 modified files and 3 new untracked ones would otherwise list all 22
        // modified and elide the 3 untracked as "and 3 more"). Named together so committing
        // only the modified ones can never look sufficient. Null only when git itself was
        // unobservable above; in that case untracked came back empty too, so there is nothing
        // to strand.
        IReadOnlyList<string>? strandedFiles = modifiedFiles is null
            ? null
            : [.. strandableUntrackedFiles, .. modifiedFiles];

        // "Uncommitted" alone stopped saying enough once strandedFiles started mixing
        // tracked-modified paths (already `git add`ed; `git commit` alone picks them up) with
        // brand-new untracked ones (`git add` first, since `git commit -am` silently skips
        // them) — a resuming agent reading only "uncommitted" could `git commit -am` the
        // modified half and reproduce the exact hollow-branch shape this check exists to catch
        // (independent pre-PR review, conformance finding, cycle 3). Built once so both reasons
        // below stay in sync.
        string? untrackedClarification = strandableUntrackedFiles.Count > 0
            ? $" {strandableUntrackedFiles.Count} of these {(strandableUntrackedFiles.Count == 1 ? "is" : "are")} " +
              "new and untracked, not modified — `git add` before `git commit`, since `git commit -a` alone will not pick them up."
            : null;

        // Research tasks are exempt from the no-commit check — their deliverable is the
        // transcript, not commits — and every spike kind is exempt too, whatever its own kind
        // (task: a spike is a run, not a walk — even a prototype spike, which does commit code
        // and does run the ordinary gates, is exempt from THIS check specifically, by the type's
        // own ruling, not because its deliverable is empty). Every other type ships its work as
        // commits. The uncommitted-files check right below is not exempt for any type: a task
        // that left modified or untracked files behind still stranded work, whatever its
        // deliverable is.
        if (task.Type != TaskType.Research && task.Type != TaskType.Spike)
        {
            // This run's own recorded base, not the project's: a stacked child's branch sits on
            // top of its parent's, so counting against the project's base would count the PARENT's
            // commits as this session's own — a stacked child that committed nothing at all would
            // read as productive and sail past the very check that exists to catch it (task: a
            // stacked pull-request edge exists as an explicit opt-in dependency).
            // The recorded fork point rides along as a second candidate boundary, and the count is
            // the smallest any of them reports: a parent force-pushed since this branch was cut
            // leaves this branch's copies of the parent's rewritten-away commits unreachable from
            // that ref, so they are counted as this session's own and a run that committed nothing
            // sails past this very check (class sweep, conformance review cycle 4).
            string baseBranch = run.BaseBranchOr(project.BaseBranch);
            int? commits = await CountBranchCommitsAsync(
                run.WorktreePath, baseBranch, run.StackedForkPoint(project.BaseBranch), cancellationToken);
            if (commits == 0)
            {
                string reason = strandedFiles is { Count: > 0 }
                    ? $"Agent produced no commits: branch '{run.Branch}' holds nothing beyond " +
                      $"'{baseBranch}'. The session ended with uncommitted files still sitting " +
                      $"in the worktree instead of being committed: {SummarizeFiles(strandedFiles)}." +
                      $"{untrackedClarification}"
                    : $"Agent produced no commits: branch '{run.Branch}' holds nothing beyond " +
                      $"'{baseBranch}'. The session ended without committing its work, so the " +
                      "gates were not run against the unmodified tree.";
                return new StrandedWorkCheck(reason, strandedFiles ?? []);
            }

            if (commits is null)
            {
                // Git is unobservable here (not a repo, unknown base ref); never guess —
                // proceed and let the gates surface whatever is actually broken.
                logger.LogWarning(
                    "Run {RunId}: could not count commits on branch {Branch} against {BaseBranch}; skipping the no-commit check",
                    run.Id, run.Branch, baseBranch);
            }
        }

        if (strandedFiles is { Count: > 0 })
        {
            string reason =
                "The session ended with uncommitted files still sitting in the worktree: " +
                $"{SummarizeFiles(strandedFiles)}.{untrackedClarification} Finished work left uncommitted " +
                "never reaches the pull request, so the gates were not run until it is committed.";
            return new StrandedWorkCheck(reason, strandedFiles);
        }

        return new StrandedWorkCheck(null, strandedFiles ?? [], Observed: strandedFiles is not null);
    }

    /// <summary>
    /// The branch the worktree is actually checked out to, or null when git could not be asked
    /// (never guessed at as matching). <c>git branch --show-current</c> rather than
    /// <c>rev-parse --abbrev-ref HEAD</c>: it exits 0 with empty output for a detached HEAD
    /// instead of failing, so a caller can tell "git is unreadable" (null, skip the check) apart
    /// from "checked out somewhere else, including detached" (empty or a different name, block)
    /// without a second command.
    /// <para>
    /// Mirrors <c>InteractiveWorktreeGit.GetCurrentBranchAsync</c>, which <c>h9k task deliver</c>
    /// itself uses for the identical branch-checkout guard — duplicated here rather than shared,
    /// because the CLI cannot reference <c>Hall9k.Daemon</c> (it never hosts Wolverine).
    /// <see cref="RunSupervisor"/>'s own deliberate-headless-start exit handler calls this before
    /// treating <see cref="DetectStrandedWorkAsync"/>'s clean-tree verdict as safe to auto-deliver:
    /// that check counts commits and reads status against whatever HEAD happens to be, never
    /// confirming HEAD is actually <c>run.Branch</c> (independent pre-PR review, cycle 1, both
    /// lenses — a session that died mid-recompose rebase can leave a clean, committed, but
    /// DETACHED tree, which would otherwise gate one tree and let <c>PullRequestOpener</c>
    /// publish a different, pre-rebase one).
    /// </para>
    /// </summary>
    internal static async Task<string?> GetCurrentBranchAsync(string worktreePath, CancellationToken cancellationToken)
    {
        (int exitCode, string output) = await RunGitAsync(worktreePath, ["branch", "--show-current"], cancellationToken);
        return exitCode == 0 ? output.Trim() : null;
    }

    /// <summary>
    /// One bounded, commit-only recovery session (task: when a session ends with finished work
    /// uncommitted, the daemon recovers on its own), spawned into the SAME worktree the failed
    /// session left dirty — never a second worktree checkout, since this run's own is still
    /// sitting right there with the retained work in it. Returns null when the recovery session
    /// actually leaves the tree clean, so the caller proceeds to the gates exactly as if nothing
    /// had gone wrong; otherwise returns the failure reason to record, always built from a FRESH
    /// re-check of the worktree — the ground truth of what actually happened, never the
    /// session's own self-reported result — rather than assumed from <paramref name="originalReason"/>.
    /// The recheck's own verdict, and the session's token usage, are both recorded on the run
    /// stream before returning (<see cref="RunUncommittedWorkRecoveryCompleted"/>, and a
    /// <c>TokensRecorded</c> when the session produced a result) — the same pair
    /// <c>BlockerContextAssembler.RecordCompletionAsync</c> records for its own one-off
    /// spawn-and-wait session, so this one's spend is never invisible to the periodic spend
    /// budget and so <c>h9k task show</c> has a real completion to read instead of inferring one
    /// from whatever the run's own state happens to be later.
    /// <para>
    /// Fresh session, not a <c>--resume</c> of the one that left the mess: a resumed session
    /// would still carry whatever review findings or fix instructions produced the uncommitted
    /// state, and this session's only job is committing what is already there
    /// (<see cref="AgentPromptBuilder.BuildUncommittedWorkRecovery"/>). Bounded twice over —
    /// <see cref="DaemonOptions.UncommittedWorkRecoveryMaxTurns"/> as a hard turn cap enforced by
    /// the executor itself, <see cref="DaemonOptions.UncommittedWorkRecoveryTimeout"/> as a
    /// wall-clock backstop enforced here — so a recovery can never cost anywhere near what the
    /// run it is recovering already cost.
    /// </para>
    /// <para>
    /// Spawn and wait are wrapped in one outer catch (the same discipline
    /// <c>BlockerContextAssembler.SynthesizeOrFallBackAsync</c> applies to its own one-off
    /// session): an exit that is neither the wait's own successful return nor its own timeout —
    /// most notably the daemon shutting down mid-wait, which cancels <paramref
    /// name="cancellationToken"/> itself rather than only the inner timeout source — still
    /// terminates the spawned process before propagating, so a session this dispatch has stopped
    /// caring about never keeps writing into the worktree unattended (independent pre-PR review,
    /// cycle 1, adversarial finding: an untracked orphan there could still be committing when a
    /// later <c>h9k task retry</c> resumes the same worktree).
    /// </para>
    /// <para>
    /// "Clean" is not enough on its own: a session that reverts or deletes an originally-stranded
    /// file also leaves `git status` clean, so <see cref="RecordRecoveryOutcomeAsync"/>'s own
    /// re-check additionally confirms every one of <paramref name="strandedFiles"/> actually
    /// reached a commit, byte for byte, rather than merely stopping to appear as dirty
    /// (independent pre-PR review, cycle 1, conformance finding). Every exit path — a spawn
    /// failure, an abandoned wait, or the ordinary success path — funnels through that same
    /// method, so the run's own recorded outcome is never left unrecorded just because the
    /// session never got to run at all.
    /// </para>
    /// </summary>
    private async Task<string?> RecoverUncommittedWorkOrExplainAsync(
        RunDetails run, TaskDetails task, ProjectDetails project, IReadOnlyList<string> strandedFiles,
        string originalReason, RunSessionLeg leg, CancellationToken cancellationToken)
    {
        Guid recoverySessionId = DomainId.New();

        // Recorded — and saved — before the spawn, not after: a daemon restart mid-wait must
        // find this fact on the stream even though the spawn's own outcome is still unknown
        // (the "save the decision before the wait" discipline commit 372acb38 fixed for the
        // session-error-retry leg). This is also what makes the attempt one-shot per leg: the
        // next VerifyAsync call for this run's SAME leg — whether this same call's own
        // post-recovery re-check, or a wholly separate later cycle's on that leg — reads this
        // back and never spawns a second one; a different leg on this run reads its own
        // eligibility independently (task: a headless build, fix, or recovery session never ends
        // its turn while a gate it started is still running in the background).
        await using (IDocumentSession recordSession = store.LightweightSession())
        {
            recordSession.Events.Append(run.Id, new RunUncommittedWorkRecoveryAttempted(
                run.Id, recoverySessionId, strandedFiles, originalReason, DateTimeOffset.UtcNow, leg));
            await recordSession.SaveChangesAsync(cancellationToken);
        }

        // Captured before the recovery session can touch anything, so a file that stops showing
        // up in `git status` afterward can be told apart from a file that was actually committed:
        // a `git checkout --` or a plain delete makes a stranded file vanish from status exactly
        // the same way committing it does, and only comparing what actually reached HEAD against
        // what the file held right here catches the difference (independent pre-PR review, cycle
        // 1, conformance finding — the prior check asked only whether the tree looked clean,
        // never whether the stranded work itself survived).
        IReadOnlyDictionary<string, string?> beforeBlobHashes =
            await HashStrandedFilesAsync(run.WorktreePath, strandedFiles, cancellationToken);

        string runDirectory = RunPaths.ResolveCurrentDirectory(run.RunDirectory);
        string streamFile = RunPaths.SessionStreamFile(runDirectory, SessionRoleName.CommitRecovery);
        // Scoped to the two fix-family legs alone (independent pre-PR review, cycle 1, conformance
        // lens, and this task's own class sweep): RunDetailsProjection only ever sets
        // LastFixEndedWaitingOnBackgroundGate from ReviewFixCompleted, and never clears it on any
        // other leg's own completion, so a later leg's recovery — a rebase-recovery session that
        // itself ended dirty, say — would otherwise be handed a prompt asserting its own
        // immediate predecessor said something only an earlier, unrelated fix session actually
        // said (AGENTS.md's never-guess rule). Both RunSessionLeg.Fix and
        // RunSessionLeg.HumanResolvedFix read ReviewFixCompleted's identical flag: the projection
        // does not distinguish which findings drove the fix session that just completed, only
        // that one did, so the flag is current for either leg itself — it is only a DIFFERENT
        // leg's own recovery this guards against.
        string prompt = AgentPromptBuilder.BuildUncommittedWorkRecovery(
            task, strandedFiles,
            priorSessionReportedBackgroundWait:
                (leg == RunSessionLeg.Fix || leg == RunSessionLeg.HumanResolvedFix)
                && run.LastFixEndedWaitingOnBackgroundGate,
            commandTimeout: options.Value.VerifyGateTimeout);

        SpawnedAgent? unfinished = null;
        AgentResult? result;
        try
        {
            SpawnedAgent agent;
            try
            {
                agent = await executor.SpawnAsync(new AgentSpawnRequest(
                    run.Id, recoverySessionId, run.WorktreePath, run.RunDirectory, prompt, run.ExecutorMode, run.Model,
                    project.SkipPermissions, SessionArtifactName: SessionRoleName.CommitRecovery,
                    MaxTurns: options.Value.UncommittedWorkRecoveryMaxTurns,
                    GuardsReviewThreadReplies: run.IsFollowUp)
                {
                    SessionName = SessionRoleName.For(DomainId.Short(task.Id), SessionRoleName.CommitRecovery),
                }, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception, "Run {RunId}: the automatic uncommitted-work recovery session could not be spawned", run.Id);
                await RecordRecoveryOutcomeAsync(
                    run, task, project, strandedFiles, beforeBlobHashes, result: null, cancellationToken);
                return $"{originalReason} An automatic commit-only recovery session could not even be started " +
                       $"({exception.Message}) — h9k task retry resumes this same worktree by hand.";
            }

            unfinished = agent;

            using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(options.Value.UncommittedWorkRecoveryTimeout);
            try
            {
                SessionWaitResult wait = await SessionResultWaiter.WaitAsync(
                    streamFile, agent.ProcessId, agent.StartedAt, processManager, onOutput: null, timeoutSource.Token);
                unfinished = null;
                result = wait.Result;

                if (wait.EndedAfterResultGrace)
                {
                    logger.LogWarning(
                        "Run {RunId}: the automatic uncommitted-work recovery session was ended after its result because it did not exit",
                        run.Id);
                }

                if (wait.Lingering.Count > 0)
                {
                    logger.LogWarning(
                        "Run {RunId}: the automatic uncommitted-work recovery session left {Count} process(es) still running after its terminal result arrived — terminated pid(s) {Pids}",
                        run.Id, wait.Lingering.Count, string.Join(", ", wait.Lingering));
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The turn cap is the primary bound; this is the backstop for the one thing it
                // cannot cover — a single turn whose own tool call hangs. Terminated rather than
                // left running, so a later `h9k task retry` never finds two sessions touching the
                // same worktree at once.
                processManager.Terminate(agent.ProcessId, agent.StartedAt);
                unfinished = null;
                logger.LogWarning(
                    "Run {RunId}: the automatic uncommitted-work recovery session exceeded its {Timeout} bound and was terminated",
                    run.Id, options.Value.UncommittedWorkRecoveryTimeout);
                result = null;
            }
        }
        catch (Exception exception)
        {
            // Reached only by an exit that is neither the wait's own successful return nor its
            // own timeout above — the daemon shutting down mid-wait (cancelling
            // cancellationToken itself, which the timeout catch above deliberately does not
            // swallow) or a throw between the spawn and the wait. Terminate whatever is still
            // running before this propagates: nothing else will ever adopt this session (it
            // reaches no RunProcessStarted-style event a startup adoption sweep keys off), so an
            // orphan here would keep writing into the worktree while a later h9k task retry
            // resumes the exact same one.
            if (unfinished is { } orphan)
            {
                try
                {
                    processManager.Terminate(orphan.ProcessId, orphan.StartedAt);
                }
                catch (Exception terminateException)
                {
                    logger.LogWarning(terminateException,
                        "Run {RunId}: could not terminate the abandoned uncommitted-work recovery session (pid {ProcessId})",
                        run.Id, orphan.ProcessId);
                }
            }

            if (exception is OperationCanceledException)
            {
                throw;
            }

            logger.LogWarning(exception,
                "Run {RunId}: the automatic uncommitted-work recovery session failed before it could finish", run.Id);
            await RecordRecoveryOutcomeAsync(
                run, task, project, strandedFiles, beforeBlobHashes, result: null, cancellationToken);
            return $"{originalReason} An automatic commit-only recovery session failed before it could finish " +
                   $"({exception.Message}) — h9k task retry resumes this same worktree by hand.";
        }

        logger.LogInformation(
            "Run {RunId}: automatic uncommitted-work recovery session ended ({Outcome})",
            run.Id,
            result is null ? "no result" : result.IsError ? $"error: {result.Summary ?? "(no message)"}" : "ok");

        RecoveryOutcome outcome = await RecordRecoveryOutcomeAsync(
            run, task, project, strandedFiles, beforeBlobHashes, result, cancellationToken);

        if (outcome.RecoveredCleanly != false)
        {
            // True (an observed clean tree with nothing discarded) and null (the re-check itself
            // was unobservable) both proceed to the gates — the same no-guess convention every
            // other unobservable git read in this method already follows: an unread worktree is
            // never treated as either clean or dirty, only as unknown.
            return null;
        }

        if (outcome.DiscardedFiles.Count > 0)
        {
            return $"{originalReason} The recovery session left the tree looking clean, but " +
                   $"{SummarizeFiles(outcome.DiscardedFiles)} no longer match(es) what was there before it ran " +
                   "— reverted or deleted rather than committed. h9k task retry resumes this same worktree by hand.";
        }

        return $"{outcome.StillStrandedReason} An automatic commit-only recovery session already ran once for the " +
               $"{leg.Value} leg and still did not leave the tree clean — h9k task retry resumes this same worktree by hand.";
    }

    /// <summary>
    /// The re-check and completion-event append shared by every exit
    /// <see cref="RecoverUncommittedWorkOrExplainAsync"/> can take — not only its success path —
    /// so a spawn failure or an abandoned wait leaves a real <see cref="RunUncommittedWorkRecoveryCompleted"/>
    /// on the stream too, rather than leaving <c>h9k task show</c> reading "outcome not yet
    /// recorded" for an outcome the daemon already knew for certain (independent pre-PR review,
    /// cycle 1, conformance finding). The discarded-files comparison only runs when the re-check
    /// itself found the tree fully clean and observable — a file still reported dirty, or a
    /// worktree that could not be read at all, is already the honest story on its own.
    /// </summary>
    private async Task<RecoveryOutcome> RecordRecoveryOutcomeAsync(
        RunDetails run, TaskDetails task, ProjectDetails project, IReadOnlyList<string> originalStrandedFiles,
        IReadOnlyDictionary<string, string?> beforeBlobHashes, AgentResult? result, CancellationToken cancellationToken)
    {
        StrandedWorkCheck recheck = await DetectStrandedWorkAsync(run, task, project, cancellationToken);

        IReadOnlyList<string> discardedFiles = recheck is { Observed: true, FailureReason: null }
            ? await DetectDiscardedFilesAsync(run.WorktreePath, originalStrandedFiles, beforeBlobHashes, cancellationToken)
            : [];

        bool? recoveredCleanly = recheck.Observed ? recheck.FailureReason is null && discardedFiles.Count == 0 : null;

        DateTimeOffset completedAt = DateTimeOffset.UtcNow;
        await using IDocumentSession completionSession = store.LightweightSession();
        if (result is not null)
        {
            completionSession.Events.Append(run.Id, result.ToTokensRecorded(run.Id, completedAt, run.Model));
        }

        completionSession.Events.Append(
            run.Id, new RunUncommittedWorkRecoveryCompleted(run.Id, recoveredCleanly, discardedFiles, completedAt));
        await completionSession.SaveChangesAsync(cancellationToken);

        return new RecoveryOutcome(recoveredCleanly, recheck.FailureReason, discardedFiles);
    }

    private readonly record struct RecoveryOutcome(
        bool? RecoveredCleanly, string? StillStrandedReason, IReadOnlyList<string> DiscardedFiles);

    /// <summary>
    /// The before-snapshot marker for a stranded file that is an uncommitted deletion (independent
    /// pre-PR review, cycle 2, medium finding): such a file has no bytes left to hash, so
    /// <see cref="HashStrandedFilesAsync"/> cannot record a blob for it the way it does for every
    /// other stranded shape. Recorded instead of null so <see cref="DetectDiscardedFilesAsync"/>
    /// still has something to weigh a recovery session's outcome against — a null entry there is
    /// skipped outright, which is exactly how a recovery session that restored the file instead of
    /// committing its removal used to pass as "recovered cleanly".
    /// </summary>
    private const string DeletedBeforeRecoveryMarker = "<deleted>";

    /// <summary>
    /// Every originally-stranded file's blob hash exactly as it sits in the worktree before the
    /// recovery session can touch it (`git hash-object`, which hashes a file's bytes the way git
    /// would store them without needing the file to be tracked or staged first). The ground truth
    /// <see cref="DetectDiscardedFilesAsync"/> later weighs the post-recovery commit against.
    /// A stranded file that no longer exists on disk — the shape a stranded deletion takes, since
    /// `git status` reported it as stranded (so it was tracked, or newly added) yet it is
    /// nonetheless absent — is recorded as <see cref="DeletedBeforeRecoveryMarker"/> rather than
    /// null: there is no blob to hash, but the fact that a deletion is what was stranded is itself
    /// still observed and worth recording. Null is reserved for a file this genuinely cannot
    /// account for — either `git status` itself is unreadable here, or `git status` reports
    /// something other than a deletion (a submodule pointer bump, a dangling symlink) that
    /// `git hash-object` still happens to fail on for reasons of its own — the same never-guess
    /// convention as every other git read in this file: a file this method cannot account for is
    /// never later flagged as discarded, since there would be nothing to compare it against.
    /// <see cref="File.Exists(string)"/> used to stand in for this and was wrong on both those
    /// shapes (independent pre-PR review, cycle 3, conformance finding): it reads false for a
    /// submodule's gitlink path (a directory, not a file) and for a dangling symlink exactly the
    /// same way it reads false for an actual deletion, so either one got misrecorded as a
    /// deletion and then unconditionally flagged discarded by <see cref="DetectDiscardedFilesAsync"/>
    /// even after the recovery session committed it correctly. `git status`'s own `D` status
    /// code is what the cycle-2 ruling actually asked for, and it does not confuse the three.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string?>> HashStrandedFilesAsync(
        string worktreePath, IReadOnlyList<string> strandedFiles, CancellationToken cancellationToken)
    {
        Dictionary<string, string?> hashes = [];
        foreach (string file in strandedFiles)
        {
            (int exitCode, string output) =
                await RunGitAsync(worktreePath, ["hash-object", "--", file], cancellationToken);
            if (exitCode == 0)
            {
                hashes[file] = output.Trim();
                continue;
            }

            bool? deletedPerStatus = await IsDeletedPerGitStatusAsync(worktreePath, file, cancellationToken);
            hashes[file] = deletedPerStatus == true ? DeletedBeforeRecoveryMarker : null;
        }

        return hashes;
    }

    /// <summary>
    /// Whether `git status` itself reports <paramref name="file"/> as a deletion (its own `D`
    /// status code, index or worktree side), read fresh rather than inferred from
    /// <see cref="File.Exists(string)"/> (independent pre-PR review, cycle 3, conformance
    /// finding — see <see cref="HashStrandedFilesAsync"/>'s own doc for why that inference was
    /// wrong). Null when `git status` itself could not be read; never guessed at as a deletion
    /// in that case.
    /// </summary>
    private static async Task<bool?> IsDeletedPerGitStatusAsync(
        string worktreePath, string file, CancellationToken cancellationToken)
    {
        (int exitCode, string output) = await RunGitAsync(
            worktreePath, ["status", "--porcelain", "-z", "--untracked-files=all", "--", file], cancellationToken);
        if (exitCode != 0)
        {
            return null;
        }

        string? entry = output.Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return entry is { Length: >= 2 } && (entry[0] == 'D' || entry[1] == 'D');
    }

    /// <summary>
    /// Which originally-stranded files the recovery session discarded rather than committed
    /// (independent pre-PR review, cycle 1, conformance finding): a file the post-recovery
    /// <see cref="DetectStrandedWorkAsync"/> no longer reports as dirty has either been committed
    /// as-is, or been made to vanish from `git status` some other way — reverted to whatever it
    /// held at the prior commit, or deleted outright — and only comparing the blob actually
    /// reachable at HEAD against the blob this file held before the recovery ran (<paramref
    /// name="beforeBlobHashes"/>) tells the two apart. Only called once the tree is otherwise
    /// fully clean, so every file this walks has already stopped being reported as stranded.
    /// A file this cannot hash on either side of the comparison is never flagged: an unobservable
    /// comparison is not evidence of a discard.
    /// <para>
    /// A file whose before-snapshot is <see cref="DeletedBeforeRecoveryMarker"/> (independent
    /// pre-PR review, cycle 2, medium finding) has no blob to compare — the check instead asks
    /// whether the deletion itself actually reached HEAD. A recovery session that restores the
    /// file rather than committing its removal leaves HEAD still holding it, which is the
    /// discard; a recovery session that commits the deletion leaves HEAD without it, which is
    /// success.
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyList<string>> DetectDiscardedFilesAsync(
        string worktreePath, IReadOnlyList<string> originalStrandedFiles,
        IReadOnlyDictionary<string, string?> beforeBlobHashes, CancellationToken cancellationToken)
    {
        List<string> discarded = [];
        foreach (string file in originalStrandedFiles)
        {
            if (!beforeBlobHashes.TryGetValue(file, out string? before) || before is null)
            {
                continue;
            }

            (int exitCode, string output) =
                await RunGitAsync(worktreePath, ["rev-parse", "-q", "--verify", $"HEAD:{file}"], cancellationToken);

            if (before == DeletedBeforeRecoveryMarker)
            {
                if (exitCode == 0)
                {
                    discarded.Add(file);
                }

                continue;
            }

            string? committed = exitCode == 0 ? output.Trim() : null;

            if (committed != before)
            {
                discarded.Add(file);
            }
        }

        return discarded;
    }

    private async Task<(bool Passed, string Summary, bool IsInfrastructureFailure, string? InfrastructureExcerpt, bool FellBackToFull, TimeSpan PermitWaitElapsed)>
        RunGateAsync(
        Guid runId, string runDirectory, string worktreePath, VerifyCommand gate, TestGateScope? scope,
        ActiveGate? recordedActiveGate, CancellationToken cancellationToken)
    {
        string logFile = Path.Combine(runDirectory, $"verify-{Sanitize(gate.Name)}.log");
        Directory.CreateDirectory(runDirectory);

        // Where CrossProcessContainerGate.AcquireAsync leaves durable evidence that a
        // dotnet-test-shaped gate was still queued on the machine-wide container gate at the
        // moment it was killed (PLAN.md §16 #132). Never the gate's own captured console output:
        // vstest.console buffers a testhost's Console.Error internally and only relays it if
        // vstest.console itself survives long enough to report the crash, which
        // process.Kill(entireProcessTree: true) below does not allow — root, dotnet test,
        // vstest.console and testhost are all killed together, so vstest.console never gets the
        // chance to notice testhost died and print anything (adversarial review, this cycle,
        // reproduced against this repo's own package versions: the marker never once reached the
        // redirected log under a real entireProcessTree kill). A file written directly by the
        // waiting process, independent of any parent surviving to relay it, has no such gap.
        // Reset per gate run so a directory left behind by an earlier attempt at this same gate
        // can never be mistaken for evidence of the current one.
        string gateWaitDirectory = Path.Combine(runDirectory, $"gate-wait-{Sanitize(gate.Name)}");
        if (Directory.Exists(gateWaitDirectory))
        {
            Directory.Delete(gateWaitDirectory, recursive: true);
        }
        Directory.CreateDirectory(gateWaitDirectory);

        // A host-coupled gate's own filter always applies when it runs — never combined with
        // scope narrowing, since this method is only ever called for one with runHostCoupledGate
        // true, which is exactly the condition under which `scope` below is never IsScoped
        // (#225). Applied before the scope block so the scope header, when one is
        // written, still describes the gate that actually ran (host-coupled and full).
        string command = ComposeGateCommand(gate);

        // Scoping only ever touches a `dotnet test`-shaped gate's own command — a build gate, a
        // lint gate, anything else configured runs exactly as the project wrote it, scope or not.
        string? header = null;
        if (scope is not null && IsDotnetTestGate(gate.Command))
        {
            header = scope.IsScoped
                ? $"# hall9k test gate: scoped -- {scope.Reason}{Environment.NewLine}# filter: {scope.FilterExpression}{Environment.NewLine}"
                : $"# hall9k test gate: full -- {scope.Reason}{Environment.NewLine}";
            if (scope.IsScoped)
            {
                command = ApplyTestFilter(command, scope.FilterExpression!);
            }
        }

        if (header is not null)
        {
            // Written before the gate runs, not appended after: the run artifacts say which mode
            // ran and why even if the gate itself times out or the process never produces output.
            try
            {
                await File.WriteAllTextAsync(logFile, header, cancellationToken);
            }
            catch (IOException ioException)
            {
                // A lock an orphaned prior attempt left on this exact log is an infrastructure
                // failure by construction (Windows field report, 2026-09-19: a daemon restart
                // mid-gate left the old process tree holding verify-test.log locked, and the
                // resumed pipeline crash-looped reopening it every dispatch cycle instead of
                // failing honestly and spending its retry) — nothing of the gate ran, so nothing
                // was observed about the agent's own work. Named against whatever gate process
                // this run's own stream still recorded before this attempt started, since that is
                // the process most likely still holding the handle if startup adoption's own
                // orphan-tree cleanup (RunSupervisor.AdoptOrphansAsync) missed it, or the
                // operating system simply has not finished releasing it yet.
                string recordedGateProcessDescription = recordedActiveGate is { } activeGate
                    ? $"the run's own recorded gate process for '{activeGate.GateName}' (pid {activeGate.ProcessId}, started {activeGate.StartedAt:u})"
                    : "no gate process recorded on the run";
                string openFailure =
                    $"Gate '{gate.Name}' could not open its verify log at '{logFile}': {ioException.Message} " +
                    $"-- {recordedGateProcessDescription} may still be holding it. Nothing of the gate ran, " +
                    "so this says nothing about the work under test.";
                return (false, openFailure, true, null, false, TimeSpan.Zero);
            }
        }

        string redirect = header is null ? ">" : ">>";
        string innerCommand = $"({command}) {redirect} \"{logFile}\" 2>&1";

        // At most one host-coupled gate runs on this node at a time (task: host-coupled tests run
        // in their own gate once per task, never in parallel with another run's copy —
        // #225): held for the whole spawn-and-wait span below, released in the
        // same finally block that already records GateEnded, so a second run's own host-coupled
        // gate never runs concurrently with this one's on the same machine.
        //
        // Timed separately from the gate process itself (independent pre-PR review, cycle 1, both
        // lenses, low): the caller's own Stopwatch wraps this whole method, so without subtracting
        // this wait back out, two runs racing for the permit would record a gate duration that is
        // queue time plus run time rather than the gate's own cost — the exact number
        // h9k task show's Gates cell prints, the anomaly flag compares against, and
        // AdHocGateRunner.ComputeComparisonBudget budgets the next clean-base comparison from.
        Stopwatch permitStopwatch = Stopwatch.StartNew();
        IAsyncDisposable? hostCoupledPermit = gate.IsHostCoupled
            ? await AcquireHostCoupledGatePermitAsync(runId, cancellationToken)
            : null;
        TimeSpan permitWaitElapsed = hostCoupledPermit is null ? TimeSpan.Zero : permitStopwatch.Elapsed;
        try
        {
            (bool passed, string summary, bool isInfrastructureFailure, string? infrastructureExcerpt, bool fellBackToFull) =
                await RunGateProcessAsync(
                    runId, runDirectory, worktreePath, gate, scope, logFile, gateWaitDirectory, innerCommand,
                    recordedActiveGate, cancellationToken);
            return (passed, summary, isInfrastructureFailure, infrastructureExcerpt, fellBackToFull, permitWaitElapsed);
        }
        finally
        {
            if (hostCoupledPermit is not null)
            {
                await hostCoupledPermit.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// The spawn, wait, and classification half of <see cref="RunGateAsync"/> — split out only so
    /// the node-wide host-coupled-gate permit (<see cref="AcquireHostCoupledGatePermitAsync"/>)
    /// can wrap this whole span in one try/finally without disturbing this method's own internal
    /// control flow (#225). <paramref name="innerCommand"/> is already the fully
    /// composed shell command — scope filter, host-coupled filter, and log redirection all
    /// applied — so this method never reads <c>gate.Command</c> for anything but
    /// <see cref="IsDotnetTestGate"/> checks and its own recursive fallback call, which goes back
    /// through <see cref="RunGateAsync"/> (never this method directly) rather than calling itself.
    /// <para>
    /// That fallback only ever triggers under <c>scope is {{ IsScoped: true }}</c> (a scoped
    /// filter that intersected to nothing), and a host-coupled gate only ever runs unscoped —
    /// <c>runHostCoupledGate</c> is keyed on <c>scopeSinceSha is null</c>, the identical condition
    /// that resolves <c>scope</c> to <see cref="TestGateScope.Full"/> — so the two paths can never
    /// meet as this code stands (independent pre-PR review, cycle 1, conformance lens: an earlier
    /// version of this comment called that a safety property, "a fallback run acquires its own
    /// permit too, when the gate it is falling back for happens to be host-coupled" — the opposite
    /// is true. If a host-coupled gate's fallback ever became reachable, the nested
    /// <c>RunGateAsync</c> would try to acquire this same node's permit while the OUTER call still
    /// holds the <c>FileShare.None</c> lock, the reopen would fail with the identical
    /// <see cref="IOException"/> the lock is designed to raise against anyone else, and the 200ms
    /// poll loop in <see cref="AcquireHostCoupledGatePermitAsync"/> would spin until this run's own
    /// cancellation fired — a hung verification, not a second permit.
    /// </para>
    /// </summary>
    private async Task<(bool Passed, string Summary, bool IsInfrastructureFailure, string? InfrastructureExcerpt, bool FellBackToFull)>
        RunGateProcessAsync(
        Guid runId, string runDirectory, string worktreePath, VerifyCommand gate, TestGateScope? scope,
        string logFile, string gateWaitDirectory, string innerCommand, ActiveGate? recordedActiveGate,
        CancellationToken cancellationToken)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            WorkingDirectory = worktreePath,
            UseShellExecute = false,
        };
        NonInteractiveGit.Apply(process.StartInfo);
        // Harmless for a gate that never touches CrossProcessContainerGate: nothing reads this
        // variable unless a dotnet-test-shaped gate's own PostgresFixture instances call
        // CrossProcessContainerGate.AcquireAsync, so it costs nothing to set it unconditionally
        // rather than special-casing IsDotnetTestGate here too.
        process.StartInfo.Environment[GateInfrastructureFailureClassifier.GateWaitEvidenceDirectoryEnvironmentVariable] = gateWaitDirectory;
        if (OperatingSystem.IsWindows())
        {
            // Windows field report item 3 (ruled 2026-09-01): two concurrent dotnet-test-shaped
            // gates on one Windows machine crashed each other's shared MSBuild child nodes with
            // MSB4166. Applied at the spawn, for every gate, rather than asked of each project's
            // own verify command — a build or lint gate that shells out to MSBuild indirectly is
            // exposed to the same node-reuse crash, not just a `dotnet test` gate. A project's own
            // verify command that sets this variable itself (`set MSBUILDDISABLENODEREUSE=0 && ...`)
            // still wins: that assignment runs inside the shell after this process's environment is
            // inherited, not before it, so it simply overwrites this default for its own session.
            // Single-process MSBuild (-m:1), the field workaround's other half, is deliberately not
            // set here: the arx workaround used both flags together, but only this cheap half
            // ships now, since GateInfrastructureFailureClassifier's own MSB4166 recognition makes
            // a residual node-reuse crash cost a retry rather than a dead task.
            //
            // Scoped to Windows only, not because MSBuild's own node reuse is Windows-specific —
            // it isn't; `dotnet build-server shutdown` exists on every platform — but because the
            // field report this fix answers was a Windows machine, and nothing has yet confirmed
            // the identical contention on a non-Windows node. The MSB4166 marker just below
            // classifies regardless of platform already, so a non-Windows occurrence still costs
            // a retry rather than a dead task; only this preventive half is narrowed to the
            // platform the incident was actually observed on (independent pre-PR review, cycle 1).
            process.StartInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";

            // The raw Arguments string, never ArgumentList (see WindowsCommandLine): a
            // project's verify command is entirely capable of carrying its own embedded
            // quotes (this repo's own CI filter, `--filter "Category!=RequiresDocker"`,
            // is exactly that shape), and ArgumentList would C-runtime-escape them in a
            // way cmd.exe's own /c parsing does not undo.
            process.StartInfo.FileName = "cmd.exe";
            process.StartInfo.Arguments = WindowsCommandLine.WrapForCmdExe(innerCommand);
        }
        else
        {
            process.StartInfo.FileName = "/bin/sh";
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add(innerCommand);
        }

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // A gate the operating system refused to launch at all is an infrastructure failure by
            // construction, whatever words the exception happened to use: not one line of the
            // agent's work ran, so nothing was observed about it, and the classifier's marker list
            // has no business being consulted (it recognizes what a gate PRINTED, and this gate
            // printed nothing). Previously this ran the message past the marker list, which
            // matches none of the shapes a spawn failure actually takes — "The system cannot find
            // the file specified", a desktop-heap or memory refusal under load — so a machine that
            // could not start the gate was recorded as the agent's own broken work and denied the
            // one retry that would have cleared it (AGENTS.md: never guess at unobserved facts).
            string startFailure =
                $"Gate '{gate.Name}' could not start: {exception.Message} Nothing of the gate ran, so this " +
                "says nothing about the work under test.";

            // No excerpt: BuildRetryCause labels one "Matching signature", and nothing matched
            // here — the classification came from the spawn failing, not from anything printed.
            return (false, startFailure, true, null, false);
        }

        // Recorded the instant the process is actually up, so a display reading this run mid-gate
        // sees the same identity this method is about to wait on (task: a run whose verification
        // gate is executing is reported as live work in progress, never as stalled with no
        // session recorded) — the gate's own pid-plus-start-time, the identical shape an agent
        // session already carries (the PID-reuse guard, log #2).
        DateTimeOffset gateProcessStartedAt = ReadProcessStartedAt(process);
        try
        {
            await RecordGateStartedAsync(runId, gate.Name, process.Id, gateProcessStartedAt, cancellationToken);
        }
        catch (Exception exception)
        {
            // Unlike a daemon shutdown caught further down this method (where GateStarted has
            // already landed, so a restart's own adoption can find and end this process later),
            // a GateStarted that never reaches the stream leaves nothing for any future adoption
            // sweep to discover at all — this process cannot become an orphan a restart later
            // cleans up, because nothing will ever know it exists (task: a gate start that fails
            // to record never leaves an unrecorded tree behind). Killed here, unconditionally,
            // before this rethrows.
            try
            {
                processManager.TerminateTree(process.Id, gateProcessStartedAt);
            }
            catch (Exception terminateException)
            {
                logger.LogWarning(terminateException,
                    "Run {RunId}: could not terminate the just-started gate '{Gate}' process (pid {ProcessId}) after GateStarted failed to record",
                    runId, gate.Name, process.Id);
            }

            logger.LogWarning(exception,
                "Run {RunId}: GateStarted failed to record for gate '{Gate}' (pid {ProcessId}) — its process tree was terminated rather than left unrecorded",
                runId, gate.Name, process.Id);
            throw;
        }

        // Whether this attempt's own end is safe to record: every path below that actually waits
        // for the process (the timeout branch's own kill-and-wait, or an ordinary exit) confirms
        // its death and leaves this true, its default. Only the one path that does neither — the
        // daemon itself shutting down mid-wait, caught below — turns it off, so a run whose gate
        // process this method never actually confirmed dead keeps reading as still attached to it
        // rather than as idle (see GateEnded's own doc for why that matters more than tidiness).
        bool gateAttemptConfirmedEnded = true;
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Value.VerifyGateTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);

                // Kill only ASKS the operating system to terminate the tree; it returns before the
                // tree is actually gone (Process.Kill's own documented contract). The classification
                // just below reads what the gate wrote, so reading here — while the gate's own shell
                // still holds the redirected log and may still be writing its last line into it —
                // reads a file mid-teardown. Waiting for the root's own exit first is what makes
                // "what the gate wrote before it was killed" a settled fact rather than a race,
                // bounded so a process the operating system cannot reap never wedges the run.
                await WaitForKilledProcessAsync(process, cancellationToken);

                string timeoutFailure = $"Gate '{gate.Name}' exceeded the {options.Value.VerifyGateTimeout.TotalMinutes:0}-minute timeout.";

                // A hang classifies on what the gate actually wrote before it was killed, not on
                // this synthetic message — the message never carries a marker, so a container that
                // never comes up (a startup hang, not a non-zero exit) would otherwise be silently
                // unclassifiable and blamed on the agent's work (adversarial review, cycle 2).
                // A log nobody could read is not the same as a gate that printed nothing: it is an
                // unobserved fact, so it classifies as infrastructure rather than being pinned on
                // work this run never got to look at (see UnreadableGateLog).
                string? timeoutOutput = ReadFullOutput(logFile);
                bool timeoutIsInfrastructureFailure =
                    timeoutOutput is null || GateInfrastructureFailureClassifier.IsInfrastructureFailure(timeoutOutput);
                string? timeoutExcerpt = timeoutOutput is null
                    ? null
                    : GateInfrastructureFailureClassifier.MatchingExcerpt(timeoutOutput);
                if (timeoutOutput is null)
                {
                    timeoutFailure =
                        $"{timeoutFailure} Also {UnreadableGateLog(logFile)}, so nothing is known about what it " +
                        "wrote before it was killed.";
                }

                // A gate whose own permit wait is still unresolved at the moment of the kill is a
                // second, distinct shape of infrastructure timeout: the process never got past
                // CrossProcessContainerGate.AcquireAsync's own unbounded wait (PLAN.md §16 #132), so
                // it never even reached the agent's own tests, and VerifyGateTimeout's budget — sized
                // for one process's own tier duration — can legitimately be
                // outlasted by ordinary cross-process contention under a raised node ceiling or a
                // concurrent foreground run. Read from gateWaitDirectory, never the gate's own
                // captured console output (see GateInfrastructureFailureClassifier.
                // IsUnresolvedGateWaitTimeout's own comment for why that can never be relied on here).
                if (!timeoutIsInfrastructureFailure &&
                    GateInfrastructureFailureClassifier.IsUnresolvedGateWaitTimeout(gateWaitDirectory, options.Value.VerifyGateTimeout))
                {
                    timeoutIsInfrastructureFailure = true;
                    timeoutExcerpt = GateInfrastructureFailureClassifier.UnresolvedGateWaitExcerpt(
                        gateWaitDirectory, options.Value.VerifyGateTimeout);
                }

                // Text only, never a classification input: what actually decided
                // timeoutIsInfrastructureFailure above is the gate's own captured output and the
                // per-run wait-evidence directory, exactly as before this task. This is
                // additional evidence for a human reading the failure — who, if anyone, held the
                // shared container gate's permits at the moment this gate was killed — not a
                // second vote on what the kill means (task: a killed gate names who held the
                // permits).
                if (ContainerGateDirectory.DescribeContents(ContainerGateDirectory.Resolve()) is { } gateDirectoryListing)
                {
                    timeoutFailure = $"{timeoutFailure} {gateDirectoryListing}";
                }

                return (false, timeoutFailure, timeoutIsInfrastructureFailure, timeoutExcerpt, false);
            }

            if (process.ExitCode == 0)
            {
                if (scope is { IsScoped: true } && IsDotnetTestGate(gate.Command))
                {
                    // The scoped filter combined with whatever filter the gate already carried (this
                    // repo's own CI filter, `Category!=RequiresDocker`, among them) can intersect to
                    // nothing even though TestScopeResolver mapped at least one class from the fix's
                    // own commits — VSTest's default (`TreatNoTestsAsError=false`) exits 0 on "no test
                    // matches the given testcase filter", which would otherwise stand a run that
                    // executed nothing in for a passed one (independent pre-PR review, cycle 1),
                    // exactly what TestGateScope's own contract promises never happens. Falling back
                    // to a full, unscoped run of this one gate costs the rare intersect-to-zero case
                    // a second gate run rather than a silent false green or a spurious failure of a
                    // perfectly good fix.
                    // A log nobody could read cannot show a VSTest summary either, and TestGateScope's
                    // own contract is that a run which executed nothing never stands in for a passed
                    // one — so an unreadable log takes the fallback rather than being assumed to have
                    // executed tests. It costs one extra full gate run in a case that should not
                    // happen at all now that the read tolerates a concurrent writer.
                    string? scopedOutput = ReadFullOutput(logFile);
                    if (scopedOutput is null || ScopedRunExecutedNoTests(scopedOutput))
                    {
                        string vacuityDescription = scopedOutput is null
                            ? $"{UnreadableGateLog(logFile)}, so whether the scoped run executed any tests is unknown"
                            : DescribeScopedRunVacuity(scopedOutput);
                        logger.LogWarning(
                            "Gate '{Gate}': {Description} (filter \"{Filter}\" combined with the gate's own " +
                            "configured filter); falling back to a full run of this gate",
                            gate.Name, vacuityDescription, scope.FilterExpression);
                        (bool fallbackPassed, string fallbackSummary, bool fallbackIsInfrastructureFailure, string? fallbackExcerpt, _, _) =
                            await RunGateAsync(
                                runId, runDirectory, worktreePath, gate,
                                TestGateScope.Full($"{vacuityDescription} ({scope.Reason})"),
                                recordedActiveGate, cancellationToken);
                        return (fallbackPassed, fallbackSummary, fallbackIsInfrastructureFailure, fallbackExcerpt, true);
                    }
                }

                return (true, "ok", false, null, false);
            }

            // Classification reads the gate's whole output, never just the truncated tail kept
            // for the summary: a marker logged early in a large `dotnet test` run must not be
            // pushed out of a fixed-size window and go unclassified (adversarial review, cycle 1).
            // A log that could not be read at all is the unobserved case, not the empty one, and is
            // classified as infrastructure for the reason UnreadableGateLog spells out.
            string? fullOutput = ReadFullOutput(logFile);
            if (fullOutput is null)
            {
                return (false,
                    $"Gate '{gate.Name}' exited {process.ExitCode}, and {UnreadableGateLog(logFile)}, so nothing " +
                    "is known about why it failed.",
                    true, null, false);
            }

            bool isInfrastructureFailure = GateInfrastructureFailureClassifier.IsInfrastructureFailure(fullOutput);
            string summary = $"Gate '{gate.Name}' exited {process.ExitCode}. Output: {TailOf(fullOutput)}";
            return (false, summary, isInfrastructureFailure, GateInfrastructureFailureClassifier.MatchingExcerpt(fullOutput), false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The daemon itself is shutting down mid-gate: this method neither waited for the
            // process to exit nor killed and confirmed it, so there is nothing observed here to
            // call an ending. Left standing, the same as a session process daemon shutdown leaves
            // running unattended — a restart's own adoption (or the next h9k status) checks this
            // recorded identity against the operating system fresh and reports honestly on
            // whatever it actually finds, rather than being told by this method that a process it
            // never confirmed dead has ended.
            gateAttemptConfirmedEnded = false;
            throw;
        }
        finally
        {
            if (gateAttemptConfirmedEnded)
            {
                await RecordGateEndedAsync(runId, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Waits for a tree this method just killed to actually be gone. <c>Process.Kill</c> is
    /// asynchronous by contract — it asks the operating system to terminate and returns — so the
    /// gate's own last writes and its handle on the redirected log both outlive the call, and a
    /// classifier reading that log immediately afterward is reading a file mid-teardown. Bounded
    /// at <see cref="KilledGateReapBudget"/>: a few seconds spent letting a killed gate finish
    /// dying is what makes the next read a settled observation, and a tree the operating system
    /// still has not reaped inside that budget is left alone rather than waited on forever.
    /// <para>
    /// The caller only reaches this path when the run itself was NOT cancelled, so
    /// <paramref name="cancellationToken"/> can only fire here as a daemon shutdown arriving
    /// mid-wait — which is a reason to stop waiting, not to keep the shutdown blocked behind the
    /// reap budget, so it is linked in rather than ignored (independent pre-PR review, cycle 1,
    /// conformance lens, low). Either way the wait ending early only costs the read that follows
    /// its settledness, and <see cref="ShareTolerantFile"/> makes that read survive a writer that
    /// still holds the log.
    /// </para>
    /// </summary>
    private static async Task WaitForKilledProcessAsync(Process process, CancellationToken cancellationToken)
    {
        using CancellationTokenSource reaping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        reaping.CancelAfter(KilledGateReapBudget);
        try
        {
            await process.WaitForExitAsync(reaping.Token);
        }
        catch (OperationCanceledException)
        {
            // Still not reaped, or the daemon is shutting down. Nothing here can make the
            // operating system finish, and the read that follows tolerates a writer that still
            // holds the log, so carry on.
        }
    }

    /// <summary>
    /// How long a gate killed for exceeding <see cref="DaemonOptions.VerifyGateTimeout"/> is given
    /// to actually die before the run stops waiting on it. Not a configurable setting: this is the
    /// operating system's own teardown latency, measured in milliseconds in practice, and the
    /// budget exists only so a tree that never dies cannot wedge the gate loop.
    /// </summary>
    private static readonly TimeSpan KilledGateReapBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the gate loop waits before its one infrastructure-classified retry (independent
    /// pre-PR review, cycle 1, adversarial lens): long enough to outlast the residual OS teardown
    /// delay right after a kill that a log-open <see cref="IOException"/> can race, short enough
    /// to cost nothing worth naming against every other infrastructure classification, which is
    /// reached only after the gate has already run for minutes.
    /// </summary>
    private static readonly TimeSpan InfrastructureRetryDelay = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Names the log that could not be read, as a clause each caller finishes with what that
    /// costs where it stands: a failing gate learns nothing about why it failed, a passing scoped
    /// gate learns nothing about whether it executed any tests. Either way an unreadable log is
    /// an unobserved fact, not an observed absence of output, so it never counts as evidence the
    /// agent's work is what failed (AGENTS.md's never-guess rule) — the caller classifies it as
    /// infrastructure, spending the gate's one retry on a second look instead.
    /// </summary>
    private static string UnreadableGateLog(string logFile) =>
        $"its own output log ({logFile}) could not be read";

    /// <summary>
    /// Commits the branch carries beyond the base — the SMALLEST count any boundary the base is
    /// known by reports, over the remote-tracking ref, the local base as the no-origin fallback
    /// (the log #4 convention), and, for a stacked child, this run's own recorded fork point
    /// (<paramref name="forkPointCommit"/>, null for every ordinary run). Null when git could not
    /// answer for any of them: an unobservable count is never treated as zero.
    /// <para>
    /// Smallest rather than first-that-resolves, because each boundary is wrong in the same
    /// direction and only sometimes: a two-dot count is inflated by every commit the boundary does
    /// not reach, so a boundary that is not on this branch counts this branch's copies of the
    /// parent's commits as this session's own. Both candidates go stale that way and neither one
    /// always — <c>origin/&lt;parent&gt;</c> when the parent was force-pushed since the cut, the
    /// recorded fork point when the branch was later brought onto a newer parent head — and none of
    /// them can ever UNDERcount, since a commit this session authored is reachable from no boundary
    /// but this branch. The smallest is therefore the most accurate answer available, and it is the
    /// safe direction for the no-commit check this feeds: an inflated count reads a session that
    /// committed nothing as productive and sails past the very check that exists to catch it (class
    /// sweep, conformance review cycle 4). For an unstacked run this still resolves to the
    /// remote-tracking ref's own count, since a stale local base branch can only be behind it and
    /// so can only count higher.
    /// </para>
    /// </summary>
    private static async Task<int?> CountBranchCommitsAsync(
        string worktreePath, string baseBranch, string? forkPointCommit, CancellationToken cancellationToken) =>
        (await ResolveBranchBoundaryAsync(worktreePath, baseBranch, forkPointCommit, cancellationToken)).SmallestCount;

    /// <summary>
    /// The boundary-selection half of <see cref="CountBranchCommitsAsync"/>'s own doc, extracted so
    /// <see cref="GetChangedPathsAsync"/> can diff against the identical winning boundary rather
    /// than recomputing its own, possibly different, notion of "this branch's base" (task: a
    /// delivered diff that touches no buildable or testable source skips the build and test
    /// gates). Returns both the smallest count <see cref="CountBranchCommitsAsync"/> has always
    /// returned and the boundary ref that produced it — null <see cref="Boundary"/> only when
    /// every candidate boundary was itself unreadable, the same unobservable case
    /// <see cref="SmallestCount"/> already reports as null.
    /// </summary>
    private static async Task<(int? SmallestCount, string? Boundary)> ResolveBranchBoundaryAsync(
        string worktreePath, string baseBranch, string? forkPointCommit, CancellationToken cancellationToken)
    {
        string[] boundaries = forkPointCommit is null
            ? [$"origin/{baseBranch}", baseBranch]
            : [forkPointCommit, $"origin/{baseBranch}", baseBranch];
        int? smallest = null;
        string? winningBoundary = null;
        foreach (string baseRef in boundaries)
        {
            (int exitCode, string output) = await RunGitAsync(
                worktreePath, ["rev-list", "--count", $"{baseRef}..HEAD"], cancellationToken);
            if (exitCode == 0 && int.TryParse(output.Trim(), out int count) && (smallest is null || count < smallest))
            {
                smallest = count;
                winningBoundary = baseRef;
            }
        }

        return (smallest, winningBoundary);
    }

    /// <summary>
    /// Every path this run's branch changed against the identical boundary
    /// <see cref="CountBranchCommitsAsync"/> itself resolves to (task: a delivered diff that
    /// touches no buildable or testable source skips the build and test gates) — deletions and
    /// renames included: <c>git diff --name-status -z</c> records a rename or copy as two
    /// NUL-terminated paths (the old name, then the new) rather than one, and both are returned
    /// separately so either one falling outside the project's non-executable set is what forces
    /// the ordinary gates to run. Null when the boundary itself could not be resolved, or the diff
    /// itself could not be read — never guessed as "nothing changed" (AGENTS.md's never-guess
    /// rule), the same convention every other git read in this file already follows.
    /// </summary>
    private static async Task<IReadOnlyList<string>?> GetChangedPathsAsync(
        string worktreePath, string baseBranch, string? forkPointCommit, CancellationToken cancellationToken)
    {
        (_, string? boundary) = await ResolveBranchBoundaryAsync(worktreePath, baseBranch, forkPointCommit, cancellationToken);
        if (boundary is null)
        {
            return null;
        }

        (int exitCode, string output) = await RunGitAsync(
            worktreePath, ["diff", "--name-status", "-z", $"{boundary}..HEAD"], cancellationToken);
        return exitCode == 0 ? ParseChangedPaths(output) : null;
    }

    /// <summary>
    /// Parses <c>git diff --name-status -z</c>'s own NUL-separated record shape: an ordinary
    /// change (A/M/D/T/U) is one status token followed by one path; a rename or copy (R/C, each
    /// carrying a trailing similarity score digit string like <c>R100</c>) is one status token
    /// followed by TWO paths, the old name and the new. A truncated trailing record (the output
    /// ended mid-record) is dropped rather than guessed at.
    /// </summary>
    private static IReadOnlyList<string> ParseChangedPaths(string nameStatusOutput)
    {
        string[] tokens = nameStatusOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        List<string> paths = [];
        int index = 0;
        while (index < tokens.Length)
        {
            string status = tokens[index++];
            char kind = status.Length > 0 ? status[0] : '\0';
            if (kind is 'R' or 'C')
            {
                if (index + 1 >= tokens.Length)
                {
                    break;
                }

                paths.Add(tokens[index++]);
                paths.Add(tokens[index++]);
                continue;
            }

            if (index >= tokens.Length)
            {
                break;
            }

            paths.Add(tokens[index++]);
        }

        return paths;
    }

    /// <summary>
    /// Every tracked file the worktree holds modified or staged at session end (backlog 57),
    /// plus, separately, every untracked one — `git status --porcelain -z --untracked-files=all`,
    /// NUL-separated rather than the default newline-and-quote form so a path holding a space or
    /// a non-ASCII character (`core.quotePath`'s octal-escaping) comes back verbatim instead of
    /// quoted (conformance review finding), and with untracked files fully expanded rather than
    /// collapsed to one entry per new directory, so a brand-new vertical slice never `git add`ed
    /// is named file by file (conformance review, independent pre-PR review cycle 2). The parsing
    /// itself — including the rename/copy entry's second NUL-terminated field — is
    /// <see cref="WorktreeGitStatus.ParsePorcelain"/>, shared with the interactive claim's own
    /// checks (<c>InteractiveWorktreeGit</c>) rather than duplicated (adversarial review, cycle 4).
    /// Untracked files are reported separately from the modified list because the caller applies
    /// different rules to each: one under src/ or tests/ is first-class strandable work (origin
    /// incident, 2026-08-29, the Jira compose/execute task — a whole feature's new source files
    /// named only in a warning, not the failing list, so a faithful commit of the named files
    /// still shipped a hollow branch), while one elsewhere (a coverage report at the repo root, a
    /// project home's own workspace notes) is still as likely a gate's own build or test output
    /// the project's `.gitignore` has not caught up with, and failing on that would be a defect a
    /// retry can never clear, since the next session's gates regenerate the same file (adversarial
    /// review, independent pre-PR review cycle 1). A `.gitignore` match (`bin/`, `obj/`,
    /// `artifacts/`) never reaches either list: git status omits an ignored path by default, with
    /// no `--ignored` flag passed here. A well-known .NET build or test output directory landing
    /// inside src/ or tests/ anyway — `TestResults/` (VSTest's own default results directory)
    /// chief among them — is excluded from the strandable list the same way, by name, regardless
    /// of `.gitignore` (<see cref="WorktreeGitStatus.IsKnownBuildOrTestOutput"/>; independent pre-PR review cycle 1
    /// again — without it, that directory living inside the test project's own folder under
    /// tests/ would otherwise fail a fully committed run on its own gate's coverage output). The
    /// modified-list slot is null when git cannot answer, the same "never guess" convention
    /// <see cref="CountBranchCommitsAsync"/> already follows: an unobservable worktree is never
    /// reported as clean.
    /// </summary>
    private static async Task<(IReadOnlyList<string>? Modified, IReadOnlyList<string> Untracked)>
        ListUncommittedFilesAsync(string worktreePath, CancellationToken cancellationToken)
    {
        (int exitCode, string output) =
            await RunGitAsync(
                worktreePath,
                ["status", "--porcelain", "-z", "--untracked-files=all"],
                cancellationToken);
        if (exitCode != 0)
        {
            return (null, []);
        }

        (IReadOnlyList<string> modified, IReadOnlyList<string> untracked) = WorktreeGitStatus.ParsePorcelain(output);
        return (modified, untracked);
    }

    private static async Task<(int ExitCode, string StandardOutput)> RunGitAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        NonInteractiveGit.Apply(process.StartInfo);
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, string.Empty);
        }

        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, output);
    }

    /// <summary>
    /// Retires an abandoned task's run with <see cref="RunSuperseded"/> (independent pre-PR
    /// review, cycle 1, both lenses) — the same retirement <see cref="Hall9k.Daemon.Review.ReviewEngine"/>'s own
    /// generation-fence rejection performs, needed here for the identical reason: refusing to
    /// verify alone leaves the run sitting live in <see cref="RunState.Verifying"/> with nothing
    /// left downstream in the chain to ever move it. A run already retired by the time this
    /// lands (no stream, or already terminal) is left alone rather than double-appended.
    /// </summary>
    private async Task RetireAbandonedRunAsync(Guid runId, Guid taskId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        if (await session.Events.FetchStreamStateAsync(runId, cancellationToken) is null)
        {
            return;
        }

        RunDetails? run = await session.LoadAsync<RunDetails>(runId, cancellationToken);
        if (run is null || run.State.IsTerminal)
        {
            return;
        }

        TaskDetails? task = await session.LoadAsync<TaskDetails>(taskId, cancellationToken);
        session.Events.Append(runId, new RunSuperseded(
            runId, task?.LeaseGeneration ?? run.LeaseGeneration, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Run {RunId}: retired as superseded — task {TaskId} is Abandoned", runId, taskId);
    }

    private async Task FailBeforeGatesAsync(Guid runId, Guid taskId, string reason, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new Domain.Features.Run.Events.RunFailed(runId, reason, now));

        // LoadFencedAsync's read must happen before the AllowsAsync identity check below —
        // not after — so a reclaim landing between the two is caught by AllowsAsync's fresh
        // read rather than baked into `current.Task` as an already-stale ownership fact
        // that AllowsAsync never gets asked about (adversarial review, cycle 2).
        (TaskAggregate Task, long Version)? fenced = await GenerationFence.LoadFencedAsync(session, taskId, cancellationToken);
        RunDetails? run = await session.LoadAsync<RunDetails>(runId, cancellationToken);
        if (fenced is { } current
            && !current.Task.State.IsTerminal
            && (run is null || await GenerationFence.AllowsAsync(
                session, logger, taskId, runId, run.LeaseGeneration, nameof(TaskFailed), cancellationToken)))
        {
            // One transaction with the RunFailed append above (Copilot review, PR #30's
            // expectedVersion fix, kept atomic with it on purpose — see
            // RunSupervisor.AppendFencedTaskFailureAsync): a lost race here rolling back
            // the run's own failure fact too is a smaller cost than a reader observing the
            // run Failed while its task still reads Claimed.
            session.Events.Append(taskId, expectedVersion: current.Version + 1, TaskDecider.Fail(current.Task, runId, reason, now));
            session.Delete<TaskLease>(taskId);
        }

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            logger.LogInformation(
                "Task {TaskId}: lost the generation race recording a pre-gate failure for run {RunId} — a newer claim committed first",
                taskId, runId);
        }
    }

    private async Task RecordPassAsync(
        Guid runId, string? note, bool ranFullScope, string? headSha, string verifyCommandsFingerprint,
        IReadOnlyList<GateDuration> gateDurations, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(
            runId,
            new VerificationPassed(
                runId, DateTimeOffset.UtcNow, note, ranFullScope, headSha, verifyCommandsFingerprint, gateDurations));
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Records <see cref="VerificationSkipped"/> in place of a pass (task: a delivered diff that
    /// touches no buildable or testable source skips the build and test gates) — see that event's
    /// own doc for why <c>RunAggregate.Apply(VerificationSkipped)</c> deliberately never sets any
    /// of the scope fields <see cref="RecordPassAsync"/>'s own <see cref="VerificationPassed"/> does.
    /// </summary>
    private async Task RecordSkipAsync(
        Guid runId, IReadOnlyList<VerificationSkippedPath> changedPaths, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new VerificationSkipped(runId, DateTimeOffset.UtcNow, changedPaths));
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The worktree's `git rev-parse HEAD`, recorded alongside a pass's own full-scope fact (task:
    /// a fix cycle's verification gate) so a later full-scope skip decision can tell whether the
    /// tip it ran against is still the tip about to settle. Null when git cannot answer — never
    /// guessed, the same convention <see cref="CountBranchCommitsAsync"/> already follows.
    /// </summary>
    private static async Task<string?> GetHeadShaAsync(string worktreePath, CancellationToken cancellationToken)
    {
        (int exitCode, string output) = await RunGitAsync(worktreePath, ["rev-parse", "HEAD"], cancellationToken);
        return exitCode == 0 ? output.Trim() : null;
    }

    private async Task RecordGateRetryAsync(Guid runId, string gate, string cause, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new GateRetried(runId, gate, cause, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordGateStartedAsync(
        Guid runId, string gateName, int processId, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new GateStarted(runId, gateName, processId, startedAt, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordGateEndedAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new GateEnded(runId, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The file name a host-coupled gate's own cross-process permit locks — one fixed name under
    /// <see cref="PlatformPaths.Home"/>, so every run on this node contends for the identical file
    /// regardless of which task or project it belongs to (task: at most one host-coupled gate runs
    /// on a node at a time — #225). <c>FileShare.None</c> gives an exclusive lock
    /// that the operating system releases automatically if the holding process dies, the same
    /// idiom <c>GitWorktreeManager.AcquireLockFileAsync</c> already uses for repository/checkout
    /// serialization — no reclaim or heartbeat bookkeeping needed.
    /// </summary>
    private const string HostCoupledGateLockFileName = ".h9k-host-coupled-gate.lock";

    /// <summary>
    /// Acquires the node-wide host-coupled-gate permit, waiting when another run's own
    /// host-coupled gate already holds it (task: at most one host-coupled gate runs on a node at a
    /// time — #225). The first attempt is silent: a permit acquired on the first
    /// try means there was nothing to wait for, so no wait is recorded on the run at all. Only a
    /// genuine wait appends <see cref="RunHostCoupledGateWaitStarted"/> before polling and
    /// <see cref="RunHostCoupledGateWaitEnded"/> the moment the permit is actually granted, so
    /// <c>h9k task show</c> reads the wait as this run's own phase rather than as a failure.
    /// </summary>
    private async Task<IAsyncDisposable> AcquireHostCoupledGatePermitAsync(Guid runId, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(PlatformPaths.Home);
        string lockFilePath = Path.Combine(PlatformPaths.Home, HostCoupledGateLockFileName);

        FileStream? stream = TryOpenHostCoupledGateLockFile(lockFilePath);
        if (stream is not null)
        {
            return new HostCoupledGatePermit(stream);
        }

        await RecordHostCoupledGateWaitStartedAsync(runId, cancellationToken);
        try
        {
            while (true)
            {
                await Task.Delay(200, cancellationToken);
                stream = TryOpenHostCoupledGateLockFile(lockFilePath);
                if (stream is not null)
                {
                    return new HostCoupledGatePermit(stream);
                }
            }
        }
        finally
        {
            // CancellationToken.None, not the (possibly already-cancelled) cancellationToken this
            // wait was given: unlike GateEnded, nothing ever rechecks a stuck wait flag against an
            // observable fact later — ActiveGate's own stuck-on-shutdown case is safe to leave
            // standing because a restart's adoption re-reads the gate's real OS process id and
            // reports honestly either way, but a wait has no such ground truth to recheck against,
            // so a daemon shutdown that lands here must still clear it or the run reads as waiting
            // on the host-coupled-gate slot forever, even once it moves on to something else
            // entirely. Best-effort: a database this cannot reach either is a rarer, harder failure
            // this catch leaves for the next thing to touch this run to surface honestly, rather
            // than losing the genuine cancellation this method is already propagating underneath it.
            try
            {
                await RecordHostCoupledGateWaitEndedAsync(runId, CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception,
                    "Run {RunId}: could not record the host-coupled-gate wait as ended", runId);
            }
        }
    }

    private static FileStream? TryOpenHostCoupledGateLockFile(string lockFilePath)
    {
        try
        {
            return new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>The permit's own disposable — releasing the file's exclusive lock is the whole release.</summary>
    private sealed class HostCoupledGatePermit(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private async Task RecordHostCoupledGateWaitStartedAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new RunHostCoupledGateWaitStarted(runId, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordHostCoupledGateWaitEndedAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new RunHostCoupledGateWaitEnded(runId, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// A just-started gate process's own start time, the other half of the identity a display
    /// checks its liveness against later (the PID-reuse guard, log #2) — mirrors
    /// <c>ProcessManagerBase.ReadStartedAt</c>'s own tolerance for the identical race on a process
    /// this method just started itself: a command that exits before this runs leaves the OS
    /// nothing left to report. <see cref="DateTimeOffset.MinValue"/> is recorded rather than a
    /// plausible-looking guess (AGENTS.md's never-guess rule) — a sentinel that can never match a
    /// real process's start time, so a gate process already gone by the time this reads it stays
    /// reported as gone rather than silently reading as whatever the OS happens to answer for a
    /// reused pid.
    /// </summary>
    private static DateTimeOffset ReadProcessStartedAt(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// How long a fresh clean-base comparison gets to answer before this run's own terminal
    /// disposition stops waiting on it (independent pre-PR review, cycle 1, both lenses, medium —
    /// the 2026-09-15 08:57 incident: about 14.5 minutes between the `test` gate actually failing
    /// and <see cref="RunFailed"/>/<c>TaskFailed</c> landing, with the task reading Claimed and
    /// nothing under Needs you for the whole gap, because <see cref="DescribeCleanBaseComparisonAsync"/>
    /// ran that same ~15-minute gate a second time, against a clean checkout of main, before
    /// <see cref="RecordFailureAsync"/> ever got a chance to write). Pinned to
    /// <see cref="DaemonOptions.PollInterval"/> itself, not a fixed guess (independent pre-PR
    /// review, cycle 1, conformance lens, medium: a fixed 10-second budget was still up to two full
    /// five-second sweeps longer than the one sweep <c>h9k status</c> is supposed to need), so the
    /// budget tracks whatever the daemon's own sweep cadence is actually configured to. Generous
    /// enough that a cache hit or a gate that genuinely fails fast even freshly run — every one of
    /// this file's own clean-base comparison tests — still lands inline exactly as before; short
    /// enough that a comparison shaped like the gate itself (minutes, not seconds) never again holds
    /// a real failure hostage behind it.
    /// </summary>
    private TimeSpan CleanBaseComparisonRecordingBudget => options.Value.PollInterval;

    /// <summary>
    /// Records a gate's real failure, first asking whether this same gate also fails against a
    /// clean checkout of the project's own base branch — the Windows field report's own origin
    /// incident (task: a verify gate that cannot pass on clean main is caught before it costs a
    /// run): a solution-level test gate that always failed under the dotnet SDK's MSBuild parked
    /// every run Failed as a bare "gate failure (test)", costing a full agent run and a human
    /// diagnosis session before anyone noticed the gate itself, not the agent's work, was broken.
    /// A run that fails a gate which also fails on clean main says so, rather than reporting that
    /// same bare failure a second time. Skipped outright when <paramref name="isInfrastructureFailure"/>
    /// is true: an environment outage (Docker down, a connection-class signature) fails the exact
    /// same way on a clean checkout of main, and re-running the comparison there would report the
    /// container's own outage as "the gate also fails on clean base" — undoing the classification
    /// this same method's caller just did (independent pre-PR review, cycle 1, conformance lens).
    /// Returns the reason actually recorded onto <see cref="RunFailed"/> — annotated when the
    /// comparison won the race, the caller's own plain <paramref name="reason"/> otherwise — so a
    /// caller such as <see cref="Hall9k.Daemon.Review.ReviewEngine"/>'s own safety net always has a
    /// real diagnostic in hand for a human, not null (independent pre-PR review, cycle 1, both
    /// lenses, medium: nothing downstream of the fail-hard path used to receive this method's own
    /// output at all).
    /// <para>
    /// The clean-base comparison is raced against <see cref="CleanBaseComparisonRecordingBudget"/>
    /// rather than always awaited before <see cref="RecordFailureAsync"/> ever runs — see
    /// <see cref="RaceCleanBaseComparisonAsync"/>, which both recording paths share, for the whole
    /// mechanism and the two incidents behind it.
    /// </para>
    /// </summary>
    private async Task<string> RecordGateFailureAsync(
        Guid runId, Guid taskId, Guid nodeId, ProjectDetails? project, VerifyCommand gate, string reason,
        bool isInfrastructureFailure, IReadOnlyList<GateDuration> gateDurations, CancellationToken cancellationToken)
    {
        string reportedReason = await RaceCleanBaseComparisonAsync(
            runId, nodeId, project, gate, reason, isInfrastructureFailure, cancellationToken);
        await RecordFailureAsync(runId, taskId, gate.Name, reportedReason, gateDurations, cancellationToken);
        return reportedReason;
    }

    /// <summary>
    /// The Settling-gate repair lap's own sibling to <see cref="RecordGateFailureAsync"/> (task: a
    /// pre-final-pass rebase that applies cleanly but breaks the mandatory gate gets a repair lap
    /// inside the same run instead of failing it): records the same <see cref="VerificationFailed"/>
    /// audit fact, with the identical clean-base-comparison annotation, but never
    /// <see cref="Domain.Features.Run.Events.RunFailed"/> or <c>TaskFailed</c> — the caller is
    /// about to dispatch a repair session rather than end the run, so nothing here should end it
    /// either. Returns the reported (possibly clean-base-annotated) reason for that caller to hand
    /// the repair session and, if the round cap is spent, name in the park.
    /// <para>
    /// Races the comparison against <see cref="CleanBaseComparisonRecordingBudget"/> through the
    /// shared <see cref="RaceCleanBaseComparisonAsync"/>, exactly as
    /// <see cref="RecordGateFailureAsync"/> does: this method awaiting it unconditionally is the
    /// 2026-09-17 defect (see that method's own doc for both machines' shapes).
    /// </para>
    /// </summary>
    private async Task<string> RecordGateFailureWithoutFailingRunAsync(
        Guid runId, Guid nodeId, ProjectDetails? project, VerifyCommand gate, string reason,
        bool isInfrastructureFailure, IReadOnlyList<GateDuration> gateDurations, CancellationToken cancellationToken)
    {
        string reportedReason = await RaceCleanBaseComparisonAsync(
            runId, nodeId, project, gate, reason, isInfrastructureFailure, cancellationToken);
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new VerificationFailed(runId, [gate.Name], DateTimeOffset.UtcNow, gateDurations));
        await session.SaveChangesAsync(cancellationToken);
        return reportedReason;
    }

    /// <summary>
    /// The clean-base comparison, raced against <see cref="CleanBaseComparisonRecordingBudget"/>
    /// (see that budget's own doc for the 2026-09-15 08:57 incident that introduced the race):
    /// returns the annotated reason when the comparison answers inside the budget, and the
    /// caller's own plain <paramref name="reason"/> the moment it does not. Losing the race never
    /// waits for the comparison's tail (independent pre-PR review, cycle 1, both lenses, medium:
    /// awaiting it even after giving up on including it reintroduced the identical gap one layer
    /// up, since every caller still sat behind the recording call's own return) — the comparison is
    /// left running in the background, observed only for an unhandled exception rather than
    /// awaited, so it still warms <see cref="CleanBaseGateVerdict"/> for whichever run next asks
    /// this same question without holding either recording path, or anything downstream of one,
    /// open a second longer than the budget. Its own annotation is discarded once it finally
    /// answers: both recording paths are append-only, so there is nothing left to attach it to. The
    /// run's own node slot is freed the moment the recording path returns (<c>NodeLoad</c> counts
    /// only live runs), while the background comparison keeps a full gate-sized process (a `dotnet
    /// build`/`dotnet test` against <c>repo/dev</c>, up to
    /// <see cref="AdHocGateRunner.ComputeComparisonBudget"/>'s own budget) running unaccounted for
    /// — a real, but bounded, cost against a freshly dispatched run landing on the same node before
    /// that background process exits (independent pre-PR review, cycle 1, conformance lens, low).
    /// <para>
    /// Shared by BOTH recording paths rather than living in <see cref="RecordGateFailureAsync"/>
    /// alone, which is the 2026-09-17 defect this extraction closes: two shapes of one miss, run
    /// 01a0ab8d on Windows and run 01a0ad4e on the Mac.
    /// <see cref="RecordGateFailureWithoutFailingRunAsync"/> — the repair-eligible sibling added
    /// for the Settling-gate repair lap — awaited the comparison unconditionally, so a run whose
    /// mandatory final full-scope gate failed after a merge-ready resolve sat with no session, no
    /// park and no failure for as long as the comparison's own gate run took: 15 minutes on the Mac
    /// (gate failed 05:31:09, repair session dispatched 05:46:17) and past 30 on Windows, where the
    /// comparison's budget is twice the whole suite's own wall clock. <c>h9k task show</c> read
    /// "no session recorded as running" throughout, because the run's own gate had already ended by
    /// then and <c>TaskPhaseComposer</c> had no <see cref="ActiveGate"/> left to name.
    /// </para>
    /// </summary>
    private async Task<string> RaceCleanBaseComparisonAsync(
        Guid runId, Guid nodeId, ProjectDetails? project, VerifyCommand gate, string reason,
        bool isInfrastructureFailure, CancellationToken cancellationToken)
    {
        Task<string> reportedReasonTask = BuildReportedGateFailureReasonAsync(
            runId, nodeId, project, gate, reason, isInfrastructureFailure, cancellationToken);
        Task first = await Task.WhenAny(
            reportedReasonTask, Task.Delay(CleanBaseComparisonRecordingBudget, cancellationToken));
        if (first == reportedReasonTask)
        {
            return await reportedReasonTask;
        }

        // The comparison is still running past the budget — this gate's own failure, and every
        // caller downstream of it, must not wait on it any longer. The comparison itself keeps
        // running, off this call's own critical path, unawaited: only watched for a fault so an
        // unobserved cancellation never reaches the process-wide
        // TaskScheduler.UnobservedTaskException handler; every other exception is already handled
        // inside BuildReportedGateFailureReasonAsync's own try/catch.
        logger.LogInformation(
            "Run {RunId}: the clean-base comparison for gate '{Gate}' has not answered within {Budget} — "
            + "recording this failure with the gate's own reason and leaving the comparison to warm the cache",
            runId, gate.Name, CleanBaseComparisonRecordingBudget);
        _ = reportedReasonTask.ContinueWith(
            static faulted => faulted.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return reason;
    }

    /// <summary>
    /// The text appended to a host-coupled gate's own failure reason in place of a clean-base
    /// comparison (task: host-coupled suites run only in the node's serialized host gate). A
    /// comparison spawns the identical gate command a second time, outside the node's one
    /// serialized host-coupled slot (<see cref="AcquireHostCoupledGatePermitAsync"/>) — the exact
    /// shape of the origin incident this task answers, where the daemon's own comparison held four
    /// permits for a dead gate's whole five-minute life while twelve other classes queued behind
    /// it. Skipping it here is a correctness fix, not a diagnostic loss: a second unserialized run
    /// of the same host-coupled suite answers nothing a caller could trust anyway.
    /// </summary>
    internal const string HostCoupledComparisonSkippedNote =
        "The clean-base comparison was skipped: running it here would spawn a second host-coupled "
        + "suite outside the node's serialized host gate.";

    /// <summary>
    /// Whether <paramref name="gate"/>'s own clean-base comparison must be skipped because the
    /// gate is host-coupled — pulled out of <see cref="BuildReportedGateFailureReasonAsync"/> as a
    /// pure, synchronous check so a test can prove this decision (and the ordinary gate's own
    /// unchanged path) without spawning anything, the identical reasoning
    /// <see cref="ScopedRunExecutedNoTests"/> and <see cref="ComposeGateCommand"/> already apply
    /// to this class's other pure decisions.
    /// </summary>
    internal static string? DescribeHostCoupledComparisonSkip(VerifyCommand gate) =>
        gate.IsHostCoupled ? HostCoupledComparisonSkippedNote : null;

    /// <summary>
    /// A gate's own failure reason, annotated with whether it also fails against a clean checkout
    /// of the project's own base branch — shared by <see cref="RecordGateFailureAsync"/> and
    /// <see cref="RecordGateFailureWithoutFailingRunAsync"/> so the annotation itself can never
    /// drift between the two.
    /// </summary>
    private async Task<string> BuildReportedGateFailureReasonAsync(
        Guid runId, Guid nodeId, ProjectDetails? project, VerifyCommand gate, string reason,
        bool isInfrastructureFailure, CancellationToken cancellationToken)
    {
        // A host-coupled gate's own failure never races a second copy of itself outside the
        // node's one serialized host-coupled slot — checked ahead of every other skip below,
        // and before anything here ever spawns a process, so this path never depends on
        // project/checkout state the way the ordinary comparison does (task: host-coupled
        // suites run only in the node's serialized host gate).
        if (DescribeHostCoupledComparisonSkip(gate) is { } skipNote)
        {
            return $"{reason} {skipNote}";
        }

        if (project is null || isInfrastructureFailure)
        {
            return reason;
        }

        try
        {
            return await DescribeCleanBaseComparisonAsync(runId, nodeId, project, gate, cancellationToken) is { } note
                ? $"{reason} {note}"
                : reason;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Best-effort diagnostic on top of a failure that is already going to be recorded
            // either way — never let the comparison's own failure (a locked repository, a git
            // error) mask or replace the real gate failure this method exists to record.
            logger.LogWarning(
                exception,
                "Run {RunId}: could not compare gate '{Gate}' against a clean checkout of the base branch",
                runId, gate.Name);
            return reason;
        }
    }

    /// <summary>
    /// Whether <paramref name="gate"/> also fails when run once against a clean checkout of the
    /// project's own base branch — null when the comparison itself cannot be made (no reachable
    /// checkout, a bare clone, a repo/dev this call cannot confirm is actually at the base
    /// branch's current tip, or the attempt is <see cref="GateCheckOutcome.Inconclusive"/>) or when
    /// the gate is actually observed to pass there, in which case the run's own failure is real and
    /// a note here would only be noise. Never guessed at either way (AGENTS.md's "never guess at
    /// unobserved facts"): silence is the honest answer when the gate's own verdict cannot be
    /// confirmed, not an assumed pass or fail. Whether the checkout itself is confirmed clean and
    /// on the base branch is a softer question — checked and named in the note rather than gating
    /// whether the note is made at all, since the gate command genuinely did run and exit with a
    /// real code either way (independent pre-PR review, cycle 1, both lenses: an ordinary clone got
    /// no confirmation at all, and a repo/dev confirmed only "up to date", never "clean").
    /// <para>
    /// A conclusive verdict (base passes or base fails) observed against a checkout confirmed clean
    /// at a given commit is remembered per <paramref name="nodeId"/>/gate/commit
    /// (<see cref="CleanBaseGateVerdict"/>) and reused by a later run against that same base commit
    /// without re-running the gate at all — the origin incident this whole method exists to fix
    /// was never the comparison failing to answer, it was every one of five failed runs against
    /// the same red main paying for its own fresh answer to the same already-answered question.
    /// An <see cref="GateCheckOutcome.Inconclusive"/> attempt is never remembered, so it is retried
    /// honestly on the next call rather than silently treated as a permanent unknown.
    /// </para>
    /// </summary>
    private async Task<string?> DescribeCleanBaseComparisonAsync(
        Guid runId, Guid nodeId, ProjectDetails project, VerifyCommand gate, CancellationToken cancellationToken)
    {
        string checkout = ProjectCheckout.ForReading(project);
        if (!Directory.Exists(checkout) || ProjectCheckout.IsBare(checkout))
        {
            return null;
        }

        // Only repo/dev is the platform's own to move — a project registered before homes existed
        // points at somebody's ordinary clone, which this comparison then reads as-is rather than
        // fast-forwarding (ProjectCheckout.IsHomeDevWorktree's own doc comment).
        if (ProjectCheckout.IsHomeDevWorktree(project, checkout))
        {
            CheckoutRefresh refresh = await worktrees.RefreshReadingCheckoutAsync(checkout, project.BaseBranch, cancellationToken);
            if (!refresh.UpToDate)
            {
                logger.LogInformation(
                    "Run {RunId}: skipping the clean-base comparison for gate '{Gate}' — {Detail}",
                    runId, gate.Name, refresh.Detail);
                return null;
            }
        }

        string? uncleanNote = await CheckoutCleanliness.DescribeNotConfirmedCleanAsync(checkout, project.BaseBranch, cancellationToken);
        string? baseCommitSha = await GetHeadShaAsync(checkout, cancellationToken);

        // Only a checkout confirmed clean at a known commit is safe to remember and reuse: an
        // unconfirmed checkout's own local state can differ next time even at the identical
        // commit sha, so nothing here is looked up or written for one (CleanBaseGateVerdict's own
        // doc comment).
        bool cacheable = baseCommitSha is not null && uncleanNote is null;

        // The composed command, not gate.Command: the comparison exists to answer "does this
        // gate also fail against a clean base", and for a host-coupled gate the gate that just
        // failed is the filtered one, never the raw configured command (independent pre-PR
        // review, cycle 3, conformance and adversarial lenses, high/medium — this call site was
        // missed when 1176a38a composed this same filter into every other gate-spawn site).
        // Keyed into the cache id too, so moving a gate's designation from one filter to another
        // never reuses a verdict recorded under the old filter's command string.
        string composedCommand = ComposeGateCommand(gate);

        await using IQuerySession query = store.QuerySession();
        if (cacheable)
        {
            CleanBaseGateVerdict? cached = await query.LoadAsync<CleanBaseGateVerdict>(
                CleanBaseGateVerdict.ComputeId(nodeId, project.Id, gate.Name, composedCommand, baseCommitSha!), cancellationToken);
            if (cached is not null)
            {
                logger.LogInformation(
                    "Run {RunId}: reusing the clean-base comparison already recorded for gate '{Gate}' at base commit {Sha}",
                    runId, gate.Name, baseCommitSha);
                return cached.BasePasses ? null : cached.FailureNote;
            }
        }

        // Serializes this checkout's gate spawn against every other caller that can run a command
        // in it at the same time (h9k task verify, h9k project set --verify, and this same method
        // for a sibling run's own failing gate) — independent pre-PR review, cycle 1, both lenses:
        // two dotnet build/test invocations sharing one obj/bin used to fail each other, and the
        // loser's exit code was then recorded as the gate itself being broken. Acquired only
        // around the gate spawn, never around the refresh above, for the identical reentrancy
        // reason ProjectSetCommand.ValidateGatesAgainstCleanBaseAsync's own lock documents.
        //
        // AcquireCheckoutLockAsync, deliberately not the broader AcquireRepositoryLockAsync
        // (independent pre-PR review, cycle 1, adversarial lens, medium): the budget below is now
        // the gate's own recorded duration, not a fixed five-minute cap, so this call can hold a
        // lock for as long as VerifyGateTimeout allows — the repository-wide lock would have held
        // that same span against every other run's own `git worktree add` and closeout's own
        // `git worktree remove` on this project, stalling the node's dispatch loop for a
        // best-effort diagnostic on top of a failure already being recorded. The checkout-scoped
        // lock still serializes against every other caller that spawns a gate command in this
        // exact checkout (h9k task verify, h9k project set --verify, this same method's own
        // sibling runs), which is all the exclusion this call actually needs.
        //
        // The wait to acquire it is itself bounded to CleanBaseCheckTimeoutCap, not left
        // open-ended (independent pre-PR review, cycle 1, adversarial lens): an unbounded wait
        // here — behind a slow gate comparison already holding it elsewhere, or a `project set
        // --verify` validation holding it for its own gates — would defer this run's own
        // RecordFailureAsync, and with it the run's failure, its lease release, and its node slot,
        // for as long as that other holder runs. A lock that cannot be acquired within budget
        // means the comparison is skipped, honestly, exactly like every other unobservable case in
        // this method — never a reason to block the real failure this method exists to record.
        // Deliberately still the fixed cap, not the gate's own recorded-duration budget below:
        // this bounds contention for the lock itself, a different question from how long the gate
        // command run under it is allowed to take.
        using CancellationTokenSource lockBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lockBudget.CancelAfter(AdHocGateRunner.CleanBaseCheckTimeoutCap);
        IAsyncDisposable gateLock;
        try
        {
            gateLock = await worktrees.AcquireCheckoutLockAsync(checkout, lockBudget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(
                "Run {RunId}: skipping the clean-base comparison for gate '{Gate}' — could not acquire the " +
                "checkout lock for {Checkout} within {Timeout}",
                runId, gate.Name, checkout, AdHocGateRunner.CleanBaseCheckTimeoutCap);
            return null;
        }

        await using (gateLock)
        {
            // Re-observed now that the lock is actually held, not reused from the capture above
            // (adversarial review, cycle 1, medium): acquiring this lock can wait — the whole
            // reason its own wait is bounded rather than instant — and RefreshReadingCheckoutAsync
            // internally takes this identical checkout's lock (nested inside its own repository
            // lock, around the rev-list/merge it runs) before fast-forwarding it, so a sibling
            // refresh can move the checkout's HEAD (or dirty it) while this call queues for the
            // lock above. Without re-observing here, the gate that is about to run under the lock —
            // against whatever commit the checkout actually holds right now — would be recorded and
            // reported as an observation of the stale commit captured before the wait, which is
            // exactly the unobserved-fact-as-fact mistake AGENTS.md's "never guess" rule exists to
            // catch. Once this lock is held, nothing else can move or dirty this checkout until it
            // is released — every other caller that mutates it (a sibling gate spawn via
            // AcquireCheckoutLockAsync, or RefreshReadingCheckoutAsync's own internal use of the
            // identical lock) takes it first — so this observation stays valid for the rest of the
            // block (independent pre-PR review, cycle 3, conformance and adversarial lenses, both
            // high: RefreshReadingCheckoutAsync used to take only the broader repository lock here,
            // a different file for a linked worktree, so it could fast-forward this checkout while
            // a gate command ran under this one unguarded).
            uncleanNote = await CheckoutCleanliness.DescribeNotConfirmedCleanAsync(checkout, project.BaseBranch, cancellationToken);
            baseCommitSha = await GetHeadShaAsync(checkout, cancellationToken);
            cacheable = baseCommitSha is not null && uncleanNote is null;

            // Re-checked now that the lock is actually held: two runs failing the same gate
            // against the same red commit within the same short window can both miss the cache
            // above and both queue up for this same lock — without this second look, the loser of
            // that race would still spawn a second, entirely redundant gate run the instant the
            // winner releases the lock, rather than reusing the verdict its sibling just recorded
            // (self-review finding: the exact shape of the origin incident this method exists to
            // fix, just narrowed from five runs to two).
            if (cacheable)
            {
                CleanBaseGateVerdict? wonRace = await query.LoadAsync<CleanBaseGateVerdict>(
                    CleanBaseGateVerdict.ComputeId(nodeId, project.Id, gate.Name, composedCommand, baseCommitSha!), cancellationToken);
                if (wonRace is not null)
                {
                    logger.LogInformation(
                        "Run {RunId}: reusing the clean-base comparison a concurrent run just recorded for gate '{Gate}' at base commit {Sha}",
                        runId, gate.Name, baseCommitSha);
                    return wonRace.BasePasses ? null : wonRace.FailureNote;
                }
            }

            // Budgeted off the gate's own most recently recorded wall-clock duration on this node,
            // not a fixed cap alone (task: the clean-base comparison can actually finish — origin
            // incident 2026-09-05/06, a 5-minute cap this project's own 11-12 minute test gate
            // could never meet, so every comparison reported "inconclusive" instead of ever
            // actually diagnosing the red main it was run for). Excludes this same run (independent
            // pre-PR review, cycle 1, adversarial lens, low): RecordGateFailureAsync now races this
            // comparison against a recording budget and can record this run's own failed gate — a
            // real, but possibly very short, wall-clock sample — before this read runs, and without
            // the exclusion that sample could become "the most recent" and shrink the budget a
            // comparison already in flight has yet to read, the same way excludingRunId already
            // keeps a run's own pass out of GateDurationHistoryQuery.LoadRecentHistoryAsync's trailing
            // average.
            TimeSpan? recentDuration = await GateDurationHistoryQuery.MostRecentDurationOnNodeAsync(
                query, project.Id, nodeId, gate.Name, excludingRunId: runId, cancellationToken);
            TimeSpan comparisonTimeout = AdHocGateRunner.ComputeComparisonBudget(recentDuration, options.Value.VerifyGateTimeout);
            GateCheckResult result = await AdHocGateRunner.RunAsync(checkout, composedCommand, comparisonTimeout, cancellationToken);
            if (result.Outcome != GateCheckOutcome.Failed)
            {
                if (result.Outcome == GateCheckOutcome.Inconclusive)
                {
                    logger.LogInformation(
                        "Run {RunId}: the clean-base comparison for gate '{Gate}' was inconclusive — {Detail}",
                        runId, gate.Name, result.OutputTail);
                    return null;
                }

                if (cacheable)
                {
                    await TryRecordCleanBaseVerdictAsync(
                        runId, nodeId, project.Id, gate.Name, composedCommand, baseCommitSha!, basePasses: true, failureNote: null,
                        cancellationToken);
                }

                return null;
            }

            // Never cached, never reported as an observation of the base branch: an environmental
            // hiccup in the comparison's own gate run (a dropped Postgres connection, Testcontainers
            // failing to bring a container up, MSB4166) says nothing about whether main itself is
            // broken, and AdHocGateRunner's own doc comment is explicit that it carries no
            // infrastructure classification of its own — this method is the caller that has to
            // supply it. Without this check, a single flaky infrastructure failure would be
            // persisted as a conclusive CleanBaseGateVerdict with basePasses: false and replayed
            // verbatim on every later run against this base commit, telling a human "main is broken"
            // on the strength of one bad environment, not one bad commit (independent pre-PR review,
            // cycle 5, adversarial lens, medium). This is the identical classifier
            // VerificationRunner's own RunGateAsync already applies to the run's own real gate above
            // — classified from result.FullOutput, not result.OutputTail: the latter is capped to
            // the trailing 400 characters kept for the summary below, so a marker logged early in an
            // eleven-minute `dotnet test` run (a Testcontainers/Npgsql failure five minutes in, with
            // megabytes of test output still to come) sat outside that window and went unclassified,
            // recording the environmental hiccup as a conclusive "main is broken" verdict — exactly
            // the mistake RunGateAsync's own ReadFullOutput(logFile) above already avoids for the
            // run's own real gate (independent pre-PR review, cycle 1, both lenses, high/medium).
            // A comparison whose own log could not be read is the same unobserved fact as the run's
            // own gate log being unreadable (see UnreadableGateLog), and it reaches this method as
            // null rather than as empty text precisely so it cannot be mistaken for "the base
            // branch's gate printed nothing and failed". Classifying an empty string here would
            // find no marker, and a marker-free failure is recorded as a conclusive
            // CleanBaseGateVerdict with basePasses: false and replayed to every later run against
            // this base commit — "main is broken" stated from a fact nobody saw, which is the
            // never-guess rule this branch exists to honour (independent pre-PR review, cycle 1,
            // both lenses, medium). Treated exactly like the infrastructure hiccup below: nothing
            // recorded, nothing reported, the comparison retried on the next run.
            if (result.FullOutput is null)
            {
                logger.LogInformation(
                    "Run {RunId}: the clean-base comparison for gate '{Gate}' failed, but its own output log " +
                    "could not be read, so nothing was actually observed about the base branch and nothing " +
                    "was recorded — {Detail}",
                    runId, gate.Name, result.OutputTail);
                return null;
            }

            if (GateInfrastructureFailureClassifier.IsInfrastructureFailure(result.FullOutput))
            {
                logger.LogInformation(
                    "Run {RunId}: the clean-base comparison for gate '{Gate}' hit an infrastructure " +
                    "failure, not a real result, and was not recorded — {Detail}",
                    runId, gate.Name, result.OutputTail);
                return null;
            }

            string checkoutDescription = CheckoutCleanliness.DescribeCheckoutForComparison(checkout, project.BaseBranch, uncleanNote);
            string note = baseCommitSha is null
                ? $"Gate '{gate.Name}' also fails when run against {checkoutDescription}: {result.OutputTail}"
                : $"Gate '{gate.Name}' also fails when run against {checkoutDescription} at commit {baseCommitSha}: {result.OutputTail}";

            if (cacheable)
            {
                await TryRecordCleanBaseVerdictAsync(
                    runId, nodeId, project.Id, gate.Name, composedCommand, baseCommitSha!, basePasses: false, failureNote: note,
                    cancellationToken);
            }

            return note;
        }
    }

    /// <summary>
    /// Best-effort: the verdict this wraps was already actually observed by the gate run that just
    /// finished, so a failure persisting it (a transient Postgres error, or the cache document's
    /// first-ever write failing under a database whose daemon role cannot create tables) must never
    /// discard that observation — only the ability to reuse it on a later run without re-running
    /// the gate (independent pre-PR review, cycle 3, conformance lens, low: this call used to throw
    /// straight out of <see cref="DescribeCleanBaseComparisonAsync"/> into
    /// <see cref="RecordFailureAsync"/>'s best-effort catch, which logs and records a bare gate
    /// failure — silently losing the clean-base note this method exists to attach). Cancellation
    /// tied to the run's own token still propagates, since that is the run stopping, not a
    /// persistence failure.
    /// </summary>
    private async Task TryRecordCleanBaseVerdictAsync(
        Guid runId, Guid nodeId, Guid projectId, string gate, string gateCommand, string baseCommitSha, bool basePasses,
        string? failureNote, CancellationToken cancellationToken)
    {
        try
        {
            await RecordCleanBaseVerdictAsync(
                nodeId, projectId, gate, gateCommand, baseCommitSha, basePasses, failureNote, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "Run {RunId}: observed the clean-base comparison for gate '{Gate}' at base commit {Sha} but could " +
                "not persist it for reuse — a later run against this same commit will re-run the gate instead of " +
                "reusing this observation",
                runId, gate, baseCommitSha);
        }
    }

    /// <summary>Overwrites, never appends — this is a cache of an observation (CleanBaseGateVerdict's own doc comment), not a fact worth a history of its own.</summary>
    private async Task RecordCleanBaseVerdictAsync(
        Guid nodeId, Guid projectId, string gate, string gateCommand, string baseCommitSha, bool basePasses, string? failureNote,
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Store(new CleanBaseGateVerdict
        {
            Id = CleanBaseGateVerdict.ComputeId(nodeId, projectId, gate, gateCommand, baseCommitSha),
            NodeId = nodeId,
            ProjectId = projectId,
            Gate = gate,
            BaseCommitSha = baseCommitSha,
            BasePasses = basePasses,
            FailureNote = failureNote,
            RecordedAt = DateTimeOffset.UtcNow,
        });
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordFailureAsync(
        Guid runId, Guid taskId, string failedGate, string reason, IReadOnlyList<GateDuration> gateDurations,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new VerificationFailed(runId, [failedGate], now, gateDurations));
        session.Events.Append(runId, new Domain.Features.Run.Events.RunFailed(runId, reason, now));

        // LoadFencedAsync's read must happen before the AllowsAsync identity check below —
        // not after — so a reclaim landing between the two is caught by AllowsAsync's fresh
        // read rather than baked into `current.Task` as an already-stale ownership fact
        // that AllowsAsync never gets asked about (adversarial review, cycle 2).
        (TaskAggregate Task, long Version)? fenced = await GenerationFence.LoadFencedAsync(session, taskId, cancellationToken);
        RunDetails? run = await session.LoadAsync<RunDetails>(runId, cancellationToken);
        if (fenced is { } current
            && TaskDecider.CanFail(current.Task)
            && (run is null || await GenerationFence.AllowsAsync(
                session, logger, taskId, runId, run.LeaseGeneration, nameof(TaskFailed), cancellationToken)))
        {
            // One transaction with the run-stream events above (Copilot review, PR #30's
            // expectedVersion fix, kept atomic with them on purpose — see
            // RunSupervisor.AppendFencedTaskFailureAsync): a lost race here rolling back
            // the run's own failure facts too is a smaller cost than a reader observing
            // the run Failed while its task still reads Claimed.
            session.Events.Append(
                taskId, expectedVersion: current.Version + 1,
                TaskDecider.Fail(current.Task, runId, $"Verification failed: {reason}", now));
            session.Delete<TaskLease>(taskId);
        }

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            logger.LogInformation(
                "Task {TaskId}: lost the generation race recording a verification failure for run {RunId} — a newer claim committed first",
                taskId, runId);
        }
    }

    /// <summary>
    /// Whether a gate's own command is `dotnet test`-shaped — the only shape
    /// <see cref="TestGateScope"/> ever narrows. A build gate, a lint gate, or a `dotnet test`
    /// wrapped in something else entirely (a script, a different runner) is left exactly as the
    /// project configured it. Matched with a regex rather than a literal-space
    /// <c>StartsWith("dotnet test")</c> (Copilot review, PR #62) for two reasons: arbitrary
    /// whitespace between the two words (`dotnet  test`, a tab) is still the same command, and a
    /// literal substring match would also — wrongly — call `dotnet testing` or `dotnet tests` a
    /// test gate, since both start with the same characters. The word boundary after `test` is a
    /// negative lookahead for a word character rather than a required whitespace-or-end
    /// (independent pre-PR review, cycle 1 — a required trailing whitespace rejected a gate whose
    /// shell control operator abuts the word, `dotnet test|tail -200` or `dotnet test&&dotnet
    /// format`, which <see cref="FindDotnetTestInvocationEnd"/> below is deliberately written to
    /// handle).
    /// </summary>
    [GeneratedRegex(@"^\s*dotnet\s+test(?!\w)")]
    private static partial Regex DotnetTestGatePattern();

    internal static bool IsDotnetTestGate(string command) => DotnetTestGatePattern().IsMatch(command);

    /// <summary>
    /// Whether THIS gate's own attempt ran at full scope (task: gate wall-clock duration is
    /// recorded and surfaced) — per gate, not per pass: the pass-level <c>ranFullScope</c>
    /// computed after the gate loop answers that question for the whole pass, but a project can
    /// configure more than one `dotnet test`-shaped gate and only one of them fall back to full
    /// while a sibling ran genuinely scoped (independent pre-PR review, cycle 1 — the same
    /// per-gate accounting <paramref name="fellBackToFull"/> already exists for). A non-test gate
    /// is always "full": scope never touches it, so its duration is comparable to every other run
    /// of the same gate regardless of what the pass's test gate did.
    /// </summary>
    private static bool GateRanFullScope(bool gateIsDotnetTest, TestGateScope? scope, bool fellBackToFull) =>
        !gateIsDotnetTest || scope is null || !scope.IsScoped || fellBackToFull;

    /// <summary>
    /// Whether a scoped `dotnet test` gate's own output shows VSTest ran zero tests rather than
    /// some passing — read from the run's own executed-test summary (`Total:` lines VSTest prints
    /// once per matched test assembly), never from the presence of the per-source "No test
    /// matches the given testcase filter" warning: that marker is emitted once per SOURCE, so a
    /// multi-project solution where the scoped filter matched in one project and missed another
    /// prints it right alongside a genuine `Total:` line from the project that actually ran
    /// (cycle-3 finding — the old substring check called that combination vacuous and discarded a
    /// passing run). Zero `Total:` lines at all — the marker alone, or no VSTest summary output
    /// whatsoever — is exactly the case still called vacuous.
    /// </summary>
    internal static bool ScopedRunExecutedNoTests(string output)
    {
        MatchCollection totals = ExecutedTestTotalPattern().Matches(output);

        // TryParse rather than Parse (adversarial review): `\d+` matches any Unicode decimal
        // digit and any run length, so a localized VSTest build or a pathologically wide count
        // must degrade to "not confirmed nonzero" — the same honest-fallback direction every
        // other unreadable signal in this class takes — rather than fault the whole run on a
        // gate that actually passed.
        return totals.Count == 0
            || totals.All(match => !int.TryParse(match.Groups["count"].Value, out int count) || count == 0);
    }

    [GeneratedRegex("""Total:\s*(?<count>\d+)""")]
    private static partial Regex ExecutedTestTotalPattern();

    [GeneratedRegex("(?i)no test matches the given testcase filter")]
    private static partial Regex NoTestMatchesWarningPattern();

    /// <summary>
    /// The honest description of why <see cref="ScopedRunExecutedNoTests"/> returned true: VSTest's
    /// own "no test matches" warning or an explicit `Total: 0` line is positive evidence the filter
    /// itself matched nothing, but the absence of any executed-test summary at all is not the same
    /// observation — a gate that suppresses VSTest's summary (`--logger "console;verbosity=quiet"`,
    /// a `grep`-filtered pipeline) produces zero `Total:` matches on a scoped run that may have
    /// executed its filtered tests just fine (conformance review finding). The
    /// recorded reason must say which one was actually observed, never assert the filter's own
    /// behavior as fact when only its absence of evidence was seen.
    /// </summary>
    private static string DescribeScopedRunVacuity(string output) =>
        ExecutedTestTotalPattern().IsMatch(output) || NoTestMatchesWarningPattern().IsMatch(output)
            ? "the scoped filter matched no tests"
            : "no executed-test summary was found in the gate's output";

    /// <summary>
    /// The command a gate actually runs: its own configured <see cref="VerifyCommand.Command"/>,
    /// with <see cref="VerifyCommand.HostCoupledFilter"/> injected via <see cref="ApplyTestFilter"/>
    /// when the gate is host-coupled (task: host-coupled tests run in their own gate once per
    /// task, never in parallel with another run's copy — #225). This is the whole
    /// of what "the filter splits the two gates" means: an ordinary gate's own command already
    /// carries whatever exclusion the project configured it with (<c>--verify</c> is free-form
    /// shell), and a host-coupled gate's command gets its own inclusion filter injected here,
    /// combined with anything it already carries the identical way a fix cycle's own scoped
    /// reverify combines one in.
    /// </summary>
    internal static string ComposeGateCommand(VerifyCommand gate) =>
        gate.HostCoupledFilter is { } filter ? ApplyTestFilter(gate.Command, filter) : gate.Command;

    /// <summary>
    /// Injects <paramref name="filterExpression"/> into a `dotnet test` command, combining with
    /// an already-configured `--filter` (this repo's own CI carries one,
    /// `--filter "Category!=RequiresDocker"`) via `&amp;` rather than emitting a second `--filter`
    /// flag, which `dotnet test` does not accept twice. A gate's own command is free-form shell
    /// (backlog: it is handed to `/bin/sh -c` / `cmd.exe`), so `dotnet test` is only the
    /// PREFIX of a gate that chains it with `&amp;&amp;`, pipes its output, or backgrounds it —
    /// both the append and the existing-`--filter` search are therefore scoped to
    /// <see cref="FindDotnetTestInvocationEnd"/>'s span, never the whole command string
    /// (independent pre-PR review, cycle 2 — appending or rewriting past that span landed the
    /// filter on a trailing program instead of on `dotnet test`, running the suite unscoped and
    /// failing the trailing program on an option it does not accept).
    /// </summary>
    internal static string ApplyTestFilter(string command, string filterExpression)
    {
        int invocationEnd = FindDotnetTestInvocationEnd(command);
        string invocation = command[..invocationEnd].TrimEnd();
        string rest = command[invocationEnd..];

        Match match = ExistingTestFilterPattern().Match(invocation);
        string updatedInvocation = match.Success
            ? string.Concat(
                invocation.AsSpan(0, match.Index),
                $"--filter \"({match.Groups["filter"].Value})&({filterExpression})\"",
                invocation.AsSpan(match.Index + match.Length))
            : $"{invocation} --filter \"{filterExpression}\"";

        return rest.Length == 0 ? updatedInvocation : $"{updatedInvocation} {rest}";
    }

    /// <summary>
    /// The index where the command's leading `dotnet test` invocation ends and, for a compound
    /// gate command, whatever follows it begins: the first unquoted shell control operator
    /// (`&amp;&amp;`, `||`, `;`, a bare `|`, or a bare `&amp;`) or the end of the string if the
    /// command is nothing but `dotnet test` itself. A bare `&amp;` immediately after `&gt;` (the
    /// `2&gt;&amp;1` file-descriptor duplication this repo's own gates use) is not a control
    /// operator and is skipped, the same way quoted text is: neither ends the invocation.
    /// </summary>
    private static int FindDotnetTestInvocationEnd(string command)
    {
        bool inSingleQuote = false;
        bool inDoubleQuote = false;
        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            if (inSingleQuote)
            {
                inSingleQuote = c != '\'';
                continue;
            }

            if (inDoubleQuote)
            {
                inDoubleQuote = c != '"';
                continue;
            }

            switch (c)
            {
                case '\'':
                    inSingleQuote = true;
                    break;
                case '"':
                    inDoubleQuote = true;
                    break;
                case ';':
                case '|':
                    return i;
                case '&' when i == 0 || command[i - 1] != '>':
                    return i;
            }
        }

        return command.Length;
    }

    [GeneratedRegex(
        """--filter(?:\s+|=|:)"(?<filter>[^"]*)"|--filter(?:\s+|=|:)'(?<filter>[^']*)'|--filter(?:\s+|=|:)(?<filter>\S+)""")]
    private static partial Regex ExistingTestFilterPattern();

    private static string Sanitize(string name) =>
        new([.. name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);

    /// <summary>
    /// The gate's whole captured output — empty when the gate wrote nothing, and <c>null</c> when
    /// the log exists but genuinely could not be read. Those two are different facts and every
    /// caller here treats them differently: the first is evidence about the gate, the second is
    /// the absence of evidence about anything.
    /// <para>
    /// Read through <see cref="ShareTolerantFile"/> rather than <c>File.ReadAllText</c>, whose
    /// <see cref="FileShare.Read"/> is refused outright while the gate's own shell still holds the
    /// redirected log — the flake that cost three review laps their full test gate on 2026-09-09
    /// and 2026-09-10; that type's own doc carries the incident and the measurement.
    /// </para>
    /// </summary>
    private static string? ReadFullOutput(string logFile) => ShareTolerantFile.TryReadAllText(logFile)?.Trim();

    private static string TailOf(string content) =>
        content.IsBlank() ? "(empty)" : content.Length <= 400 ? content : content[^400..];

    /// <summary>
    /// A file list for a one-line failure reason (backlog 57): capped the same way
    /// <see cref="TailOf"/> caps gate output, so a wide-rewrite session's file list cannot
    /// blow out the attention pane's one-line cause (<c>AttentionComposer.FailureCause</c>,
    /// `h9k status`) into dozens of wrapped terminal lines (conformance review finding).
    /// </summary>
    private const int MaxListedFiles = 20;

    private static string SummarizeFiles(IReadOnlyList<string> files) =>
        files.Count <= MaxListedFiles
            ? string.Join(", ", files)
            : $"{string.Join(", ", files.Take(MaxListedFiles))}, and {files.Count - MaxListedFiles} more";

    /// <summary>
    /// The recorded summary plus the excerpt around the marker that actually classified the
    /// gate as infrastructure, so the durable <see cref="GateRetried"/> event still explains
    /// the classification even when that marker sits outside the summary's 400-character tail
    /// (adversarial review, PR #36's Copilot review).
    /// </summary>
    private static string BuildRetryCause(string summary, string? matchingExcerpt) =>
        matchingExcerpt is null ? summary : $"{summary} Matching signature: {matchingExcerpt}";
}
