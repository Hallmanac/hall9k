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
        string xml = WindowsDaemonAutostart.TaskXmlContent(Binary, Log, Environment);

        xml.Should().Contain("<LogonTrigger>");
        // Decisions Log #3: never a service identity — a Windows service would run as a
        // different account by default, the same credential problem the daemon exists to
        // avoid everywhere else.
        xml.Should().Contain("<LogonType>InteractiveToken</LogonType>");
    }

    [Fact]
    public void Crash_restart_never_resurrects_a_clean_stop()
    {
        string xml = WindowsDaemonAutostart.TaskXmlContent(Binary, Log, Environment);

        // RestartOnFailure only restarts on a nonzero exit; h9kd's own graceful shutdown
        // (WindowsStopRequestWatcher) exits 0, indistinguishable from a task with nothing
        // left to do — mirrors launchd's KeepAlive SuccessfulExit=false.
        xml.Should().Contain("<RestartOnFailure>");
        xml.Should().Contain("<Count>3</Count>");
    }

    [Fact]
    public void The_execution_time_limit_is_unlimited()
    {
        string xml = WindowsDaemonAutostart.TaskXmlContent(Binary, Log, Environment);

        // Task Scheduler's own default (72 hours) would otherwise kill a daemon meant to
        // run indefinitely.
        xml.Should().Contain("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>");
    }

    [Fact]
    public void The_action_runs_the_binary_through_cmd_with_the_log_redirected()
    {
        string xml = WindowsDaemonAutostart.TaskXmlContent(Binary, Log, Environment);

        xml.Should().Contain(@"<Command>%WINDIR%\System32\cmd.exe</Command>");
        xml.Should().Contain(Binary);
        xml.Should().Contain(Log);
        xml.Should().Contain("2&gt;&amp;1", "the redirection syntax is XML-escaped, not left raw in the element");
    }

    [Fact]
    public void The_captured_environment_is_set_scoped_to_this_one_process_tree()
    {
        string xml = WindowsDaemonAutostart.TaskXmlContent(Binary, Log, Environment);

        // Not a registry mutation (Decisions Log #3's Windows answer to launchd's per-job
        // EnvironmentVariables dict): each captured variable is a `set` ahead of h9kd
        // inside the same cmd.exe invocation, so it never touches anything outside this
        // one task.
        xml.Should().Contain("PATH=");
        xml.Should().Contain("HALL9K_CLAUDE_PATH=");
        xml.Should().NotContain("HKCU", "the environment travels with this one task, never through the registry");
    }

    [Fact]
    public void An_unobserved_environment_is_left_out_rather_than_invented()
    {
        string xml = WindowsDaemonAutostart.TaskXmlContent(Binary, Log, []);

        xml.Should().NotContain("HALL9K_CLAUDE_PATH");
        xml.Should().Contain(Binary);
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
        // The value sits in the unquoted `set NAME=VALUE&` position of the task action's
        // command line (see CommandLine), so an unescaped metacharacter here is real cmd.exe
        // syntax rather than data — the origin finding this closes is a Postgres password
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
    public void A_value_containing_a_cmd_metacharacter_is_carried_intact_into_the_task_action()
    {
        KeyValuePair<string, string>[] environment =
        [
            new(Hall9k.Domain.Infrastructure.Persistence.Hall9kDatabase.EnvironmentVariableName,
                "Host=localhost;Password=p&ss"),
        ];

        string xml = WindowsDaemonAutostart.TaskXmlContent(Binary, Log, environment);

        // XML-escaped once (SecurityElement.Escape turns the caret-escaped `^&` sequence's
        // own `&` into `&amp;`) — the caret survives that pass, so the roundtrip through the
        // task file still reads as an escaped ampersand, never a bare one that would end the
        // set statement early.
        xml.Should().Contain("p^&amp;ss");
    }
}
