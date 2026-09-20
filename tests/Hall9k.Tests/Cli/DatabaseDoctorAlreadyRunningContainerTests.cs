using FluentAssertions;
using Hall9k.Cli.Diagnostics;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The not-configured path's other fix (<c>DatabaseDoctor.OfferAndRecordAlreadyRunningContainerAsync</c>):
/// a confirmed-<c>Running</c> <c>hall9k-postgres</c> has nothing to start, only something to
/// point at, and used to dead-end on "Set one: export …" advice because
/// <c>OfferAndStartAsync</c> refuses the already-running case outright (the pre-existing defect
/// this class exercises the fix for, originally at <c>DatabaseDoctor.OfferAndStartAsync</c>). A
/// fake probe stands in for
/// <see cref="DatabaseReachability.ProbeAsync"/> so this exercises the reachable and unreachable
/// cases without depending on a real Postgres bound to the exact host and port
/// <see cref="Hall9kDatabase.DefaultConnectionString"/> names — the same seam
/// <c>DatabaseDoctorReadinessTests</c> already uses for the readiness poll.
/// </summary>
// This class still clears HALL9K_CONNECTION_STRING directly rather than through
// ScopedConnectionString: clearing matters here specifically, since it outranks the platform
// config file this class writes to and reads back from, and ScopedConnectionString can only
// redirect to a specific value, not force "nothing configured" ahead of whatever the ambient
// environment names (Decisions Log PLACEHOLDER-98484f36). That literal write is what keeps this
// class in the one serial collection left; HALL9K_HOME is redirected through ScopedTestHome like
// everywhere else.
[Collection("Environment")]
[Trait("Category", "Environment")]
public sealed class DatabaseDoctorAlreadyRunningContainerTests : IDisposable
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ShortPollInterval = TimeSpan.FromMilliseconds(20);

    private readonly ScopedTestHome scopedHome = new();

    private readonly string? previousConnectionString =
        Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);

    public DatabaseDoctorAlreadyRunningContainerTests() =>
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, null);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previousConnectionString);
        scopedHome.Dispose();
    }

    [Fact]
    public async Task An_unreachable_default_connection_string_records_nothing()
    {
        ConnectionStringResolution? resolution = await DatabaseDoctor.OfferAndRecordAlreadyRunningContainerAsync(
            assumeYes: true, _ => Task.FromResult(RefusedConnection()), ShortTimeout, ShortPollInterval, TimeProvider.System, CancellationToken.None);

        resolution.Should().BeNull("the container is confirmed Running but nothing actually answered at the default address");
        File.Exists(Hall9kDatabase.ConfigFile).Should().BeFalse("nothing reachable means nothing to record");
    }

    [Fact]
    public async Task A_container_still_initialising_is_waited_for_rather_than_reported_unreachable_immediately()
    {
        // A freshly-started container can report Running before Postgres inside it has finished
        // initialising — the sibling OfferAndStartAsync path already waits for this; this
        // exercises the same wait added here (cycle-1 conformance and adversarial review
        // findings, DatabaseDoctor.cs:361). A clock that advances one poll interval per read,
        // not the wall clock, so the third probe's readiness must land inside the timeout
        // regardless of how slow the runner actually is — the same reason
        // DatabaseDoctorReadinessTests.A_slow_container_that_becomes_ready_before_the_timeout_is_caught
        // uses SteppingClock rather than TimeProvider.System (origin incident: raced the real
        // clock and failed on a loaded windows-latest CI runner, GitHub Actions run 32897678640).
        int calls = 0;
        Task<ReachabilityReport> Probe(CancellationToken token)
        {
            calls++;
            return Task.FromResult(calls < 3 ? RefusedConnection() : Reachable());
        }

        SteppingClock clock = new(ShortPollInterval);

        ConnectionStringResolution? resolution = await DatabaseDoctor.OfferAndRecordAlreadyRunningContainerAsync(
            assumeYes: true, Probe, ShortTimeout, ShortPollInterval, clock, CancellationToken.None);

        resolution.Should().NotBeNull("readiness that arrives on the third probe is still well inside the timeout");
        calls.Should().Be(3, "the loop must keep polling — a slow start is not the same as a dead one");
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
        ConnectionStringResolution? resolution = null;
        string output = await ScopedAnsiConsoleCapture.CaptureAsync(async () =>
        {
            resolution = await DatabaseDoctor.OfferAndRecordAlreadyRunningContainerAsync(
                assumeYes: false, _ => Task.FromResult(Reachable()), CancellationToken.None);
        });

        resolution.Should().BeNull("nobody was there to confirm it, and assumeYes was not set");
        File.Exists(Hall9kDatabase.ConfigFile).Should().BeFalse("a skipped offer must never write anything");
        output.Should().Contain("h9k doctor --yes",
            "a skipped prompt has to name the exact flag that answers it, not just advise trying again");
    }

    [Fact]
    public async Task The_full_doctor_run_never_asks_docker_to_start_an_already_running_container()
    {
        // "docker info" (Running) and "docker ps -a" both read the same fixed stdout;
        // "running" is what a confirmed-Running hall9k-postgres reports. This exercises
        // DiagnoseNotConfiguredAsync's own routing decision — not RunAsync's full path, which
        // would also reach CheckReachabilityAndSchemaAsync's own unfaked probe of whatever
        // connection string got recorded — with the same faked already-running-container probe
        // the three tests above already use, so this never depends on a real Postgres bound to
        // the exact host and port DefaultConnectionString names.
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding("running\n");

        ConnectionStringResolution resolution = await DatabaseDoctor.DiagnoseNotConfiguredAsync(
            offerFixes: true, assumeYes: true, runner.Runner, _ => Task.FromResult(Reachable()), CancellationToken.None);

        runner.Calls.Should().NotContain(
            call => call.Arguments.Count > 0
                && (call.Arguments[0] == "start" || call.Arguments[0] == "volume" || call.Arguments[0] == "compose"),
            "a container already confirmed Running is never the one to restart or bring up — the fix here is "
            + "probing and recording the connection string directly, never another docker mutation");
        resolution.Value.Should().Be(Hall9kDatabase.DefaultConnectionString,
            "the routing has to actually reach OfferAndRecordAlreadyRunningContainerAsync and record the "
            + "connection string, not merely avoid a docker mutation — reverting the routing fix would leave "
            + "this unconfigured instead");
        Hall9kDatabase.ConnectionStringStateAndValueInConfigFile().Value.Should().Be(Hall9kDatabase.DefaultConnectionString);
    }

    private static ReachabilityReport Reachable() =>
        new(ReachabilityStatus.Reachable, string.Empty, "localhost", 5432, "hall9k");

    private static ReachabilityReport RefusedConnection() =>
        new(ReachabilityStatus.RefusedConnection, "nothing listening", "localhost", 5432, "hall9k");
}
