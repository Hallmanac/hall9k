using FluentAssertions;
using Hall9k.Connectors.Replication;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Connectors.Replication;

/// <summary>
/// An events-unavailable answer is recorded as an observation — which node said it, when, and why
/// — rather than as one overwritten reason string. <see cref="EventCatchUpDeclineLog"/> is pure, so
/// every rule here is asserted without a container.
/// </summary>
public sealed class EventCatchUpDeclineLogTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 13, 41, 0, TimeSpan.Zero);

    [Fact]
    public void Records_the_declining_node_the_time_and_the_reason_rather_than_only_the_reason()
    {
        EventCatchUpRequest request = new() { Id = DomainId.New(), ProjectId = DomainId.New() };
        Guid mac = DomainId.New();

        EventCatchUpDeclineLog.Record(request, mac, Now, "nothing held here matches this request")
            .Should().BeTrue();

        request.Declines.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new EventCatchUpDecline(mac, Now, "nothing held here matches this request"));
        request.DeclinedReason.Should().Be(
            "nothing held here matches this request", "the long-standing 'most recent reason' field still means that");
    }

    [Fact]
    public void Collects_one_entry_per_declining_node_in_the_order_they_were_read()
    {
        EventCatchUpRequest request = new() { Id = DomainId.New(), ProjectId = DomainId.New() };
        Guid mac = DomainId.New();
        Guid laptop = DomainId.New();

        EventCatchUpDeclineLog.Record(request, mac, Now, "nothing here");
        EventCatchUpDeclineLog.Record(request, laptop, Now.AddMinutes(2), "nothing here either");

        request.Declines.Select(decline => decline.DeclinedByNodeId).Should().Equal(mac, laptop);
        request.Declines.Select(decline => decline.DeclinedAt).Should().Equal(Now, Now.AddMinutes(2));
    }

    [Fact]
    public void A_second_decline_from_a_node_that_already_declined_adds_nothing_but_refreshes_the_reason()
    {
        EventCatchUpRequest request = new() { Id = DomainId.New(), ProjectId = DomainId.New() };
        Guid mac = DomainId.New();

        EventCatchUpDeclineLog.Record(request, mac, Now, "nothing here");
        EventCatchUpDeclineLog.Record(request, mac, Now.AddHours(1), "still nothing here")
            .Should().BeFalse("one node's inability to answer one request is one fact");

        request.Declines.Should().ContainSingle();
        request.Declines[0].DeclinedAt.Should().Be(Now, "the first time it said so is what was observed");
        request.DeclinedReason.Should().Be("still nothing here");
    }

    [Fact]
    public void A_decline_carrying_no_reason_is_labelled_rather_than_left_blank_or_invented()
    {
        EventCatchUpRequest request = new() { Id = DomainId.New(), ProjectId = DomainId.New() };

        EventCatchUpDeclineLog.Record(request, DomainId.New(), Now, "   ");

        request.Declines.Should().ContainSingle().Which.Reason.Should().Be(EventCatchUpDeclineLog.UnstatedReason);
    }
}
