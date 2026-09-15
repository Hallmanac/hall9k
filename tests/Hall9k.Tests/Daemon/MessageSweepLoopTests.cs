using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>The message sweep's own cadence (Brian's ruling 2026-09-13: 15 to 25 s active, 30 to
/// 45 s idle, with jitter) — <see cref="MessageSweepLoop.JitteredInterval"/> is the pure interval
/// pick behind it, unit-testable without a running loop.</summary>
public sealed class MessageSweepLoopTests
{
    private static readonly NullLogger<MessageSweepLoop> Logger = NullLogger<MessageSweepLoop>.Instance;

    [Fact]
    public void Active_cadence_stays_within_the_configured_range()
    {
        DaemonOptions options = new();
        for (int i = 0; i < 200; i++)
        {
            TimeSpan interval = MessageSweepLoop.JitteredInterval(activeCadence: true, options, Logger);
            interval.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(options.MessageActivePollMinSeconds));
            interval.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(options.MessageActivePollMaxSeconds));
        }
    }

    [Fact]
    public void Idle_cadence_stays_within_the_configured_range()
    {
        DaemonOptions options = new();
        for (int i = 0; i < 200; i++)
        {
            TimeSpan interval = MessageSweepLoop.JitteredInterval(activeCadence: false, options, Logger);
            interval.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(options.MessageIdlePollMinSeconds));
            interval.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(options.MessageIdlePollMaxSeconds));
        }
    }

    [Fact]
    public void An_inverted_configured_range_falls_back_to_the_shipped_default_rather_than_throwing()
    {
        DaemonOptions options = new() { MessageActivePollMinSeconds = 50, MessageActivePollMaxSeconds = 10 };

        TimeSpan interval = MessageSweepLoop.JitteredInterval(activeCadence: true, options, Logger);

        interval.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(15));
        interval.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(25));
    }

    [Fact]
    public void A_sub_one_second_configured_minimum_falls_back_to_the_shipped_default_rather_than_throwing()
    {
        DaemonOptions options = new() { MessageIdlePollMinSeconds = 0, MessageIdlePollMaxSeconds = 45 };

        TimeSpan interval = MessageSweepLoop.JitteredInterval(activeCadence: false, options, Logger);

        interval.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(30));
        interval.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void A_max_beyond_what_task_delay_accepts_is_clamped_rather_than_thrown()
    {
        DaemonOptions options = new() { MessageIdlePollMinSeconds = 30, MessageIdlePollMaxSeconds = int.MaxValue };

        TimeSpan interval = MessageSweepLoop.JitteredInterval(activeCadence: false, options, Logger);

        interval.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(30));
        interval.Should().BeLessThanOrEqualTo(MessageSweepLoop.MaxSupportedInterval);
    }

    [Fact]
    public void A_min_and_max_both_beyond_what_task_delay_accepts_are_clamped_rather_than_thrown()
    {
        DaemonOptions options = new() { MessageIdlePollMinSeconds = 5_000_000, MessageIdlePollMaxSeconds = 5_000_000 };

        TimeSpan interval = MessageSweepLoop.JitteredInterval(activeCadence: false, options, Logger);

        interval.Should().BeLessThanOrEqualTo(MessageSweepLoop.MaxSupportedInterval);
    }
}
