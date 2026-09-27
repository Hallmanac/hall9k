using FluentAssertions;
using Hall9k.Cli.Diagnostics;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The exact sequence field reports (2026-09-27, Mac and Windows, transport note 4fa918ac) showed
/// breaking <c>h9k doctor --yes</c> (and, through it, <c>h9k update --restart</c>'s hand-off): the
/// guarded recreate (<see cref="DatabaseDoctor.CheckContainerPortBindingAsync(bool,ProcessRunner,Func{bool},CancellationToken)"/>)
/// waits for one clean answer and reports "Recreated", and the very next reachability probe — a
/// fresh connection, moments later, from the doctor's own next question
/// (<see cref="DatabaseDoctor.DiagnoseRefusedConnectionAsync"/>) — used to fail outright on a
/// single refused sample instead of giving the still-finishing-startup container the same benefit
/// of the doubt the recreate's own wait already gets. No real container, no real Postgres and no
/// real sleep anywhere here: a fake probe function stands in for
/// <see cref="DatabaseReachability.ProbeAsync"/>, and a shrunk timeout/poll interval (with the real
/// clock, since neither test needs to force a timeout deterministically) keeps every case fast.
/// </summary>
public sealed class DatabaseDoctorRecreateThenReachabilityTests : IDisposable
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ShortPollInterval = TimeSpan.FromMilliseconds(20);

    private readonly ScopedTestHome scopedHome = new();

    public void Dispose() => scopedHome.Dispose();

    private static Func<bool> NoDaemonRunning => () => false;

    [Fact]
    public async Task A_container_that_answers_once_during_the_recreate_wait_and_then_drops_is_retried_until_it_settles()
    {
        // Models the field report literally: the recreate's own readiness probe gets exactly one
        // clean answer (call 1), the doctor's own next probe against the same address refuses
        // right afterward (call 2, "Exception while reading from stream" — Postgres transiently
        // dropping a connection while it finishes starting), and only the retry this task adds
        // (call 3) actually proves it ready.
        int calls = 0;
        Task<ReachabilityReport> Probe(CancellationToken token)
        {
            calls++;
            return Task.FromResult(calls == 2 ? RefusedConnection() : Reachable());
        }

        RecordingProcessRunner runner = RunningContainerRunner();

        string recreateOutput = await ScopedAnsiConsoleCapture.CaptureAsync(() => DatabaseDoctor.CheckContainerPortBindingAsync(
            assumeYes: true, runner.Runner, NoDaemonRunning, Probe, ShortTimeout, ShortPollInterval, TimeProvider.System,
            CancellationToken.None));
        recreateOutput.Should().Contain("Recreated", "the guarded recreate itself must still succeed before this sequence means anything");

        ReachabilityReport initialProbe = await Probe(CancellationToken.None);
        ConnectionStringResolution resolution = ConfiguredResolution();

        ReachabilityReport result = null!;
        string checkOutput = await ScopedAnsiConsoleCapture.CaptureAsync(async () =>
        {
            result = await DatabaseDoctor.DiagnoseRefusedConnectionAsync(
                initialProbe, resolution, Hall9kDatabase.DefaultConnectionString, offerFixes: true, assumeYes: true,
                runner.Runner, Probe, ShortTimeout, ShortPollInterval, TimeProvider.System, CancellationToken.None);
        });

        result.Status.Should().Be(ReachabilityStatus.Reachable,
            "the retry has to actually recover from the one transient drop, not just report it");
        calls.Should().Be(3, "recreate's own wait, the doctor's first probe, and exactly one retry — no more");
        checkOutput.Should().Contain("Retrying for up to",
            "the wait it tried has to be named, not silently swallowed");
        checkOutput.Should().NotContain("Is Postgres running?",
            "the generic give-up message is for a container that was never confirmed Running at all");
    }

    [Fact]
    public async Task A_container_that_never_becomes_reachable_after_the_recreate_fails_loudly_once_the_bound_expires()
    {
        // The other shape the same defect could take (raised alongside the transient-drop case
        // above): whatever the recreate's own readiness probe reached (call 1) is not what every
        // later probe reaches — for instance a "Host=localhost" that keeps resolving to ::1, with
        // nothing published there, while the container only ever answers on 127.0.0.1 the recreate
        // happened to reach once. Either way, once the bound below is exhausted this has to fail
        // loudly and name the wait it tried, not hang or silently report success.
        int calls = 0;
        Task<ReachabilityReport> Probe(CancellationToken token)
        {
            calls++;
            return Task.FromResult(calls == 1 ? Reachable() : RefusedConnection());
        }

        RecordingProcessRunner runner = RunningContainerRunner();

        string recreateOutput = await ScopedAnsiConsoleCapture.CaptureAsync(() => DatabaseDoctor.CheckContainerPortBindingAsync(
            assumeYes: true, runner.Runner, NoDaemonRunning, Probe, ShortTimeout, ShortPollInterval, TimeProvider.System,
            CancellationToken.None));
        recreateOutput.Should().Contain("Recreated");

        ReachabilityReport initialProbe = await Probe(CancellationToken.None);
        ConnectionStringResolution resolution = ConfiguredResolution();
        SteppingClock clock = new(ShortPollInterval);

        ReachabilityReport result = null!;
        string checkOutput = await ScopedAnsiConsoleCapture.CaptureAsync(async () =>
        {
            result = await DatabaseDoctor.DiagnoseRefusedConnectionAsync(
                initialProbe, resolution, Hall9kDatabase.DefaultConnectionString, offerFixes: true, assumeYes: true,
                runner.Runner, Probe, ShortTimeout, ShortPollInterval, clock, CancellationToken.None);
        });

        result.Status.Should().Be(ReachabilityStatus.RefusedConnection,
            "a container that never actually becomes reachable has to be reported that way, not upgraded to success");
        checkOutput.Should().Contain($"waiting up to {ShortTimeout.TotalSeconds:0}s",
            "AC-3's rule applies to this wait too: naming the exact bound it tried, not just failing silently");
        checkOutput.Should().NotContain("Is Postgres running? Start it, then try again.",
            "the container was confirmed Running the whole time — the generic never-started advice does not fit here");
    }

    /// <summary>
    /// A confirmed-Running <c>hall9k-postgres</c>, drifted onto <c>0.0.0.0</c>, mounting exactly
    /// the pinned volume and created from this install's own compose file — the same shape
    /// <c>DatabaseDoctorPortBindingTests.RunningContainerRunner</c> uses, so <c>--yes</c>'s guard
    /// conditions all hold and the recreate actually runs, and so the same "confirmed Running"
    /// docker answers still hold for the reachability check that follows it.
    /// </summary>
    private static RecordingProcessRunner RunningContainerRunner()
    {
        RecordingProcessRunner runner = null!;
        runner = new RecordingProcessRunner(() => runner.Calls[^1].Arguments switch
        {
            ["info"] => new(0, string.Empty, string.Empty),
            ["ps", "-a", ..] => new(0, "running\n", string.Empty),
            ["inspect", ..] => new(0, $"0.0.0.0|{PostgresRuntime.ComposeFile}|hall9k-pgdata \n", string.Empty),
            ["compose", ..] => new(0, string.Empty, string.Empty),
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
