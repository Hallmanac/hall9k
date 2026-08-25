using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Cli.DaemonControl;

/// <summary>
/// Windows start-at-login via a Task Scheduler logon task — never a Windows service
/// (Decisions Log #3): a service runs as a different identity by default, the same
/// credential problem PLAN.md §6.1 ruled out for the daemon everywhere. A logon trigger
/// with an interactive-token principal runs h9kd as the signed-in user instead, seeing
/// the same Claude Code, git, and gh credentials an on-demand <c>h9k daemon start</c>
/// would.
/// <para>
/// The environment a service-manager-started daemon needs (<see cref="DaemonEnvironment"/>'s
/// problem for launchd) has no Task Scheduler equivalent of launchd's per-job
/// EnvironmentVariables dict, and the tempting shortcut — writing the captured PATH into
/// the user's persistent registry environment — would be a global, hard-to-reverse
/// mutation for what should be a per-job setting (and would balloon on every re-enable,
/// since the next capture already includes what the last enable wrote). Instead the
/// captured variables are set inside the SAME cmd.exe invocation that then runs h9kd, with
/// <c>set NAME=VALUE&amp;</c> prefixes scoped to that one process tree only — Windows's
/// answer to launchd's per-job env dict, touching nothing outside this task.
/// </para>
/// <para>
/// "Stopped means stopped" (Decisions Log #31) needs no explicit unload step here the way
/// launchd's KeepAlive does: <c>RestartOnFailure</c> below only restarts on a NONZERO exit,
/// and h9kd's graceful shutdown (<see cref="WindowsStopRequestWatcher"/> in the daemon,
/// triggered by <see cref="DaemonLifecycle"/>'s stop-request file) already exits 0 —
/// indistinguishable from the task simply not having anything left to do. So
/// <see cref="StopAsync"/> just performs the same graceful request
/// <see cref="DaemonLifecycle"/>'s own direct-signal fallback does, and there is nothing
/// forceful for it to also reach for.
/// </para>
/// </summary>
public sealed class WindowsDaemonAutostart : IDaemonAutostart
{
    /// <summary>A Task Scheduler path, not a bare name — namespaced under its own folder the
    /// way launchd's reverse-DNS label namespaces the LaunchAgent.</summary>
    public const string TaskName = @"\Hall9k\h9kd";

    /// <summary>The folder half of <see cref="TaskName"/> — schtasks.exe's own <c>/TN</c>
    /// takes the combined path, but <c>Get-ScheduledTask</c>'s <c>-TaskName</c> matches only
    /// the leaf (<c>MSFT_ScheduledTask.TaskName</c> holds the leaf alone; the folder is the
    /// separate <c>-TaskPath</c> parameter), so the query needs the two halves apart.</summary>
    private const string TaskFolder = @"\Hall9k\";

    /// <summary>The leaf half of <see cref="TaskName"/> — see <see cref="TaskFolder"/>.</summary>
    private const string TaskLeafName = "h9kd";

    /// <summary>
    /// The persistent VBScript launcher the task action invokes through <c>wscript.exe</c>
    /// (see <see cref="TaskXmlContent"/> and <see cref="LaunchScriptContent"/> for why a
    /// script host stands between Task Scheduler and cmd.exe). Written by
    /// <see cref="EnableAsync"/> before the task is registered, overwritten on every
    /// re-enable, and best-effort deleted by <see cref="DisableAsync"/> — its lifecycle
    /// mirrors the task's own rather than needing separate uninstall bookkeeping.
    /// </summary>
    private static string LaunchScriptFile => Path.Combine(RunPaths.Root, "h9kd-autostart-launch.vbs");

    public bool IsSupported => true;

    public string NotSupportedMessage => string.Empty;

    public string MechanismDescription => "Task Scheduler logon task";

    public bool IsEnabled => QueryExists();

