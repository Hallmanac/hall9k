using FluentAssertions;
using Hall9k.Cli.Diagnostics;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The exact sequence field reports (2026-09-27, Mac and Windows, transport note 4fa918ac) showed
/// breaking <c>h9k doctor --yes</c> (and, through it, <c>h9k update --restart</c>'s hand-off): the
/// guarded recreate (<see cref="DatabaseDoctor.CheckContainerPortBindingAsync(bool,Hall9k.Connectors.Processes.ProcessRunner,Func{bool},CancellationToken)"/>,
/// covered separately by <c>DatabaseDoctorPortBindingTests</c>) waits for one clean answer and
/// reports "Recreated", and the very next reachability probe — a fresh connection, moments later,
/// from the doctor's own next question (<see cref="DatabaseDoctor.DiagnoseRefusedConnectionAsync"/>)
/// — used to fail outright on a single refused sample instead of giving the still-finishing-startup
/// container the same benefit of the doubt the recreate's own wait already gets. These tests drive
/// <see cref="DatabaseDoctor.DiagnoseRefusedConnectionAsync"/> directly with an already-refused
/// sample, rather than running the recreate first, since the recreate step itself contributes
/// nothing this method's own retry behaviour needs and is already proven elsewhere. No real
/// container and no real Postgres: a fake probe function stands in for
/// <see cref="DatabaseReachability.ProbeAsync"/>. The first test lets the poll interval run on the
/// real clock (it recovers on its very first retry, so the real elapsed time is negligible); the
/// second forces the timeout deterministically with a <see cref="SteppingClock"/> so it cannot flake
/// on a slow or loaded runner — <c>Task.Delay(pollInterval, cancellationToken)</c> inside the poll
/// still sleeps for real between reads of that clock, the same as the existing
/// <c>DatabaseDoctorReadinessTests</c> pattern this follows.
/// </summary>
public sealed class DatabaseDoctorRecreateThenReachabilityTests : IDisposable
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ShortPollInterval = TimeSpan.FromMilliseconds(20);

    private readonly ScopedTestHome scopedHome = new();

    public void Dispose() => scopedHome.Dispose();

    [Fact]
    public async Task A_container_that_refuses_once_and_then_recovers_is_retried_until_reachable()
    {
        // Models the field report literally: the doctor's own probe against the freshly recreated
        // container refuses ("Exception while reading from stream" — Postgres transiently dropping
        // a connection while it finishes starting), and only the retry this task adds actually
        // proves it ready.
        int calls = 0;
        Task<ReachabilityReport> Probe(CancellationToken token)
        {
            calls++;
            return Task.FromResult(calls == 1 ? RefusedConnection() : Reachable());
        }

        RecordingProcessRunner runner = ConfirmedRunningContainerRunner();
        ConnectionStringResolution resolution = ConfiguredResolution();

        ReachabilityReport result = null!;
        string checkOutput = await ScopedAnsiConsoleCapture.CaptureAsync(async () =>
        {
            result = await DatabaseDoctor.DiagnoseRefusedConnectionAsync(
                RefusedConnection(), resolution, Hall9kDatabase.DefaultConnectionString, offerFixes: true,
                assumeYes: true, runner.Runner, Probe, ShortTimeout, ShortPollInterval, TimeProvider.System,
                CancellationToken.None);
        });

        result.Status.Should().Be(ReachabilityStatus.Reachable,
            "the retry has to actually recover from the one transient drop, not just report it");
        calls.Should().Be(2, "the retry's own first probe, and exactly one more before it settles — no more");
        checkOutput.Should().Contain("Retrying for up to",
            "the wait it tried has to be named, not silently swallowed");
        checkOutput.Should().NotContain("Is Postgres running?",
            "the generic give-up message is for a container that was never confirmed Running at all");
    }

    [Fact]
    public async Task A_container_that_never_becomes_reachable_fails_loudly_once_the_bound_expires()
    {
        // The other shape the same defect could take (raised alongside the transient-drop case
        // above): whatever the recreate's own readiness probe reached is not what every later probe
        // reaches — for instance a "Host=localhost" that keeps resolving to ::1, with nothing
        // published there, while the container only ever answers on 127.0.0.1. Either way, once the
        // bound below is exhausted this has to fail loudly and name the wait it tried, not hang or
        // silently report success.
        Task<ReachabilityReport> Probe(CancellationToken token) => Task.FromResult(RefusedConnection());

        RecordingProcessRunner runner = ConfirmedRunningContainerRunner();
        ConnectionStringResolution resolution = ConfiguredResolution();
        SteppingClock clock = new(ShortPollInterval);

        ReachabilityReport result = null!;
        string checkOutput = await ScopedAnsiConsoleCapture.CaptureAsync(async () =>
        {
            result = await DatabaseDoctor.DiagnoseRefusedConnectionAsync(
                RefusedConnection(), resolution, Hall9kDatabase.DefaultConnectionString, offerFixes: true,
                assumeYes: true, runner.Runner, Probe, ShortTimeout, ShortPollInterval, clock, CancellationToken.None);
        });

        result.Status.Should().Be(ReachabilityStatus.RefusedConnection,
            "a container that never actually becomes reachable has to be reported that way, not upgraded to success");
        checkOutput.Should().Contain($"waiting up to {ShortTimeout.TotalSeconds:0}s",
            "AC-3's rule applies to this wait too: naming the exact bound it tried, not just failing silently");
        checkOutput.Should().NotContain("Is Postgres running? Start it, then try again.",
            "the container was confirmed Running the whole time — the generic never-started advice does not fit here");
    }

    /// <summary>
    /// A confirmed-Running <c>hall9k-postgres</c> — enough for the docker probes
    /// <see cref="DatabaseDoctor.DiagnoseRefusedConnectionAsync"/> makes on its own
    /// (<c>docker info</c>, <c>docker ps -a</c>) to confirm the container by name. The recreate
    /// flow's own richer shape (port binding, mounted volume, compose label) lives in
    /// <c>DatabaseDoctorPortBindingTests.RunningContainerRunner</c> instead, since nothing here
    /// exercises the recreate itself.
    /// </summary>
    private static RecordingProcessRunner ConfirmedRunningContainerRunner()
    {
        RecordingProcessRunner runner = null!;
        runner = new RecordingProcessRunner(() => runner.Calls[^1].Arguments switch
        {
            ["info"] => new(0, string.Empty, string.Empty),
            ["ps", "-a", ..] => new(0, "running\n", string.Empty),
            _ => new(1, string.Empty, "unexpected call"),
        });
        return runner;
    }

    private static ReachabilityReport Reachable() =>
        new(ReachabilityStatus.Reachable, string.Empty, "localhost", 5432, "hall9k");

    private static ReachabilityReport RefusedConnection() =>
        new(ReachabilityStatus.RefusedConnection, "Exception while reading from stream", "localhost", 5432, "hall9k");

    private static ConnectionStringResolution ConfiguredResolution() =>
        new(Hall9kDatabase.DefaultConnectionString, ConnectionStringOrigin.PlatformConfigFile, Hall9kDatabase.ConfigFile);
}
