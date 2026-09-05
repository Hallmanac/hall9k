using FluentAssertions;
using Hall9k.Cli.Diagnostics;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The not-configured path's other fix (<c>DatabaseDoctor.OfferAndRecordAlreadyRunningContainerAsync</c>):
/// a confirmed-<c>Running</c> <c>hall9k-postgres</c> has nothing to start, only something to
/// point at, and used to dead-end on "Set one: export …" advice because
/// <c>OfferAndStartAsync</c> refuses the already-running case outright (Decisions Log finding on
/// <c>DatabaseDoctor.cs:309</c>). A fake probe stands in for
/// <see cref="DatabaseReachability.ProbeAsync"/> so this exercises the reachable and unreachable
/// cases without depending on a real Postgres bound to the exact host and port
/// <see cref="Hall9kDatabase.DefaultConnectionString"/> names — the same seam
/// <c>DatabaseDoctorReadinessTests</c> already uses for the readiness poll.
/// </summary>
// Writes the platform config file under HALL9K_HOME when it records a connection string;
// sharing the collection serializes this against every other test that redirects the same
// environment.
[Collection("Hall9kHome")]
public sealed class DatabaseDoctorAlreadyRunningContainerTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), $"h9k-doctor-already-running-{Path.GetRandomFileName()}");
    private readonly string? previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    public DatabaseDoctorAlreadyRunningContainerTests()
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
    public async Task An_unreachable_default_connection_string_records_nothing()
    {
        ConnectionStringResolution? resolution = await DatabaseDoctor.OfferAndRecordAlreadyRunningContainerAsync(
            assumeYes: true, _ => Task.FromResult(RefusedConnection()), CancellationToken.None);

        resolution.Should().BeNull("the container is confirmed Running but nothing actually answered at the default address");
        File.Exists(Hall9kDatabase.ConfigFile).Should().BeFalse("nothing reachable means nothing to record");
    }

    [Fact]
    public async Task Assume_yes_records_the_default_connection_string_once_it_answers()
    {
        ConnectionStringResolution? resolution = await DatabaseDoctor.OfferAndRecordAlreadyRunningContainerAsync(
            assumeYes: true, _ => Task.FromResult(Reachable()), CancellationToken.None);

        resolution.Should().NotBeNull(
            "a confirmed Running container that actually answers is exactly the case OfferAndStartAsync refuses to touch — "
            + "this is the other half that has to record it instead");
        resolution!.Value.Should().Be(Hall9kDatabase.DefaultConnectionString);
        Hall9kDatabase.ConnectionStringStateAndValueInConfigFile().Value.Should().Be(Hall9kDatabase.DefaultConnectionString);
    }

    [Fact]
    public async Task A_non_interactive_session_without_yes_skips_recording_and_names_the_flag()
    {
        // This test process is never an interactive console (DatabaseDoctorNotConfiguredTests'
        // own tests already establish that), so this exercises the same skip-and-name-the-flag
        // rule the start and schema offers already use rather than hanging on a prompt.
        ConnectionStringResolution? resolution = await DatabaseDoctor.OfferAndRecordAlreadyRunningContainerAsync(
            assumeYes: false, _ => Task.FromResult(Reachable()), CancellationToken.None);

        resolution.Should().BeNull("nobody was there to confirm it, and assumeYes was not set");
        File.Exists(Hall9kDatabase.ConfigFile).Should().BeFalse("a skipped offer must never write anything");
    }

    [Fact]
    public async Task The_full_doctor_run_never_asks_docker_to_start_an_already_running_container()
    {
        // "docker info" (Running) and "docker ps -a" both read the same fixed stdout;
        // "running" is what a confirmed-Running hall9k-postgres reports.
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding("running\n");

        await DatabaseDoctor.RunAsync(offerFixes: true, assumeYes: true, runner.Runner, CancellationToken.None);

        runner.Calls.Should().NotContain(
            call => call.Arguments.Count > 0
                && (call.Arguments[0] == "start" || call.Arguments[0] == "volume" || call.Arguments[0] == "compose"),
            "a container already confirmed Running is never the one to restart or bring up — the fix here is "
            + "probing and recording the connection string directly, never another docker mutation");
    }

    private static ReachabilityReport Reachable() =>
        new(ReachabilityStatus.Reachable, string.Empty, "localhost", 5432, "hall9k");

    private static ReachabilityReport RefusedConnection() =>
        new(ReachabilityStatus.RefusedConnection, "nothing listening", "localhost", 5432, "hall9k");
}
