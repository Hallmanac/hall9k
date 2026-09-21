using FluentAssertions;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Task 9eb5b245: whether asking for one stream again actually asks. The origin incident is
/// 2026-09-19: Windows broadcast for ec35ceca's stream at 13:40, the Mac declined it at 13:41, and
/// the 23:36 re-run was told the old request was still on its way — because the only question
/// anybody asked was whether a request existed, never whether it was still outstanding. These are a
/// pure function over the requests a node already holds, precisely so the rule is checkable without
/// a database, a daemon, or a second node.
/// </summary>
public sealed class StreamRequestDecisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 23, 36, 0, TimeSpan.Zero);

    [Fact]
    public void Nothing_asked_before_is_a_first_ask()
    {
        StreamRequestDecision.Decide([], again: false, reMintCooldown: null, Now)
            .Should().Be(StreamRequestOutcome.Queued);

        StreamRequestDecision.Decide([], again: true, reMintCooldown: null, Now)
            .Should().Be(
                StreamRequestOutcome.Queued,
                "--again with nothing outstanding has nothing to supersede, so it is an ordinary first ask");
    }

    [Fact]
    public void A_request_a_peer_declined_is_re_asked_rather_than_reported_as_in_flight()
    {
        EventCatchUpRequest declined = new()
        {
            Id = DomainId.New(),
            SentAt = Now.AddHours(-10),
            AnsweredAt = Now.AddHours(-10).AddMinutes(1),
            DeclinedReason = "nothing held here matches this request",
        };

        StreamRequestDecision.Decide([declined], again: false, reMintCooldown: null, Now)
            .Should().Be(
                StreamRequestOutcome.ReAsked,
                "a decline closes a broadcast, and a closed request is not an ask in flight");
    }

    [Fact]
    public void An_answered_request_is_re_asked_too_and_so_is_an_exhausted_or_superseded_one()
    {
        EventCatchUpRequest answered = new() { Id = DomainId.New(), SentAt = Now.AddDays(-1), AnsweredAt = Now.AddDays(-1) };
        EventCatchUpRequest exhausted = new() { Id = DomainId.New(), SentAt = Now.AddHours(-3), Exhausted = true };
        EventCatchUpRequest superseded = new() { Id = DomainId.New(), SentAt = Now.AddHours(-2), SupersededAt = Now.AddHours(-2) };

        StreamRequestDecision.Decide([answered, exhausted, superseded], again: false, reMintCooldown: null, Now)
            .Should().Be(StreamRequestOutcome.ReAsked);
    }

    [Fact]
    public void An_outstanding_request_suppresses_the_ask_and_only_again_clears_it()
    {
        // The exact shape of request 01a0bac1 of 2026-09-19: minted before v0.10.5, declined by the
        // one peer that could have answered, and left standing because the inbox of the day applied
        // a decline to a candidate cascade alone. Nothing but --again can clear it.
        EventCatchUpRequest stale = new() { Id = DomainId.New(), SentAt = Now.AddHours(-10) };

        StreamRequestDecision.Decide([stale], again: false, reMintCooldown: null, Now)
            .Should().Be(StreamRequestOutcome.AlreadyOutstanding);

        StreamRequestDecision.Decide([stale], again: true, reMintCooldown: null, Now)
            .Should().Be(StreamRequestOutcome.Superseded);
    }

    [Fact]
    public void One_outstanding_request_among_closed_ones_still_suppresses_the_ask()
    {
        EventCatchUpRequest closed = new() { Id = DomainId.New(), SentAt = Now.AddDays(-2), AnsweredAt = Now.AddDays(-2) };
        EventCatchUpRequest outstanding = new() { Id = DomainId.New(), SentAt = Now.AddMinutes(-5) };

        StreamRequestDecision.Decide([closed, outstanding], again: false, reMintCooldown: null, Now)
            .Should().Be(StreamRequestOutcome.AlreadyOutstanding);
    }

    /// <summary>
    /// A human's own ask never cools down, but the platform's dependency asks run on every landing
    /// of any task in the project (<c>TaskDependencyCatchUp</c>), and a dependency nobody in the
    /// project holds is declined immediately by every member — so an uncooled automatic ask would
    /// mint a fresh request, and a signed commit and push, per landing, forever.
    /// </summary>
    [Fact]
    public void An_automatic_ask_waits_out_its_cooldown_and_asks_once_it_has_elapsed()
    {
        TimeSpan cooldown = TimeSpan.FromHours(6);
        EventCatchUpRequest justDeclined = new()
        {
            Id = DomainId.New(),
            SentAt = Now.AddHours(-1),
            AnsweredAt = Now.AddHours(-1),
            DeclinedReason = "nothing held here matches this request",
        };

        StreamRequestDecision.Decide([justDeclined], again: false, cooldown, Now)
            .Should().Be(StreamRequestOutcome.CoolingDown);

        StreamRequestDecision.Decide([justDeclined], again: false, cooldown, Now.AddHours(6))
            .Should().Be(StreamRequestOutcome.ReAsked);
    }

    [Theory]
    [InlineData(StreamRequestOutcome.Queued, true)]
    [InlineData(StreamRequestOutcome.ReAsked, true)]
    [InlineData(StreamRequestOutcome.Superseded, true)]
    [InlineData(StreamRequestOutcome.AlreadyOutstanding, false)]
    [InlineData(StreamRequestOutcome.CoolingDown, false)]
    public void Only_the_outcomes_that_actually_sent_an_envelope_report_that_they_queued(
        StreamRequestOutcome outcome, bool queues) => outcome.Queues().Should().Be(queues);

    [Fact]
    public void A_superseded_request_is_no_longer_outstanding_and_never_claims_it_was_answered()
    {
        EventCatchUpRequest superseded = new()
        {
            Id = DomainId.New(),
            SentAt = Now.AddHours(-10),
            SupersededAt = Now,
            SupersededByRequestId = DomainId.New(),
        };

        superseded.IsOutstanding.Should().BeFalse("h9k status shows outstanding requests, and this one was replaced");
        superseded.AnsweredAt.Should().BeNull(
            "nothing answered it; recording a supersede as an answer would claim an observation nobody made");
    }
}
