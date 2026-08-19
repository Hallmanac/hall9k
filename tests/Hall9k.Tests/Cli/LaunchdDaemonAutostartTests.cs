using FluentAssertions;
using Hall9k.Cli.DaemonControl;
using Xunit;

namespace Hall9k.Tests.Cli;

public sealed class LaunchdDaemonAutostartTests
{
    private const string Binary = "/Users/someone/.hall9k/bin/h9kd";
    private const string Log = "/Users/someone/.hall9k/h9kd.log";

    private static readonly KeyValuePair<string, string>[] Environment =
    [
        new("PATH", "/opt/homebrew/bin:/usr/bin:/bin"),
        new("HALL9K_CLAUDE_PATH", "/Users/someone/.local/bin/claude"),
    ];

    [Fact]
    public void Plist_runs_the_installed_binary_at_load_and_logs_to_the_daemon_log()
    {
        string plist = LaunchdDaemonAutostart.PlistContent(Binary, Log, Environment);

        plist.Should().Contain($"<string>{LaunchdDaemonAutostart.Label}</string>");
        plist.Should().Contain($"<string>{Binary}</string>");
        plist.Should().Contain("<key>RunAtLoad</key>");
        plist.Should().Contain($"<string>{Log}</string>");
    }

    [Fact]
    public void Crash_restart_never_resurrects_a_clean_stop()
    {
        string plist = LaunchdDaemonAutostart.PlistContent(Binary, Log, Environment);

        // KeepAlive with SuccessfulExit=false restarts only after a nonzero exit: a
        // graceful stop (exit 0) stays stopped, and h9k daemon stop bootouts the job
        // anyway. A bare <key>KeepAlive</key><true/> here would resurrect what the
        // human just killed.
        int keepAlive = plist.IndexOf("<key>KeepAlive</key>", StringComparison.Ordinal);
        keepAlive.Should().BePositive();
        plist[keepAlive..].Should().StartWith(
            """
            <key>KeepAlive</key>
                <dict>
                    <key>SuccessfulExit</key>
                    <false/>
                </dict>
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Bootout_outlives_the_daemons_graceful_shutdown_budget()
    {
        string plist = LaunchdDaemonAutostart.PlistContent(Binary, Log, Environment);

        // launchd's default ExitTimeOut (20s) SIGKILLs inside the daemon's 30s
        // graceful-shutdown budget — mid-append, exactly what stop promises never
        // happens. 45s matches DaemonLifecycle.StopTimeout.
        plist.Should().Contain(
            """
            <key>ExitTimeOut</key>
                <integer>45</integer>
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Tearing_the_job_down_never_sweeps_the_detached_agents()
    {
        string plist = LaunchdDaemonAutostart.PlistContent(Binary, Log, Environment);

        // The agents h9kd spawns share its process group (no setsid), and launchd
        // signals the whole group when it tears a job down — so without this key,
        // h9k daemon stop under autostart SIGTERMs every running agent, the one thing
        // stop promises it never does.
        plist.Should().Contain(
            """
            <key>AbandonProcessGroup</key>
                <true/>
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void The_captured_environment_travels_into_the_job()
    {
        string plist = LaunchdDaemonAutostart.PlistContent(Binary, Log, Environment);

        // Without this the job runs with launchd's default PATH
        // (/usr/bin:/bin:/usr/sbin:/sbin), where neither claude nor gh lives: the daemon
        // starts, reports healthy, and fails every run that spawns one of them.
        plist.Should().Contain(
            """
            <key>EnvironmentVariables</key>
                <dict>
                    <key>PATH</key>
                    <string>/opt/homebrew/bin:/usr/bin:/bin</string>
                    <key>HALL9K_CLAUDE_PATH</key>
                    <string>/Users/someone/.local/bin/claude</string>
                </dict>
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void An_unobserved_environment_is_left_out_rather_than_invented()
    {
        string plist = LaunchdDaemonAutostart.PlistContent(Binary, Log, []);

        plist.Should().NotContain("EnvironmentVariables");
        plist.Should().Contain("<key>StandardOutPath</key>");
    }

    [Fact]
    public void Paths_and_environment_values_are_xml_escaped()
    {
        string plist = LaunchdDaemonAutostart.PlistContent(
            "/tmp/a&b/h9kd", Log, [new KeyValuePair<string, string>("PATH", "/opt/a&b/bin")]);

        plist.Should().Contain("/tmp/a&amp;b/h9kd");
        plist.Should().Contain("<string>/opt/a&amp;b/bin</string>");
    }

    [Fact]
    public void A_running_job_reports_the_pid_launchd_owns()
    {
        // Abridged real launchctl print output.
        string output = """
            com.hall9k.h9kd = {
            	active count = 1
            	path = /Users/someone/Library/LaunchAgents/com.hall9k.h9kd.plist
            	state = running

            	program = /Users/someone/.hall9k/bin/h9kd
            	pid = 54321
            	immediate reason = speculative
            }
            """;

        LaunchdDaemonAutostart.ParseProcessId(output).Should().Be(54321);
    }

    [Fact]
    public void A_loaded_but_idle_job_reports_no_pid()
    {
        // The single-instance loser exits 0 and KeepAlive deliberately leaves it
        // stopped: the label stays bootstrapped with nothing running under it, which is
        // the case "is it loaded?" cannot tell apart from a live daemon.
        string output = """
            com.hall9k.h9kd = {
            	active count = 0
            	path = /Users/someone/Library/LaunchAgents/com.hall9k.h9kd.plist
            	state = not running

            	program = /Users/someone/.hall9k/bin/h9kd
            	last exit code = 0
            }
            """;

        LaunchdDaemonAutostart.ParseProcessId(output).Should().BeNull();
    }
}
