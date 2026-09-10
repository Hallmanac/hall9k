using System.ComponentModel;
using System.Diagnostics;
using Hall9k.Connectors.Processes;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Connectors.Verification;

/// <summary>
/// Whether a gate command was actually observed to pass or fail, or never reached either verdict
/// at all — a process that could not start, or one that had to be killed for overrunning its
/// timeout, tells you nothing about whether the command itself is broken, only that this attempt
/// could not answer the question (independent pre-PR review, cycle 1, adversarial lens: both
/// callers of this type used to read a timeout as a failure and then stated "also fails when run
/// against a clean checkout" as an observed fact — the exact unobserved-fact-as-fact mistake
/// AGENTS.md's "never guess" rule exists to catch, just pointed at this feature's own output).
/// </summary>
public enum GateCheckOutcome
{
    Passed,
    Failed,
    Inconclusive,
}

/// <summary>
/// A single gate command spawned once, with no scoping, no retry, and (with one exception) no
/// infrastructure classification of its own — the daemon's own <c>VerificationRunner</c> is
/// deliberately not reused for this because both of <see cref="AdHocGateRunner"/>'s callers ask a
/// narrower question than a real gate pass answers: does this command exit zero here, once, right
/// now, or could that not even be determined. <c>OutputTail</c> is trimmed to a bounded length —
/// the same 400 characters <c>VerificationRunner.TailOf</c> already holds a gate's recorded
/// summary to — so a large build's own output cannot blow out a one-line refusal or the attention
/// pane's one-line failure cause (conformance review, cycle 1: this used to cap at 2000, five
/// times that budget).
/// </summary>
/// <param name="FullOutput">
/// The gate's entire redirected output, empty except on <see cref="GateCheckOutcome.Failed"/>
/// (independent pre-PR review, cycle 1, both lenses, high: a caller classifying an infrastructure
/// failure — <c>VerificationRunner.DescribeCleanBaseComparisonAsync</c> — used to read
/// <see cref="OutputTail"/> for it, so a marker logged early in a long `dotnet test` run sat
/// outside the last 400 characters and went unclassified, the same mistake
/// <c>VerificationRunner.RunGateAsync</c>'s own <c>ReadFullOutput</c> already exists to avoid for
/// the run's own real gate). This class cannot reference
/// <c>GateInfrastructureFailureClassifier</c> itself (Reference graph: Connectors references only
/// Domain, never Daemon), so classification stays the caller's job — this field only makes sure
/// the caller has the text that job actually needs. Read once, from the log file, before it is
/// deleted below; never held across the process's own run the way an in-process
/// <c>OutputDataReceived</c> accumulator would, so a chatty gate's own heap cost stays exactly the
/// one-time file read <c>ReadFullOutput</c> already pays elsewhere, not a second, ongoing one.
/// Left empty for <see cref="GateCheckOutcome.Passed"/> and
/// <see cref="GateCheckOutcome.Inconclusive"/>: neither caller classifies those.
/// <para>
/// <c>null</c> means something different again, and only ever on <see cref="GateCheckOutcome.Failed"/>:
/// the log exists and genuinely could not be read, so nobody saw what the gate printed. Folding that
/// into <see cref="string.Empty"/> would hand a classifying caller "the gate printed nothing" for a
/// fact nobody observed, and a marker-free output is exactly what makes a failure look like the work's
/// own — the never-guess rule this type's reader (<see cref="ShareTolerantFile"/>) exists to honour.
/// A caller that classifies on this text has to handle the null; one that only reports
/// <see cref="OutputTail"/> never sees it.
/// </para>
/// </param>
public sealed record GateCheckResult(GateCheckOutcome Outcome, string OutputTail, string? FullOutput = "");

