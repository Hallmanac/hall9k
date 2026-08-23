using System.Diagnostics;
using Hall9k.Cli.Diagnostics;
using Hall9k.Cli.Infrastructure;
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
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CatchUpTimeout = TimeSpan.FromSeconds(30);

    // The daemon's graceful-shutdown budget is 30s (HostOptions.ShutdownTimeout); give
    // it that plus margin before reporting the stop as still in progress.
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(45);

    public static async Task<int> StartAsync(IDaemonAutostart autostart, string? binaryOverride, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            await Console.Error.WriteLineAsync(
                "The daemon lifecycle on Windows arrives with S1-14 (Decisions Log #3); macOS (and other Unix) only for now.");
            return ExitCodes.Error;
        }

        if (DaemonProcess.Probe() is { } running)
        {
            await Console.Error.WriteLineAsync(
                $"h9kd is already running (pid {running.ProcessId}, started {running.StartedAt:u}). "
                + "One daemon per node is the rule — the single-instance guard would refuse a second anyway. "
                + "See it with h9k daemon status; end it with h9k daemon stop.");
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
            // missing file back, so an unresolvable path surfaces only as the 10s start
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
        if (!await DatabaseDoctor.RunAsync(offerFixes: true, cancellationToken))
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
            // An explicit --binary is a promise launchd cannot keep: the registration
            // points at the installed binary, so routing through it would start
            // something other than what was asked for, and say nothing about it. The
            // override wins and the divergence is stated. Stop is unaffected — it
            // signals the recorded pid directly whether or not launchd owns the job.
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Autostart is registered, but --binary was given — starting {binary} directly, outside launchd. The registered binary is what starts at login, and what h9k daemon start uses without --binary.[/]");
            startThroughAutostart = false;
        }

        if (startThroughAutostart)
        {
            // Autostart owns the job: starting through launchd keeps stop/restart with
            // the service manager instead of leaving it a process launchd knows nothing about.
            AnsiConsole.MarkupLine("[dim]Autostart is registered — starting through launchd.[/]");
            if (!await autostart.StartAsync(cancellationToken))
            {
                await Console.Error.WriteLineAsync(
                    "launchctl could not start the daemon — try h9k daemon autostart disable, then h9k daemon start.");
                return ExitCodes.Error;
            }
        }
        else
        {
            SpawnDetached(binary);
        }

        DaemonProcessDescriptor? started = await PollAsync(DaemonProcess.Probe, StartTimeout, cancellationToken);
        if (started is null)
        {
            await Console.Error.WriteLineAsync(
                $"h9kd did not come up within {StartTimeout.TotalSeconds:0}s — the log has the story:");
            foreach (string line in DaemonLog.Tail(5))
            {
                await Console.Error.WriteLineAsync($"  {line}");
            }

            return ExitCodes.Error;
        }

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
        if (OperatingSystem.IsWindows())
        {
            await Console.Error.WriteLineAsync(
                "The daemon lifecycle on Windows arrives with S1-14 (Decisions Log #3); macOS (and other Unix) only for now.");
            return ExitCodes.Error;
        }

        DaemonProcessDescriptor? running = DaemonProcess.Probe();
        if (running is null)
        {
            AnsiConsole.MarkupLine("[dim]h9kd is not running — nothing to stop.[/]");
            return ExitCodes.Ok;
        }

        if (autostart.IsSupported && await autostart.IsLoadedAsync(cancellationToken))
        {
            // Stopped must mean stopped: unloading through launchctl keeps the
            // KeepAlive policy from resurrecting a daemon the human just killed.
            AnsiConsole.MarkupLine("[dim]Autostart owns the job — stopping through launchctl (bootout).[/]");
            if (!await autostart.StopAsync(cancellationToken))
            {
                await Console.Error.WriteLineAsync("launchctl bootout failed — signaling the daemon directly.");
            }
        }

        // A loaded job only proves the label is bootstrapped, not that launchd owns the
        // running pid: a detached daemon can win the single-instance race against
        // launchd's RunAtLoad instance, leaving the job loaded but idle — bootout then
        // removes the idle job and never touches the real daemon. So the recorded pid,
        // if still alive, always gets the direct signal; a second SIGTERM to a host
        // already shutting down is a no-op.
        if (DaemonProcess.IsAlive(running.ProcessId, running.StartedAt))
        {
            await Exec.RunAsync("/bin/kill", ["-TERM", running.ProcessId.ToString()], cancellationToken);
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
    /// </summary>
    private static void SpawnDetached(string binaryPath)
    {
        ProcessStartInfo shell = new()
        {
            FileName = "/bin/sh",
            WorkingDirectory = RunPaths.Root,
            UseShellExecute = false,
        };
        shell.ArgumentList.Add("-c");
        shell.ArgumentList.Add("\"$0\" </dev/null >>\"$1\" 2>&1 &");
        shell.ArgumentList.Add(binaryPath);
        shell.ArgumentList.Add(DaemonRuntime.LogFile);

        using Process? intermediary = Process.Start(shell);
        intermediary?.WaitForExit();
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
