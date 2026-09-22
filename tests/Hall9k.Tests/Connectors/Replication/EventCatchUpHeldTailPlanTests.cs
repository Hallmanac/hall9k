using FluentAssertions;
using Hall9k.Connectors.Replication;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Connectors.Replication;

/// <summary>
/// The held-tail ask's whole decision: which streams this node holds only the tail of get a
/// broadcast request this sweep. <see cref="EventCatchUpCoordinator.PlanHeldTailAsks"/> is pure,
/// so the dedupe, the per-sweep cap, the cooldown, the attempt stop and the settle window that
/// keeps a bootstrap quiet are all asserted without a container.
/// </summary>
public sealed class EventCatchUpHeldTailPlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 35, 0, TimeSpan.Zero);
    private static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

    /// <summary>A stream held long enough to have outlived a sweep, never asked for before.</summary>
    private static HeldTailStreamState Settled(
        int attempts = 0, DateTimeOffset? mostRecentAskSentAt = null, bool askOutstanding = false,
        TimeSpan? heldFor = null) => new(
            DomainId.New(),
            Now - (heldFor ?? TimeSpan.FromMinutes(10)),
            attempts,
            mostRecentAskSentAt,
            askOutstanding);

    private static HeldTailAskPlan Planned(params HeldTailStreamState[] streams) =>
        EventCatchUpCoordinator.PlanHeldTailAsks(
            streams, SettleWindow, Cooldown, EventCatchUpCoordinator.MaxHeldTailAsksPerSweep, Now);

    private static IReadOnlyList<Guid> Plan(params HeldTailStreamState[] streams) => Planned(streams).ToAsk;

    [Fact]
    public void Asks_for_a_stream_whose_tail_has_been_held_longer_than_one_sweep()
    {
        HeldTailStreamState stream = Settled();

        Plan(stream).Should().Equal(stream.StreamId);
    }

    /// <summary>
    /// The bootstrap case, and the reason the settle window exists at all: a <c>--since all</c>
    /// answer lands hundreds of tails whose own genesis events are still in the same batch, the
    /// inbox replays and deletes every one of them within that read, and nothing that resolves on
    /// its own is ever asked about. Expressed here as the rule rather than as the deletion, because
    /// the rule is what holds when the replay takes one more read than the one it arrived in.
    /// </summary>
    [Fact]
    public void A_bootstrap_whose_tails_are_still_landing_mints_no_ask_at_all()
    {
        HeldTailStreamState[] justHeld = [.. Enumerable.Range(0, 200)
            .Select(_ => Settled(heldFor: TimeSpan.FromSeconds(2)))];

        Plan(justHeld).Should().BeEmpty("a hold the ordinary flow has not had one sweep to fix is not a gap yet");
    }

    [Fact]
    public void Never_asks_twice_for_a_stream_whose_request_is_still_outstanding()
    {
        HeldTailStreamState outstanding = Settled(askOutstanding: true, mostRecentAskSentAt: Now - TimeSpan.FromHours(2));

        Plan(outstanding).Should().BeEmpty();
    }

    /// <summary>
    /// A decline closes a broadcast the moment it is read, and an answer that lands without the
    /// genesis closes one just as fast — so "not outstanding" arrives within seconds either way,
    /// and the cooldown is the only thing standing between that and a fresh ask every sweep.
    /// </summary>
    [Fact]
    public void Waits_out_the_cooldown_after_an_ask_that_closed_without_bringing_the_genesis()
    {
        HeldTailStreamState justDeclined = Settled(attempts: 1, mostRecentAskSentAt: Now - TimeSpan.FromMinutes(1));

        Plan(justDeclined).Should().BeEmpty("the ask went out a minute ago and the cooldown is five");
    }

    [Fact]
    public void Asks_again_once_the_cooldown_has_elapsed()
    {
        HeldTailStreamState cooled = Settled(attempts: 1, mostRecentAskSentAt: Now - TimeSpan.FromMinutes(6));

        Plan(cooled).Should().Equal(cooled.StreamId);
    }

    [Fact]
    public void Stops_asking_after_three_attempts()
    {
        HeldTailStreamState exhausted = Settled(
            attempts: EventCatchUpCoordinator.MaxHeldTailAttempts,
            mostRecentAskSentAt: Now - TimeSpan.FromHours(4));

        Plan(exhausted).Should().BeEmpty("a tail nobody in the fleet can complete is not completable by a fourth ask");
    }

    /// <summary>
    /// The verdict lands one sweep after the ask that earned it, never in the same breath as the
    /// mint: a stream is given up on when the ask it would otherwise make now is the fourth.
    /// </summary>
    [Fact]
    public void Gives_up_on_the_stream_whose_fourth_ask_this_sweep_declines_to_mint()
    {
        HeldTailStreamState exhausted = Settled(
            attempts: EventCatchUpCoordinator.MaxHeldTailAttempts,
            mostRecentAskSentAt: Now - TimeSpan.FromHours(4));

        Planned(exhausted).ToGiveUp.Should().Equal(exhausted.StreamId);
    }

    /// <summary>
    /// The reason the verdict waits at all: marking a stream given up the instant its third ask
    /// was minted had <c>h9k status</c> reporting it abandoned while the fleet still had the
    /// request in hand and might yet answer it (independent pre-PR review, cycle 1, both lenses).
    /// </summary>
    [Fact]
    public void Does_not_give_up_while_the_third_ask_is_still_outstanding()
    {
        HeldTailStreamState inFlight = Settled(
            attempts: EventCatchUpCoordinator.MaxHeldTailAttempts,
            mostRecentAskSentAt: Now - TimeSpan.FromMinutes(1),
            askOutstanding: true);

        Planned(inFlight).ToGiveUp.Should().BeEmpty("the ask it is being judged on is still in flight");
    }

    [Fact]
    public void Does_not_give_up_while_the_last_ask_is_still_inside_its_cooldown()
    {
        HeldTailStreamState cooling = Settled(
            attempts: EventCatchUpCoordinator.MaxHeldTailAttempts,
            mostRecentAskSentAt: Now - TimeSpan.FromMinutes(1));

        Planned(cooling).ToGiveUp.Should().BeEmpty("a late answer inside the cooldown still completes the stream");
    }

    [Fact]
    public void Gives_up_on_no_more_streams_in_one_sweep_than_it_asks_about()
    {
        List<HeldTailStreamState> exhausted = [.. Enumerable.Range(1, 25)
            .Select(minutes => Settled(
                attempts: EventCatchUpCoordinator.MaxHeldTailAttempts,
                mostRecentAskSentAt: Now - TimeSpan.FromHours(4),
                heldFor: TimeSpan.FromMinutes(minutes)))];

        HeldTailAskPlan plan = Planned([.. exhausted]);

        plan.ToAsk.Should().BeEmpty();
        plan.ToGiveUp.Should().HaveCount(
            EventCatchUpCoordinator.MaxHeldTailAsksPerSweep,
            "the caller pays a stream-state read per stream it acts on, whichever way it acts");
    }

    [Fact]
    public void Still_asks_on_the_third_attempt_itself()
    {
        HeldTailStreamState twoDown = Settled(
            attempts: EventCatchUpCoordinator.MaxHeldTailAttempts - 1,
            mostRecentAskSentAt: Now - TimeSpan.FromHours(4));

        Plan(twoDown).Should().Equal([twoDown.StreamId], "three attempts means three, not two");
    }

    [Fact]
    public void Mints_at_most_ten_asks_in_one_sweep_and_takes_the_longest_held_first()
    {
        List<HeldTailStreamState> streams = [.. Enumerable.Range(1, 25)
            .Select(minutes => Settled(heldFor: TimeSpan.FromMinutes(minutes)))];

        IReadOnlyList<Guid> planned = Plan([.. streams]);

        planned.Should().HaveCount(EventCatchUpCoordinator.MaxHeldTailAsksPerSweep);
        planned.Should().Equal(
            streams.OrderByDescending(stream => Now - stream.OldestHeldAt)
                .Take(EventCatchUpCoordinator.MaxHeldTailAsksPerSweep)
                .Select(stream => stream.StreamId),
            "the cap has to choose, and waiting longest is the only order that starves nothing");
    }

    /// <summary>
    /// The cap counts asks, not candidates: a sweep looking at fifteen streams of which five are
    /// cooling still mints the ten it is allowed, rather than spending cap on streams it skipped.
    /// </summary>
    [Fact]
    public void Spends_the_cap_only_on_streams_that_actually_produce_an_ask()
    {
        List<HeldTailStreamState> cooling = [.. Enumerable.Range(0, 5)
            .Select(_ => Settled(attempts: 1, mostRecentAskSentAt: Now - TimeSpan.FromMinutes(1),
                heldFor: TimeSpan.FromHours(1)))];
        List<HeldTailStreamState> askable = [.. Enumerable.Range(1, 12)
            .Select(minutes => Settled(heldFor: TimeSpan.FromMinutes(minutes)))];

        IReadOnlyList<Guid> planned = Plan([.. cooling, .. askable]);

        planned.Should().HaveCount(EventCatchUpCoordinator.MaxHeldTailAsksPerSweep);
        planned.Should().NotIntersectWith(cooling.Select(stream => stream.StreamId));
    }

    /// <summary>
    /// The build-version bound lift (task: a run stream whose first event is a reconstruction): a
    /// stream given up while this node ran an older build earns three fresh asks the moment this
    /// node upgrades, without any human running a pull. <see cref="EventCatchUpCoordinator.RequestHeldTailStreamsAsync"/>
    /// is the caller that actually resets such a record's own attempts and give-up mark before
    /// <see cref="EventCatchUpCoordinator.PlanHeldTailAsks"/> ever sees it — this is the pure
    /// decision underneath that reset, checkable without a database.
    /// </summary>
    [Theory]
    [InlineData(null, "0.10.31", false, "no recorded version counts as older than anything")]
    [InlineData("0.10.27", "0.10.31", false, "given up on an older build no longer stands")]
    [InlineData("0.10.31", "0.10.31", true, "given up on the current build still stands")]
    [InlineData("0.10.32", "0.10.31", true, "given up on a build newer than the one running now still stands")]
    public void GivenUpMarkStillStands_reads_an_older_build_as_no_longer_given_up(
        string? givenUpOnBuildVersion, string currentBuildVersion, bool expected, string because)
    {
        EventCatchUpCoordinator.GivenUpMarkStillStands(givenUpOnBuildVersion, currentBuildVersion)
            .Should().Be(expected, because);
    }
}