/// <summary>
/// Runs one gate command against some checkout — a clean base-branch checkout being validated
/// before <c>h9k project set --verify</c> ever records it (Windows field report item 11b), or a
/// run's own failed gate being re-run against clean base to tell "this gate was never going to
/// pass" apart from a bare gate failure. Both callers need the identical answer to the identical
/// question, so the process-spawning is written once here rather than duplicated a third time
/// alongside <c>VerificationRunner.RunGateAsync</c> and <c>TaskVerifyCommand.RunGateAsync</c>.
/// </summary>
public static class AdHocGateRunner
{
    /// <summary>Mirrors DaemonOptions.VerifyGateTimeout's own default; a caller with its own value passes it.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The budget a clean-base comparison's gate spawn gets on the CLI side of the CLI/daemon
    /// split (<c>h9k project set --verify</c>, <c>h9k task verify</c>) — a best-effort diagnostic
    /// on top of a failure (or a refusal) that is already being recorded either way, not a real
    /// gate pass, so it has no claim on <see cref="DefaultTimeout"/>'s own 30-minute budget
    /// (independent pre-PR review, cycle 1, conformance lens: <c>h9k project set --verify</c>
    /// used to hold the repository-wide worktree lock for up to <see cref="DefaultTimeout"/> per
    /// gate, with no cap of its own). The daemon's own comparison no longer uses this as its gate
    /// budget (task: the clean-base comparison can actually finish — origin incident
    /// 2026-09-05/06): <see cref="ComputeComparisonBudget"/> budgets that one off the gate's own
    /// recorded duration instead, since this fixed value alone could never fit a slow project's
    /// full test suite. This constant survives there as <see cref="ComputeComparisonBudget"/>'s own
    /// starting point when no duration has been recorded yet (or the recorded one is small) — not a
    /// floor it never budgets below, since the caller's own <c>verifyGateTimeout</c> can still clamp
    /// the result under it (a daemon configured with <c>DaemonOptions.VerifyGateTimeout</c> under
    /// five minutes — a node-level setting shared by every project it serves, not a per-project
    /// override — gets exactly that shorter budget for every comparison it runs; the test
    /// <c>An_inconclusive_comparison_is_retried_on_the_next_run</c> relies on this). It also
    /// survives — on both sides of the split — as the budget a caller is
    /// willing to wait to *acquire* the checkout lock before giving up on the comparison rather than
    /// blocking indefinitely behind whichever other caller is already holding it; that acquisition
    /// wait stays fixed even where the gate's own run below it does not, since an unbounded wait
    /// would defer the run's own real failure for as long as the other holder runs.
    /// </summary>
    public static readonly TimeSpan CleanBaseCheckTimeoutCap = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How far above a gate's own most recently recorded wall-clock duration
    /// <see cref="ComputeComparisonBudget"/> budgets a comparison for — headroom for the ordinary
    /// run-to-run variance a single sample carries, not a promise the comparison will always fit.
    /// </summary>
    private const double DurationMargin = 2.0;

    /// <summary>
    /// The budget a clean-base comparison actually gets for one gate: the larger of
    /// <see cref="CleanBaseCheckTimeoutCap"/> and <paramref name="recentDuration"/> (when known)
    /// times <see cref="DurationMargin"/>, but never more than <paramref name="verifyGateTimeout"/>
    /// — a comparison is a diagnostic on top of a failure already being recorded, never a claim on
    /// more time than a real gate pass itself gets. Origin incident 2026-09-05/06: this project's
    /// own full test gate takes 11-12 minutes, so every comparison against the fixed 5-minute cap
    /// alone timed out as "inconclusive" on all five of the run's own failed gates, and the
    /// thirteen-hour red main it was diagnosing was never actually diagnosed. Null
    /// <paramref name="recentDuration"/> — nothing recorded for this gate on this node yet — keeps
    /// the fixed cap rather than guessing at a number nobody has actually observed.
    /// </summary>
    public static TimeSpan ComputeComparisonBudget(TimeSpan? recentDuration, TimeSpan verifyGateTimeout)
    {
        TimeSpan budget = recentDuration is { } duration && duration * DurationMargin > CleanBaseCheckTimeoutCap
            ? duration * DurationMargin
            : CleanBaseCheckTimeoutCap;
        return budget < verifyGateTimeout ? budget : verifyGateTimeout;
    }

    private const int MaxOutputTailLength = 400;

    public static async Task<GateCheckResult> RunAsync(
        string workingDirectory, string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Redirected to a file at the shell level, the same technique VerificationRunner.RunGateAsync
        // already uses, rather than accumulated in an in-process StringBuilder via
        // OutputDataReceived: a chatty gate (an MSBuild loop, a retrying test harness) run for up
        // to this method's own timeout used to cost hundreds of MB of the daemon's own heap for
        // output only the last MaxOutputTailLength characters of which is ever read (independent
        // pre-PR review, cycle 1, adversarial lens, medium).
        string logFile = Path.Combine(Path.GetTempPath(), $"hall9k-adhoc-gate-{Guid.NewGuid():N}.log");
        string innerCommand = $"({command}) > \"{logFile}\" 2>&1";

        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };
        NonInteractiveGit.Apply(process.StartInfo);

        if (OperatingSystem.IsWindows())
        {
            // Windows field report item 3 (ruled 2026-09-01): two concurrent dotnet-test-shaped
            // gates on one Windows machine crashed each other's shared MSBuild child nodes with
            // MSB4166. VerificationRunner.RunGateAsync and TaskVerifyCommand.RunGateAsync both set
            // this at their own spawn; this is the platform's third Windows verify-gate spawner,
            // missed when that fix first landed (cycle 1 review, both lenses) — and the one with
            // no infrastructure classification or retry of its own, so an MSB4166 crash here would
            // otherwise be reported as an observed gate outcome rather than recognized as unread.
            process.StartInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";

            // Raw Arguments, never ArgumentList — a project's own gate command is entirely free
            // to carry embedded quotes (VerificationRunner.RunGateAsync's own comment gives this
            // repo's own CI filter as the example), which ArgumentList would escape in a way
            // cmd.exe's own /c parsing does not undo.
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
            try
            {
                process.Start();
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                return new GateCheckResult(GateCheckOutcome.Inconclusive, $"could not start: {exception.Message}");
            }

            using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already exited between the check and the kill — nothing left to do.
                }

