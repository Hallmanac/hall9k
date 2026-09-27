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
/// <c>DatabaseDoctorAlreadyRunningContainerTests</c> already uses for the readiness poll.
/// </summary>
public sealed class DatabaseDoctorPortBindingTests : IDisposable
{
    private readonly ScopedTestHome scopedHome = new();

    public void Dispose() => scopedHome.Dispose();

    private static Func<bool> NoDaemonRunning => () => false;

    private static Func<bool> DaemonIsRunning => () => true;

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
        RecordingProcessRunner runner = RunningContainerRunner(hostIp: "0.0.0.0", volumes: "hall9k-pgdata", label: PostgresRuntime.ComposeFile);

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(() => DatabaseDoctor.CheckContainerPortBindingAsync(
            assumeYes: true, runner.Runner, NoDaemonRunning, CancellationToken.None));

        output.Should().Contain("Recreated");
        runner.Calls.Should().ContainSingle(call =>
            call.Arguments.SequenceEqual(new[] { "compose", "-f", PostgresRuntime.ComposeFile, "up", "-d" }));
        runner.Calls.Should().NotContain(call =>
            call.Arguments.Count > 0 && call.Arguments[0] == "start", "a recreate, never docker start, is what actually rolls the binding forward");
    }

    [Fact]
    public async Task A_stopped_container_with_drift_is_recreated_the_same_way_never_docker_started()
    {
        RecordingProcessRunner runner = RunningContainerRunner(
            hostIp: "0.0.0.0", volumes: "hall9k-pgdata", label: PostgresRuntime.ComposeFile, containerState: "exited");

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(() => DatabaseDoctor.CheckContainerPortBindingAsync(
            assumeYes: true, runner.Runner, NoDaemonRunning, CancellationToken.None));

        output.Should().Contain("Recreated");
        runner.Calls.Should().ContainSingle(call => call.Arguments.Count > 0 && call.Arguments[0] == "compose");
        runner.Calls.Should().NotContain(call =>
            call.Arguments.Count > 0 && call.Arguments[0] == "start",
            "starting a stopped container with the old binding would keep publishing on every interface");
    }

    [Fact]
    public async Task No_action_when_the_container_does_not_mount_exactly_the_pinned_volume()
    {
        // The Mac observed in this task's own context: the live container mounts a
        // Compose-project-prefixed volume from a different checkout, not the pinned hall9k-pgdata —
        // recreating it would either collide with that project or orphan its real data.
        RecordingProcessRunner runner = RunningContainerRunner(hostIp: "0.0.0.0", volumes: "dev_hall9k-pgdata", label: PostgresRuntime.ComposeFile);

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(() => DatabaseDoctor.CheckContainerPortBindingAsync(
            assumeYes: true, runner.Runner, NoDaemonRunning, CancellationToken.None));

        output.Should().Contain("h9k daemon stop").And.Contain("h9k daemon start");
        output.Should().NotContain("Recreated");
        runner.Calls.Should().NotContain(call => call.Arguments.Count > 0 && call.Arguments[0] == "compose");
    }

    [Fact]
    public async Task No_action_when_the_config_files_label_names_a_different_file()
    {
        string otherComposeFile = Path.Combine(Path.GetTempPath(), $"other-{Guid.NewGuid():N}", "docker-compose.yml");
        RecordingProcessRunner runner = RunningContainerRunner(hostIp: "0.0.0.0", volumes: "hall9k-pgdata", label: otherComposeFile);

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(() => DatabaseDoctor.CheckContainerPortBindingAsync(
            assumeYes: true, runner.Runner, NoDaemonRunning, CancellationToken.None));

        output.Should().Contain("h9k daemon stop").And.Contain("h9k daemon start");
        output.Should().NotContain("Recreated");
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

    private static RecordingProcessRunner RunningContainerRunner(
        string hostIp, string volumes, string label, string containerState = "running")
    {
        RecordingProcessRunner runner = null!;
        runner = new RecordingProcessRunner(() => runner.Calls[^1].Arguments switch
        {
            ["info"] => new(0, string.Empty, string.Empty),
            ["ps", "-a", ..] => new(0, $"{containerState}\n", string.Empty),
            ["inspect", ..] => new(0, $"{hostIp}|{label}|{volumes} \n", string.Empty),
            ["compose", ..] => new(0, string.Empty, string.Empty),
            _ => new(1, string.Empty, "unexpected call"),
        });
        return runner;
    }
}
