using System.Diagnostics;
using Hall9k.Cli.DaemonControl;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Spawns <c>claude -p</c> detached from this process — no attached terminal, no waiting for the
/// child to finish — for <see cref="TaskStartCommand"/> (task 8a56af78-h9k, "a deliberate human
/// kick-off dispatches a task on the spot"). The one CLI launch that genuinely needs to survive
/// this process's own exit: <c>h9k task work</c>'s own launch is foreground and waited on
/// (<c>Process.WaitForExitAsync</c>), so it never had to solve this.
/// <para>
/// The redirection shape mirrors <c>Hall9k.Daemon.ProcessManagement</c>'s own Unix/Windows split
/// exactly (not referenced — the CLI cannot reference <c>Hall9k.Daemon</c>, Reference graph:
/// Cli -> Domain + Connectors): stdin from the prompt file, stdout to the stream file, stderr to
/// its own file, so <c>h9k logs</c> reads this run's transcript exactly as it would a headless
/// daemon dispatch's. <c>-p --output-format stream-json</c> is Claude Code's own headless mode: it
/// reads the prompt once, works to completion with its own tools, and exits on its own — there is
/// no ongoing conversation this process would otherwise have to keep attached to feed.
/// </para>
/// <para>
/// Unix backgrounds the spawn inside <c>/bin/sh -c "... &amp;"</c> rather than running <c>exec</c>
/// in the foreground the way <c>UnixProcessManager</c> does for the daemon's own (already-detached)
/// spawns: the daemon's process is itself already reparented off any terminal
/// (<c>DaemonLifecycle.SpawnDetached</c>), but <c>h9k task start</c> is ordinarily run directly
/// from an interactive shell, so the same double-fork-shaped protection that lets <c>h9kd</c>
/// itself survive its own launching terminal closing (Decisions Log #31, origin incident: "the
/// hand-started daemon died three times in one day with its parent shell") applies here too.
/// Backgrounding loses the "exec replaces the shell, so the returned pid IS the real command"
/// trick <c>UnixProcessManager</c> relies on (a backgrounded job forks a distinct child the
/// wrapper's own pid never becomes), so the real pid is instead captured with the shell's own
/// <c>$!</c> right after backgrounding it, written to a scratch pidfile the wrapper's short
/// life easily outlives, then read back and deleted once the (near-instant) wrapper exits.
/// </para>
/// <para>
/// Windows needs none of this: an orphaned Windows process already outlives the process that
/// started it with no reparenting step required (<c>DaemonLifecycle.SpawnDetachedWindows</c>'s own
/// doc), so this spawns through <c>cmd.exe /c</c> exactly as <c>WindowsProcessManager</c> does for
/// the daemon's own agent spawns — a plain, non-backgrounded call that blocks only cmd.exe itself
/// until claude.exe exits — and simply never waits for it. The identity this records is cmd.exe's
/// own pid, the same asymmetry <c>WindowsProcessManager</c> already accepts (there is no
/// Windows <c>exec</c> to replace cmd.exe's own process image with claude.exe's), which is why
/// <see cref="InteractiveSessionLiveness"/>'s pid check works unchanged for either platform's claim.
/// </para>
/// </summary>
internal static class HeadlessLaunch
{
    public static (int ProcessId, DateTimeOffset StartedAt) SpawnDetached(
        string worktreePath, Guid claudeSessionId, string sessionName, AgentModel model, string promptFile,
        string streamFile, string standardErrorFile, string settingsFile, bool skipPermissions)
    {
        List<string> arguments =
        [
            "-p",
            "--output-format stream-json",
            "--verbose",
            $"--name \"{sessionName}\"",
            $"--session-id {claudeSessionId}",
            $"--model \"{model.Value}\"",
            $"--settings \"{settingsFile}\"",
        ];
        if (skipPermissions)
        {
            arguments.Add("--dangerously-skip-permissions");
        }

        string claudeCommand = $"\"{ClaudeBinary()}\" {string.Join(' ', arguments)}";
        string redirected =
            $"{claudeCommand} < \"{promptFile}\" > \"{streamFile}\" 2> \"{standardErrorFile}\"";

        return OperatingSystem.IsWindows()
            ? SpawnDetachedWindows(worktreePath, redirected, standardErrorFile)
            : SpawnDetachedUnix(worktreePath, redirected, standardErrorFile);
    }

