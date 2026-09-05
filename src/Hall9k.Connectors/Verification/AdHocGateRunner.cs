using System.ComponentModel;
using System.Diagnostics;
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
/// A single gate command spawned once, with no scoping, no infrastructure classification, and no
/// retry — the daemon's own <c>VerificationRunner</c> is deliberately not reused for this because
/// both of <see cref="AdHocGateRunner"/>'s callers ask a narrower question than a real gate pass
/// answers: does this command exit zero here, once, right now, or could that not even be
/// determined. <c>OutputTail</c> is trimmed to a bounded length — the same 400 characters
/// <c>VerificationRunner.TailOf</c> already holds a gate's recorded summary to — so a large
/// build's own output cannot blow out a one-line refusal or the attention pane's one-line failure
/// cause (conformance review, cycle 1: this used to cap at 2000, five times that budget).
/// </summary>
public sealed record GateCheckResult(GateCheckOutcome Outcome, string OutputTail);

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
    /// The budget a clean-base comparison gets, on either side of the CLI/daemon split — a
    /// best-effort diagnostic on top of a failure (or a refusal) that is already being recorded
    /// either way, not a real gate pass, so it has no claim on <see cref="DefaultTimeout"/>'s own
    /// 30-minute budget. Shared here rather than duplicated per caller (independent pre-PR
    /// review, cycle 1, conformance lens: <c>h9k project set --verify</c> used to hold the
    /// repository-wide worktree lock for up to <see cref="DefaultTimeout"/> per gate, with no cap
    /// of its own, while the daemon's own comparison already capped itself at exactly this value
    /// for exactly this reason). Also doubles as the budget a caller is willing to wait to
    /// *acquire* that same lock before giving up on the comparison rather than blocking
    /// indefinitely behind whichever other caller is already holding it.
    /// </summary>
    public static readonly TimeSpan CleanBaseCheckTimeoutCap = TimeSpan.FromMinutes(5);

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

            return new GateCheckResult(
                process.ExitCode == 0 ? GateCheckOutcome.Passed : GateCheckOutcome.Failed, Tail(ReadTailOutput(logFile)));
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

            using FileStream stream = new(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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