    public async Task<bool> IsLoadedAsync(CancellationToken cancellationToken)
    {
        // Get-ScheduledTask's State is a TaskState enum, not a display string —
        // .ToString() gives the member name ("Running") on every Windows UI language,
        // where schtasks's own /FO LIST output renders the localized "Status:"/"Running"
        // text (e.g. "État :"/"En cours d'exécution" on French Windows) that a literal
        // English match would silently read as never running everywhere but English.
        ExecResult result = await Exec.RunAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", StateQueryCommand()],
            cancellationToken);
        return result.Succeeded && ParseIsRunning(result.StandardOutput);
    }

    /// <summary>
    /// The <c>Get-ScheduledTask</c> invocation <see cref="IsLoadedAsync"/> runs. Internal for
    /// direct unit coverage the same way <see cref="ParseIsRunning"/> is — passing
    /// <see cref="TaskName"/>'s combined <c>\Hall9k\h9kd</c> path to <c>-TaskName</c> alone
    /// never matches (that parameter takes the leaf name only), which silently always
    /// answered "not loaded"; the folder and leaf go to <c>-TaskPath</c> and <c>-TaskName</c>
    /// separately.
    /// </summary>
    internal static string StateQueryCommand() =>
        $"(Get-ScheduledTask -TaskPath '{TaskFolder}' -TaskName '{TaskLeafName}').State.ToString()";

    public async Task<IReadOnlyList<string>> EnableAsync(
        string daemonBinaryPath,
        IReadOnlyList<KeyValuePair<string, string>> environment,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Written before the task is registered, so the very first logon that fires the
        // trigger already finds a script in place — overwritten on every re-enable the same
        // way the task registration itself is (schtasks /Create /F).
        // Encoding.Unicode (UTF-16LE with a BOM), not UTF-8: WSH has no UTF-8 auto-detection —
        // an unmarked file is read against the system ANSI codepage, and a BOM is exactly what
        // tells wscript.exe to read UTF-16 instead. Without it, a non-ASCII profile path (an
        // accented Windows username) would decode as the wrong codepage and corrupt the command
        // line silently. The task XML two lines below already makes this same choice.
        await File.WriteAllTextAsync(
            LaunchScriptFile,
            LaunchScriptContent(daemonBinaryPath, DaemonRuntime.LogFile, environment),
            Encoding.Unicode,
            cancellationToken);

        string xmlPath = Path.Combine(Path.GetTempPath(), $"hall9k-h9kd-task-{Path.GetRandomFileName()}.xml");
        await File.WriteAllTextAsync(xmlPath, TaskXmlContent(), Encoding.Unicode, cancellationToken);
        try
        {
            ExecResult result = await Exec.RunAsync(
                "schtasks.exe", ["/Create", "/XML", xmlPath, "/TN", TaskName, "/F"], cancellationToken);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"schtasks /Create failed (exit {result.ExitCode}): {result.StandardError}");
            }
        }
        finally
        {
            // Best-effort, same discipline as AtomicFileWrite's own temp-file cleanup:
            // nothing reads this file again once schtasks has registered the task from it
            // (the launch script, not this XML, carries the command line — see InnerCommand's
            // own doc on why the connection string is not here either), so a delete failure
            // (antivirus, an indexer still holding it open) must not shadow a registration
            // that actually succeeded by reporting the command as failed.
            try
            {
                File.Delete(xmlPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return RecordedVariableNames(environment);
    }

    /// <summary>
    /// The names <see cref="EnableAsync"/> actually carries into the registration —
    /// everything captured except <see cref="Hall9kDatabase.EnvironmentVariableName"/> (see
    /// <see cref="InnerCommand"/>'s own doc for why). Internal for direct unit coverage,
    /// the same way <see cref="EscapeForCmdExe"/> is: a caller reporting what was recorded
    /// must ask this rather than assume it is everything it captured.
    /// </summary>
    internal static IReadOnlyList<string> RecordedVariableNames(
        IReadOnlyList<KeyValuePair<string, string>> environment) =>
        [.. environment
            .Select(variable => variable.Key)
            .Where(name => name != Hall9kDatabase.EnvironmentVariableName)];

    public async Task<DaemonAutostartDisableOutcome> DisableAsync(CancellationToken cancellationToken)
    {
        // Mirrors LaunchdDaemonAutostart.DisableAsync's own "had to be stopped is a claim
        // about a process" discipline: read what is actually running before unregistering,
        // rather than guess from whether the task happens to be present. launchd answers
        // "did the JOB start this" with the pid it itself reports for the job; Task
        // Scheduler has no pid-per-job query, so IsLoadedAsync's Status: Running is the
        // Windows equivalent — a daemon the operator started with h9k daemon start leaves
        // the task Ready, never Running, so a bare DaemonProcess.Probe() here would stop
        // (and claim ownership of) a daemon this task never started.
        DaemonProcessDescriptor? running = await IsLoadedAsync(cancellationToken)
            ? DaemonProcess.Probe()
            : null;

        ExecResult result = await Exec.RunAsync("schtasks.exe", ["/Delete", "/TN", TaskName, "/F"], cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"schtasks /Delete failed (exit {result.ExitCode}): {result.StandardError}");
        }

        // Best-effort, same discipline as EnableAsync's temp XML cleanup: nothing will ever
        // invoke this script again once the task is gone, so a delete failure here (the file
        // open in a stuck wscript, an indexer) leaves a harmless stale copy rather than a
        // reason to report a disable that otherwise fully succeeded as failed.
        try
        {
            File.Delete(LaunchScriptFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        if (running is null)
        {
            return DaemonAutostartDisableOutcome.NothingStopped;
        }

        await RequestStopAsync(running, cancellationToken);
        return await WaitForExitAsync(running.ProcessId, running.StartedAt, cancellationToken)
            ? DaemonAutostartDisableOutcome.DaemonStopped
            : DaemonAutostartDisableOutcome.DaemonStopping;
    }

    public async Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        ExecResult result = await Exec.RunAsync("schtasks.exe", ["/Run", "/TN", TaskName], cancellationToken);
        return result.Succeeded;
    }

    public async Task<bool> StopAsync(CancellationToken cancellationToken)
    {
        DaemonProcessDescriptor? running = DaemonProcess.Probe();
        if (running is null)
        {
            return false;
        }

        await RequestStopAsync(running, cancellationToken);
        return true;
    }

    // Pid plus start time, never a bare pid (Decisions Log #2) — see the matching doc
    // comment on DaemonLifecycle.RequestGracefulStopAsync for the trap a bare pid opens.
    private static Task RequestStopAsync(DaemonProcessDescriptor running, CancellationToken cancellationToken) =>
        DaemonPidFile.WriteAsync(DaemonRuntime.StopRequestFile, running, cancellationToken);

    // How long DisableAsync watches a signalled daemon before reporting it as still
    // shutting down — matches LaunchdDaemonAutostart's own ExitObservationWindow: short on
    // purpose, since the daemon's full graceful-shutdown budget is 30s and unregistering
    // should not block for it.
    private static readonly TimeSpan ExitObservationWindow = TimeSpan.FromSeconds(10);

    private static async Task<bool> WaitForExitAsync(int processId, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + ExitObservationWindow;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!DaemonProcess.IsAlive(processId, startedAt))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        return !DaemonProcess.IsAlive(processId, startedAt);
    }

    private static bool QueryExists()
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        process.StartInfo.ArgumentList.Add("/Query");
        process.StartInfo.ArgumentList.Add("/TN");
        process.StartInfo.ArgumentList.Add(TaskName);

        try
        {
            if (!process.Start())
            {
                return false;
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return false;
        }

        process.WaitForExit();
        return process.ExitCode == 0;
    }

    /// <summary>
    /// Parses <c>Get-ScheduledTask</c>'s <c>State</c> enum output. Internal for direct unit
    /// coverage against captured output, the same way <see cref="LaunchdDaemonAutostart.ParseProcessId"/>
    /// is tested against real launchctl output rather than requiring a live task. An enum
    /// member name is never localized, unlike the schtasks display text this used to match.
    /// </summary>
    internal static bool ParseIsRunning(string queryOutput) =>
        queryOutput.Trim().Equals("Running", StringComparison.Ordinal);

    /// <summary>
    /// The task definition XML <c>schtasks /Create /XML</c> registers. A LogonTrigger with
    /// an InteractiveToken principal (Decisions Log #3: the daemon runs as the signed-in
    /// user, never as a service identity). The trigger names the enabling user explicitly
    /// via <c>UserId</c> — <c>Get-ScheduledTask</c>'s own documentation is blunt that
    /// omitting it means "fire at any user's logon", not "fire at the registering user's",
    /// and the macOS equivalent (a per-user LaunchAgent) has no such any-user mode to
    /// accidentally match. Left unset, a second local account logging on would fire the
    /// trigger, Task Scheduler would try to run the action under that account's own
    /// interactive token per the Principal above, and — since the launch script and
    /// captured PATH belong to the enabling user, not this one — the run would fail
    /// repeatedly and burn <c>RestartOnFailure</c>'s budget without ever starting the
    /// enabling user's own daemon. RestartOnFailure mirrors launchd's KeepAlive
    /// SuccessfulExit=false (restart only after a crash, never after h9kd's own clean
    /// exit); ExecutionTimeLimit is set to PT0S (unlimited) because Task Scheduler's
    /// default of 72 hours would otherwise kill a daemon meant to run indefinitely.
    /// <para>
    /// The action is <c>wscript.exe</c> running <see cref="LaunchScriptFile"/>, never cmd.exe
    /// directly: there is no Task Scheduler setting that suppresses a console window for an
    /// action process (<c>&lt;Hidden&gt;</c> hides the task from the Task Scheduler UI, not
    /// the window a console-subsystem action creates), and an InteractiveToken principal runs
    /// the action on the signed-in user's own visible desktop precisely so h9kd inherits their
    /// Claude Code/git/gh credentials, the same reason
    /// <see cref="DaemonLifecycle.SpawnDetachedWindows"/> gives up passing
    /// <c>CREATE_NO_WINDOW</c> the way it can for a CLI-launched daemon. cmd.exe run this way
    /// would sit on the desktop as a visible window for the daemon's entire life, and closing
    /// it — the obvious reaction — delivers CTRL_CLOSE_EVENT, cutting the 30s graceful-shutdown
    /// budget down to Windows's ~5s console-close grace period. <c>wscript.exe</c> is a
    /// Windows-subsystem host (unlike its console-subsystem sibling <c>cscript.exe</c>): it
    /// never allocates a console for itself, and <see cref="LaunchScriptContent"/>'s
    /// <c>WScript.Shell.Run(..., 0, True)</c> call starts cmd.exe with an explicit hidden
    /// window style, so nothing on the desktop appears at any point in the chain.
    /// <c>//B</c> (batch mode) additionally suppresses script-error dialogs, so a malformed
    /// script fails silently into the log rather than popping a message box with nothing to
    /// dismiss it.
    /// </para>
    /// </summary>
    internal static string TaskXmlContent()
    {
        string arguments = SecurityElement.Escape($"//B \"{LaunchScriptFile}\"");
        string userId = SecurityElement.Escape(CurrentUserId());

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Hall9k daemon (h9kd) — starts at logon (Decisions Log #3)</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{userId}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RestartOnFailure>
                  <Interval>PT1M</Interval>
                  <Count>3</Count>
                </RestartOnFailure>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>%WINDIR%\System32\wscript.exe</Command>
                  <Arguments>{arguments}</Arguments>
                </Exec>
              </Actions>
            </Task>

            """.ReplaceLineEndings("\n");
    }

    /// <summary>
    /// The identity <see cref="TaskXmlContent"/>'s LogonTrigger names via <c>UserId</c> —
    /// <c>DOMAIN\User</c> (or <c>MachineName\User</c> for a local account), the same form
    /// Task Scheduler accepts and displays for a trigger scoped to one user rather than
    /// every user's logon. <see cref="Environment.UserDomainName"/>/<see cref="Environment.UserName"/>
    /// rather than <c>WindowsIdentity.GetCurrent()</c>: both resolve to this same identity on
    /// Windows, but the former runs on every platform without throwing, which several of
    /// this file's own tests (<c>WindowsDaemonAutostartTests</c>) depend on by calling
    /// <see cref="TaskXmlContent"/> unconditionally on every CI leg, not just Windows's.
    /// </summary>
    private static string CurrentUserId() => $@"{Environment.UserDomainName}\{Environment.UserName}";

    /// <summary>
    /// The VBScript <see cref="EnableAsync"/> writes to <see cref="LaunchScriptFile"/> and the
    /// task action (<see cref="TaskXmlContent"/>) runs through <c>wscript.exe</c> at every
    /// logon. Its one statement hides the window <c>cmd.exe</c> would otherwise show
    /// (<c>0</c> is <c>SW_HIDE</c>) and waits for it to exit (<c>True</c>), so the task
    /// instance's own lifetime still tracks the daemon's, exactly as it did when cmd.exe was
    /// the action directly. <c>WScript.Shell.Run</c> returns the exited process's own exit
    /// code when called with <c>True</c>, but only when that return value is actually used —
    /// called as a bare statement, wscript.exe still exits 0 regardless of what cmd.exe (and
    /// h9kd inside it) actually returned. <c>WScript.Quit</c> around the call is what carries
    /// that code out to wscript.exe's own exit code, which is what Task Scheduler's action
    /// result — and so <see cref="RestartOnFailure"/> above, which restarts only on a nonzero
    /// exit — actually observes.
    /// </summary>
    internal static string LaunchScriptContent(
        string daemonBinaryPath, string logFilePath, IReadOnlyList<KeyValuePair<string, string>> environment)
    {
        string commandLine = "cmd.exe " + WindowsCommandLine.WrapForCmdExe(InnerCommand(daemonBinaryPath, logFilePath, environment));
        return $"WScript.Quit CreateObject(\"WScript.Shell\").Run({VbScriptStringLiteral(commandLine)}, 0, True)\n";
    }

    /// <summary>
    /// Escapes <paramref name="value"/> as a VBScript double-quoted string literal for
    /// <see cref="LaunchScriptContent"/> — VBScript has no backslash-escape syntax, so the
    /// only character its string literals treat specially is the quote itself, doubled.
    /// </summary>
    private static string VbScriptStringLiteral(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// The full <c>cmd.exe /c "..."</c> command line the launch script runs: every captured
    /// variable set ahead of h9kd, scoped to this one process tree (see the type-level doc
    /// on why this is the Windows answer to launchd's per-job EnvironmentVariables dict),
    /// then h9kd itself with stdin from NUL and stdout/stderr appended to the log — wrapped
    /// for cmd.exe's own quote handling by <see cref="WindowsCommandLine"/>, the same as
    /// every other cmd.exe invocation on this platform that carries embedded quotes. cmd.exe
    /// parses its own <c>/c</c> argument with this same quirky fallback rule regardless of
    /// what started it, so wrapping this exact string in a VBScript literal for
    /// <c>WScript.Shell.Run</c> (which hands it to <c>CreateProcess</c> unmodified, the same
    /// as <see cref="System.Diagnostics.ProcessStartInfo.Arguments"/> did before) needs no
    /// change to the wrapping itself.
    /// <para>
    /// <see cref="Hall9kDatabase.EnvironmentVariableName"/> is deliberately left out of the
    /// captured set here even when <see cref="EnableAsync"/> is handed it: unlike PATH or
    /// <c>HALL9K_CLAUDE_PATH</c>, the connection string already has a durable home once an
    /// install reaches this point — <see cref="Hall9kDatabase.Resolve"/> falls back to
    /// <see cref="Hall9kDatabase.ConfigFile"/> (written by <c>h9k doctor</c>'s start-offer,
    /// which the documented install walk runs before autostart is ever enabled) whenever the
    /// environment does not carry it. Embedding it anyway would only add a second, weaker copy
    /// of the same secret in plaintext on disk — this file has no equivalent of the config
    /// file's own permissions or its inclusion in every other secret-handling path.
    /// </para>
    /// </summary>
    private static string InnerCommand(
        string daemonBinaryPath, string logFilePath, IReadOnlyList<KeyValuePair<string, string>> environment)
    {
        IReadOnlyList<string> recordedNames = RecordedVariableNames(environment);
        StringBuilder inner = new();
        foreach ((string name, string value) in environment)
        {
            if (!recordedNames.Contains(name))
            {
                continue;
            }

            inner.Append("set ").Append(EscapeForCmdExe(name)).Append('=').Append(EscapeForCmdExe(value)).Append("& ");
        }

        // Tells h9kd this is the cmd.exe `>>` redirect WindowsAppendOnlyLog exists to
        // survive a rotation of — never one of the operator's own captured variables, so
        // it is set unconditionally here rather than threaded through RecordedVariableNames.
        inner.Append("set ").Append(DaemonRuntime.AppendOnlyLogEnvironmentVariable).Append("=1& ");

        inner.Append('"').Append(daemonBinaryPath).Append('"')
            .Append(" < NUL >> \"").Append(logFilePath).Append("\" 2>&1");
        return inner.ToString();
    }

    /// <summary>
    /// Escapes a captured environment name or value for the unquoted <c>set NAME=VALUE&amp;</c>
    /// position in <see cref="InnerCommand"/> — this text sits outside the quoted path
    /// segments, so cmd.exe parses it as real command syntax rather than as data. Without
    /// this, a connection string password containing <c>&amp;</c> truncates the variable
    /// and runs the remainder of its own value as a command. <c>^</c> escapes the other
    /// cmd.exe metacharacters when they appear outside quotes.
    /// <para>
    /// <c>%</c> is left alone rather than doubled: doubling to <c>%%</c> is a batch-FILE
    /// rule (the same one behind <c>for %%i</c> only working inside a .bat/.cmd file), not
    /// a rule of the <c>cmd.exe /c "..."</c> command line this text lands on, where <c>%%</c>
    /// stays two literal percent signs rather than collapsing to one. There is no escape
    /// that reliably produces a literal <c>%</c> on a command line, so a lone <c>%</c> is
    /// the honest choice: it is left as written unless it happens to pair with another
    /// <c>%</c> later in the same value to look like a <c>%VARNAME%</c> reference.
    /// </para>
    /// </summary>
    internal static string EscapeForCmdExe(string value)
    {
        StringBuilder escaped = new(value.Length);
        foreach (char character in value)
        {
            if ("^&|<>()\"".Contains(character))
            {
                escaped.Append('^').Append(character);
            }
            else
            {
                escaped.Append(character);
            }
        }

        return escaped.ToString();
    }
}
