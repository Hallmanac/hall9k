using System.Security;
using System.Text;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Cli.DaemonControl;

/// <summary>
/// macOS start-at-login via a per-user launchd LaunchAgent. RunAtLoad starts the daemon
/// at login; KeepAlive with SuccessfulExit=false restarts it only after a crash
/// (nonzero exit) — a graceful stop exits 0 and stays stopped. h9k daemon stop goes
/// through launchctl bootout while the job is loaded, so launchd never resurrects a
/// daemon the human just killed (Decisions Log #31).
///
/// AbandonProcessGroup is what keeps that stop from taking the agents with it. The
/// daemon spawns detached claude agents without a setsid, so they share h9kd's process
/// group; launchd, left to its default, signals the whole group when it tears the job
/// down, and bootout would SIGTERM every agent running under it. Abandoning the group
/// confines the teardown to h9kd itself, which is what stop already promises the
/// operator ("Detached agents keep running by design — the next start adopts them").
/// The non-autostart path never had this problem: it signals the recorded pid alone.
/// </summary>
public sealed class LaunchdDaemonAutostart : IDaemonAutostart
{
    public const string Label = "com.hall9k.h9kd";

    public static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", $"{Label}.plist");

    public bool IsSupported => true;

    public string NotSupportedMessage => string.Empty;

    public string MechanismDescription => "launchd LaunchAgent";

    public bool IsEnabled => File.Exists(PlistPath);

    public async Task<bool> IsLoadedAsync(CancellationToken cancellationToken) =>
        (await JobStateAsync(cancellationToken)).IsLoaded;

    public async Task EnableAsync(
        string daemonBinaryPath,
        IReadOnlyList<KeyValuePair<string, string>> environment,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
        await File.WriteAllTextAsync(
            PlistPath, PlistContent(daemonBinaryPath, DaemonRuntime.LogFile, environment), cancellationToken);
    }

    public async Task<DaemonAutostartDisableOutcome> DisableAsync(CancellationToken cancellationToken)
    {
        // "Had to be stopped" is a claim about a process, so it is answered by one: the
        // pid launchd reports for the job before the bootout, seen gone after it. A
        // loaded job proves only that the label is bootstrapped — bootout succeeds just
        // as happily on a loaded but idle one (the single-instance loser exits 0 and
        // KeepAlive deliberately leaves it stopped) — so keying the message off "loaded"
        // told the operator a daemon was killed that was never alive.
        LaunchdJobState job = await JobStateAsync(cancellationToken);
        if (job.IsLoaded)
        {
            await StopAsync(cancellationToken);
        }

        File.Delete(PlistPath);
        if (job.ProcessId is not { } processId)
        {
            return DaemonAutostartDisableOutcome.NothingStopped;
        }

        return await WaitForExitAsync(processId, cancellationToken)
            ? DaemonAutostartDisableOutcome.DaemonStopped
            : DaemonAutostartDisableOutcome.DaemonStopping;
    }

    public async Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        string uid = await UserIdAsync(cancellationToken);
        ExecResult bootstrap = await Exec.RunAsync(
            "/bin/launchctl", ["bootstrap", $"gui/{uid}", PlistPath], cancellationToken);
        if (bootstrap.Succeeded)
        {
            return true;
        }

