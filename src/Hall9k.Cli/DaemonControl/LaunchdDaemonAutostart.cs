using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Hall9k.Domain.Infrastructure.Storage;
using Spectre.Console;

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
///
/// The plist carries no ProcessType. An earlier version wrote Background, which launchd
/// applies to the job and everything it spawns: the gates and agents ran at the same low
/// scheduling priority as backupd and a host gate that took 13 minutes began to exceed
/// its 30-minute limit. Omitting the key gives launchd's standard band; Interactive was
/// considered and rejected, so it must not be added. <see cref="StartAsync"/> removes the
/// key from a plist an earlier version wrote.
/// </summary>
public sealed partial class LaunchdDaemonAutostart : IDaemonAutostart
{
    public const string Label = "com.hall9k.h9kd";

    private static string AgentsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");

    public static string PlistPath => Path.Combine(AgentsDirectory, $"{Label}.plist");

    public bool IsSupported => true;

    public string NotSupportedMessage => string.Empty;

    public string MechanismDescription => "launchd LaunchAgent";

    public string StopMechanismDescription => MechanismDescription;

    public bool IsEnabled => File.Exists(PlistPath);

    public async Task<bool> IsLoadedAsync(CancellationToken cancellationToken) =>
        (await JobStateAsync(cancellationToken)).IsLoaded;

    public async Task<IReadOnlyList<string>> EnableAsync(
        string daemonBinaryPath,
        IReadOnlyList<KeyValuePair<string, string>> environment,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(AgentsDirectory);

        // The rewrite below would hide a Background ProcessType from the next start's cure
        // while a loaded, idle job still holds it, so that job is booted out here instead.
        if (await HasBackgroundProcessTypeAsync(PlistPath, cancellationToken)
            && await ReleaseJobAsync(cancellationToken) == JobRelease.BootoutFailed)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Could not boot out the loaded launchd job, so it keeps its ProcessType Background classification until the next h9k daemon stop and start.[/]");
        }

        await File.WriteAllTextAsync(
            PlistPath, PlistContent(daemonBinaryPath, DaemonRuntime.LogFile, environment), cancellationToken);
        return [.. environment.Select(variable => variable.Key)];
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
        ProcessTypeCure cure = await CureBackgroundProcessTypeAsync(PlistPath, ReleaseJobAsync, cancellationToken);
        switch (cure.Outcome)
        {
            case ProcessTypeCureOutcome.Removed:
                AnsiConsole.MarkupLineInterpolated(
                    $"[dim]Removed ProcessType Background from {PlistPath}, so launchd no longer classifies the daemon as Background.[/]");
                break;
            case ProcessTypeCureOutcome.RemovedLeavingJobRunning:
                AnsiConsole.MarkupLineInterpolated(
                    $"[dim]Removed ProcessType Background from {PlistPath}, but a launchd job for it is running and keeps that classification until the next h9k daemon stop and start.[/]");
                break;
            case ProcessTypeCureOutcome.Skipped:
                AnsiConsole.MarkupLineInterpolated(
                    $"[dim]Skipped removing ProcessType Background from {PlistPath} ({cure.Detail}); starting anyway, so the daemon may keep running at background priority.[/]");
                break;
        }

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

