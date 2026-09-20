using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Hall9k.Cli.DaemonControl;
using Xunit;

namespace Hall9k.Tests.Cli;

public sealed class WindowsDaemonAutostartTests
{
    private const string Launcher = @"C:\Users\someone\.hall9k\bin\h9k.exe";
    private const string Binary = @"C:\Users\someone\.hall9k\bin\h9kd.exe";
    private const string Log = @"C:\Users\someone\.hall9k\h9kd.log";

    private static readonly KeyValuePair<string, string>[] Environment =
    [
        new("PATH", @"C:\tools;C:\Windows\System32"),
        new("HALL9K_CLAUDE_PATH", @"C:\Users\someone\AppData\Local\claude.exe"),
    ];

    [Fact]
    public void The_task_carries_a_logon_trigger_running_as_the_signed_in_user()
    {
        string xml = WindowsDaemonAutostart.TaskXmlContent();

        xml.Should().Contain("<LogonTrigger>");
        // Decisions Log #3: never a service identity — a Windows service would run as a
        // different account by default, the same credential problem the daemon exists to
        // avoid everywhere else.
        xml.Should().Contain("<LogonType>InteractiveToken</LogonType>");
    }

    [Fact]
    public void The_logon_trigger_names_the_enabling_user_rather_than_firing_at_any_logon()
    {
        // Get-ScheduledTask's own documentation is explicit that an omitted UserId on a
        // LogonTrigger means "fire at any user's logon" — a second local account signing
        // in would then fire the trigger, Task Scheduler would try to run the action under
        // an interactive token that account does not have (the Principal above has no
        // UserId of its own either, so it resolves to whoever registers the task), and
        // RestartOnFailure would burn its budget on a run that could never succeed while
        // never starting the enabling user's own daemon.
        string xml = WindowsDaemonAutostart.TaskXmlContent();

        xml.Should().Contain($@"{System.Environment.UserDomainName}\{System.Environment.UserName}");
        int logonTriggerEnd = xml.IndexOf("</LogonTrigger>", StringComparison.Ordinal);
        xml[..logonTriggerEnd].Should().Contain("<UserId>", "the trigger must name a user, not fire at every logon");
    }

    [Fact]
    public void The_task_only_restarts_on_a_nonzero_exit()
    {
        string xml = WindowsDaemonAutostart.TaskXmlContent();

        // This only asserts the task definition's own shape (RestartOnFailure's presence and
        // count) — it says nothing about whether wscript.exe's exit code, the thing
        // RestartOnFailure actually evaluates, carries h9kd's real exit code up through the
        // launch script at all. That is what
        // The_launch_script_propagates_the_real_process_exit_code_through_wscript proves for
        // real, since no XML assertion can observe it.
        xml.Should().Contain("<RestartOnFailure>");
        xml.Should().Contain("<Count>3</Count>");
    }

    [Fact]
    public void The_launch_script_propagates_the_real_process_exit_code_through_wscript()
    {
        // Runs for real only on the Windows CI leg (the DaemonEnvironmentTests convention for
        // assertions no other platform can make: cscript.exe does not exist off Windows).
        // WScript.Shell.Run's return value only reaches wscript.exe's own exit code — the
        // thing Task Scheduler's RestartOnFailure actually observes — when a caller does
        // something with it; called as a bare statement (the pre-fix shape), wscript.exe
        // always exits 0 regardless of what cmd.exe (and h9kd inside it) returned, which
        // would make RestartOnFailure silently never fire. Proving that requires actually
        // running the generated script through cscript.exe against a real process exit code,
        // not reading the script's text.
        //
        // The launcher is stubbed rather than real: this covers the wscript-and-cmd half of the
        // chain, and WindowsDaemonLaunchTests covers the other half — that h9k daemon autostart
        // launch answers with h9kd's own exit code rather than its own.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunLaunchScript(7).ExitCode.Should().Be(7, "a crashing h9kd's exit code must reach wscript.exe for RestartOnFailure to see it");
        RunLaunchScript(0).ExitCode.Should().Be(0, "h9kd's own clean-stop exit code must also reach wscript.exe unchanged");
    }

