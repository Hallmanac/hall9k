using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Connectors.Processes;

/// <summary>A step's own command, run to completion: its exit code and everything it printed, both streams merged in the order they arrived is not something the OS offers, so they are kept apart.</summary>
public sealed record ShellResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>Whether it succeeded, on the only signal a shell gives.</summary>
    public bool Succeeded => ExitCode == 0;

    /// <summary>What it said, for quoting back to a reviewer whose launch just failed — stderr first, since that is where a failing command usually explains itself.</summary>
    public string Output =>
        string.Join(
            "\n",
            new[] { StandardError.Trim(), StandardOutput.Trim() }.Where(text => text.Length > 0));
}

/// <summary>
/// Running a run skill's own command lines on a worktree (idea b9b09779, piece 5). A command line,
/// not a program and an argument list: a run skill is prose a session composed from a repository's
/// own documentation, so what it carries is the line a person would paste into a terminal —
/// pipelines, redirections, shell quoting and all — and handing that to
/// <see cref="ProcessStartInfo.ArgumentList"/> would run a program literally named
/// <c>docker compose up -d</c>.
/// <para>
/// It lives in Connectors rather than beside the daemon's own <c>IProcessManager</c> because both
/// ends of a local launch need it and they are in different projects: the CLI starts the launch
/// and stops it, and the daemon's sweep tears it down. <c>IProcessManager</c> is the richer seam —
/// descendant snapshots, lingering-child naming, a platform parity suite — and it is the daemon's,
/// serving agent sessions the daemon itself spawned. This is the narrow half of the same idea
/// (Decisions Log #2's pid-plus-start-time identity, and a child that owns its own output handle
/// so it outlives whatever started it) with nothing in it an agent session needs.
/// </para>
/// </summary>
public static class WorktreeShell
{
    /// <summary>How long a step's own command gets before the launch stops waiting on it and calls the step failed.</summary>
    public static readonly TimeSpan StepDeadline = TimeSpan.FromMinutes(10);

    // Start times drift slightly between recording and reading; within this window is the same
    // process, beyond it the pid was reused (the same tolerance ProcessManagerBase applies).
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Runs <paramref name="command"/> in <paramref name="workingDirectory"/> and waits for it,
    /// draining both streams. For the steps that finish — a prerequisite check, a restore, a
    /// migration — never for the one that serves the product, which is <see cref="Spawn"/>.
    /// <para>
    /// A step's child never outlives this call, whichever way the wait ends. The deadline says so
    /// in its own result; the reviewer pressing Ctrl+C says so by the caller's token, and that one
    /// has to kill the child explicitly rather than trusting the console signal to reach it — the
    /// child is started with <c>CreateNoWindow</c> on Windows, so it shares no console and never
    /// sees the Ctrl+C, and <c>h9k</c> exiting would leave a restore running against the worktree
    /// with nothing recorded anywhere that could ever end it.
    /// </para>
    /// </summary>
    public static async Task<ShellResult> RunAsync(
        string command, string workingDirectory, TimeSpan deadline, CancellationToken cancellationToken)
    {
        using Process process = new() { StartInfo = ShellStartInfo(command, workingDirectory) };
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;

        StringBuilder standardOutput = new();
        StringBuilder standardError = new();
        process.OutputDataReceived += (_, line) => Append(standardOutput, line.Data);
        process.ErrorDataReceived += (_, line) => Append(standardError, line.Data);

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start: {command}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(deadline);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            return new ShellResult(
                -1, standardOutput.ToString(),
                $"{standardError}\nThis step was still running after {deadline.TotalMinutes:0.#} minute(s) and was ended.");
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        return new ShellResult(process.ExitCode, standardOutput.ToString(), standardError.ToString());
    }