    /// <summary>
    /// Removes a top-level ProcessType Background from the plist at <paramref name="plistPath"/>,
    /// leaving every other key and value as it was. A plist without that exact pair is not
    /// rewritten; one that cannot be read or rewritten is reported as skipped rather than thrown,
    /// because a start must not cost on a repair.
    /// </summary>
    /// <remarks>
    /// launchd keeps the definition it loaded, so a cured file only reaches a job that is already
    /// bootstrapped through a bootout. <paramref name="releaseJobAsync"/> does that before the file
    /// is touched, and a failed bootout leaves the file as it was: the file is what marks the job
    /// as still needing the cure, so every later start tries again instead of finding a clean
    /// file over a job that still holds Background.
    /// </remarks>
    internal static async Task<ProcessTypeCure> CureBackgroundProcessTypeAsync(
        string plistPath,
        Func<CancellationToken, Task<JobRelease>> releaseJobAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            string text = await File.ReadAllTextAsync(plistPath, cancellationToken);
            if (RemoveBackgroundProcessType(text) is not { } cured)
            {
                return new ProcessTypeCure(ProcessTypeCureOutcome.NotNeeded, string.Empty);
            }

            JobRelease release = await releaseJobAsync(cancellationToken);
            if (release == JobRelease.BootoutFailed)
            {
                return new ProcessTypeCure(
                    ProcessTypeCureOutcome.Skipped,
                    "launchctl bootout of the loaded job failed, so the file is left as it is for the next start to retry");
            }

            await ReplaceFileAsync(plistPath, cured, cancellationToken);
            return new ProcessTypeCure(
                release == JobRelease.LeftRunning
                    ? ProcessTypeCureOutcome.RemovedLeavingJobRunning
                    : ProcessTypeCureOutcome.Removed,
                string.Empty);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException)
        {
            return new ProcessTypeCure(ProcessTypeCureOutcome.Skipped, exception.Message);
        }
    }

    /// <summary>
    /// Makes the loaded job give up its definition when that is safe. A job with a live process
    /// is left alone: the bootout would kill that daemon, and the plist and label belong to the
    /// user account while the guard in DaemonLifecycle only sees the current home's pid file, so
    /// a scratch home can reach this with the real daemon running. A job that is loaded but idle
    /// has nothing to kill, and one that is not loaded has nothing to release.
    /// </summary>
    private async Task<JobRelease> ReleaseJobAsync(CancellationToken cancellationToken)
    {
        LaunchdJobState job = await JobStateAsync(cancellationToken);
        return ReleaseActionFor(job.IsLoaded, job.ProcessId) switch
        {
            JobReleaseAction.BootOut => await StopAsync(cancellationToken)
                ? JobRelease.Released
                : JobRelease.BootoutFailed,
            JobReleaseAction.LeaveRunning => JobRelease.LeftRunning,
            _ => JobRelease.Released,
        };
    }

    internal static JobReleaseAction ReleaseActionFor(bool isLoaded, int? processId) =>
        (isLoaded, processId) switch
        {
            (false, _) => JobReleaseAction.Nothing,
            (true, not null) => JobReleaseAction.LeaveRunning,
            (true, null) => JobReleaseAction.BootOut,
        };

    private static async Task<bool> HasBackgroundProcessTypeAsync(string plistPath, CancellationToken cancellationToken)
    {
        try
        {
            return RemoveBackgroundProcessType(await File.ReadAllTextAsync(plistPath, cancellationToken)) is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes <paramref name="content"/> over the file through a sibling temporary file and a
    /// rename, so a crash never leaves a half-written plist. The file can carry the database
    /// connection string, so the replacement is created with the original's permissions rather
    /// than the default ones, a symlinked plist is rewritten at its target, and the temporary
    /// file is removed whatever happens.
    /// </summary>
    private static async Task ReplaceFileAsync(string plistPath, string content, CancellationToken cancellationToken)
    {
        string target = new FileInfo(plistPath).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? plistPath;
        string temporaryPath = $"{target}.tmp";
        File.Delete(temporaryPath);
        try
        {
            FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            UnixFileMode? mode = null;
            if (!OperatingSystem.IsWindows())
            {
                mode = File.GetUnixFileMode(target);
                options.UnixCreateMode = mode;
            }

            await using (FileStream stream = new(temporaryPath, options))
            {
                await stream.WriteAsync(new UTF8Encoding(false).GetBytes(content), cancellationToken);
            }

            if (mode is { } originalMode && !OperatingSystem.IsWindows())
            {
                // The create mode is subject to the umask; this puts it back exactly.
                File.SetUnixFileMode(temporaryPath, originalMode);
            }

            File.Move(temporaryPath, target, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    /// <summary>
    /// The plist text without its top-level ProcessType Background pair, or null when it has
    /// none. The document is parsed so an environment variable that happens to be named
    /// ProcessType is never mistaken for the job's own key, and the pair is then cut out of the
    /// original text by position, so every other byte stays exactly as it was.
    /// </summary>
    internal static string? RemoveBackgroundProcessType(string plistText)
    {
        XDocument document = XDocument.Parse(plistText, LoadOptions.SetLineInfo);
        XElement? key = document.Root?.Element("dict")?.Elements("key").FirstOrDefault(candidate => candidate.Value == "ProcessType");
        if (key?.ElementsAfterSelf().FirstOrDefault() is not { Name.LocalName: "string" } value
            || value.Value.Trim() != "Background")
        {
            return null;
        }

        int keyStart = OffsetOf(plistText, key);
        int valueOpen = OffsetOf(plistText, value);
        int valueClose = valueOpen < 0
            ? -1
            : plistText.IndexOf("</string>", valueOpen, StringComparison.Ordinal);
        int valueEnd = valueClose + "</string>".Length;

        // Cutting at a guessed position would corrupt the file, so unless the text found at
        // those offsets is exactly the pair that was parsed, decline to touch it.
        if (keyStart < 0 || valueClose < 0 || !ProcessTypePair().IsMatch(plistText.AsSpan(keyStart, valueEnd - keyStart)))
        {
            throw new XmlException("the ProcessType entry could not be located in the file text");
        }

        // The indentation before the key goes with it, so no blank line is left behind.
        int removeFrom = keyStart;
        while (removeFrom > 0 && char.IsWhiteSpace(plistText[removeFrom - 1]))
        {
            removeFrom--;
        }

        return plistText.Remove(removeFrom, valueEnd - removeFrom);
    }

    [GeneratedRegex(@"^<key>\s*ProcessType\s*</key>\s*<string>\s*Background\s*</string>$")]
    private static partial Regex ProcessTypePair();

    /// <summary>
    /// The index of the '&lt;' that opens the element, from the line and column the parser
    /// recorded, or -1 when the text has fewer lines than that. The parser ends a line at CRLF,
    /// LF, or a bare CR, so this counts them the same way.
    /// </summary>
    private static int OffsetOf(string text, XElement element)
    {
        IXmlLineInfo position = element;
        int lineStart = 0;
        for (int line = 1; line < position.LineNumber; line++)
        {
            int lineBreak = text.AsSpan(lineStart).IndexOfAny('\r', '\n');
            if (lineBreak < 0)
            {
                return -1;
            }

            lineBreak += lineStart;
            lineStart = lineBreak + 1;
            if (text[lineBreak] == '\r' && lineStart < text.Length && text[lineStart] == '\n')
            {
                lineStart++;
            }
        }

        int offset = lineStart + position.LinePosition - 2;
        return offset >= 0 && offset < text.Length ? offset : -1;
    }

    internal enum ProcessTypeCureOutcome
    {
        NotNeeded,
        Removed,
        RemovedLeavingJobRunning,
        Skipped,
    }

    internal enum JobReleaseAction
    {
        Nothing,
        LeaveRunning,
        BootOut,
    }

    internal enum JobRelease
    {
        Released,
        LeftRunning,
        BootoutFailed,
    }

    internal sealed record ProcessTypeCure(ProcessTypeCureOutcome Outcome, string Detail);

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