    /// <summary>
    /// See the class doc for why this backgrounds rather than <c>exec</c>s in the foreground, and
    /// why the real pid is captured through a scratch pidfile rather than the wrapper's own.
    /// </summary>
    private static (int ProcessId, DateTimeOffset StartedAt) SpawnDetachedUnix(
        string worktreePath, string redirectedCommand, string standardErrorFile)
    {
        string pidFile = Path.Combine(Path.GetTempPath(), $"hall9k-task-start-pid-{Guid.NewGuid():N}");
        try
        {
            // Proven live (adversarial review, cycle 1): the shell below backgrounds claude
            // BEFORE it ever writes this pidfile ("... &\necho $! > pidfile"), so if that write
            // fails — a stale exported TMPDIR naming a directory that no longer exists,
            // Path.GetTempPath() returns it verbatim and unvalidated — claude is already running,
            // unrecorded, with nothing downstream (RunSupervisor never adopts a run dispatched
            // under the sentinel Guid.Empty node id) able to see the orphan. Preparing the
            // pidfile's own directory and proving a write actually lands, before the shell ever
            // runs, turns that failure into "claude never launches" instead of "claude launches
            // and nobody ever finds out" — the one shape closes the stale-TMPDIR case outright
            // (CreateDirectory recreates a merely-missing directory) and narrows every other cause
            // to the same tiny window every other write to this path already carries.
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(pidFile)!);
                File.WriteAllText(pidFile, string.Empty);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Could not prepare the detach wrapper's pidfile ({pidFile}) before launching claude: "
                    + exception.Message);
            }

            ProcessStartInfo shell = new()
            {
                FileName = "/bin/sh",
                WorkingDirectory = worktreePath,
                UseShellExecute = false,
            };
            shell.ArgumentList.Add("-c");
            shell.ArgumentList.Add($"{redirectedCommand} &\necho $! > \"{pidFile}\"\n");

            using Process wrapper = Process.Start(shell)
                ?? throw new InvalidOperationException("Failed to start the detach wrapper (/bin/sh).");
            // The wrapper's whole job is backgrounding claude and echoing its pid — both
            // near-instant — so this waits on the wrapper only, never on claude itself.
            wrapper.WaitForExit();
            if (wrapper.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"The detach wrapper exited {wrapper.ExitCode} before recording claude's pid.");
            }

            string pidText;
            try
            {
                pidText = File.ReadAllText(pidFile).Trim();
            }
            catch (IOException exception)
            {
                throw new InvalidOperationException(
                    $"Could not read the detach wrapper's pidfile ({pidFile}) after it exited 0: "
                    + exception.Message);
            }

            if (!int.TryParse(pidText, out int processId))
            {
                throw new InvalidOperationException(
                    $"The detach wrapper's pidfile ({pidFile}) did not contain a valid process id "
                    + (pidText.IsNotBlank() ? $"(read \"{pidText}\")." : "(it was empty)."));
            }

            // Verified empirically (self-review, task 8a56af78-h9k): a missing claude binary does
            // NOT fail the wrapper above — "command not found" happens asynchronously inside the
            // backgrounded job (the shell forks it before evaluating $!, so the pid is real even
            // though the fork is already doomed), written to standardErrorFile, with the wrapper
            // itself still exiting 0 and $! still naming that job's pid. A brief settle window
            // gives that near-instant failure time to actually land before this asks whether the
            // pid is still there — checking immediately raced it and missed it, catching a
            // GetProcessById that transiently succeeded against a process already mid-exit.
            Thread.Sleep(150);
            try
            {
                using Process claude = Process.GetProcessById(processId);
                return (processId, InteractiveSessionLiveness.ReadStartedAt(claude));
            }
            catch (ArgumentException)
            {
                string hint = TryReadStandardError(standardErrorFile);
                throw new InvalidOperationException(
                    $"The detached process (pid {processId}) had already exited by the time its liveness could "
                    + "be checked — most likely the claude binary could not be started."
                    + (hint.IsNotBlank() ? $" It reported: {hint}" : string.Empty));
            }
        }
        finally
        {
            // Scratch state for this launch alone; nothing downstream reads it once claude's pid
            // and start time are captured.
            try
            {
                File.Delete(pidFile);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// No pidfile dance needed: a plain, non-backgrounded <c>cmd.exe /c</c> call blocks only
    /// cmd.exe itself until claude.exe exits, so the pid <see cref="Process.Start(ProcessStartInfo)"/>
    /// returns here is stable and immediately readable — it is simply cmd.exe's own, not
    /// claude.exe's, the same asymmetry the daemon's own <c>WindowsProcessManager</c> accepts.
    /// <para>
    /// The same near-instant-failure hazard the Unix path guards against applies here too, in a
    /// different shape (independent pre-PR review, cycle 1, conformance lens): with
    /// <c>claude</c> missing from PATH (or a moved <c>HALL9K_CLAUDE_PATH</c>), cmd.exe's own
    /// <c>/c</c> parse fails immediately rather than blocking on claude.exe, so
    /// <c>Process.Start</c> alone cannot tell that apart from a live headless session —
    /// nothing downstream compensates, since <c>RunSupervisor</c> never adopts a run dispatched
    /// under the sentinel <see cref="Guid.Empty"/> node id. The same settle window plus a
    /// liveness recheck the Unix path already pays for closes the gap here too.
    /// </para>
    /// </summary>
    private static (int ProcessId, DateTimeOffset StartedAt) SpawnDetachedWindows(
        string worktreePath, string redirectedCommand, string standardErrorFile)
    {
        ProcessStartInfo shell = new()
        {
            FileName = "cmd.exe",
            WorkingDirectory = worktreePath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // The raw Arguments string, never ArgumentList (see WindowsCommandLine): the redirected
        // command already carries its own embedded quotes (a quoted flag value, a quoted
        // redirected file path), and ArgumentList would C-runtime-escape them in a way cmd.exe's
        // own /c parsing does not undo.
        shell.Arguments = WindowsCommandLine.WrapForCmdExe(redirectedCommand);

        using IDisposable handleGuard = WindowsStandardHandleInheritance.SuppressForChildProcesses();
        using Process process = Process.Start(shell)
            ?? throw new InvalidOperationException("Failed to start the detach wrapper (cmd.exe).");
        DateTimeOffset startedAt = InteractiveSessionLiveness.ReadStartedAt(process);

        // Mirrors the Unix path's own settle window exactly: a failure inside cmd.exe's /c
        // parse (claude missing, or the worktree gone) exits near-instantly, so a brief pause
        // gives that failure time to actually land before asking whether the process is still
        // there — checking immediately would race it.
        Thread.Sleep(150);
        if (process.HasExited)
        {
            string hint = TryReadStandardError(standardErrorFile);
            throw new InvalidOperationException(
                $"The detached process (pid {process.Id}) had already exited by the time its liveness could "
                + "be checked — most likely the claude binary could not be started."
                + (hint.IsNotBlank() ? $" It reported: {hint}" : string.Empty));
        }

        return (process.Id, startedAt);
    }

    private static string ClaudeBinary() =>
        Environment.GetEnvironmentVariable("HALL9K_CLAUDE_PATH") ?? "claude";

    /// <summary>Best-effort: a launch failure is reported honestly either way, with or without this hint.</summary>
    private static string TryReadStandardError(string standardErrorFile)
    {
        try
        {
            return File.Exists(standardErrorFile) ? File.ReadAllText(standardErrorFile).Trim() : string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

}