    [Fact]
    public void The_launcher_is_invoked_with_the_daemon_and_log_paths_intact_through_cmds_own_quoting()
    {
        // cmd.exe parses its own /c argument with a fallback rule nothing else uses (see
        // WindowsCommandLine), and this command line now nests three levels of quoting: a
        // VBScript string literal around a cmd.exe /c wrapper around two quoted path arguments.
        // Reading the generated script proves what was written; only running it proves what
        // cmd.exe actually hands the launcher, which is the boundary that silently mangles
        // quotes when it is wrong.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        (int exitCode, string arguments, string daemonPath, string logPath) = RunLaunchScript(0);

        exitCode.Should().Be(0);
        arguments.Trim().Should().Be($"daemon autostart launch --binary \"{daemonPath}\" --log \"{logPath}\"");
    }

    private static (int ExitCode, string Arguments, string DaemonPath, string LogPath) RunLaunchScript(
        int simulatedExitCode)
    {
        string directory = Directory.CreateTempSubdirectory("h9k-launch-script-").FullName;
        try
        {
            string fakeDaemon = Path.Combine(directory, "fake-h9kd.exe");
            string log = Path.Combine(directory, "h9kd.log");
            string recordedArguments = Path.Combine(directory, "arguments.txt");

            // Stands in for the installed h9k the script invokes: it records the arguments cmd.exe
            // actually handed it and exits with whatever the real one would have relayed from
            // h9kd. Nothing here ever reaches h9kd itself, so the daemon path only has to be a
            // path — the stub never runs it.
            string fakeLauncher = Path.Combine(directory, "fake-h9k.cmd");
            File.WriteAllText(
                fakeLauncher,
                $"@echo off\r\necho %*>\"{recordedArguments}\"\r\nexit /b {simulatedExitCode}\r\n");

            string script = Path.Combine(directory, "launch.vbs");
            File.WriteAllText(
                script,
                WindowsDaemonAutostart.LaunchScriptContent(fakeLauncher, fakeDaemon, log, []),
                Encoding.Unicode);

            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = "cscript.exe",
                ArgumentList = { "//nologo", "//B", script },
                UseShellExecute = false,
            })!;
            process.WaitForExit();
            return (
                process.ExitCode,
                File.Exists(recordedArguments) ? File.ReadAllText(recordedArguments) : string.Empty,
                fakeDaemon,
                log);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_launch_script_this_version_writes_is_not_read_as_predating_the_append_handle()
    {
        WindowsDaemonAutostart.LaunchScriptPredatesTheAppendHandle(
            WindowsDaemonAutostart.LaunchScriptContent(Launcher, Binary, Log, Environment))
            .Should().BeFalse();
    }

    [Fact]
    public void A_launch_script_that_still_redirects_through_cmd_is_read_as_predating_the_append_handle()
    {
        // The script as it shipped before the launcher opened the log, verbatim: nothing but
        // h9k daemon autostart enable rewrites one of these, so an updated machine keeps
        // launching h9kd behind cmd.exe's own FILE_SHARE_READ append handle until someone
        // re-runs it — which is what h9k daemon start warns about when it sees this.
        const string legacy =
            "WScript.Quit CreateObject(\"WScript.Shell\").Run(\"cmd.exe /c \"\"set PATH=C:\\tools& "
            + "set HALL9K_DAEMON_APPEND_ONLY_LOG=1& \"\"C:\\Users\\someone\\.hall9k\\bin\\h9kd.exe\"\" "
            + "< NUL >> \"\"C:\\Users\\someone\\.hall9k\\h9kd.log\"\" 2>&1\"\", 0, True)\n";

        WindowsDaemonAutostart.LaunchScriptPredatesTheAppendHandle(legacy).Should().BeTrue();
    }

