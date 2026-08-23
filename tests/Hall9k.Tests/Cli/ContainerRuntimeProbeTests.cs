using FluentAssertions;
using Hall9k.Cli.Diagnostics;
using Hall9k.Connectors.Processes;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// Question 4 of the doctor check (Decisions Log #73): what a shelled-out <c>docker</c>
/// reports, mapped without a live Docker daemon — the refusal paths (no docker on PATH,
/// docker installed but not running, a stopped hall9k-postgres container) are exactly the
/// ones hardest to arrange for real and the ones a human most needs read correctly.
/// </summary>
// ComposeUpAsync writes PostgresRuntime's compose file under HALL9K_HOME; redirecting it
// to a temp directory here keeps that write off a developer's or CI runner's real home,
// and sharing the collection serializes this against every other HALL9K_HOME redirect.
[Collection("Hall9kHome")]
public sealed class ContainerRuntimeProbeTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), $"h9k-runtime-probe-{Path.GetRandomFileName()}");
    private readonly string? previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    public ContainerRuntimeProbeTests()
    {
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("HALL9K_HOME", home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", previousHome);
        Directory.Delete(home, recursive: true);
    }

    [Fact]
    public async Task Docker_info_succeeding_means_the_runtime_is_running()
    {
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding(string.Empty);

        ContainerRuntimeStatus status = await ContainerRuntimeProbe.RuntimeStatusAsync(runner.Runner, CancellationToken.None);

        status.Should().Be(ContainerRuntimeStatus.Running);
    }

    [Fact]
    public async Task Docker_info_failing_means_installed_but_not_running()
    {
        RecordingProcessRunner runner = RecordingProcessRunner.Failing("Cannot connect to the Docker daemon");

        ContainerRuntimeStatus status = await ContainerRuntimeProbe.RuntimeStatusAsync(runner.Runner, CancellationToken.None);

        status.Should().Be(ContainerRuntimeStatus.NotRunning);
    }

    [Fact]
    public async Task Docker_missing_from_path_is_named_separately_from_not_running()
    {
        RecordingProcessRunner runner = RecordingProcessRunner.Unstartable(
            new System.ComponentModel.Win32Exception("No such file or directory"));

        ContainerRuntimeStatus status = await ContainerRuntimeProbe.RuntimeStatusAsync(runner.Runner, CancellationToken.None);

        status.Should().Be(ContainerRuntimeStatus.NotInstalled,
            "installing Docker and starting Docker Desktop are two different fixes");
    }

    [Fact]
    public async Task No_matching_container_is_absent()
    {
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding(string.Empty);

        PostgresContainerStatus status =
            await ContainerRuntimeProbe.Hall9kContainerStatusAsync(runner.Runner, CancellationToken.None);

        status.Should().Be(PostgresContainerStatus.Absent);
    }

    [Fact]
    public async Task A_container_docker_reports_as_exited_is_stopped()
    {
        // "your database exists, it is just not running" — the one-line fix the doctor check
        // exists to surface (Decisions Log #58).
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding("exited\n");

        PostgresContainerStatus status =
            await ContainerRuntimeProbe.Hall9kContainerStatusAsync(runner.Runner, CancellationToken.None);

        status.Should().Be(PostgresContainerStatus.Stopped);
    }

    [Fact]
    public async Task A_container_docker_reports_as_running_is_running()
    {
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding("running\n");

        PostgresContainerStatus status =
            await ContainerRuntimeProbe.Hall9kContainerStatusAsync(runner.Runner, CancellationToken.None);

        status.Should().Be(PostgresContainerStatus.Running);
    }

    [Fact]
    public async Task Nothing_answers_on_a_closed_local_port()
    {
        // No fixture needed: an ephemeral port nothing is bound to refuses the connection
        // immediately, the same shape as "nothing listening" the reachability check reports.
        bool listening = await ContainerRuntimeProbe.PortListeningAsync("127.0.0.1", 1, CancellationToken.None);

        listening.Should().BeFalse();
    }

    [Fact]
    public async Task A_caller_cancellation_propagates_instead_of_reading_as_not_listening()
    {
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        Func<Task> probe = () => ContainerRuntimeProbe.PortListeningAsync("127.0.0.1", 1, cancelled.Token);

        await probe.Should().ThrowAsync<OperationCanceledException>(
            "a requested cancel must stop the doctor check, not look like a closed port");
    }

    [Fact]
    public async Task Starting_a_stopped_container_is_a_plain_exit_code_check()
    {
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding(string.Empty);

        bool started = await ContainerRuntimeProbe.StartStoppedContainerAsync(runner.Runner, CancellationToken.None);

        started.Should().BeTrue();
        runner.Calls.Should().ContainSingle(call =>
            call.Arguments.Count == 2 && call.Arguments[0] == "start" && call.Arguments[1] == "hall9k-postgres");
    }

    [Fact]
    public async Task Compose_up_fails_honestly_when_docker_refuses()
    {
        RecordingProcessRunner runner = RecordingProcessRunner.Failing("no configuration file provided");

        bool started = await ContainerRuntimeProbe.ComposeUpAsync(runner.Runner, CancellationToken.None);

        started.Should().BeFalse();
    }
}
