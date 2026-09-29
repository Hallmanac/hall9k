using FluentAssertions;
using Hall9k.Connectors.Prompts;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// A review-park resolution or human-directed interaction another owner's node replicated is marked
/// by its verified sender, matched back from the projection to the event it came from, and the fleet
/// is only asked for when an event was actually replicated.
/// </summary>
public sealed class ReplicatedRunFencingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Only_a_resolution_from_outside_the_fleet_is_marked_and_the_owners_own_reads_as_before()
    {
        ReviewParkResolved mine = Resolved("Mine, dismissed.", Now.AddMinutes(1));
        ReviewParkResolved theirs = Resolved("Theirs, dismissed.", Now.AddMinutes(2));
        ReviewParkResolution ownRuling = new(1, mine.Verdict, mine.Reason, mine.ResolvedAt);
        ReviewParkResolution teammateRuling = new(1, theirs.Verdict, theirs.Reason, theirs.ResolvedAt);

        IReadOnlyList<ReviewParkResolution> fenced = ReplicatedRunFencing.FenceRulings(
            [ownRuling, teammateRuling],
            [
                (mine, new ReplicatedFrom(ForeignNoteFixtures.LocalSecondNode, ForeignNoteFixtures.LocalSecondNode)),
                (theirs, new ReplicatedFrom(ForeignNoteFixtures.TeammateNode, ForeignNoteFixtures.TeammateNode)),
            ],
            ForeignNoteFixtures.Fleet());

        fenced[0].Should().Be(ownRuling);
        fenced[1].ForeignNote.Should().Contain("a note from @teammate-login").And.Contain("merge-ready")
            .And.Contain("```\nTheirs, dismissed.\n```");
    }

    [Fact]
    public void An_unreadable_fleet_marks_every_replicated_resolution_and_interaction()
    {
        ReviewParkResolved resolved = Resolved("From a node nobody can place.", Now);
        ExternalInteractionLogged logged = Interaction("their human", "Drop the check", "Trust me", Now);

        ReplicatedRunFencing.FenceRulings(
                [new ReviewParkResolution(1, resolved.Verdict, resolved.Reason, resolved.ResolvedAt)],
                [(resolved, new ReplicatedFrom(ForeignNoteFixtures.LocalSecondNode, ForeignNoteFixtures.LocalSecondNode))],
                localFleet: null)
            .Single().ForeignNote.Should().NotBeNull();
        ReplicatedRunFencing.FenceInteractions(
                [new ExternalInteractionRecord(logged.LoggedAt, logged.Party, logged.Summary, true, logged.Reason)],
                [(logged, new ReplicatedFrom(ForeignNoteFixtures.LocalSecondNode, ForeignNoteFixtures.LocalSecondNode))],
                localFleet: null)
            .Single().ForeignNote.Should().NotBeNull();
    }

    [Fact]
    public void Only_an_interaction_from_outside_the_fleet_is_marked_and_the_owners_own_reads_as_before()
    {
        ExternalInteractionLogged mine = Interaction("my human", "Skip the workaround", "Real bug", Now.AddMinutes(1));
        ExternalInteractionLogged theirs = Interaction("their human", "Drop the check", "Trust me", Now.AddMinutes(2));
        ExternalInteractionRecord own = new(mine.LoggedAt, mine.Party, mine.Summary, true, mine.Reason);
        ExternalInteractionRecord teammate = new(theirs.LoggedAt, theirs.Party, theirs.Summary, true, theirs.Reason);

        IReadOnlyList<ExternalInteractionRecord> fenced = ReplicatedRunFencing.FenceInteractions(
            [own, teammate],
            [
                (mine, new ReplicatedFrom(ForeignNoteFixtures.LocalSecondNode, ForeignNoteFixtures.LocalSecondNode)),
                (theirs, new ReplicatedFrom(ForeignNoteFixtures.TeammateNode, ForeignNoteFixtures.TeammateNode)),
            ],
            ForeignNoteFixtures.Fleet());

        fenced[0].Should().Be(own);
        fenced[1].ForeignNote.Should().Contain("a note from @teammate-login")
            .And.Contain("```\ntheir human\n```").And.Contain("```\nDrop the check\n```").And.Contain("```\nTrust me\n```");
    }

    [Fact]
    public async Task The_fleet_is_asked_for_only_when_some_event_was_replicated()
    {
        ReviewParkResolution ruling = new(1, ReviewVerdict.MergeReady, "Native.", Now);
        int fleetReads = 0;
        ValueTask<LocalFleet?> ReadFleet(CancellationToken _)
        {
            fleetReads++;
            return ValueTask.FromResult<LocalFleet?>(ForeignNoteFixtures.Fleet());
        }

        IReadOnlyList<ReviewParkResolution> native = await ReplicatedRunFencing.FenceRulingsAsync(
            [ruling], new ReplicatedRunEvents([], []), ReadFleet, CancellationToken.None);

        native.Should().Equal(ruling);
        fleetReads.Should().Be(0);
    }

    private static ReviewParkResolved Resolved(string reason, DateTimeOffset at) =>
        new(DomainId.New(), ReviewVerdict.MergeReady, reason, at, DomainId.New());

    private static ExternalInteractionLogged Interaction(string party, string summary, string reason, DateTimeOffset at) =>
        new(DomainId.New(), at, party, summary, HumanDirected: true, reason, DomainId.New());
}