    [Fact]
    public void The_execution_time_limit_is_unlimited()
    {
        string xml = WindowsDaemonAutostart.TaskXmlContent();

        // Task Scheduler's own default (72 hours) would otherwise kill a daemon meant to
        // run indefinitely.
        xml.Should().Contain("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>");
    }

    [Fact]
    public void The_action_runs_the_launch_script_through_a_windowless_host()
    {
        string xml = WindowsDaemonAutostart.TaskXmlContent();

        // wscript.exe, not cmd.exe directly: there is no Task Scheduler setting that hides
        // a console-subsystem action's window, and an InteractiveToken principal runs it on
        // the signed-in user's own visible desktop. wscript.exe is a Windows-subsystem host
        // that never allocates a console of its own, and //B keeps a malformed script from
        // popping an error dialog with nothing to dismiss it.
        xml.Should().Contain(@"<Command>%WINDIR%\System32\wscript.exe</Command>");
        xml.Should().Contain("//B");
        xml.Should().Contain("h9kd-autostart-launch.vbs");
    }

    [Fact]
    public void The_launch_script_hides_cmd_and_waits_for_it_to_exit()
    {
        string script = WindowsDaemonAutostart.LaunchScriptContent(Launcher, Binary, Log, Environment);

        // Window style 0 is SW_HIDE; True waits for cmd.exe to exit, so the task instance's
        // own lifetime keeps tracking the daemon's the same way it did when cmd.exe was the
        // action directly, and closing the (now nonexistent) window can no longer cut the
        // 30s graceful-shutdown budget down to Windows's ~5s console-close grace period.
        // Waiting is load-bearing at every link: h9k daemon autostart launch waits on h9kd,
        // cmd.exe waits on it, and this waits on cmd.exe.
        script.Should().Contain("CreateObject(\"WScript.Shell\").Run");
        script.Should().Contain(", 0, True");
    }

    [Fact]
    public void The_launch_script_starts_the_daemon_through_the_h9k_launcher_rather_than_a_shell_redirect()
    {
        string script = WindowsDaemonAutostart.LaunchScriptContent(Launcher, Binary, Log, Environment);

        // The structural fix (PLAN.md §16 PLACEHOLDER-d4e64dfa). cmd.exe is still here, because its
        // `set NAME=VALUE&` prefixes are the only per-job environment Task Scheduler has, but it
        // no longer opens the log: h9k daemon autostart launch does, with a share mode that
        // refuses nobody, and hands the handle to h9kd. A `>>` anywhere in this script means
        // cmd.exe is holding the log with FILE_SHARE_READ for the daemon's whole run again,
        // which is exactly what kept h9kd's own rotation-safe takeover and its 8 MB log budget
        // from ever working on Windows.
        script.Should().Contain("cmd.exe");
        script.Should().Contain(Launcher);
        script.Should().Contain("daemon autostart launch");
        script.Should().Contain($"--binary \"\"{Binary}\"\"");
        script.Should().Contain($"--log \"\"{Log}\"\"");
        script.Should().NotContain(">>", "cmd.exe must hold no handle on the log at all");
        script.Should().NotContain("2>&1");
        script.Should().NotContain("< NUL", "stdin comes from a NUL handle the launcher opens, not a cmd.exe redirect");
    }

    [Fact]
    public void The_captured_environment_is_set_scoped_to_this_one_process_tree()
    {
        string script = WindowsDaemonAutostart.LaunchScriptContent(Launcher, Binary, Log, Environment);

        // Not a registry mutation (Decisions Log #3's Windows answer to launchd's per-job
        // EnvironmentVariables dict): each captured variable is a `set` ahead of the launch
        // inside the same cmd.exe invocation, so it never touches anything outside this
        // one task. h9kd inherits them by way of the launcher, which copies its own
        // environment into the block it creates the daemon with.
        script.Should().Contain("PATH=");
        script.Should().Contain("HALL9K_CLAUDE_PATH=");
        script.Should().NotContain("HKCU", "the environment travels with this one task, never through the registry");
    }

