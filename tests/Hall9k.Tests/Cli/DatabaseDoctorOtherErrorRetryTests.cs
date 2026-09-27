using FluentAssertions;
using Hall9k.Cli.Diagnostics;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="DatabaseDoctor.DiagnoseOtherErrorAsync"/> — the <see cref="ReachabilityStatus.OtherError"/>
/// sibling of <see cref="DatabaseDoctor.DiagnoseRefusedConnectionAsync"/>, covered separately by
/// <c>DatabaseDoctorRecreateThenReachabilityTests</c>. A container still finishing startup can
/// answer either with a refused connection or with Postgres itself replying "the database system
/// is starting up" (SQL state 57P03, which <see cref="DatabaseReachability.ProbeAsync"/> turns into
/// <see cref="ReachabilityStatus.OtherError"/>) — only the first got a bounded retry before this
/// task, so a container that happened to answer with 57P03 instead still failed on a single sample
/// (cycle-1 pre-PR review, conformance lens). No real container and no real Postgres: a fake probe
/// stands in for <see cref="DatabaseReachability.ProbeAsync"/>, and a fake process runner stands in
/// for the two docker calls (<c>docker info</c>, <c>docker ps -a</c>) this method makes to confirm
/// <c>hall9k-postgres</c> by name before deciding whether "still starting up" plausibly explains the
/// error.
/// </summary>
public sealed class DatabaseDoctorOtherErrorRetryTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ShortPollInterval = TimeSpan.FromMilliseconds(20);

    [Fact]
    public async Task An_other_error_from_a_confirmed_running_container_is_retried_until_reachable()
    {
        int calls = 0;
        Task<ReachabilityReport> Probe(CancellationToken token)
        {
            calls++;
            return Task.FromResult(calls == 1 ? StartingUp() : Reachable());
        }

        RecordingProcessRunner runner = ConfirmedRunningContainerRunner();

        ReachabilityReport result = await DatabaseDoctor.DiagnoseOtherErrorAsync(
            StartingUp(), offerFixes: true, runner.Runner, Probe, ShortTimeout, ShortPollInterval, TimeProvider.System,
            CancellationToken.None);

        result.Status.Should().Be(ReachabilityStatus.Reachable,
            "a container still finishing startup deserves the same retry a bare refusal already gets");
        calls.Should().Be(2, "the retry's own first probe, and exactly one more before it settles — no more");
    }

    [Fact]
    public async Task An_other_error_that_never_clears_is_reported_once_the_bound_expires()
    {
        Task<ReachabilityReport> Probe(CancellationToken token) => Task.FromResult(StartingUp());

        RecordingProcessRunner runner = ConfirmedRunningContainerRunner();
        SteppingClock clock = new(ShortPollInterval);

        ReachabilityReport result = await DatabaseDoctor.DiagnoseOtherErrorAsync(
            StartingUp(), offerFixes: true, runner.Runner, Probe, ShortTimeout, ShortPollInterval, clock,
            CancellationToken.None);

        result.Status.Should().Be(ReachabilityStatus.OtherError,
            "a container that never actually becomes reachable has to be reported that way, not upgraded to success");
    }

    [Fact]
    public async Task An_other_error_that_flips_to_refused_connection_mid_retry_is_reported_not_silently()
    {
        // The status can flip across polls (Postgres briefly closing its listening socket
        // mid-restart) — WaitForReachableAsync retries both OtherError and RefusedConnection
        // alike, so the bound can expire on a RefusedConnection sample even though this method
        // started from OtherError. The caller's ReportUnreachable stays silent for
        // RefusedConnection, trusting whichever path reached it to have already explained it —
        // so this method has to be the one that explains it here, or the operator sees nothing
        // at all once the bound expires (cycle-4 pre-PR review, conformance lens).
        Task<ReachabilityReport> Probe(CancellationToken token) => Task.FromResult(RefusedConnection());

        RecordingProcessRunner runner = ConfirmedRunningContainerRunner();
        SteppingClock clock = new(ShortPollInterval);

        ReachabilityReport result = null!;
        string output = await ScopedAnsiConsoleCapture.CaptureAsync(async () =>
        {
            result = await DatabaseDoctor.DiagnoseOtherErrorAsync(
                StartingUp(), offerFixes: true, runner.Runner, Probe, ShortTimeout, ShortPollInterval, clock,
                CancellationToken.None);
        });

        result.Status.Should().Be(ReachabilityStatus.RefusedConnection,
            "the final sample the bound expired on is what the caller has to act on, unchanged");
        output.Should().Contain($"waiting up to {ShortTimeout.TotalSeconds:0}s",
            "the caller's own ReportUnreachable prints nothing for RefusedConnection, so this method has to name the wait itself");
    }

    [Fact]
    public async Task A_passive_diagnosis_that_is_not_offering_fixes_never_retries_or_touches_docker()
    {
        // Program.cs's own passive diagnosis after an ordinary command's NpgsqlException calls
        // this with offerFixes: false — it must not add up to ShortTimeout's worth of waiting to
        // that failure, and it must not shell out to docker to find out whether it should.
        int calls = 0;
        Task<ReachabilityReport> Probe(CancellationToken token)
        {
            calls++;
            return Task.FromResult(StartingUp());
        }

        ReachabilityReport result = await DatabaseDoctor.DiagnoseOtherErrorAsync(
            StartingUp(), offerFixes: false, RecordingProcessRunner.NeverInvoked(), Probe, ShortTimeout,
            ShortPollInterval, TimeProvider.System, CancellationToken.None);

        result.Status.Should().Be(ReachabilityStatus.OtherError, "the original sample is returned unchanged");
        calls.Should().Be(0, "nothing here ever re-probes when fixes are not being offered");
    }

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

    private static ReachabilityReport StartingUp() =>
        new(ReachabilityStatus.OtherError, "the database system is starting up", "localhost", 5432, "hall9k");

    private static ReachabilityReport RefusedConnection() =>
        new(ReachabilityStatus.RefusedConnection, "Exception while reading from stream", "localhost", 5432, "hall9k");
}
