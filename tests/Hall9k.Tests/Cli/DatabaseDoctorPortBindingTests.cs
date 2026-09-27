using FluentAssertions;
using Hall9k.Cli.Diagnostics;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="DatabaseDoctor.CheckContainerPortBindingAsync(bool,System.Threading.CancellationToken)"/>
/// (security review idea 6be68ee2, secrets-files-network finding 1): the doctor always rewrites
/// the compose file from the shipped constant, then reports and — only under <c>--yes</c> and only
/// once every guard condition holds — recreates <c>hall9k-postgres</c> when it is not publishing
/// port 5432 on <c>127.0.0.1</c> alone. A fake daemon-running probe stands in for
/// <see cref="Hall9k.Cli.DaemonControl.DaemonProcess.Probe"/>, the same seam
/// <c>DatabaseDoctorAlreadyRunningContainerTests</c> already uses for the readiness poll — and,
/// for a successful recreate, so does the readiness probe itself, so this never depends on a real
/// Postgres answering at <see cref="Hall9kDatabase.DefaultConnectionString"/> within the real 30s
/// timeout.
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

    [Fact]
    public async Task The_compose_file_is_rewritten_from_the_shipped_constant_on_every_run()
    {
        // No container runtime at all — nothing left to inspect — but the rewrite has to happen
        // before that early return, on every path, per this task's own acceptance criteria.
        RecordingProcessRunner runner = RecordingProcessRunner.Failing("Cannot connect to the Docker daemon");

        await DatabaseDoctor.CheckContainerPortBindingAsync(
            assumeYes: false, runner.Runner, NoDaemonRunning, CancellationToken.None);

        File.Exists(PostgresRuntime.ComposeFile).Should().BeTrue();
        File.ReadAllText(PostgresRuntime.ComposeFile).Should().Be(PostgresRuntime.ComposeFileContents);
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
            assumeYes: true, runner.Runner, NoDaemonRunning, CancellationToken.None));

        output.Should().BeEmpty("no container exists yet, so there is nothing to report drift about");
        runner.Calls.Should().NotContain(call => call.Arguments.Count > 0 && call.Arguments[0] == "inspect");
    }

    [Fact]
    public async Task A_loopback_only_binding_is_not_reported_as_drift()
    {
        RecordingProcessRunner runner = RunningContainerRunner(hostIp: "127.0.0.1", volumes: "hall9k-pgdata", label: PostgresRuntime.ComposeFile);

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(() => DatabaseDoctor.CheckContainerPortBindingAsync(
            assumeYes: true, runner.Runner, NoDaemonRunning, CancellationToken.None));

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
            assumeYes: true, runner.Runner, NoDaemonRunning, AlwaysReachable, ShortTimeout, ShortPollInterval,
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
            assumeYes: true, runner.Runner, NoDaemonRunning, neverReachable, ShortTimeout, ShortPollInterval,
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
            assumeYes: true, runner.Runner, NoDaemonRunning, CancellationToken.None));

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
            assumeYes: true, runner.Runner, NoDaemonRunning, CancellationToken.None));

        output.Should().Contain("was created from a different compose file").And.Contain("docs/operations.md");
        output.Should().NotContain("Recreated").And.NotContain("h9k daemon stop");
        runner.Calls.Should().NotContain(call => call.Arguments.Count > 0 && call.Arguments[0] == "compose");
    }

    [Fact]
    public async Task No_action_while_a_daemon_is_running()
    {
        RecordingProcessRunner runner = RunningContainerRunner(hostIp: "0.0.0.0", volumes: "hall9k-pgdata", label: PostgresRuntime.ComposeFile);

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(() => DatabaseDoctor.CheckContainerPortBindingAsync(
            assumeYes: true, runner.Runner, DaemonIsRunning, CancellationToken.None));

        output.Should().Contain("h9k daemon stop").And.Contain("h9k daemon start");
        output.Should().NotContain("Recreated");
        runner.Calls.Should().NotContain(call => call.Arguments.Count > 0 && call.Arguments[0] == "compose");
    }

    [Fact]
    public async Task No_action_without_yes_even_when_every_other_guard_condition_holds()
    {
        RecordingProcessRunner runner = RunningContainerRunner(hostIp: "0.0.0.0", volumes: "hall9k-pgdata", label: PostgresRuntime.ComposeFile);

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(() => DatabaseDoctor.CheckContainerPortBindingAsync(
            assumeYes: false, runner.Runner, NoDaemonRunning, CancellationToken.None));

        output.Should().Contain("h9k daemon stop").And.Contain("h9k daemon start");
        output.Should().NotContain("Recreated");
        runner.Calls.Should().NotContain(call => call.Arguments.Count > 0 && call.Arguments[0] == "compose");
    }

    private static RecordingProcessRunner RunningContainerRunner(string hostIp, string volumes, string label)
    {
        RecordingProcessRunner runner = null!;
        runner = new RecordingProcessRunner(() => runner.Calls[^1].Arguments switch
        {
            ["info"] => new(0, string.Empty, string.Empty),
            ["ps", "-a", ..] => new(0, "running\n", string.Empty),
            ["inspect", ..] => new(0, $"{hostIp}|{label}|{volumes} \n", string.Empty),
            ["compose", ..] => new(0, string.Empty, string.Empty),
            _ => new(1, string.Empty, "unexpected call"),
        });
        return runner;
    }
}
