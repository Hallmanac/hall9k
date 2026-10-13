using FluentAssertions;
using Hall9k.Connectors.Releases;
using Hall9k.Daemon.Releases;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The release loop's cadence, driven by a clock the test steps by hand: the first check about a
/// minute after the node has an identity, then one per interval, with the interval read fresh after
/// every check so changing it changes the very next wait. No real delay occurs anywhere.
/// </summary>
public sealed class ReleaseNoticeLoopTests : IAsyncLifetime
{
    private static readonly Guid NodeId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTimeOffset Start = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly ManualTimeProvider clock = new(Start);
    private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
    private readonly ListLogger<ReleaseNoticeLoop> logger = new();
    private readonly TaskCompletionSource<Guid> nodeReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TimeSpan interval = TimeSpan.FromHours(6);
    private int lookups;
    private bool lookupThrows;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        timeout.Dispose();
        return Task.CompletedTask;
    }

    private ReleaseNoticeLoop Loop()
    {
        ReleaseNoticeEngine engine = new(
            _ =>
            {
                lookups++;
                return lookupThrows
                    ? throw new InvalidOperationException("boom")
                    : Task.FromResult(LatestReleaseResult.Found(new ReleaseChannelResolution(ReleaseChannel.Cleared, null), "v0.13.0"));
            },
            new NeverRecordsStore(), "0.13.0", clock, new ListLogger<ReleaseNoticeEngine>());
        return new ReleaseNoticeLoop(
            engine, cancellationToken => nodeReady.Task.WaitAsync(cancellationToken), _ => Task.FromResult(interval),
            clock, logger);
    }

    [Fact]
    public async Task Nothing_is_checked_until_the_node_has_an_identity_and_then_a_minute_passes()
    {
        ReleaseNoticeLoop loop = Loop();
        await loop.StartAsync(timeout.Token);

        lookups.Should().Be(0, "the node's identity is not ready");

        nodeReady.SetResult(NodeId);
        (await clock.NextDelayAsync(timeout.Token)).Should().Be(TimeSpan.FromMinutes(1));
        lookups.Should().Be(0);

        await clock.FireNextAsync(timeout.Token);
        await WaitForLookupsAsync(1);

        await loop.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task After_the_first_check_it_waits_the_default_six_hours_between_checks()
    {
        nodeReady.SetResult(NodeId);
        ReleaseNoticeLoop loop = Loop();
        await loop.StartAsync(timeout.Token);

        await clock.FireNextAsync(timeout.Token);
        await WaitForLookupsAsync(1);
        (await clock.NextDelayAsync(timeout.Token)).Should().Be(TimeSpan.FromHours(6));

        await clock.FireNextAsync(timeout.Token);
        await WaitForLookupsAsync(2);
        (await clock.NextDelayAsync(timeout.Token)).Should().Be(TimeSpan.FromHours(6));

        await loop.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Changing_the_interval_changes_the_wait_that_follows_the_next_check_with_no_restart()
    {
        nodeReady.SetResult(NodeId);
        ReleaseNoticeLoop loop = Loop();
        await loop.StartAsync(timeout.Token);

        await clock.FireNextAsync(timeout.Token);
        await WaitForLookupsAsync(1);
        (await clock.NextDelayAsync(timeout.Token)).Should().Be(TimeSpan.FromHours(6));

        interval = TimeSpan.FromHours(1);
        await clock.FireNextAsync(timeout.Token);
        await WaitForLookupsAsync(2);
        (await clock.NextDelayAsync(timeout.Token)).Should().Be(TimeSpan.FromHours(1));

        await loop.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task An_interval_past_what_one_timer_can_wait_is_waited_out_in_slices_never_faulting_the_loop()
    {
        nodeReady.SetResult(NodeId);
        interval = TimeSpan.FromDays(70);
        ReleaseNoticeLoop loop = Loop();
        await loop.StartAsync(timeout.Token);

        await clock.FireNextAsync(timeout.Token);
        await WaitForLookupsAsync(1);

        (await clock.NextDelayAsync(timeout.Token)).Should().Be(ReleaseNoticeLoop.LongestSingleWait);
        await clock.FireNextAsync(timeout.Token);
        (await clock.NextDelayAsync(timeout.Token)).Should().Be(ReleaseNoticeLoop.LongestSingleWait);
        await clock.FireNextAsync(timeout.Token);
        (await clock.NextDelayAsync(timeout.Token)).Should().Be(TimeSpan.FromDays(10), "the remainder, not another full slice");
        lookups.Should().Be(1, "no check happens until the whole interval has elapsed");
        await clock.FireNextAsync(timeout.Token);
        await WaitForLookupsAsync(2);

        await loop.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_check_that_throws_is_logged_and_the_loop_waits_for_the_next_interval()
    {
        nodeReady.SetResult(NodeId);
        lookupThrows = true;
        ReleaseNoticeLoop loop = Loop();
        await loop.StartAsync(timeout.Token);

        await clock.FireNextAsync(timeout.Token);
        await WaitForLookupsAsync(1);

        (await clock.NextDelayAsync(timeout.Token)).Should().Be(TimeSpan.FromHours(6));
        logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);

        await loop.StopAsync(CancellationToken.None);
    }

    private async Task WaitForLookupsAsync(int expected)
    {
        // The check runs on whichever thread fired the timer, so by the time the next timer exists it has finished.
        await clock.NextDelayAsync(timeout.Token);
        lookups.Should().Be(expected);
    }

    private sealed class NeverRecordsStore : IReleaseNoticeStore
    {
        public Task<ReleaseNoticeIssued?> RecordIfDueAsync(
            Guid nodeId, Func<ReleaseNoticeState, ReleaseNoticeIssued?> decide, CancellationToken cancellationToken) =>
            Task.FromResult(decide(new ReleaseNoticeState(null, null)));
    }
}
