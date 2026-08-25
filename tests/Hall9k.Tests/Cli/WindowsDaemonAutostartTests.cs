using FluentAssertions;
using Hall9k.Cli.DaemonControl;
using Xunit;

namespace Hall9k.Tests.Cli;

public sealed class WindowsDaemonAutostartTests
{
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
    public void Crash_restart_never_resurrects_a_clean_stop()
    {
        string xml = WindowsDaemonAutostart.TaskXmlContent();

        // RestartOnFailure only restarts on a nonzero exit; h9kd's own graceful shutdown
        // (WindowsStopRequestWatcher) exits 0, indistinguishable from a task with nothing
        // left to do — mirrors launchd's KeepAlive SuccessfulExit=false. The wait that
        // carries this exit code up now goes through the launch script's WScript.Shell.Run,
        // but the task's own RestartOnFailure setting is unchanged by that indirection.
        xml.Should().Contain("<RestartOnFailure>");
        xml.Should().Contain("<Count>3</Count>");
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
        string script = WindowsDaemonAutostart.LaunchScriptContent(Binary, Log, Environment);

        // Window style 0 is SW_HIDE; True waits for cmd.exe to exit, so the task instance's
        // own lifetime keeps tracking the daemon's the same way it did when cmd.exe was the
        // action directly, and closing the (now nonexistent) window can no longer cut the
        // 30s graceful-shutdown budget down to Windows's ~5s console-close grace period.
        script.Should().Contain("CreateObject(\"WScript.Shell\").Run");
        script.Should().Contain(", 0, True");
    }

    [Fact]
    public void The_launch_script_runs_the_binary_through_cmd_with_the_log_redirected()
    {
        string script = WindowsDaemonAutostart.LaunchScriptContent(Binary, Log, Environment);

        script.Should().Contain("cmd.exe");
        script.Should().Contain(Binary);
        script.Should().Contain(Log);
        script.Should().Contain("2>&1", "the redirection syntax lands in a VBScript string literal, not an XML element");
    }

    [Fact]
    public void The_captured_environment_is_set_scoped_to_this_one_process_tree()
    {
        string script = WindowsDaemonAutostart.LaunchScriptContent(Binary, Log, Environment);

        // Not a registry mutation (Decisions Log #3's Windows answer to launchd's per-job
        // EnvironmentVariables dict): each captured variable is a `set` ahead of h9kd
        // inside the same cmd.exe invocation, so it never touches anything outside this
        // one task.
        script.Should().Contain("PATH=");
        script.Should().Contain("HALL9K_CLAUDE_PATH=");
        script.Should().NotContain("HKCU", "the environment travels with this one task, never through the registry");
    }

    [Fact]
    public void An_unobserved_environment_is_left_out_rather_than_invented()
    {
        string script = WindowsDaemonAutostart.LaunchScriptContent(Binary, Log, []);

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
        KeyValuePair<string, string>[] environment =
        [
            new(Hall9k.Domain.Infrastructure.Persistence.Hall9kDatabase.EnvironmentVariableName,
                "Host=localhost;Password=p&ss"),
        ];

        string script = WindowsDaemonAutostart.LaunchScriptContent(Binary, Log, environment);

        // Caret-escaped at the cmd.exe layer, same as before the launch script existed — the
        // caret survives being embedded in the VBScript string literal (VBScript has no
        // backslash-escape syntax, so only the literal's own quotes get doubled), so the
        // command cmd.exe actually runs still reads as an escaped ampersand, never a bare
        // one that would end the set statement early.
        script.Should().Contain("p^&ss");
    }

    [Fact]
    public void A_double_quote_in_the_command_line_is_doubled_for_the_vbscript_string_literal()
    {
        // VBScript string literals have no backslash-escape syntax — the only way to carry
        // a literal quote inside one is to double it. Every quoted path segment the cmd.exe
        // command line carries (the binary path, the log path) crosses this boundary, so a
        // single un-doubled quote here would terminate the Run(...) argument early and cut
        // the rest of the command off as a second, unrelated VBScript argument.
        string script = WindowsDaemonAutostart.LaunchScriptContent(Binary, Log, []);

        script.Should().Contain($"\"\"{Binary}\"\"");
    }
}