    [Fact]
    public void The_launch_script_leaves_the_append_only_log_marker_to_the_launcher()
    {
        // Program.cs only takes over its own console output with WindowsAppendOnlyLog when it
        // sees this marker (DaemonRuntime.AppendOnlyLogEnvironmentVariable) — without it, every
        // other way of starting h9kd on Windows would have its console silently redirected into
        // the installed daemon's log too. The script used to set it, back when the script itself
        // was what redirected the log; now WindowsDaemonLaunch puts it on h9kd's own environment
        // block (proven by WindowsDaemonLaunchTests), which is the one place both launch paths
        // get it from. Setting it here as well would put it on the LAUNCHER's environment, where
        // it means nothing and would be inherited by anything else h9k happened to spawn.
        string script = WindowsDaemonAutostart.LaunchScriptContent(Launcher, Binary, Log, Environment);

        script.Should().NotContain("HALL9K_DAEMON_APPEND_ONLY_LOG");
    }

    [Fact]
    public void An_unobserved_environment_is_left_out_rather_than_invented()
    {
        string script = WindowsDaemonAutostart.LaunchScriptContent(Launcher, Binary, Log, []);

        script.Should().NotContain("HALL9K_CLAUDE_PATH");
        script.Should().Contain(Binary);
    }

    [Fact]
    public void The_state_query_passes_the_folder_and_leaf_name_separately()
    {
        // Get-ScheduledTask's -TaskName matches only the leaf (the folder is the separate
        // -TaskPath parameter) — passing the combined \Hall9k\h9kd path to -TaskName alone
        // never matches anything, which silently always answered "not loaded".
        string command = WindowsDaemonAutostart.StateQueryCommand();

        command.Should().Contain("-TaskPath '\\Hall9k\\'");
        command.Should().Contain("-TaskName 'h9kd'");
        command.Should().NotContain("-TaskName '\\Hall9k\\h9kd'");
    }

    [Fact]
    public void A_running_task_reports_running()
    {
        // Get-ScheduledTask's State enum member name — the same on every Windows UI
        // language, unlike schtasks's own localized "Status:"/"Running" display text.
        WindowsDaemonAutostart.ParseIsRunning("Running\r\n").Should().BeTrue();
    }

    [Fact]
    public void An_idle_task_does_not_report_running()
    {
        WindowsDaemonAutostart.ParseIsRunning("Ready\r\n").Should().BeFalse();
    }

    [Fact]
    public void Escaping_a_value_with_no_special_characters_leaves_it_unchanged()
    {
        WindowsDaemonAutostart.EscapeForCmdExe("Host=localhost;Database=hall9k").Should().Be(
            "Host=localhost;Database=hall9k");
    }

    [Theory]
    [InlineData("Password=p&ss", "Password=p^&ss")]
    [InlineData("a|b", "a^|b")]
    [InlineData("a<b>c", "a^<b^>c")]
    [InlineData("a^b", "a^^b")]
    [InlineData("say \"hi\"", "say ^\"hi^\"")]
    [InlineData("(a)", "^(a^)")]
    public void Escaping_a_cmd_exe_metacharacter_carets_it(string value, string expected)
    {
        // The value sits in the unquoted `set NAME=VALUE&` position of the launch script's
        // cmd.exe command line, so an unescaped metacharacter here is real cmd.exe syntax
        // rather than data — the origin finding this closes is a Postgres password
        // containing `&` truncating the variable and running the rest as a command.
        WindowsDaemonAutostart.EscapeForCmdExe(value).Should().Be(expected);
    }

