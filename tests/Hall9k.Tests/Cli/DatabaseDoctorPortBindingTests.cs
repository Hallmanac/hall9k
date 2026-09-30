using FluentAssertions;
using Hall9k.Cli.Diagnostics;
using Hall9k.Connectors.Processes;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="DatabaseDoctor.CheckContainerPortBindingAsync(bool,System.Threading.CancellationToken)"/>
/// (security review idea 6be68ee2, secrets-files-network finding 1): the doctor
/// reports, and (only under <c>--yes</c> and only once every guard condition holds) rewrites the
/// compose file from the shipped constant and recreates <c>hall9k-postgres</c> when it is not
/// publishing port 5432 on <c>127.0.0.1</c> alone; a check that stops short of that writes nothing,
/// and a node whose connection string names a Postgres on another host is left alone entirely, with no
/// <c>docker</c> call, because where docker reaches another machine's engine the name
/// <c>hall9k-postgres</c> may be that machine's container (a loopback address on another port is this
/// machine's own Postgres, so its exposed container is still inspected and reported, never recreated). A fake daemon-running probe stands in for
/// <see cref="Hall9k.Cli.DaemonControl.DaemonProcess.Probe"/>, the same seam
/// <c>DatabaseDoctorAlreadyRunningContainerTests</c> already uses for the readiness poll — and,
/// for a successful recreate, so does the readiness probe itself, so this never depends on a real
/// Postgres answering at the compose file's own recorded password within the real 30s timeout.
/// </summary>
public sealed class DatabaseDoctorPortBindingTests : IDisposable
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ShortPollInterval = TimeSpan.FromMilliseconds(20);

    private readonly ScopedTestHome scopedHome = new();

    public void Dispose() => scopedHome.Dispose();

    private static Func<bool> NoDaemonRunning => () => false;

    private static Func<bool> DaemonIsRunning => () => true;

    private static Func<CancellationToken, Task<ReachabilityReport>> AlwaysReachable =>
        _ => Task.FromResult(new ReachabilityReport(ReachabilityStatus.Reachable, string.Empty, "localhost", 5432, "hall9k"));

    private static Func<ConnectionStringResolution> NothingConfigured => () => ConnectionStringResolution.NotConfigured;

    private static Func<ConnectionStringResolution> Configured(string connectionString) =>
        () => new ConnectionStringResolution(connectionString, ConnectionStringOrigin.Configured, null);

    private const string StalePassword = "stale-known-password";

    private static void SeedStaleComposeFile()
    {
        Directory.CreateDirectory(PostgresRuntime.ComposeDirectory);
        File.WriteAllText(PostgresRuntime.ComposeFile, PostgresRuntime.ComposeFileContentsFor(StalePassword));
    }

    [Theory]
    [InlineData("no-container-runtime", false)]
    [InlineData("no-container-runtime", true)]
    [InlineData("container-absent", false)]
    [InlineData("container-absent", true)]
    [InlineData("status-unconfirmed", false)]
    [InlineData("status-unconfirmed", true)]
    [InlineData("inspect-failed", false)]
    [InlineData("inspect-failed", true)]
    [InlineData("loopback-binding", false)]
    [InlineData("loopback-binding", true)]
    [InlineData("drift-owned", false)]
    [InlineData("drift-owned", true)]
    [InlineData("drift-foreign", false)]
    [InlineData("drift-foreign", true)]
    public async Task A_check_without_yes_writes_nothing_on_any_path(string scenario, bool composeFileAlreadyExists)
    {
        RecordingProcessRunner runner = ScenarioRunner(scenario);
        if (composeFileAlreadyExists)
        {
            SeedStaleComposeFile();
        }

        byte[]? before = composeFileAlreadyExists ? File.ReadAllBytes(PostgresRuntime.ComposeFile) : null;

        await ScopedAnsiConsoleCapture.CaptureAsync(() => DatabaseDoctor.CheckContainerPortBindingAsync(
            assumeYes: false, runner.Runner, NoDaemonRunning, NothingConfigured, CancellationToken.None));

        switch (before)
        {
            case null:
                File.Exists(PostgresRuntime.ComposeFile).Should().BeFalse("a check that is not recreating anything creates no file");
                break;
            default:
                File.ReadAllBytes(PostgresRuntime.ComposeFile).Should().Equal(before, "an existing file is left byte for byte as it was");
                break;
        }
    }

    [Fact]
    public async Task The_compose_file_is_rewritten_only_right_before_the_recreate()
    {
        SeedStaleComposeFile();
        string? contentsWhenComposeRan = null;
        RecordingProcessRunner runner = RunningContainerRunner(
            hostIp: "0.0.0.0", volumes: "hall9k-pgdata", label: PostgresRuntime.ComposeFile,
            onCompose: () => contentsWhenComposeRan = File.ReadAllText(PostgresRuntime.ComposeFile));

        await ScopedAnsiConsoleCapture.CaptureAsync(() => DatabaseDoctor.CheckContainerPortBindingAsync(
            assumeYes: true, runner.Runner, NoDaemonRunning, NothingConfigured, AlwaysReachable, ShortTimeout, ShortPollInterval,
            TimeProvider.System, CancellationToken.None));

        contentsWhenComposeRan.Should().Be(
            PostgresRuntime.ComposeFileContentsFor(StalePassword),
            "the file holds the shipped constant, with the password already in effect kept, by the time compose reads it");
        runner.Calls.Should().ContainSingle(call =>
            call.Arguments.SequenceEqual(new[] { "compose", "-f", PostgresRuntime.ComposeFile, "up", "-d" }));
    }

    [Fact]
    public async Task A_failed_compose_file_write_is_reported_and_never_escapes()
    {
        // A plain file where the directory should be makes Directory.CreateDirectory throw
        // IOException on every platform.
        File.WriteAllText(PostgresRuntime.ComposeDirectory, "not a directory");
        RecordingProcessRunner runner = RunningContainerRunner(hostIp: "0.0.0.0", volumes: "hall9k-pgdata", label: PostgresRuntime.ComposeFile);

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(() => DatabaseDoctor.CheckContainerPortBindingAsync(
            assumeYes: true, runner.Runner, NoDaemonRunning, NothingConfigured, AlwaysReachable, ShortTimeout, ShortPollInterval,
            TimeProvider.System, CancellationToken.None));

        output.Should().Contain(PostgresRuntime.ComposeFile).And.Contain("was not recreated");
        output.Should().NotContain("Recreated");
        runner.Calls.Should().NotContain(call => call.Arguments.Count > 0 && call.Arguments[0] == "compose");
    }

    [Theory]
    [InlineData("foreign-volume")]
    [InlineData("foreign-label")]
    [InlineData("daemon-running")]
    public async Task A_stale_compose_file_is_left_alone_when_the_recreate_is_not_allowed(string reason)
    {
        SeedStaleComposeFile();
        byte[] before = File.ReadAllBytes(PostgresRuntime.ComposeFile);
        string otherComposeFile = Path.Combine(Path.GetTempPath(), $"other-{Guid.NewGuid():N}", "docker-compose.yml");
        RecordingProcessRunner runner = RunningContainerRunner(
            hostIp: "0.0.0.0",
            volumes: reason == "foreign-volume" ? "dev_hall9k-pgdata" : "hall9k-pgdata",
            label: reason == "foreign-label" ? otherComposeFile : PostgresRuntime.ComposeFile);

        await ScopedAnsiConsoleCapture.CaptureAsync(() => DatabaseDoctor.CheckContainerPortBindingAsync(
            assumeYes: true, runner.Runner, reason == "daemon-running" ? DaemonIsRunning : NoDaemonRunning,
            NothingConfigured, CancellationToken.None));

        File.ReadAllBytes(PostgresRuntime.ComposeFile).Should().Equal(before);
        runner.Calls.Should().NotContain(call => call.Arguments.Count > 0 && call.Arguments[0] == "compose");
    }

    [Fact]
    public async Task A_node_whose_database_is_on_another_host_is_left_alone_and_told_so()
    {
        await Hall9kDatabase.WriteConfiguredConnectionStringAsync(
            "Host=10.211.55.2;Port=5432;Database=hall9k;Username=postgres;Password=not-the-default", CancellationToken.None);
        RecordingProcessRunner runner = RunningContainerRunner(hostIp: "0.0.0.0", volumes: "hall9k-pgdata", label: PostgresRuntime.ComposeFile);

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(() => DatabaseDoctor.CheckContainerPortBindingAsync(
            assumeYes: true, runner.Runner, NoDaemonRunning,
            () => Hall9kDatabase.ResolveFromConfigFile(Hall9kDatabase.ConfigFile), CancellationToken.None));

        File.Exists(PostgresRuntime.ComposeFile).Should().BeFalse();
        runner.Calls.Should().BeEmpty();
        output.Should().Contain("10.211.55.2:5432")
            .And.Contain("not hall9k's own container at 127.0.0.1:5432")
            .And.Contain("does not manage that Postgres")
            .And.Contain("Postgres on another host");
        output.Should().NotContain("shipped default password", "the password here is not hall9k's default");
        output.Should().NotContain("not 127.0.0.1").And.NotContain("Point the connection string back")
            .And.NotContain("known default credentials");
    }

    [Fact]
    public async Task The_shipped_default_password_on_another_host_is_named_in_its_own_sentence()
    {
        RecordingProcessRunner runner = RunningContainerRunner(hostIp: "0.0.0.0", volumes: "hall9k-pgdata", label: PostgresRuntime.ComposeFile);

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(() => DatabaseDoctor.CheckContainerPortBindingAsync(
            assumeYes: true, runner.Runner, NoDaemonRunning,
            Configured($"Host=10.211.55.2;Database=hall9k;Username=postgres;Password={Hall9kDatabase.LegacyPassword}"),
            CancellationToken.None));

        output.Should().Contain("That database still uses hall9k's shipped default password, which has to be rotated by hand.");
        runner.Calls.Should().BeEmpty();
        output.Should().NotContain("not 127.0.0.1").And.NotContain("Point the connection string back")
            .And.NotContain("known default credentials");
    }

    [Fact]
    public async Task A_local_address_on_another_port_is_named_and_its_exposed_container_is_reported_not_recreated()
    {
        RecordingProcessRunner runner = RunningContainerRunner(hostIp: "0.0.0.0", volumes: "hall9k-pgdata", label: PostgresRuntime.ComposeFile);

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(() => DatabaseDoctor.CheckContainerPortBindingAsync(
            assumeYes: true, runner.Runner, NoDaemonRunning,
            Configured("Host=localhost;Port=5433;Database=hall9k;Username=postgres;Password=x"), CancellationToken.None));

        output.Should().Contain("localhost:5433").And.Contain("not hall9k's own container at 127.0.0.1:5432");
        output.Replace("\"Postgres on another host\"", string.Empty).Should()
            .NotContain("another host", "the address is local, only the port is not hall9k's");
        runner.Calls.Should().Contain(call => call.Arguments.Count > 0 && call.Arguments[0] == "inspect",
            "the inspect is read-only, so a container exposed on this machine is reported whatever the port says");
        runner.Calls.Should().NotContain(call => call.Arguments.Count > 0 && call.Arguments[0] == "compose");
        File.Exists(PostgresRuntime.ComposeFile).Should().BeFalse();
        output.Should().Contain("publishes port 5432 on 0.0.0.0").And.Contain("Not recreating it")
            .And.NotContain("Point the connection string back");
    }

    [Theory]
    [InlineData("Host=127.0.0.1;Database=hall9k;Username=postgres;Password=x", "a missing port is 5432, the way the probe reads it")]
    [InlineData("Database=hall9k;Username=postgres;Password=x", "a missing host is localhost, the way the probe reads it")]
    [InlineData("this is not a connection string", "a string that does not parse is today's local case, never an exception")]
    public async Task A_local_or_unreadable_connection_string_is_checked_as_before(string connectionString, string because)
    {
        RecordingProcessRunner runner = RunningContainerRunner(hostIp: "127.0.0.1", volumes: "hall9k-pgdata", label: PostgresRuntime.ComposeFile);

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(() => DatabaseDoctor.CheckContainerPortBindingAsync(
            assumeYes: true, runner.Runner, NoDaemonRunning, Configured(connectionString), CancellationToken.None));

        output.Should().BeEmpty(because);
        runner.Calls.Should().Contain(call => call.Arguments.Count > 0 && call.Arguments[0] == "inspect", because);
    }

    [Fact]
    public async Task A_malformed_config_file_is_checked_as_before()
    {
        Directory.CreateDirectory(PlatformPaths.Home);
        File.WriteAllText(Hall9kDatabase.ConfigFile, "{ not json");
        RecordingProcessRunner runner = RunningContainerRunner(hostIp: "127.0.0.1", volumes: "hall9k-pgdata", label: PostgresRuntime.ComposeFile);

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(() => DatabaseDoctor.CheckContainerPortBindingAsync(
            assumeYes: true, runner.Runner, NoDaemonRunning,
            () => Hall9kDatabase.ResolveFromConfigFile(Hall9kDatabase.ConfigFile), CancellationToken.None));

        output.Should().BeEmpty();
        runner.Calls.Should().Contain(call => call.Arguments.Count > 0 && call.Arguments[0] == "inspect");
    }

    [Fact]
    public async Task A_label_that_only_resolves_to_this_installs_compose_file_is_not_this_installs_own()
    {
        string? label = RelativeLabelResolvingToOwnComposeFile();
        if (label is null)
        {
            // Windows, with the working directory on another drive than the temp home: nothing
            // built without a drive letter can resolve to the compose file here. The other-platform
            // case below still runs.
            return;
        }

        Path.IsPathFullyQualified(label).Should().BeFalse();
        Path.GetFullPath(label).Should().Be(
            Path.GetFullPath(PostgresRuntime.ComposeFile), "otherwise this proves nothing about the fix");

        await AssertLabelIsNotThisInstallsOwnAsync(label);
    }

    [Fact]
    public async Task A_label_fully_qualified_only_on_the_other_platform_is_not_this_installs_own()
    {
        string label = OperatingSystem.IsWindows()
            ? PostgresRuntime.ComposeFile[2..].Replace('\\', '/')
            : "C:" + PostgresRuntime.ComposeFile.Replace('/', '\\');

        await AssertLabelIsNotThisInstallsOwnAsync(label);
    }

    private async Task AssertLabelIsNotThisInstallsOwnAsync(string label)
    {
        SeedStaleComposeFile();
        RecordingProcessRunner runner = RunningContainerRunner(hostIp: "0.0.0.0", volumes: "hall9k-pgdata", label: label);

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(() => DatabaseDoctor.CheckContainerPortBindingAsync(
            assumeYes: true, runner.Runner, NoDaemonRunning, NothingConfigured, CancellationToken.None));

        output.Should().Contain("was created from a different compose file");
        runner.Calls.Should().NotContain(call => call.Arguments.Count > 0 && call.Arguments[0] == "compose");
    }

    private static string? RelativeLabelResolvingToOwnComposeFile()
    {
        string composeFile = Path.GetFullPath(PostgresRuntime.ComposeFile);
        string workingDirectory = Directory.GetCurrentDirectory();
        if (!OperatingSystem.IsWindows())
        {
            return Path.GetRelativePath(workingDirectory, composeFile);
        }

        // Drive letter removed: rooted on the current drive, which is this install's own only
        // when the working directory is on the same drive as the compose file.
        string root = Path.GetPathRoot(composeFile)!;
        return string.Equals(root, Path.GetPathRoot(workingDirectory), StringComparison.OrdinalIgnoreCase)
            ? composeFile[(root.Length - 1)..]
            : null;
    }

    private static RecordingProcessRunner ScenarioRunner(string scenario)
    {
        if (scenario == "no-container-runtime")
        {
            return RecordingProcessRunner.Failing("Cannot connect to the Docker daemon");
        }

        return scenario switch
        {
            "container-absent" => new RecordingProcessRunner(arguments => arguments switch
            {
                ["info"] or ["ps", "-a", ..] => new(0, string.Empty, string.Empty),
                _ => new(1, string.Empty, "unexpected call"),
            }),
            "status-unconfirmed" => new RecordingProcessRunner(arguments => arguments switch
            {
                ["info"] => new(0, string.Empty, string.Empty),
                _ => new(1, string.Empty, "docker ps failed"),
            }),
            "inspect-failed" => new RecordingProcessRunner(arguments => arguments switch
            {
                ["info"] => new(0, string.Empty, string.Empty),
                ["ps", "-a", ..] => new(0, "running\n", string.Empty),
                _ => new(1, string.Empty, "docker inspect failed"),
            }),
            "loopback-binding" => RunningContainerRunner("127.0.0.1", "hall9k-pgdata", PostgresRuntime.ComposeFile),
            "drift-owned" => RunningContainerRunner("0.0.0.0", "hall9k-pgdata", PostgresRuntime.ComposeFile),
            "drift-foreign" => RunningContainerRunner("0.0.0.0", "dev_hall9k-pgdata", PostgresRuntime.ComposeFile),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
        };
    }

    [Fact]
    public async Task No_container_means_nothing_further_to_check()
    {
        RecordingProcessRunner runner = null!;
        runner = new RecordingProcessRunner(() => runner.Calls[^1].Arguments switch
        {
            ["info"] => new(0, string.Empty, string.Empty),
            ["ps", "-a", ..] => new(0, string.Empty, string.Empty),
            _ => new(1, string.Empty, "unexpected call"),
        });

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(() => DatabaseDoctor.CheckContainerPortBindingAsync(
            assumeYes: true, runner.Runner, NoDaemonRunning, NothingConfigured, CancellationToken.None));

        output.Should().BeEmpty("no container exists yet, so there is nothing to report drift about");
        runner.Calls.Should().NotContain(call => call.Arguments.Count > 0 && call.Arguments[0] == "inspect");
    }

    [Fact]
    public async Task A_loopback_only_binding_is_not_reported_as_drift()
    {
        RecordingProcessRunner runner = RunningContainerRunner(hostIp: "127.0.0.1", volumes: "hall9k-pgdata", label: PostgresRuntime.ComposeFile);

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(() => DatabaseDoctor.CheckContainerPortBindingAsync(
            assumeYes: true, runner.Runner, NoDaemonRunning, NothingConfigured, CancellationToken.None));

        output.Should().BeEmpty();
        runner.Calls.Should().NotContain(call => call.Arguments.Count > 0 && call.Arguments[0] == "compose");
    }

    [Fact]
    public async Task Drift_is_recreated_when_every_guard_condition_holds()
    {
        // CheckContainerPortBindingAsync never branches on Running versus Stopped (it only reads
        // whether a container is PostgresContainerStatus.Absent), so this same assertion already
        // covers a stopped drifted container recreated the same way, never docker started, which
        // used to be a second test driving the identical seam (cycle-1 pre-PR review, adversarial
        // lens: test-hygiene check 1 — folded in here rather than kept standalone).
        RecordingProcessRunner runner = RunningContainerRunner(hostIp: "0.0.0.0", volumes: "hall9k-pgdata", label: PostgresRuntime.ComposeFile);

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(() => DatabaseDoctor.CheckContainerPortBindingAsync(
            assumeYes: true, runner.Runner, NoDaemonRunning, NothingConfigured, AlwaysReachable, ShortTimeout, ShortPollInterval,
            TimeProvider.System, CancellationToken.None));

        output.Should().Contain("Recreated");
        runner.Calls.Should().ContainSingle(call =>
            call.Arguments.SequenceEqual(new[] { "compose", "-f", PostgresRuntime.ComposeFile, "up", "-d" }));
        runner.Calls.Should().NotContain(call =>
            call.Arguments.Count > 0 && call.Arguments[0] == "start", "a recreate, never docker start, is what actually rolls the binding forward");
    }

    /// <summary>
    /// A successful <c>docker compose up -d</c> returns once the container has started, not once
    /// Postgres inside it is accepting connections — the very next question this doctor (or the
    /// caller it returns to) asks tries the database once, with no retry of its own, so a
    /// container that never comes up within the readiness window has to be reported, not silently
    /// handed back as though the recreate made it usable (cycle-1 pre-PR review, both lenses,
    /// <c>DatabaseDoctor.cs:201/204</c>).
    /// </summary>
    [Fact]
    public async Task A_recreated_container_that_never_becomes_reachable_is_reported_not_ready()
    {
        RecordingProcessRunner runner = RunningContainerRunner(hostIp: "0.0.0.0", volumes: "hall9k-pgdata", label: PostgresRuntime.ComposeFile);
        Func<CancellationToken, Task<ReachabilityReport>> neverReachable =
            _ => Task.FromResult(new ReachabilityReport(ReachabilityStatus.RefusedConnection, "nothing listening", "localhost", 5432, "hall9k"));

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(() => DatabaseDoctor.CheckContainerPortBindingAsync(
            assumeYes: true, runner.Runner, NoDaemonRunning, NothingConfigured, neverReachable, ShortTimeout, ShortPollInterval,
            TimeProvider.System, CancellationToken.None));

        output.Should().Contain("Recreated, but it was not answering");
        runner.Calls.Should().ContainSingle(call =>
            call.Arguments.SequenceEqual(new[] { "compose", "-f", PostgresRuntime.ComposeFile, "up", "-d" }),
            "the recreate itself still has to happen — only the readiness wait afterwards times out");
    }

    [Fact]
    public async Task No_action_when_the_container_does_not_mount_exactly_the_pinned_volume()
    {
        // The Mac observed in this task's own context: the live container mounts a
        // Compose-project-prefixed volume from a different checkout, not the pinned hall9k-pgdata —
        // recreating it would either collide with that project or orphan its real data, so this is
        // never safe to do automatically or by the printed hand commands either (cycle-1 pre-PR
        // review, adversarial lens, DatabaseDoctor.cs:215).
        RecordingProcessRunner runner = RunningContainerRunner(hostIp: "0.0.0.0", volumes: "dev_hall9k-pgdata", label: PostgresRuntime.ComposeFile);

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(() => DatabaseDoctor.CheckContainerPortBindingAsync(
            assumeYes: true, runner.Runner, NoDaemonRunning, NothingConfigured, CancellationToken.None));

        output.Should().Contain("does not mount exactly the pinned").And.Contain("docs/operations.md");
        output.Should().NotContain("Recreated").And.NotContain("h9k daemon stop");
        runner.Calls.Should().NotContain(call => call.Arguments.Count > 0 && call.Arguments[0] == "compose");
    }

    [Fact]
    public async Task No_action_when_the_config_files_label_names_a_different_file()
    {
        string otherComposeFile = Path.Combine(Path.GetTempPath(), $"other-{Guid.NewGuid():N}", "docker-compose.yml");
        RecordingProcessRunner runner = RunningContainerRunner(hostIp: "0.0.0.0", volumes: "hall9k-pgdata", label: otherComposeFile);

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(() => DatabaseDoctor.CheckContainerPortBindingAsync(
            assumeYes: true, runner.Runner, NoDaemonRunning, NothingConfigured, CancellationToken.None));

        output.Should().Contain("was created from a different compose file").And.Contain("docs/operations.md");
        output.Should().NotContain("Recreated").And.NotContain("h9k daemon stop");
        runner.Calls.Should().NotContain(call => call.Arguments.Count > 0 && call.Arguments[0] == "compose");
    }

    [Fact]
    public async Task No_action_while_a_daemon_is_running()
    {
        RecordingProcessRunner runner = RunningContainerRunner(hostIp: "0.0.0.0", volumes: "hall9k-pgdata", label: PostgresRuntime.ComposeFile);

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(() => DatabaseDoctor.CheckContainerPortBindingAsync(
            assumeYes: true, runner.Runner, DaemonIsRunning, NothingConfigured, CancellationToken.None));

        output.Should().Contain("h9k daemon stop").And.Contain("h9k doctor --yes").And.Contain("h9k daemon start");
        output.Should().NotContain("Recreated");
        runner.Calls.Should().NotContain(call => call.Arguments.Count > 0 && call.Arguments[0] == "compose");
    }

    [Fact]
    public async Task No_action_without_yes_even_when_every_other_guard_condition_holds()
    {
        RecordingProcessRunner runner = RunningContainerRunner(hostIp: "0.0.0.0", volumes: "hall9k-pgdata", label: PostgresRuntime.ComposeFile);

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(() => DatabaseDoctor.CheckContainerPortBindingAsync(
            assumeYes: false, runner.Runner, NoDaemonRunning, NothingConfigured, CancellationToken.None));

        output.Should().Contain("h9k daemon stop").And.Contain("h9k doctor --yes").And.Contain("h9k daemon start");
        output.Should().NotContain("Recreated");
        runner.Calls.Should().NotContain(call => call.Arguments.Count > 0 && call.Arguments[0] == "compose");
    }

    private static RecordingProcessRunner RunningContainerRunner(
        string hostIp, string volumes, string label, Action? onCompose = null)
    {
        RecordingProcessRunner runner = null!;
        runner = new RecordingProcessRunner(() => runner.Calls[^1].Arguments switch
        {
            ["info"] => new(0, string.Empty, string.Empty),
            ["ps", "-a", ..] => new(0, "running\n", string.Empty),
            ["inspect", ..] => new(0, $"{hostIp}|{label}|{volumes} \n", string.Empty),
            ["compose", ..] => RunCompose(onCompose),
            _ => new(1, string.Empty, "unexpected call"),
        });
        return runner;
    }

    private static ProcessResult RunCompose(Action? onCompose)
    {
        onCompose?.Invoke();
        return new(0, string.Empty, string.Empty);
    }
}