    /// <summary>
    /// Starts <paramref name="command"/> detached, with both its streams redirected into
    /// <paramref name="logFile"/> by the shell itself, and returns its identity without waiting.
    /// The child owning that file handle directly rather than a pipe is the whole point: the
    /// <c>h9k</c> process that started it exits seconds later, and the product has to still be
    /// up when the reviewer opens the address they were handed.
    /// </summary>
    public static LaunchedProcess Spawn(string command, string workingDirectory, string logFile)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(logFile) is { Length: > 0 } directory ? directory : workingDirectory);
        using Process process = new()
        {
            StartInfo = ShellStartInfo(Redirected(command, logFile), workingDirectory),
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start: {command}");
        }

        return new LaunchedProcess(process.Id, ReadStartedAt(process));
    }

    /// <summary>
    /// This process's own identity, for a caller that has to record itself as the one doing
    /// something — <c>h9k task run-local</c> recording the walk it is about to make, so a second
    /// invocation can tell a walk still in flight from one that died part-way through. The same
    /// pid-plus-start-time pair as everything else here, read the same way, so
    /// <see cref="IsAlive"/> answers for it too.
    /// </summary>
    public static LaunchedProcess Current()
    {
        using Process process = Process.GetCurrentProcess();
        return new LaunchedProcess(process.Id, ReadStartedAt(process));
    }

    /// <summary>
    /// Whether the process recorded as <paramref name="processId"/> started at
    /// <paramref name="startedAt"/> is still that same process. Both halves, always: over the
    /// hours a reviewer might leave a launch up, a bare pid says nothing (Decisions Log #2).
    /// </summary>
    public static bool IsAlive(int processId, DateTimeOffset startedAt)
    {
        using Process? process = TryGet(processId, startedAt);
        return process is not null;
    }

    /// <summary>
    /// Ends the whole tree rooted at the recorded process, and says whether it found anything
    /// alive to end. False is not a failure — a product the reviewer already closed, or one that
    /// crashed, leaves nothing here to kill, and the launch is recorded as stopped either way.
    /// </summary>
    public static bool TerminateTree(int processId, DateTimeOffset startedAt)
    {
        using Process? process = TryGet(processId, startedAt);
        if (process is null)
        {
            return false;
        }

        try
        {
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception
            or NotSupportedException or AggregateException)
        {
            // It exited between the identity check and the kill, or the OS refused partway
            // through a tree it had already started dismantling. Either way there is nothing
            // left for this caller to do, and reporting it as an error would turn an already-gone
            // process into a failed stop.
            return false;
        }
    }

    /// <summary>
    /// The shell invocation for a command line: <c>cmd.exe /c</c> on Windows, <c>/bin/sh -c</c>
    /// on Unix. The shell itself stays as an intermediary on both, waiting exactly as long as the
    /// real command runs — the same trade <c>WindowsProcessManager</c> already makes and
    /// documents, and invisible to a caller because every check here is liveness or a tree kill.
    /// <para>
    /// The Unix half deliberately does NOT <c>exec</c> the command, though that would make the pid
    /// returned the command's own. <c>exec</c> takes a program and its arguments, and a run skill's
    /// lines are neither: <c>exec PORT=3000 npm start</c> fails with exit 127 on the leading
    /// assignment, which is one of the three port forms this feature recognises, and
    /// <c>exec npm install &amp;&amp; npm run build</c> replaces the shell at the first command so the
    /// second never runs and the step still reports success. <c>UnixProcessManager</c> gets away
    /// with <c>exec</c> because it always spawns one known binary; this class takes the line a
    /// person would paste into a terminal, which is the whole point of it.
    /// </para>
    /// </summary>
    private static ProcessStartInfo ShellStartInfo(string command, string workingDirectory)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new ProcessStartInfo("cmd.exe")
            {
                // The raw Arguments string, never ArgumentList: the command already carries its
                // own quoting, and ArgumentList would C-runtime-escape it in a way cmd.exe's /c
                // parsing does not undo (WindowsCommandLine's own note).
                Arguments = WindowsCommandLine.WrapForCmdExe(command),
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
        }

        ProcessStartInfo shell = new("/bin/sh")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };
        shell.ArgumentList.Add("-c");
        shell.ArgumentList.Add(command);
        return shell;
    }

    /// <summary>
    /// Both streams into one file, and stdin closed — syntax <c>/bin/sh</c> and <c>cmd.exe</c>
    /// already agree on. Appending rather than truncating, because a run skill's launch section
    /// may start more than one process into the same log; the caller clears it once when the
    /// launch opens, so a new launch never reads as a continuation of the last one.
    /// </summary>
    private static string Redirected(string command, string logFile) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"{command} >> \"{logFile}\" 2>&1 < NUL"
            : $"{command} >> \"{logFile}\" 2>&1 < /dev/null";

    private static void Append(StringBuilder buffer, string? line)
    {
        if (line is not null)
        {
            buffer.Append(line).Append('\n');
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception
            or NotSupportedException or AggregateException)
        {
            // Already gone. Nothing to report: the caller is about to say the step timed out,
            // which is the fact that matters.
        }
    }

    /// <summary>
    /// A just-started process's start time, or <see cref="DateTimeOffset.MinValue"/> when the OS
    /// has nothing left to report because the command already exited. The sentinel rather than
    /// "now": this value becomes the process identity every later check keys off, and a plausible
    /// stamp could false-match a recycled pid, while the sentinel simply never matches — so a
    /// launch whose command died instantly reads as gone, which it is.
    /// </summary>
    private static DateTimeOffset ReadStartedAt(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static Process? TryGet(int processId, DateTimeOffset startedAt)
    {
        try
        {
            Process process = Process.GetProcessById(processId);
            DateTimeOffset actual = new(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            if ((actual - startedAt).Duration() <= StartTimeTolerance)
            {
                return process;
            }

            process.Dispose();
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or Win32Exception)
        {
            return null;
        }
    }
}

/// <summary>What <see cref="WorktreeShell.Spawn"/> hands back: pid and start time together, which is a process identity (Decisions Log #2).</summary>
public sealed record LaunchedProcess(int ProcessId, DateTimeOffset StartedAt);