        // Already bootstrapped (e.g. a clean stop left the job loaded but idle):
        // kickstart runs the loaded job instead.
        ExecResult kickstart = await Exec.RunAsync(
            "/bin/launchctl", ["kickstart", $"gui/{uid}/{Label}"], cancellationToken);
        return kickstart.Succeeded;
    }

    public async Task<bool> StopAsync(CancellationToken cancellationToken)
    {
        ExecResult result = await Exec.RunAsync(
            "/bin/launchctl", ["bootout", $"gui/{await UserIdAsync(cancellationToken)}/{Label}"], cancellationToken);
        return result.Succeeded;
    }

    // launchd's default ExitTimeOut SIGKILLs 20s after bootout's SIGTERM — inside the
    // daemon's 30s graceful-shutdown budget (HostOptions.ShutdownTimeout), which exists
    // so in-flight event appends finish. 45s matches the CLI stop's own patience.
    private const int ExitTimeOutSeconds = 45;

    // How long disable watches a booted-out daemon before reporting it as still
    // shutting down. Short on purpose: the daemon's full budget is 30s, and unregistering
    // should not block for it — an honest "still shutting down" beats a silent wait.
    private static readonly TimeSpan ExitObservationWindow = TimeSpan.FromSeconds(10);

    internal static string PlistContent(
        string daemonBinaryPath, string logFilePath, IReadOnlyList<KeyValuePair<string, string>> environment)
    {
        string binary = SecurityElement.Escape(daemonBinaryPath);
        string log = SecurityElement.Escape(logFilePath);

        // A raw string literal keeps whatever line endings its source file was checked
        // out with, so a Windows checkout would hand launchd a plist whose literal part
        // is CRLF and whose environment block (built with explicit \n below) is LF. The
        // file this writes is a macOS one either way, so it is normalized to LF here
        // rather than left to depend on where h9k happened to be compiled.
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{Label}</string>
                <key>ProgramArguments</key>
                <array>
                    <string>{binary}</string>
                </array>
                <key>RunAtLoad</key>
                <true/>
                <key>KeepAlive</key>
                <dict>
                    <key>SuccessfulExit</key>
                    <false/>
                </dict>
                <key>ExitTimeOut</key>
                <integer>{ExitTimeOutSeconds}</integer>
                <key>AbandonProcessGroup</key>
                <true/>
            {EnvironmentBlock(environment)}    <key>StandardOutPath</key>
                <string>{log}</string>
                <key>StandardErrorPath</key>
                <string>{log}</string>
                <key>ProcessType</key>
                <string>Background</string>
            </dict>
            </plist>

            """.ReplaceLineEndings("\n");
    }

    /// <summary>
    /// The recorded environment, or nothing at all when none was observed — launchd
    /// then falls back to its own minimal environment, which is at least not a
    /// fabricated one (see <see cref="DaemonEnvironment"/> for why this block exists).
    /// </summary>
    private static string EnvironmentBlock(IReadOnlyList<KeyValuePair<string, string>> environment)
    {
        if (environment.Count == 0)
        {
            return string.Empty;
        }

        StringBuilder block = new();
        block.Append("    <key>EnvironmentVariables</key>\n    <dict>\n");
        foreach ((string name, string value) in environment)
        {
            block.Append($"        <key>{SecurityElement.Escape(name)}</key>\n");
            block.Append($"        <string>{SecurityElement.Escape(value)}</string>\n");
        }

        return block.Append("    </dict>\n").ToString();
    }

    /// <summary>
    /// The pid launchd runs for the job, parsed from launchctl print. Absent when the
    /// job is loaded but idle — which is exactly the case a bare "is it loaded?" cannot
    /// tell apart from a live one.
    /// </summary>
    internal static int? ParseProcessId(string launchctlPrintOutput)
    {
        const string marker = "pid = ";
        foreach (string line in launchctlPrintOutput.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith(marker, StringComparison.Ordinal)
                && int.TryParse(trimmed[marker.Length..].Trim(), out int processId))
            {
                return processId;
            }
        }

        return null;
    }

    private async Task<LaunchdJobState> JobStateAsync(CancellationToken cancellationToken)
    {
        ExecResult result = await Exec.RunAsync(
            "/bin/launchctl", ["print", $"gui/{await UserIdAsync(cancellationToken)}/{Label}"], cancellationToken);
        return result.Succeeded
            ? new LaunchdJobState(true, ParseProcessId(result.StandardOutput))
            : new LaunchdJobState(false, null);
    }

    private static async Task<bool> WaitForExitAsync(int processId, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + ExitObservationWindow;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!DaemonProcess.Exists(processId))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        return !DaemonProcess.Exists(processId);
    }

    private static async Task<string> UserIdAsync(CancellationToken cancellationToken)
    {
        ExecResult result = await Exec.RunAsync("/usr/bin/id", ["-u"], cancellationToken);
        return result.StandardOutput.Trim();
    }

    private sealed record LaunchdJobState(bool IsLoaded, int? ProcessId);
}
