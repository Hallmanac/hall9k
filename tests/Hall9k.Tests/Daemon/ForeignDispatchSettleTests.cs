using FluentAssertions;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The settle period counts only time the node was awake after a sweep first found another node's dispatch overdue, so a
/// node that sleeps past it still gives its message sweep the whole period after waking.
/// </summary>
public sealed class ForeignDispatchSettleTests
{
    private static readonly TimeSpan SettlePeriod = TimeSpan.FromMinutes(10);

    [Fact]
    public void Time_spent_suspended_after_the_first_overdue_sweep_does_not_count_toward_the_settle_period()
    {
        SettableClock clock = new(new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero));
        ForeignDispatchSettle settle = new(SettlePeriod, clock);
        Guid session = DomainId.New();
        settle.HasSettled(session).Should().BeFalse("the first sweep that finds it overdue only notes it");

        clock.Suspend(TimeSpan.FromHours(1));
        settle.HasSettled(session).Should().BeFalse("an hour asleep is no time for the message sweep to deliver anything");

        clock.Advance(SettlePeriod);
        settle.HasSettled(session).Should().BeTrue();
    }
}