    [Fact]
    public void Escaping_leaves_a_percent_alone_rather_than_doubling_it()
    {
        // Doubling to %% is a batch-FILE rule, not a rule of the cmd.exe /c "..." command
        // line this text lands on — there %% stays two literal percent signs rather than
        // collapsing to one, so doubling here would corrupt any captured value containing
        // a percent (e.g. a URL-encoded connection string password).
        WindowsDaemonAutostart.EscapeForCmdExe("100%done").Should().Be("100%done");
    }

    [Fact]
    public void A_value_containing_a_cmd_metacharacter_is_carried_intact_into_the_launch_script()
    {
        KeyValuePair<string, string>[] environment = [new("HALL9K_CLAUDE_PATH", "C:\\tools\\p&ss\\claude.exe")];

        string script = WindowsDaemonAutostart.LaunchScriptContent(Launcher, Binary, Log, environment);

        // Caret-escaped at the cmd.exe layer, same as before the launch script existed — the
        // caret survives being embedded in the VBScript string literal (VBScript has no
        // backslash-escape syntax, so only the literal's own quotes get doubled), so the
        // command cmd.exe actually runs still reads as an escaped ampersand, never a bare
        // one that would end the set statement early.
        script.Should().Contain("p^&ss");
    }

    [Fact]
    public void The_connection_string_is_left_out_of_the_launch_script_even_when_captured()
    {
        // Unlike PATH or HALL9K_CLAUDE_PATH, the connection string already has a durable
        // fallback (Hall9kDatabase.Resolve reads the platform config file h9k doctor writes
        // before autostart is ever enabled), so embedding it here would only add a second,
        // weaker plaintext copy of the same secret to a file with no equivalent of the config
        // file's own protections.
        KeyValuePair<string, string>[] environment =
        [
            new(Hall9k.Domain.Infrastructure.Persistence.Hall9kDatabase.EnvironmentVariableName,
                "Host=localhost;Password=super-secret"),
            new("PATH", @"C:\tools"),
        ];

        string script = WindowsDaemonAutostart.LaunchScriptContent(Launcher, Binary, Log, environment);

        script.Should().NotContain("super-secret");
        script.Should().Contain("PATH=");
    }

    [Fact]
    public void The_recorded_variable_names_leave_out_the_connection_string()
    {
        // What EnableAsync reports back as actually recorded must match what
        // InnerCommand/LaunchScriptContent actually embed (proven not to contain the
        // connection string by The_connection_string_is_left_out_of_the_launch_script_
        // even_when_captured above) — a caller reporting success reports what happened,
        // not what it was asked to record. Origin: the enable command's own confirmation
        // message once named HALL9K_CONNECTION_STRING as recorded when it never was.
        KeyValuePair<string, string>[] environment =
        [
            new("PATH", @"C:\tools"),
            new(Hall9k.Domain.Infrastructure.Persistence.Hall9kDatabase.EnvironmentVariableName, "Host=localhost"),
        ];

        IReadOnlyList<string> recorded = WindowsDaemonAutostart.RecordedVariableNames(environment);

        recorded.Should().Contain("PATH");
        recorded.Should().NotContain(Hall9k.Domain.Infrastructure.Persistence.Hall9kDatabase.EnvironmentVariableName);
    }

    [Fact]
    public void A_double_quote_in_the_command_line_is_doubled_for_the_vbscript_string_literal()
    {
        // VBScript string literals have no backslash-escape syntax — the only way to carry
        // a literal quote inside one is to double it. Every quoted path segment the cmd.exe
        // command line carries (the binary path, the log path) crosses this boundary, so a
        // single un-doubled quote here would terminate the Run(...) argument early and cut
        // the rest of the command off as a second, unrelated VBScript argument.
        string script = WindowsDaemonAutostart.LaunchScriptContent(Launcher, Binary, Log, []);

        script.Should().Contain($"\"\"{Binary}\"\"");
    }
}
