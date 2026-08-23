using FluentAssertions;
using Hall9k.Cli.Diagnostics;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The post-start readiness poll (<c>DatabaseDoctor.WaitForReadinessAsync</c>) that runs after
/// the doctor offers to start Docker: no real Postgres or Docker involved here, a fake probe
/// stands in for <see cref="DatabaseReachability.ProbeAsync"/> and a shrunk timeout/poll
/// interval keeps the timeout and eventually-ready cases fast.
/// </summary>
public sealed class DatabaseDoctorReadinessTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    [Fact]
    public async Task Ready_on_the_first_probe_returns_true_without_polling_again()
    {
        int calls = 0;
        Task<ReachabilityReport> Probe(CancellationToken token)
        {
            calls++;
            return Task.FromResult(Reachable());
        }

        bool ready = await DatabaseDoctor.WaitForReadinessAsync(Probe, Timeout, PollInterval, CancellationToken.None);

        ready.Should().BeTrue();
        calls.Should().Be(1, "a container that is ready immediately needs exactly one probe, not a wasted poll");
    }

    [Fact]
    public async Task A_slow_container_that_becomes_ready_before_the_timeout_is_caught()
    {
        int calls = 0;
        Task<ReachabilityReport> Probe(CancellationToken token)
        {
            calls++;
            return Task.FromResult(calls < 3 ? RefusedConnection() : Reachable());
        }

        bool ready = await DatabaseDoctor.WaitForReadinessAsync(Probe, Timeout, PollInterval, CancellationToken.None);

        ready.Should().BeTrue("readiness that arrives on the third probe is still well inside the timeout");
        calls.Should().Be(3, "the loop must keep polling — a slow start is not the same as a dead one");
    }

    [Fact]
    public async Task Never_ready_returns_false_once_the_timeout_elapses()
    {
        Task<ReachabilityReport> Probe(CancellationToken token) => Task.FromResult(RefusedConnection());

        bool ready = await DatabaseDoctor.WaitForReadinessAsync(Probe, Timeout, PollInterval, CancellationToken.None);

        ready.Should().BeFalse("a container that never answers must time out rather than poll forever");
    }

    [Fact]
    public async Task Cancellation_stops_the_wait_instead_of_running_to_the_timeout()
    {
        using CancellationTokenSource cancellation = new();
        Task<ReachabilityReport> Probe(CancellationToken token)
        {
            cancellation.Cancel();
            return Task.FromResult(RefusedConnection());
        }

        Func<Task> waiting = () => DatabaseDoctor.WaitForReadinessAsync(
            Probe, TimeSpan.FromSeconds(30), PollInterval, cancellation.Token);

        await waiting.Should().ThrowAsync<OperationCanceledException>(
            "cancelling the caller's token must interrupt the poll rather than waiting out the full 30s timeout");
    }

    [Fact]
    public async Task The_callers_cancellation_token_reaches_every_probe()
    {
        using CancellationTokenSource cancellation = new();
        CancellationToken? tokenSeenByProbe = null;
        Task<ReachabilityReport> Probe(CancellationToken token)
        {
            tokenSeenByProbe = token;
            return Task.FromResult(Reachable());
        }

        await DatabaseDoctor.WaitForReadinessAsync(Probe, Timeout, PollInterval, cancellation.Token);

        tokenSeenByProbe.Should().Be(cancellation.Token,
            "the probe has to be cancellable by the same token as the wait, or a caller cancelling never stops it");
    }

    private static ReachabilityReport Reachable() =>
        new(ReachabilityStatus.Reachable, string.Empty, "localhost", 5432, "hall9k");

    private static ReachabilityReport RefusedConnection() =>
        new(ReachabilityStatus.RefusedConnection, "nothing listening", "localhost", 5432, "hall9k");
}
