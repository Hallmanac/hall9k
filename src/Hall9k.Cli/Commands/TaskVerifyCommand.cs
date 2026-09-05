using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Verification;
using Hall9k.Connectors.Worktrees;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Run the project's build and test gates on demand against an interactive claim's worktree, so
/// an operator can check their work before delivering it — without waiting for h9k task deliver
/// to hand off to the daemon's own pipeline. Records the outcome as the same
/// VerificationPassed/VerificationFailed gate events a headless run's own gates record, on this
/// run's stream, so h9k task show reads one history regardless of who ran the gate. Deliberately
/// simpler than the daemon's own VerificationRunner: every gate always runs at full scope, once,
/// with no infrastructure-failure retry and no dotnet-test scoping — an operator watching the
/// output can see and re-run a flake themselves, and h9k task deliver's own hand-off pays for the
/// full machinery's retry and scoping regardless, so nothing here needs to duplicate it.
/// </summary>
public sealed class TaskVerifyCommand : Hall9kAsyncCommand<TaskVerifyCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--force")]
        [Description("Verify even though the claim's interactive session was recorded on another machine this one cannot check — attests you confirmed by hand that it has exited")]
        public bool Force { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        TaskDetails task = await session.LoadAsync<TaskDetails>(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        if (task.State != TaskState.Claimed || !task.IsInteractiveClaim || task.CurrentRunId is not { } runId)
        {
            throw new DomainConflictException(
                $"Task {taskId} is {task.State.Value} — only a task with an active interactive claim verifies this way.");
        }

        RunDetails run = await session.LoadAsync<RunDetails>(runId, cancellationToken)
            ?? throw new DomainConflictException(
                $"Task {taskId} is claimed interactively but run {runId} has no record — the process likely died "
                + $"while preparing the worktree. h9k task release {taskId} to give the claim back to the "
                + "dispatch queue.");

        // A pr-review task's own Claimed+sentinel state is never a human's own interactive claim
        // (TaskWorkCommand and TaskStartCommand both refuse to create one) — it is auto-pr-review's
        // Now-speed deliberate claim (AutoPrReviewEngine.CreateOneAsync), driven headlessly by this
        // daemon's own RunSupervisor. Running the project's build/test gates here would execute the
        // untrusted pull-request-head worktree's own code under the operator's account — exactly the
        // boundary RunLauncher's UntrustedWorkingDirectory flag and ClaudeExecutor's own
        // settings/hooks/MCP stripping exist to hold (independent pre-PR review, cycle 6, conformance
        // lens). Mirrors TaskHandbackCommand's and TaskReleaseCommand's identical guard, but stays
        // unconditional on run state where those two exempt a terminal run: neither of them has an
        // untrusted worktree to protect, while there is no run state at all in which executing this
        // one's gates locally becomes safe. Checked after the run-record load above, not before: a
        // crashed Now-speed launch (no RunDispatched ever committed) has no run to be "already
        // running headlessly" in, and this guard used to fire unconditionally on task.Type alone
        // ahead of that load, overclaiming an unobserved "running" fact for exactly the case the
        // no-record message above exists to describe honestly instead (independent pre-PR review,
        // cycle 7, conformance lens). The message is composed from the run's own observed state for
        // the same reason: a run parked ReviewParked or BudgetParked is not running either, and a
        // fixed "already running headlessly" sentence overclaimed it the same way (independent
        // pre-PR review, cycle 8, class sweep off the release/handback findings).
        if (task.Type == TaskType.PrReview)
        {
            throw PrReviewSentinelClaim.Refuse(taskId, run.State, "verify");
        }

        // An operator's own session, still attached in another terminal, is editing and possibly
        // rebuilding this same worktree right now — running gates here would collide with it in
        // shared obj/bin output exactly as the daemon's own gates and review sessions would
        // (adversarial review, cycle 1). Skipped when this invocation is that very session asking
        // for itself (a direct launch's own injected env var, or a self-registered session's own
        // CLAUDE_PID matching its recorded process — InteractiveSessionLiveness.IsSelfInvocation's
        // own doc has both signals): it is blocked waiting on this command to finish rather than
        // racing it, so there is nothing to collide with (conformance review, cycle 2).
        if (!InteractiveSessionLiveness.IsSelfInvocation(run))
        {
            InteractiveSessionLiveness.EnsureNotAttachedElsewhere(run, taskId, "verify", settings.Force);
        }

        // Mirrors TaskWorkCommand.ReenterAsync's own guard: once h9k task deliver or handback
        // hands the run to the standard pipeline, the task can still read Claimed+interactive
        // for the whole review loop, but the worktree now belongs to the daemon's own gates and
        // review sessions — running dotnet build/test here would collide with them (adversarial
        // review, cycle 1).
        if (run.State != RunState.Dispatched && run.State != RunState.Running)
        {
            throw new DomainConflictException(
                $"Task {taskId}'s run {runId} is already {run.State.Value} — it was handed off with "
                + $"h9k task deliver (or handback) and is now in the standard pipeline. h9k task show {taskId} "
                + "to see where it stands.");
        }

        ProjectDetails project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s project no longer exists.");

        // Unlike h9k task deliver's own refusal, uncommitted files here are only reported, never
        // a hard failure: an operator mid-edit is the ordinary state of an interactive claim, not
        // an anomaly, and dotnet build/test read the working tree regardless of git status — the
        // headless pre-gate check this used to mirror exists because a dispatched session's
        // process dies at its final message and stranded files never ship, which does not apply
        // to an operator who is present and still working. h9k task deliver's own uncommitted-file
        // refusal already covers the case that matters: nothing ships with files left behind
        // (conformance review, cycle 4 — the prior hard failure made the self-invocation exemption
        // above unreachable for its own stated purpose).
        (IReadOnlyList<string>? modified, IReadOnlyList<string> untracked) =
            await InteractiveWorktreeGit.ListUncommittedFilesAsync(run.WorktreePath, cancellationToken);
        IReadOnlyList<string> strandable = [];
        if (untracked.Count > 0)
        {
            // Split the same way VerificationRunner and h9k task deliver do, with the same shared
            // classification: an untracked path under src/ or tests/ is not "not counted" at all —
            // it is exactly what the daemon's own pre-gate check, and h9k task deliver's refusal,
            // will fail over once this claim is delivered. Saying "not counted against the check"
            // about that path here would make this on-demand rehearsal read green for a tree that
            // is about to fail for real (independent pre-PR review, adversarial finding, cycle 1).
            (strandable, IReadOnlyList<string> byproduct) = WorktreeGitStatus.SplitUntracked(untracked);

            if (strandable.Count > 0)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[yellow]Untracked file(s) under src/ or tests/ (not counted against this check, but h9k task deliver will refuse and the platform's own verification will fail the run over them): {string.Join(", ", strandable)}[/]");
            }

            if (byproduct.Count > 0)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[yellow]Untracked file(s) in the worktree (not counted against the check): {string.Join(", ", byproduct)}[/]");
            }
        }

        if (modified is null)
        {
            // Never guessed at as clean (InteractiveWorktreeGit's own contract): git could not
            // be asked, so the check is honestly skipped rather than silently passed.
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Could not read the worktree's git status at {run.WorktreePath}; skipping the uncommitted-files check.[/]");
        }
        else if (modified.Count > 0)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Modified-but-uncommitted file(s) in the worktree (gates run against them anyway): {string.Join(", ", modified)}[/]");
        }

        // RanFullScope/HeadSha together are ReviewEngine's own contract for "a full gate pass
        // was actually recorded over exactly this head" (VerificationPassed's own doc comment) —
        // true only when the tree was confirmed clean at the moment the gates ran. A dirty or
        // unreadable tree ran the gates over HEAD-plus-something-else, so claiming RanFullScope
        // against HeadSha there would be recording an unobserved fact as though it were observed
        // (AGENTS.md's "never guess at unobserved facts"; adversarial review, cycle 6). A
        // strandable untracked file (under src/ or tests/) is exactly that same
        // HEAD-plus-something-else case — dotnet build/test glob it in regardless of git status —
        // so it must hold the tree unclean too, not just a modified tracked file. Harmless
        // today only because RunSupervisor.ResumePipeline always re-verifies and overwrites
        // LastGate* before any review cycle reads it — the recorded fact should still be honest
        // on its own terms, since h9k task show renders this run's verification history from it.
        bool treeConfirmedClean = modified is not null && modified.Count == 0 && strandable.Count == 0;

        string gatesFingerprint = VerifyCommand.Fingerprint(project.VerifyCommands);

        if (project.VerifyCommands.Count == 0)
        {
            string? headSha = await InteractiveWorktreeGit.GetHeadShaAsync(run.WorktreePath, cancellationToken);
            await RecordPassAsync(
                session, runId, "No verification gates configured for this project.", headSha, gatesFingerprint,
                treeConfirmedClean, gateDurations: [], cancellationToken);
            AnsiConsole.MarkupLine("[green]No verification gates configured for this project — nothing to run.[/]");
            return ExitCodes.Ok;
        }

        // Every gate's own wall-clock duration this pass (task: gate wall-clock duration is
        // recorded and surfaced), in the order the gates ran.
        List<GateDuration> gateDurations = [];

        foreach (VerifyCommand gate in project.VerifyCommands)
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]Running gate '{gate.Name}'...[/]");
            Stopwatch gateStopwatch = Stopwatch.StartNew();
            (bool passed, string summary) = await RunGateAsync(run.WorktreePath, gate, cancellationToken);
            TimeSpan gateElapsed = gateStopwatch.Elapsed;
            if (passed)
            {
                gateDurations.Add(new GateDuration(gate.Name, gateElapsed, Passed: true));
                AnsiConsole.MarkupLineInterpolated($"[green]Gate '{gate.Name}' passed.[/]");
                continue;
            }

            gateDurations.Add(new GateDuration(gate.Name, gateElapsed, Passed: false));
            await RecordFailureAsync(session, runId, [gate.Name], gateDurations, cancellationToken);
            AnsiConsole.MarkupLineInterpolated($"[red]Gate '{gate.Name}' failed:[/]");
            AnsiConsole.WriteLine(summary);

            // The same distinction VerificationRunner's own headless failure now reports (task: a
            // verify gate that cannot pass on clean main is caught before it costs a run) — best
            // effort here too, so an operator watching this command's own output is not left to
            // rediscover by hand that the gate itself, not their branch, is what is broken.
            // Announced up front, the same as ProjectSetCommand's own validation loop, so an
            // operator watching a failed gate's output is not left staring at a silent terminal
            // for up to this comparison's own timeout with no indication anything is still
            // happening (independent pre-PR review, cycle 1, adversarial lens, low).
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Checking whether gate '{gate.Name}' also fails against a clean checkout of '{project.BaseBranch}'...[/]");
            if (await DescribeCleanBaseComparisonAsync(project, gate, cancellationToken) is { } note)
            {
                AnsiConsole.MarkupLineInterpolated($"[yellow]{note}[/]");
            }

            return ExitCodes.Conflict;
        }

        string? passHeadSha = await InteractiveWorktreeGit.GetHeadShaAsync(run.WorktreePath, cancellationToken);
        await RecordPassAsync(
            session, runId, $"h9k task verify: {project.VerifyCommands.Count} gate(s) ran full scope.", passHeadSha,
            gatesFingerprint, treeConfirmedClean, gateDurations, cancellationToken);
        AnsiConsole.MarkupLineInterpolated($"[green]Verification passed ({project.VerifyCommands.Count} gate(s)).[/]");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Whether <paramref name="gate"/> also fails when run once against a clean checkout of the
    /// project's own base branch — null when the comparison cannot be made (no reachable checkout,
    /// a bare clone, a repo/dev this call cannot confirm is at the base branch's current tip, or the
    /// attempt is <see cref="GateCheckOutcome.Inconclusive"/>) or when the gate is actually observed
    /// to pass there, in which case this run's own failure is real and a note here would only be
    /// noise. Mirrors VerificationRunner.DescribeCleanBaseComparisonAsync's own daemon-side logic
    /// (this project cannot reference Hall9k.Daemon), best effort: a failure here is swallowed
    /// rather than replacing the real gate failure this command already reported. Whether the
    /// checkout itself is confirmed clean and on the base branch is checked and named in the note
    /// rather than gating whether the note is made at all — the gate command genuinely did run and
    /// exit with a real code either way (independent pre-PR review, cycle 1, both lenses, sweeping
    /// the identical shape found in VerificationRunner's own version of this method).
    /// </summary>
    private static async Task<string?> DescribeCleanBaseComparisonAsync(
        ProjectDetails project, VerifyCommand gate, CancellationToken cancellationToken)
    {
        try
        {
            string checkout = ProjectCheckout.ForReading(project);
            if (!Directory.Exists(checkout) || ProjectCheckout.IsBare(checkout))
            {
                return null;
            }

            GitWorktreeManager worktrees = new(new ConsoleWorktreeLogger<GitWorktreeManager>());

            if (ProjectCheckout.IsHomeDevWorktree(project, checkout))
            {
                CheckoutRefresh refresh = await worktrees.RefreshReadingCheckoutAsync(checkout, project.BaseBranch, cancellationToken);
                if (!refresh.UpToDate)
                {
                    return null;
                }
            }

            string? uncleanNote = await CheckoutCleanliness.DescribeNotConfirmedCleanAsync(checkout, project.BaseBranch, cancellationToken);

            // Serializes this checkout's gate spawn against every other caller that can run a
            // command in it at the same time (the daemon's own post-failure comparison, another
            // concurrent h9k task verify, h9k project set --verify) — the identical reasoning
            // VerificationRunner.DescribeCleanBaseComparisonAsync's own lock documents.
            await using IAsyncDisposable gateLock = await worktrees.AcquireRepositoryLockAsync(checkout, cancellationToken);
            GateCheckResult result = await AdHocGateRunner.RunAsync(checkout, gate.Command, CleanBaseComparisonTimeout, cancellationToken);
            if (result.Outcome != GateCheckOutcome.Failed)
            {
                return null;
            }

            string suffix = uncleanNote is null
                ? string.Empty
                : $" (checkout {uncleanNote}, so this may reflect the checkout's own local state rather than '{project.BaseBranch}' itself)";
            return $"Gate '{gate.Name}' also fails when run against a clean checkout of '{project.BaseBranch}'{suffix}: {result.OutputTail}";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    // Bounded well under GateTimeout below the same way VerificationRunner.CleanBaseComparisonTimeoutCap
    // is: this is a best-effort diagnostic on top of a failure already reported either way, not the
    // gate itself, so it has no claim on the same 30-minute budget a real gate run gets.
    private static readonly TimeSpan CleanBaseComparisonTimeout = TimeSpan.FromMinutes(5);

    // Mirrors DaemonOptions.VerifyGateTimeout's own default (30 minutes, PLAN.md §16 #132's
    // follow-up review) — this project cannot reference Hall9k.Daemon (Reference graph:
    // Cli -> Domain + Connectors), so there is no per-project override here.
    private static readonly TimeSpan GateTimeout = TimeSpan.FromMinutes(30);

    // Mirrors GateInfrastructureFailureClassifier.GateWaitEvidenceDirectoryEnvironmentVariable's
    // literal value (Hall9k.Daemon) rather than referencing it, for the identical reason
    // GateTimeout above duplicates DaemonOptions.VerifyGateTimeout instead of reading it.
    private const string GateWaitEvidenceDirectoryEnvironmentVariable = "HALL9K_VERIFY_GATE_WAIT_DIR";

    private static readonly Regex ElapsedSecondsPattern = new(@"\((?<seconds>\d+)s elapsed", RegexOptions.Compiled);

    private static async Task<(bool Passed, string Summary)> RunGateAsync(
        string worktreePath, VerifyCommand gate, CancellationToken cancellationToken)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            WorkingDirectory = worktreePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // Where CrossProcessContainerGate.AcquireAsync leaves durable evidence that a
        // dotnet-test-shaped gate was still queued on the machine-wide container gate at the
        // moment it was killed (PLAN.md §16 #132) — never the gate's own captured console output,
        // which vstest.console buffers internally and only relays if it survives long enough to
        // report the testhost's own death, which the entireProcessTree kill below never allows
        // (the same reasoning VerificationRunner.RunGateAsync's own identical plumbing documents).
        // Scoped to this one gate invocation and removed once it finishes, in the finally block
        // below, so a directory from an earlier verify never lingers or is mistaken for this one's.
        string gateWaitDirectory = Directory.CreateTempSubdirectory("hall9k-verify-gate-wait-").FullName;
        process.StartInfo.Environment[GateWaitEvidenceDirectoryEnvironmentVariable] = gateWaitDirectory;

        if (OperatingSystem.IsWindows())
        {
            process.StartInfo.FileName = "cmd.exe";
            // WindowsCommandLine.WrapForCmdExe's own doc comment records why: cmd.exe's /c
            // parsing does not follow the CommandLineToArgvW convention ArgumentList assumes, so
            // a gate command carrying its own embedded quotes gets mangled unless it is wrapped
            // in one extra pair and set as the raw Arguments string exactly as
            // VerificationRunner.RunGateAsync already does for the identical cmd.exe path
            // (adversarial review, cycle 1).
            process.StartInfo.Arguments = WindowsCommandLine.WrapForCmdExe(gate.Command);
        }
        else
        {
            process.StartInfo.FileName = "/bin/sh";
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add(gate.Command);
        }

        // Streamed to the console line by line as it arrives, and buffered in parallel for the
        // failure summary's own tail — buffering to completion and printing nothing until the
        // gate finishes left an operator watching a silent terminal for however long `dotnet
        // test` takes, with no way to tell "nothing is happening yet" from "it hung", and no
        // output at all on a passing run (conformance review, cycle 1: this type's own doc
        // comment promises "an operator watching the output can see and re-run a flake
        // themselves", which buffering to completion cannot deliver). stdout and stderr each
        // fire on their own thread-pool thread, so both the console write and the shared
        // builder are guarded by one lock.
        StringBuilder output = new();
        object outputLock = new();
        void OnOutputReceived(object? sender, DataReceivedEventArgs e)
        {
            if (e.Data is null)
            {
                return;
            }

            lock (outputLock)
            {
                Console.WriteLine(e.Data);
                output.AppendLine(e.Data);
            }
        }

        process.OutputDataReceived += OnOutputReceived;
        process.ErrorDataReceived += OnOutputReceived;

        try
        {
            try
            {
                process.Start();
            }
            catch (Win32Exception exception)
            {
                // The worktree can vanish between h9k task work claiming it and this gate running
                // (deleted by hand, or pruned) — TaskWorkCommand.ReenterAsync guards this exact
                // state explicitly, and this is the one command in the interactive surface that
                // would otherwise crash on it with a raw stack trace instead of the domain-shaped
                // failure every other gate outcome here already returns (adversarial review,
                // cycle 8).
                return (false, $"Gate '{gate.Name}' could not start: {exception.Message}");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Mirrors VerificationRunner.RunGateAsync's own kill-on-cancel and timeout: an
            // operator's own Ctrl-C, or a gate that simply hangs, must not leave it writing into
            // the claim's worktree after this command has already walked away from it, and must
            // not block the command indefinitely either (adversarial review, cycle 1).
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(GateTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                        // Already exited between the check and the kill — nothing left to do.
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                // Honest about which it was, rather than blaming a broken gate for what may be
                // ordinary cross-process contention on CrossProcessContainerGate (PLAN.md §16
                // #132) — an operator reading a plain "exceeded its timeout" here has no way to
                // tell the two apart otherwise (independent pre-PR review, cycle 1). Never
                // retried: this command is deliberately simpler than the daemon's own
                // VerificationRunner (see this type's own doc comment) — an operator who reads
                // "still queued" can just run h9k task verify again.
                string? waitExcerpt = DescribeUnresolvedGateWait(gateWaitDirectory, GateTimeout);
                string timeoutSummary = waitExcerpt is null
                    ? $"Gate '{gate.Name}' exceeded its {GateTimeout.TotalMinutes:0}-minute timeout."
                    : $"Gate '{gate.Name}' exceeded its {GateTimeout.TotalMinutes:0}-minute timeout while " +
                      "still queued on the cross-process container gate (PLAN.md §16 #132) — it never even " +
                      "reached its own tests. This is very likely contention from another concurrent " +
                      "dotnet test invocation (a headless run, this project's own foreground suite, or " +
                      $"another operator's own h9k task verify), not a broken gate; try again once it " +
                      $"finishes. {waitExcerpt}";
                return (false, timeoutSummary);
            }

            string tail;
            lock (outputLock)
            {
                tail = Tail(output.ToString());
            }

            return process.ExitCode == 0
                ? (true, "ok")
                : (false, $"Gate '{gate.Name}' exited {process.ExitCode}. Output: {tail}");
        }
        finally
        {
            try
            {
                Directory.Delete(gateWaitDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort: a leftover scratch directory under the OS temp root is not this
                // gate's problem to solve, the same convention CrossProcessContainerGate's own
                // evidence-file cleanup follows.
            }
        }
    }

    // The evidence file this gate's own CrossProcessContainerGate.AcquireAsync writes is a short,
    // fixed-shape diagnostic line, always well under this — HALL9K_VERIFY_GATE_WAIT_DIR is
    // exported to the gate's whole process tree, i.e. to the agent's own test code too, so a file
    // there is skipped rather than read unbounded into memory (independent pre-PR review, cycle 1,
    // the same reasoning GateInfrastructureFailureClassifier.UnresolvedGateWaitExcerpt now applies).
    private const long MaxWaitEvidenceBytes = 4096;

    /// <summary>
    /// Whether some file in <paramref name="gateWaitDirectory"/> shows a wait that consumed most
    /// of <paramref name="gateTimeout"/> — proof this gate's own process never got a permit for
    /// nearly the whole run, not merely that a class happened to be one of the ordinarily several
    /// queued behind CrossProcessContainerGate's fixed permit count at any given instant during a
    /// busy tier. Mirrors GateInfrastructureFailureClassifier.UnresolvedGateWaitExcerpt's own
    /// fix for the identical false-positive (independent pre-PR review, cycle 1) rather than
    /// referencing it — this project cannot reference Hall9k.Daemon.
    /// </summary>
    private static string? DescribeUnresolvedGateWait(string gateWaitDirectory, TimeSpan gateTimeout)
    {
        if (!Directory.Exists(gateWaitDirectory))
        {
            return null;
        }

        TimeSpan threshold = gateTimeout * 0.8;
        foreach (string file in Directory.EnumerateFiles(gateWaitDirectory))
        {
            string content;
            try
            {
                if (new FileInfo(file).Length > MaxWaitEvidenceBytes)
                {
                    continue;
                }

                content = File.ReadAllText(file);
            }
            catch (IOException)
            {
                // Deleted or rewritten between the enumeration and the read by a class whose own
                // wait just resolved — not this gate's own evidence to report; try the next file.
                continue;
            }

            Match match = ElapsedSecondsPattern.Match(content);
            if (match.Success
                && int.TryParse(match.Groups["seconds"].Value, out int seconds)
                && TimeSpan.FromSeconds(seconds) >= threshold)
            {
                return content.Trim();
            }
        }

        return null;
    }

    private static string Tail(string content) =>
        content.Length <= 4000 ? content : content[^4000..];

    private static async Task RecordPassAsync(
        IDocumentSession session, Guid runId, string? note, string? headSha, string verifyCommandsFingerprint,
        bool ranFullScope, IReadOnlyList<GateDuration> gateDurations, CancellationToken cancellationToken)
    {
        session.Events.Append(
            runId,
            new VerificationPassed(
                runId, DateTimeOffset.UtcNow, note, ranFullScope, headSha, verifyCommandsFingerprint, gateDurations));
        await session.SaveChangesAsync(cancellationToken);
    }

    private static async Task RecordFailureAsync(
        IDocumentSession session, Guid runId, IReadOnlyList<string> failedGates,
        IReadOnlyList<GateDuration> gateDurations, CancellationToken cancellationToken)
    {
        session.Events.Append(runId, new VerificationFailed(runId, failedGates, DateTimeOffset.UtcNow, gateDurations));
        await session.SaveChangesAsync(cancellationToken);
    }
}