                return new GateCheckResult(GateCheckOutcome.Inconclusive, $"exceeded its {DescribeTimeout(timeout)} timeout");
            }

            GateCheckOutcome outcome = process.ExitCode == 0 ? GateCheckOutcome.Passed : GateCheckOutcome.Failed;
            return new GateCheckResult(
                outcome,
                Tail(ReadTailOutput(logFile)),
                outcome == GateCheckOutcome.Failed ? ReadFullOutput(logFile) : string.Empty);
        }
        finally
        {
            try
            {
                File.Delete(logFile);
            }
            catch (IOException)
            {
                // Best-effort: a leftover scratch file under the OS temp root is not this gate's
                // problem to solve, the same convention TaskVerifyCommand's own gate-wait-evidence
                // cleanup follows.
            }
        }
    }

    // Rounding to whole minutes reports a sub-minute timeout as "0-minute", which is not what was
    // actually configured; seconds below a minute, whole minutes at or above it (independent
    // pre-PR review, cycle 2, adversarial lens — Copilot).
    private static string DescribeTimeout(TimeSpan timeout) => timeout.TotalMinutes < 1
        ? $"{timeout.TotalSeconds:0}-second"
        : $"{timeout.TotalMinutes:0}-minute";

    /// <summary>
    /// The last <see cref="MaxOutputTailLength"/> characters, read via a bounded seek rather than
    /// <c>File.ReadAllText</c>ing the whole file: a chatty gate's redirected log could otherwise
    /// still blow out this method's own heap even though the file it reads from was already meant
    /// to keep output off the process during the run (independent pre-PR review, cycle 2,
    /// adversarial lens — Copilot).
    /// </summary>
    private static string ReadTailOutput(string logFile)
    {
        try
        {
            if (!File.Exists(logFile))
            {
                return string.Empty;
            }

            // FileShare.ReadWrite | FileShare.Delete for the reason ShareTolerantFile's own doc
            // spells out: this log's writer may still hold it, and this method's own `finally`
            // deletes it. The bounded seek is why this cannot simply call ShareTolerantFile.
            using FileStream stream = new(
                logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long tailLength = Math.Min(stream.Length, MaxOutputTailLength);
            stream.Seek(-tailLength, SeekOrigin.End);

            byte[] buffer = new byte[tailLength];
            stream.ReadExactly(buffer);

            // A tail boundary that lands mid-character (a multi-byte UTF-8 sequence whose leading
            // byte fell just before the seek point) decodes to a leading U+FFFD replacement
            // character rather than the text that was actually there. Leading continuation bytes
            // (10xxxxxx) are fragments of whatever character the seek cut in half, never the start
            // of a new one, so they are skipped rather than decoded. At most 3 can precede a valid
            // start byte (the longest UTF-8 sequence is 4 bytes), so this loop is always bounded.
            int start = 0;
            while (start < buffer.Length && (buffer[start] & 0b1100_0000) == 0b1000_0000)
            {
                start++;
            }

            return System.Text.Encoding.UTF8.GetString(buffer, start, buffer.Length - start);
        }
        catch (IOException)
        {
            return "(unreadable)";
        }
    }

    /// <summary>
    /// The whole log file, read once, for the one case (a failed comparison) whose caller has to
    /// classify a marker that could be anywhere in it, not just its trailing 400 characters —
    /// mirrors <c>VerificationRunner.ReadFullOutput</c>'s own identical bounded-by-being-a-single-
    /// read approach for the run's own real gate, not the tail-only <see cref="ReadTailOutput"/>
    /// this method's other caller (a plain pass/fail check, never a classification) still uses.
    /// Null flows straight out to <see cref="GateCheckResult.FullOutput"/> rather than collapsing
    /// into <see cref="string.Empty"/>: the whole reason this branch's reader distinguishes
    /// unreadable from empty is so the one caller that classifies on this text can too, and
    /// <c>VerificationRunner.RunGateAsync</c>'s own <c>ReadFullOutput</c> keeps the same shape for
    /// the same reason (independent pre-PR review, cycle 1, both lenses, medium: this reader used
    /// to swallow the distinction the same sentence of the decisions log claimed it honoured).
    /// </summary>
    private static string? ReadFullOutput(string logFile) => ShareTolerantFile.TryReadAllText(logFile)?.Trim();

    private static string Tail(string content)
    {
        string trimmed = content.Trim();
        if (trimmed.Length == 0)
        {
            return "(empty)";
        }

        return trimmed.Length <= MaxOutputTailLength ? trimmed : trimmed[^MaxOutputTailLength..];
    }
}
