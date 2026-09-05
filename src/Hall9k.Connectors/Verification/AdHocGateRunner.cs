using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Connectors.Verification;

/// <summary>
/// Whether a single gate command exited zero, spawned once, with no scoping, no infrastructure
/// classification, and no retry — the daemon's own <c>VerificationRunner</c> is deliberately not
/// reused for this because both of <see cref="AdHocGateRunner"/>'s callers ask a narrower question
/// than a real gate pass answers: does this command exit zero here, once, right now. <c>OutputTail</c>
/// is trimmed to a bounded length so a large build's own output cannot blow out a one-line refusal
/// or attention-pane cause.
/// </summary>
public sealed record GateCheckResult(bool Passed, string OutputTail);

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

    private const int MaxOutputTailLength = 2000;

    public static async Task<GateCheckResult> RunAsync(
        string workingDirectory, string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        if (OperatingSystem.IsWindows())
        {
            // Raw Arguments, never ArgumentList — a project's own gate command is entirely free
            // to carry embedded quotes (VerificationRunner.RunGateAsync's own comment gives this
            // repo's own CI filter as the example), which ArgumentList would escape in a way
            // cmd.exe's own /c parsing does not undo.
            process.StartInfo.FileName = "cmd.exe";
            process.StartInfo.Arguments = WindowsCommandLine.WrapForCmdExe(command);
        }
        else
        {
            process.StartInfo.FileName = "/bin/sh";
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add(command);
        }

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
                output.AppendLine(e.Data);
            }
        }

        process.OutputDataReceived += OnOutputReceived;
        process.ErrorDataReceived += OnOutputReceived;

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return new GateCheckResult(false, $"could not start: {exception.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

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

            return new GateCheckResult(false, $"exceeded its {timeout.TotalMinutes:0}-minute timeout");
        }

        string tail;
        lock (outputLock)
        {
            tail = Tail(output.ToString());
        }

        return new GateCheckResult(process.ExitCode == 0, tail);
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
