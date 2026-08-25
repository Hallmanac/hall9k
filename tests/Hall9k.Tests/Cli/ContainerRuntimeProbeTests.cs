using FluentAssertions;
using Hall9k.Cli.Diagnostics;
using Hall9k.Connectors.Processes;
using Hall9k.Domain.Infrastructure.Storage;
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
    public async Task An_empty_volume_ls_means_the_volume_does_not_exist()
    {
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding(string.Empty);

        bool exists = await ContainerRuntimeProbe.VolumeExistsAsync(runner.Runner, CancellationToken.None);

        exists.Should().BeFalse();
        runner.Calls.Should().ContainSingle(call =>
            call.Arguments.SequenceEqual(new[] { "volume", "ls", "--filter", "name=^hall9k-pgdata$", "--format", "{{.Name}}" }));
    }

    [Fact]
    public async Task A_matching_volume_ls_line_means_the_volume_exists()
    {
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding("hall9k-pgdata\n");

        bool exists = await ContainerRuntimeProbe.VolumeExistsAsync(runner.Runner, CancellationToken.None);

        exists.Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_volume_ls_answers_exists_rather_than_claiming_absence()
    {
        // Empty stdout means the same thing whether docker succeeded and found nothing, or
        // failed outright — this uninstall feature's own pre-PR review found that a purge
        // reads a failed `docker volume ls` as "already gone" and reports destruction that was
        // never observed. Answering true on failure sends the caller on to actually attempt
        // (and honestly fail) the removal instead.
        RecordingProcessRunner runner = RecordingProcessRunner.Failing("Cannot connect to the Docker daemon");

        bool exists = await ContainerRuntimeProbe.VolumeExistsAsync(runner.Runner, CancellationToken.None);

        exists.Should().BeTrue();
    }

    [Fact]
    public async Task Data_volume_name_reads_the_containers_actual_mount()
    {
        // Not the bare PostgresRuntime.VolumeName literal: a container created before this
        // branch's compose name: pin mounts a Compose-project-prefixed volume instead, e.g.
        // postgres_hall9k-pgdata, and purge has to ask the container rather than guess.
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding("postgres_hall9k-pgdata\n");

        (bool confirmed, string? name) = await ContainerRuntimeProbe.DataVolumeNameAsync(runner.Runner, CancellationToken.None);

        confirmed.Should().BeTrue();
        name.Should().Be("postgres_hall9k-pgdata");
        runner.Calls.Should().ContainSingle(call =>
            call.Arguments.Count >= 2 && call.Arguments[0] == "inspect" && call.Arguments[1] == "hall9k-postgres");
    }

    [Fact]
    public async Task Data_volume_name_is_null_for_a_container_with_no_named_volume_mount()
    {
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding(string.Empty);

        (bool confirmed, string? name) = await ContainerRuntimeProbe.DataVolumeNameAsync(runner.Runner, CancellationToken.None);

        confirmed.Should().BeTrue("docker inspect answered — it just reported no named volume mount");
        name.Should().BeNull();
    }

    [Fact]
    public async Task Data_volume_name_is_unconfirmed_rather_than_null_when_docker_inspect_fails()
    {
        // A failed docker inspect (the daemon dropping the connection, a container removed out
        // from under this call) is not the same fact as "this container mounts no named volume"
        // — this uninstall feature's own pre-PR review found a purge reading the two the same
        // way and destroying a container it believed had nothing left to lose.
        RecordingProcessRunner runner = RecordingProcessRunner.Failing("Error: No such object: hall9k-postgres");

        (bool confirmed, string? name) = await ContainerRuntimeProbe.DataVolumeNameAsync(runner.Runner, CancellationToken.None);

        confirmed.Should().BeFalse("docker inspect itself failed — this is not a confirmed absence");
        name.Should().BeNull();
    }

    [Fact]
    public async Task Compose_up_fails_honestly_when_docker_refuses()
    {
        RecordingProcessRunner runner = null!;
        runner = new RecordingProcessRunner(() => runner.Calls[^1].Arguments switch
        {
            ["volume", "ls", ..] => new ProcessResult(0, string.Empty, string.Empty),
            _ => new ProcessResult(1, string.Empty, "no configuration file provided"),
        });

        ComposeUpResult result = await ContainerRuntimeProbe.ComposeUpAsync(runner.Runner, CancellationToken.None);

        result.Should().Be(ComposeUpResult.Failed);
    }

    [Fact]
    public async Task Compose_up_refuses_rather_than_guessing_when_the_legacy_volume_check_itself_fails()
    {
        // VolumeExistsAsync's own fail-safe direction (its remarks): a failed docker volume ls
        // is not the same fact as "no such volume", so it answers true rather than false. That
        // same caution has to survive here — a legacy-volume check that could not actually be
        // answered must not be read as "confirmed absent, safe to create a fresh volume".
        RecordingProcessRunner runner = RecordingProcessRunner.Failing("Cannot connect to the Docker daemon");

        ComposeUpResult result = await ContainerRuntimeProbe.ComposeUpAsync(runner.Runner, CancellationToken.None);

        result.Should().Be(ComposeUpResult.LegacyVolumeDetected);
    }

    [Fact]
    public async Task Compose_up_refuses_rather_than_creating_a_fresh_volume_beside_a_legacy_one()
    {
        // The one call site that would otherwise silently create a new, empty, pinned
        // hall9k-pgdata volume while a pre-pin install's real data sits untouched under the
        // Compose-project-prefixed name docs/operations.md's Provisioning section describes —
        // this uninstall feature's own pre-PR review found nothing detecting that transition.
        RecordingProcessRunner runner = new(() => new ProcessResult(0, "postgres_hall9k-pgdata\n", string.Empty));

        ComposeUpResult result = await ContainerRuntimeProbe.ComposeUpAsync(runner.Runner, CancellationToken.None);

        result.Should().Be(ComposeUpResult.LegacyVolumeDetected);
        runner.Calls.Should().NotContain(call => call.Arguments.Count > 0 && call.Arguments[0] == "compose",
            "a legacy volume was detected — nothing should be brought up until it is migrated by hand");
    }

    [Fact]
    public async Task Compose_up_starts_normally_when_no_legacy_volume_exists()
    {
        RecordingProcessRunner runner = new(() => new ProcessResult(0, string.Empty, string.Empty));

        ComposeUpResult result = await ContainerRuntimeProbe.ComposeUpAsync(runner.Runner, CancellationToken.None);

        result.Should().Be(ComposeUpResult.Started);
        runner.Calls.Should().Contain(call => call.Arguments.Count > 0 && call.Arguments[0] == "compose");
    }

    [Fact]
    public void The_compose_file_pins_the_volume_to_its_literal_name()
    {
        // Without an explicit name:, Compose prefixes an unnamed volume with its own notion of
        // the project name (the invoking working directory's basename by default), so the
        // volume h9k uninstall --purge-data actually has to remove would not be the bare
        // PostgresRuntime.VolumeName this file names in its own docker volume rm. Origin
        // incident: this uninstall feature's own pre-PR review found purge silently failing to
        // remove the real volume for exactly this reason.
        PostgresRuntime.ComposeFileContents.Should().Contain($"name: {PostgresRuntime.VolumeName}");
    }

    [Fact]
    public void The_repositorys_own_compose_file_pins_the_volume_too()
    {
        // PostgresRuntime.ComposeFileContents's own docstring says it mirrors this file, kept
        // in sync by hand — this branch's name: pin landed in the shipped constant without
        // landing here too, so a contributor running docker compose up -d from a checkout
        // (AGENTS.md's documented manual path) got an unpinned, Compose-project-prefixed
        // volume name that h9k uninstall --purge-data could never find by the bare literal.
        string contents = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "docker-compose.yml"));

        contents.Should().Contain($"name: {PostgresRuntime.VolumeName}");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Hall9k.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException($"No Hall9k.slnx found above {AppContext.BaseDirectory}.");
    }
}
