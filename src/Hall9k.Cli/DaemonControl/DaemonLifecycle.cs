using System.Diagnostics;
using Hall9k.Cli.Diagnostics;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Spectre.Console;

namespace Hall9k.Cli.DaemonControl;

/// <summary>
/// The CLI-owned daemon lifecycle (Decisions Log #31): start detached on demand, stop
/// gracefully, both routed through the service manager whenever autostart owns the job.
/// Shared by the daemon commands and h9k install's restart offer.
/// </summary>
public static class DaemonLifecycle
{
    // Wide enough to absorb the ~15s assembly-resolution-and-JIT boot for the top-level
    // entry point — before the daemon ever reaches its own single-instance guard, so
    // Wolverine's later handler-discovery scan is not the cause — observed on the Arx
    // Windows node (2026-09-03), so the ordinary slow-boot case still resolves to a
    // confirmed pid rather than falling through to the "still starting" guess below.
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CatchUpTimeout = TimeSpan.FromSeconds(30);

    // The daemon's graceful-shutdown budget is 30s (HostOptions.ShutdownTimeout); give
    // it that plus margin before reporting the stop as still in progress.
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(45);

    public static async Task<int> StartAsync(IDaemonAutostart autostart, string? binaryOverride, CancellationToken cancellationToken)
    {
        DaemonBootStatus initialStatus = DaemonProcess.ProbeBootStatus();
        if (initialStatus is { State: DaemonBootState.Running, Running: { } running })
        {
            await Console.Error.WriteLineAsync(
                $"h9kd is already running (pid {running.ProcessId}, started {running.StartedAt:u}). "
                + "One daemon per node is the rule — the single-instance guard would refuse a second anyway. "
                + "See it with h9k daemon status; end it with h9k daemon stop.");
            return ExitCodes.Conflict;
        }

        if (initialStatus.State == DaemonBootState.Starting)
        {
            await Console.Error.WriteLineAsync(
                "h9kd is already starting — a spawn from moments ago is still booting (assembly "
                + "resolution and JIT for the entry point, before it ever reaches its own single-instance "
                + "guard, has taken up to ~15s on at least one real machine) and has not yet been observed "
                + "to reach that guard, so whether it already holds the lock isn't known here. Refusing to "
                + "spawn a second attempt rather than risk racing it. Give it a little longer; h9k daemon "
                + "status shows it as starting until its pid file lands.");
            return ExitCodes.Conflict;
        }

        string binary;
        if (binaryOverride is null)
        {
            string? located = DaemonBinary.Locate();
            if (located is null)
            {
                await Console.Error.WriteLineAsync(
                    $"No h9kd binary found. Install one with h9k install (publishes to {DaemonRuntime.BinDirectory}), "
                    + "or build the solution so the dev-loop binary exists (dotnet build).");
                return ExitCodes.Error;
            }

            binary = located;
        }
        else
        {
            // An override is resolved against the caller's directory and checked here,
            // for the same reason DaemonBinary.Locate() checks every candidate it
            // returns: SpawnDetached runs /bin/sh in ~/.hall9k, which cannot report a
            // missing file back, so an unresolvable path surfaces only as the 20s start
            // timeout — a diagnosis that names the wrong rule. Origin incident:
            // h9k daemon start --binary ./src/Hall9k.Daemon/bin/Debug/net10.0/h9kd from
            // the repo root, the natural dev-loop invocation, blocked for the full
            // timeout and then blamed startup for a path that was never going to exist.
            string workingDirectory = Directory.GetCurrentDirectory();
            string? resolved = DaemonBinary.ResolveOverride(binaryOverride, workingDirectory);
            if (resolved is null || !File.Exists(resolved))
            {
                await Console.Error.WriteLineAsync(
                    $"No h9kd binary at {resolved ?? binaryOverride} (--binary {binaryOverride}, "
                    + $"resolved against {workingDirectory}). Point --binary at a built h9kd, or drop it to use "
                    + $"the installed one ({DaemonRuntime.BinDirectory}) or the dev-loop build output.");
                return ExitCodes.Error;
            }

            binary = resolved;
        }

        // The reachability probe runs before anything else here, deliberately: the
        // "started" line below is a claim, and claims wait for the fact (origin incident,
        // 2026-08-21 — post-restart, daemon start printed started-with-pid and then "exited
        // during startup" with an Npgsql stack trace, because Postgres was not back up yet).
        // Interactive and unreachable-because-nothing-is-running offers to start it via
        // Docker (Decisions Log #73); non-interactive gets today's behavior, a named fix and
        // a refusal to spawn.
        //
        // The resolved string, not just a yes/no, is what SpawnDetached needs: h9kd runs
        // with its working directory forced to RunPaths.Root, so if it re-resolved on its
        // own it could walk up for a project override file from the wrong place and land on
        // a different connection string than the one just proven reachable here.
        if (await DatabaseDoctor.RunAsync(offerFixes: true, assumeYes: false, cancellationToken) is not { } connectionString)
        {
            return ExitCodes.Error;
        }

        Directory.CreateDirectory(RunPaths.Root);

        // Start with a log that is inside its budget, so this run's output is not read
        // out of a file that was already at the threshold. The running daemon enforces
        // the same budget on its own timer (LogRotationService) — a daemon left up for
        // weeks never reaches a start path again — and rotation is a copy-then-truncate
        // either way, so the two never fight over the file.
        try
        {
            if (DaemonLog.RotateIfOversized())
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[dim]The log had grown past its budget — rolled it aside to {DaemonLog.PreviousLogFile}.[/]");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A log that cannot be rolled is no reason to refuse to start the daemon — a
            // permission problem on the log directory no more than a locked file.
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Could not roll the oversized log aside ({exception.Message}) — starting anyway.[/]");
        }

        long logOffset = DaemonLog.CurrentLength();

        bool startThroughAutostart = autostart.IsSupported && autostart.IsEnabled;
        if (startThroughAutostart && binaryOverride is not null)
        {
            // An explicit --binary is a promise the service manager cannot keep: the
            // registration points at the installed binary, so routing through it would
            // start something other than what was asked for, and say nothing about it.
            // The override wins and the divergence is stated. Stop is unaffected — it
            // signals the recorded pid directly whether or not autostart owns the job.
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Autostart is registered, but --binary was given — starting {binary} directly, outside {autostart.MechanismDescription}. The registered binary is what starts at login, and what h9k daemon start uses without --binary.[/]");
            startThroughAutostart = false;
        }

        // Written just before the spawn, not after: it is the only evidence a concurrent
        // h9k daemon status has that this attempt is in flight during however long it
        // takes the daemon to reach its own single-instance guard and write the pid file
        // that would otherwise be the sole source of truth (DaemonRuntime.StartingMarkerFile's
        // own doc has the field incident this covers). The marker is advisory — a status
        // read during this window that misses it just reads as "not running" the way it
        // always did before this feature existed — so a write that fails (a locked file, a
        // concurrent h9k daemon status holding a read handle on Windows) must not abort the
        // boot it was only ever meant to describe.
        try
        {
            DaemonStartingMarker.Write(DaemonRuntime.StartingMarkerFile, DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Could not record the starting marker ({exception.Message}) — starting anyway; h9k daemon status may read this launch as not running until its pid file lands.[/]");
        }

        try
        {
            if (startThroughAutostart)
            {
                // Autostart owns the job: starting through the service manager keeps
                // stop/restart with it instead of leaving it a process it knows nothing about.
                AnsiConsole.MarkupLineInterpolated($"[dim]Autostart is registered — starting through {autostart.MechanismDescription}.[/]");
                if (!await autostart.StartAsync(cancellationToken))
                {
                    // The service manager itself reported failure — there is no launch in
                    // flight for the marker to describe, so leaving it behind would tell a
                    // concurrent h9k daemon status the opposite of what just happened.
                    DaemonStartingMarker.Delete(DaemonRuntime.StartingMarkerFile);
                    await Console.Error.WriteLineAsync(
                        $"{autostart.MechanismDescription} could not start the daemon — try h9k daemon autostart disable, then h9k daemon start.");
                    return ExitCodes.Error;
                }
            }
            else
            {
                SpawnDetached(binary, connectionString);
            }
        }
        catch
        {
            // Only the spawn attempt itself is covered here — SpawnDetached failing
            // outright, or autostart.StartAsync throwing rather than returning false.
            // Either means there is no launch in flight for the marker to describe, so
            // leaving it behind would misreport "starting" for the rest of its grace
            // period. Once this block returns without throwing, the daemon (or the
            // service manager's own child) is genuinely launched and detached from this
            // CLI process by design — the whole point of SpawnDetached's double fork — so
            // it must not also wrap the poll below: a Ctrl-C there cancels this command,
            // not the boot already under way, and deleting the marker on that exception
            // (pre-PR review, cycle 1's own fix, corrected here) would misreport a launch
            // that is still genuinely booting as not running, the exact bug task 92da629d
            // exists to fix, reintroduced by a different trigger.
            DaemonStartingMarker.Delete(DaemonRuntime.StartingMarkerFile);
            throw;
        }

        DaemonProcessDescriptor? started = await PollAsync(DaemonProcess.Probe, StartTimeout, cancellationToken);

        if (started is null)
        {
            // Not a failure: the marker just written proves this attempt is genuinely in
            // flight, and the pid file not landing within StartTimeout is exactly the
            // Windows boot-time gap this command exists to stop misreporting (task
            // 92da629d) — never leave the operator believing the daemon is down while a
            // first spawn is still booting. Whether it has reached its own
            // single-instance guard yet hasn't been observed either way.
            AnsiConsole.MarkupLine(
                $"[yellow]h9kd is still starting[/] after {StartTimeout.TotalSeconds:0}s — assembly "
                + "resolution and JIT for the entry point, before it ever reaches its own "
                + "single-instance guard, can run past this wait on some machines. That guard hasn't "
                + "been observed to run yet, so this is not necessarily a failed launch. Check again "
                + "shortly with h9k daemon status; the log has what it has said so far:");
            foreach (string line in DaemonLog.Tail(5))
            {
                AnsiConsole.MarkupLineInterpolated($"  [dim]{line}[/]");
            }

            return ExitCodes.Ok;
        }

        DaemonStartingMarker.Delete(DaemonRuntime.StartingMarkerFile);

        AnsiConsole.MarkupLineInterpolated(
            $"[green]h9kd started[/] (pid {started.ProcessId}) — logging to {DaemonRuntime.LogFile}");

        // The catch-up report is the visible price of on-demand: latency, never
        // correctness (Decisions Log #29, #31). Wait for it briefly — and notice a
        // daemon that dies during startup (Postgres down being the usual story) instead
        // of pretending it is catching up.
        DateTimeOffset deadline = DateTimeOffset.UtcNow + CatchUpTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (DaemonLog.FindCatchUpReport(logOffset) is { } catchUp)
            {
                AnsiConsole.MarkupLineInterpolated($"[dim]{catchUp}[/]");
                return ExitCodes.Ok;
            }

            if (!DaemonProcess.IsAlive(started.ProcessId, started.StartedAt))
            {
                await Console.Error.WriteLineAsync("h9kd exited during startup — the log has the story:");
                if (DaemonLog.FindLine(logOffset, "fail:") is { } failure)
                {
                    await Console.Error.WriteLineAsync($"  {Truncate(failure, 300)}");
                }
                else
                {
                    foreach (string line in DaemonLog.Tail(5))
                    {
                        await Console.Error.WriteLineAsync($"  {line}");
                    }
                }

                // The preflight probe above already found Postgres reachable moments ago, so
                // reaching this is a race (it went away in between) rather than the ordinary
                // case — h9k doctor re-diagnoses fresh rather than this guessing why.
                await Console.Error.WriteLineAsync("Run h9k doctor to diagnose why, then h9k daemon start again.");
                return ExitCodes.Error;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        AnsiConsole.MarkupLine(
            "[yellow]Still catching up[/] (waiting for Postgres?) — the catch-up report lands in the log; "
            + "h9k daemon status shows progress.");
        return ExitCodes.Ok;
    }

    public static async Task<int> StopAsync(IDaemonAutostart autostart, CancellationToken cancellationToken)
    {
        DaemonProcessDescriptor? running = DaemonProcess.Probe();
        if (running is null)
        {
            AnsiConsole.MarkupLine("[dim]h9kd is not running — nothing to stop.[/]");
            return ExitCodes.Ok;
        }

        bool stoppedThroughAutostart = false;
        if (autostart.IsSupported && await autostart.IsLoadedAsync(cancellationToken))
        {
            // Stopped must mean stopped: stopping through the service manager keeps its
            // own crash-restart policy from resurrecting a daemon the human just killed.
            AnsiConsole.MarkupLineInterpolated($"[dim]Autostart owns the job — stopping through {autostart.StopMechanismDescription}.[/]");
            stoppedThroughAutostart = await autostart.StopAsync(cancellationToken);
            if (!stoppedThroughAutostart)
            {
                await Console.Error.WriteLineAsync(
                    $"{autostart.StopMechanismDescription} could not stop the daemon — signaling it directly.");
            }
        }

        // A loaded job only proves the registration is bootstrapped, not that the service
        // manager owns the running pid: a detached daemon can win the single-instance race
        // against a RunAtLoad/logon-trigger instance, leaving the job loaded but idle —
        // the step above then removes the idle job and never touches the real daemon. So
        // the recorded pid, if still alive, always gets the direct signal — except on
        // Windows when the step above already delivered one: unlike a second Unix SIGTERM
        // (harmless — the same signal, sent twice), the Windows "signal" is a second write
        // of the identical stop-request file. DaemonPidFile.WriteAsync's atomic stage-and-
        // rename and WindowsStopRequestWatcher's atomic claim-by-rename (cycle-7 pre-PR
        // review finding) make a second write safe either way — claimed by this daemon's
        // watcher if it is still polling, or left for the next daemon to log as stale and
        // discard once this one has already exited — so skipping it here is purely to avoid
        // that redundant write and the stale-request warning it would otherwise leave behind
        // in the next daemon's log, not a correctness requirement.
        bool skipDirectSignal = OperatingSystem.IsWindows() && stoppedThroughAutostart;
        if (!skipDirectSignal && DaemonProcess.IsAlive(running.ProcessId, running.StartedAt))
        {
            await RequestGracefulStopAsync(running, cancellationToken);
        }

        bool exited = await WaitUntilAsync(
            () => !DaemonProcess.IsAlive(running.ProcessId, running.StartedAt), StopTimeout, cancellationToken);
        if (!exited)
        {
            await Console.Error.WriteLineAsync(
                $"h9kd (pid {running.ProcessId}) is still shutting down after {StopTimeout.TotalSeconds:0}s — "
                + "it finishes in-flight event appends before exiting. Check again with h9k daemon status.");
            return ExitCodes.Error;
        }

        // A starting marker describes a launch that, if it ever existed, has definitely
        // ended by the time the daemon it would have described is confirmed stopped here
        // — leaving it behind would have status/start keep reporting "starting" for the
        // rest of its grace period after a deliberate, successful stop (pre-PR review,
        // cycle 1).
        DaemonStartingMarker.Delete(DaemonRuntime.StartingMarkerFile);

        AnsiConsole.MarkupLineInterpolated(
            $"[green]h9kd stopped[/] (pid {running.ProcessId}). Detached agents keep running by design — the next start adopts them.");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The detach mechanism, honestly (Decisions Log #31): a /bin/sh intermediary
    /// backgrounds h9kd and exits immediately, so h9kd is reparented to launchd (pid 1)
    /// with stdin from /dev/null and stdout/stderr appended to the log — the
    /// double-fork pattern, no parent shell or CLI lifetime involved. Origin incident:
    /// the hand-started daemon died three times in one day with its parent shell.
    /// <paramref name="connectionString"/> is set on the child's environment explicitly,
    /// rather than left for h9kd to re-resolve: its working directory is forced to
    /// <see cref="RunPaths.Root"/> below, which is not where this connection string was
    /// resolved and proven reachable, so a project-override-file tier re-resolution there
    /// could silently land on a different (or no) connection string.
    /// </summary>
    private static void SpawnDetached(string binaryPath, string connectionString)
    {
        if (OperatingSystem.IsWindows())
        {
            SpawnDetachedWindows(binaryPath, connectionString);
            return;
        }

        ProcessStartInfo shell = new()
        {
            FileName = "/bin/sh",
            WorkingDirectory = RunPaths.Root,
            UseShellExecute = false,
        };
        shell.Environment[Hall9kDatabase.EnvironmentVariableName] = connectionString;
        shell.ArgumentList.Add("-c");
        shell.ArgumentList.Add("\"$0\" </dev/null >>\"$1\" 2>&1 &");
        shell.ArgumentList.Add(binaryPath);
        shell.ArgumentList.Add(DaemonRuntime.LogFile);

        using Process? intermediary = Process.Start(shell);
        intermediary?.WaitForExit();
    }

    /// <summary>
    /// Windows's detach needs no double-fork: an orphaned Windows process outlives the
    /// process that started it with no reparenting step required, unlike the Unix side
    /// above (which exists specifically to reparent h9kd off this CLI invocation and off
    /// its parent shell). What Windows lacks instead is a way to redirect a child's
    /// stdout/stderr to a FILE without this process owning the pipe (log #2's "the child
    /// owns the handle" requirement) — cmd.exe's own <c>&gt;&gt;</c>/<c>2&gt;&amp;1</c>
    /// syntax supplies that, exactly as <c>WindowsProcessManager</c> uses it for agent
    /// sessions. cmd.exe stays alive for h9kd's whole run (a plain <c>/c "command"</c>
    /// blocks until the child exits) — deliberately never awaited here, so this call
    /// returns the moment the process is created and h9k's own start command keeps
    /// running without waiting on the daemon's entire lifetime.
    /// <para>
    /// <see cref="WindowsStandardHandleInheritance"/> wraps the spawn: cmd.exe living for
    /// h9kd's whole run means any handle it inherits from this process lives that whole run
    /// too, and .NET's <see cref="Process.Start(ProcessStartInfo)"/> hands a child every
    /// inheritable handle this process holds regardless of what <paramref name="binaryPath"/>'s
    /// own <c>ProcessStartInfo</c> asks to redirect. Left unguarded, a caller piping or
    /// redirecting this command's own output hands cmd.exe a duplicate of that pipe's write
    /// handle at creation, and the caller then blocks reading it until the daemon itself
    /// exits — see the guard's own doc for the origin incident.
    /// </para>
    /// </summary>
    private static void SpawnDetachedWindows(string binaryPath, string connectionString)
    {
        ProcessStartInfo shell = new()
        {
            FileName = "cmd.exe",
            WorkingDirectory = RunPaths.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        shell.Environment[Hall9kDatabase.EnvironmentVariableName] = connectionString;
        // Tells h9kd this is the cmd.exe `>>` redirect WindowsAppendOnlyLog exists to
        // survive a rotation of — see DaemonRuntime.AppendOnlyLogEnvironmentVariable's own
        // doc for why this can't just be OperatingSystem.IsWindows().
        shell.Environment[DaemonRuntime.AppendOnlyLogEnvironmentVariable] = "1";
        // The raw Arguments string, never ArgumentList (see WindowsCommandLine): this
        // command carries its own embedded quotes around the binary path and the log
        // file, and ArgumentList would C-runtime-escape them in a way cmd.exe's own /c
        // parsing does not undo.
        shell.Arguments = WindowsCommandLine.WrapForCmdExe($"\"{binaryPath}\" < NUL >> \"{DaemonRuntime.LogFile}\" 2>&1");

        using IDisposable handleGuard = WindowsStandardHandleInheritance.SuppressForChildProcesses();
        using Process? process = Process.Start(shell);
    }

    /// <summary>
    /// Unix sends a real SIGTERM; Windows has none to send to an arbitrary process, so it
    /// writes <see cref="DaemonRuntime.StopRequestFile"/> instead, which
    /// <c>WindowsStopRequestWatcher</c> (running inside h9kd) polls for and honors by
    /// calling its own graceful shutdown — the same "ask nicely, poll for the answer"
    /// idiom every other cross-process signal in this platform already uses, standing in
    /// for the OS-level signal Windows does not have (Decisions Log #3, S1-14). The
    /// request names pid plus start time, never a bare pid (Decisions Log #2): a bare pid
    /// left behind by a daemon that died some other way before the watcher's next tick
    /// (a force-kill, a reboot) would otherwise match whichever later daemon is assigned
    /// the same pid and stop it seconds after a clean-looking start.
    /// </summary>
    private static async Task RequestGracefulStopAsync(DaemonProcessDescriptor running, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            await DaemonPidFile.WriteAsync(DaemonRuntime.StopRequestFile, running, cancellationToken);
            return;
        }

        await Exec.RunAsync("/bin/kill", ["-TERM", running.ProcessId.ToString()], cancellationToken);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static async Task<T?> PollAsync<T>(Func<T?> probe, TimeSpan timeout, CancellationToken cancellationToken)
        where T : class
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (probe() is { } result)
            {
                return result;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        return probe();
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        return condition();
    }
}
