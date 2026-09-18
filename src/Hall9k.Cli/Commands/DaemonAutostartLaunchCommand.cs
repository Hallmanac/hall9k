using System.ComponentModel;
using Hall9k.Cli.DaemonControl;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Infrastructure.Storage;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The vehicle the registered logon task runs, and the one command here a human has no reason
/// to type: <c>h9k daemon start</c> is the on-demand path, and this is what Task Scheduler
/// reaches by way of <c>wscript.exe</c> and <c>cmd.exe</c> at login.
/// <para>
/// It exists because a handle cannot be passed through a VBScript command line. The autostart
/// chain composes its command inside <c>WindowsDaemonAutostart.LaunchScriptContent</c> for
/// <c>WScript.Shell.Run</c>, so the shell redirect it used to carry
/// (<c>h9kd &lt; NUL &gt;&gt; h9kd.log 2&gt;&amp;1</c>) was the only way that path had of giving
/// h9kd a log — and cmd.exe's append redirect holds the log with <c>FILE_SHARE_READ</c> only for
/// the whole run, which is what kept h9kd's own rotation-safe takeover and the 8 MB budget from
/// ever working on Windows (PLAN.md §16 PLACEHOLDER-d4e64dfa). Standing here instead, h9k can open that handle
/// itself with a share mode that refuses nobody and hand it over — see
/// <see cref="WindowsDaemonLaunch"/>.
/// </para>
/// <para>
/// It waits for h9kd and exits with the daemon's own exit code, because the chain above it has
/// to: Task Scheduler's <c>RestartOnFailure</c> restarts only on a nonzero action exit, and a
/// task that reports <c>Running</c> for exactly as long as its daemon runs is what
/// <c>WindowsDaemonAutostart.DisableAsync</c> reads to tell a daemon the task started from one
/// an operator started by hand.
/// </para>
/// </summary>
public sealed class DaemonAutostartLaunchCommand : Hall9kAsyncCommand<DaemonAutostartLaunchCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--binary <PATH>")]
        [Description("The h9kd binary to launch — the installed one the autostart registration was written against, never resolved afresh here, so this launch starts exactly what h9k daemon autostart enable recorded")]
        public string? Binary { get; init; }

        [CommandOption("--log <PATH>")]
        [Description("The log h9kd's stdout and stderr are appended to, opened here with FILE_APPEND_DATA and a share mode that admits readers, other writers, and a delete (default: ~/.hall9k/h9kd.log)")]
        public string? Log { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            await Console.Error.WriteLineAsync(
                "h9k daemon autostart launch is the Windows logon task's own vehicle — on this platform launchd "
                + "starts the daemon from the LaunchAgent directly, with no launcher in between. Use h9k daemon start.");
            return ExitCodes.Error;
        }

        if (settings.Binary is not { Length: > 0 } binary)
        {
            await Console.Error.WriteLineAsync(
                "h9k daemon autostart launch needs --binary: it starts exactly the h9kd the autostart "
                + "registration was written against rather than resolving one of its own. Re-run h9k daemon "
                + "autostart enable to rewrite the launch script this is invoked from.");
            return ExitCodes.Usage;
        }

        if (!File.Exists(binary))
        {
            await Console.Error.WriteLineAsync(
                $"h9kd is not at {binary} — the autostart registration points at a binary that has since moved "
                + "or been removed. Run h9k install, then h9k daemon autostart enable.");
            return ExitCodes.Error;
        }

        string logFile = settings.Log is { Length: > 0 } log ? log : DaemonRuntime.LogFile;
        Directory.CreateDirectory(RunPaths.Root);

        try
        {
            // The connection string is deliberately not passed down here the way DaemonLifecycle's
            // own start path passes the one it just probed: nothing in this chain probes Postgres,
            // and WindowsDaemonAutostart never embeds the secret in the launch script (its own doc
            // says why). h9kd resolves it from the platform config file instead, which is the
            // fallback h9k daemon autostart enable checks before it ever warns about the gap.
            return await WindowsDaemonLaunch.RunUntilExitAsync(
                binary, RunPaths.Root, logFile, [], cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or Win32Exception)
        {
            // The log open and CreateProcess itself, the two things that fail before h9kd exists.
            // Reported rather than left to reach a stack trace, and reported as a NONZERO exit on
            // purpose: this runs at logon, when an on-access scanner is at its busiest, and a
            // nonzero action exit is exactly what makes the logon task's own RestartOnFailure try
            // again a minute later. Nobody is watching this console, so the line is for the
            // operator who reads the task's last result afterwards.
            await Console.Error.WriteLineAsync(
                $"h9kd was not started: {exception.Message}. If that names {logFile}, something else was "
                + "holding the log with a share mode that excludes writers when the logon task fired.");
            return ExitCodes.Error;
        }
    }
}
