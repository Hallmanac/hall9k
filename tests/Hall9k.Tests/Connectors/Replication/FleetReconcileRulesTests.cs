using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Replication;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Connectors.Replication;

/// <summary>
/// Task 252bc5cf: every decision a fleet reconcile makes is a pure function over already-read
/// state, so the whole rule set is provable with no database, no repository and no remote (Brian's
/// 2026-09-13 testing rule). What the two-store integration suite proves on top of this is that the
/// envelopes actually travel; what these prove is that the verdicts themselves are right, including
/// the two that matter most — a fleet peer is asked once and never again, and two nodes settle into
/// one exchange each way rather than asking each other forever.
/// </summary>
public sealed class FleetReconcileRulesTests
{
    private const string MyOwnerRoot = "my-owner-root-fingerprint";
    private const string TeammateRoot = "teammate-root-fingerprint";
    private static readonly TimeSpan Retention = TimeSpan.FromHours(48);
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid MyNodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SiblingNodeId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SecondSiblingNodeId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid TeammateNodeId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    /// <summary>A chain where this owner's fleet is this node plus two siblings, and a teammate owns
    /// a third node — the fleet's own root node is named, since that is the one node an owner is
    /// guaranteed to have and the one a naive read of <c>Nodes</c> alone would miss.</summary>
    private static TrustChain Chain() => new(
        new Dictionary<string, TrustedOwner>
        {
            [MyOwnerRoot] = new TrustedOwner(
                MyOwnerRoot, "ssh-ed25519 AAAAFAKEMINE mine",
                [
                    new TrustedNode(SiblingNodeId.ToString(), "ssh-ed25519 AAAAFAKESIB sibling", "sibling-fingerprint", Now),
                    new TrustedNode(
                        SecondSiblingNodeId.ToString(), "ssh-ed25519 AAAAFAKESIB2 sibling-2", "sibling-2-fingerprint", Now),
                ],
                RootNodeId: MyNodeId.ToString()),
            [TeammateRoot] = new TrustedOwner(
                TeammateRoot, "ssh-ed25519 AAAAFAKETEAM teammate",
                [
                    new TrustedNode(TeammateNodeId.ToString(), "ssh-ed25519 AAAAFAKETEAMNODE teammate-node", "teammate-fingerprint", Now),
                ]),
        },
        [
            new ProjectMember(MyOwnerRoot, MembershipRole.Owner, Now),
            new ProjectMember(TeammateRoot, MembershipRole.Member, Now),
        ]);

    [Fact]
    public void The_fleet_is_this_owners_own_nodes_minus_this_one()
    {
        IReadOnlyList<Guid> peers = FleetReconcileRules.FleetPeers(Chain(), MyOwnerRoot, MyNodeId);

        peers.Should().BeEquivalentTo(
            new[] { SiblingNodeId, SecondSiblingNodeId },
            "a reconcile is with this owner's own siblings, never with a teammate's node, and never with itself");
    }

    [Fact]
    public void An_owner_this_chain_holds_no_entry_for_has_no_proven_fleet()
    {
        IReadOnlyList<Guid> peers = FleetReconcileRules.FleetPeers(Chain(), "an-owner-root-nobody-vouched", MyNodeId);

        peers.Should().BeEmpty("an unproven fleet membership is represented as unknown, never guessed at");
    }

    [Fact]
    public void A_fleet_peer_with_no_record_is_asked_and_a_second_sweep_asks_nobody()
    {
        IReadOnlyList<Guid> fleetPeers = FleetReconcileRules.FleetPeers(Chain(), MyOwnerRoot, MyNodeId);

        IReadOnlyList<Guid> firstSweep = FleetReconcileRules.PeersToAsk(
            fleetPeers, peersWithRecord: new HashSet<Guid>(), peersWithOutstandingBootstrap: new HashSet<Guid>());
        firstSweep.Should().BeEquivalentTo(fleetPeers, "one ask per sibling, on the sweep that first sees it");

        // The first sweep's own asks each wrote a record for their pair, which is the whole guard.
        IReadOnlyList<Guid> secondSweep = FleetReconcileRules.PeersToAsk(
            fleetPeers, peersWithRecord: new HashSet<Guid>(firstSweep), peersWithOutstandingBootstrap: new HashSet<Guid>());
        secondSweep.Should().BeEmpty("the record's own existence is what keeps the sweep from asking again");
    }

    [Fact]
    public void A_peer_with_an_outstanding_bootstrap_waits_rather_than_being_asked_twice()
    {
        IReadOnlyList<Guid> fleetPeers = FleetReconcileRules.FleetPeers(Chain(), MyOwnerRoot, MyNodeId);

        IReadOnlyList<Guid> toAsk = FleetReconcileRules.PeersToAsk(
            fleetPeers,
            peersWithRecord: new HashSet<Guid>(),
            peersWithOutstandingBootstrap: new HashSet<Guid> { SiblingNodeId });

        toAsk.Should().BeEquivalentTo(
            new[] { SecondSiblingNodeId },
            "a brand-new node must not fetch the whole project twice from the one peer its bootstrap is already with");
    }

    [Fact]
    public void A_node_outside_this_owners_fleet_is_not_a_fleet_peer()
    {
        FleetReconcileRules.IsFleetPeer(Chain(), MyOwnerRoot, TeammateNodeId, MyNodeId).Should().BeFalse(
            "a teammate's own project-history request is answered and nothing more — no reverse ask");
        FleetReconcileRules.IsFleetPeer(Chain(), MyOwnerRoot, DomainId.New(), MyNodeId).Should().BeFalse(
            "a node no chain here recognizes is never treated as a sibling");
        FleetReconcileRules.IsFleetPeer(Chain(), MyOwnerRoot, MyNodeId, MyNodeId).Should().BeFalse(
            "this node is not its own sibling");
        FleetReconcileRules.IsFleetPeer(Chain(), MyOwnerRoot, SiblingNodeId, MyNodeId).Should().BeTrue();
    }

    [Fact]
    public void The_terminal_envelope_is_what_completes_a_reconcile_and_it_keeps_both_counts()
    {
        FleetProjectReconcile record = FleetReconcileRules.NewRecord(SiblingNodeId, ProjectId, RequestId, Now);

        FleetReconcileRules.NoteAnswerEnvelopes(record, envelopes: 12, recordsApplied: 2340, Now.AddMinutes(1));
        FleetReconcileRules.NoteAnswerEnvelopes(record, envelopes: 2, recordsApplied: 0, Now.AddMinutes(2));

        record.IsInProgress.Should().BeTrue("fourteen envelopes arriving is not the peer saying it is finished");
        record.FirstAnswerAt.Should().Be(Now.AddMinutes(1), "the FIRST answer stamps it and no later one re-stamps it");
        record.EnvelopesRead.Should().Be(14);
        record.RecordsApplied.Should().Be(2340, "an envelope whose every record was already held applies nothing");

        FleetReconcileRules.NoteComplete(record, envelopeCount: 14, Now.AddMinutes(3));

        record.CompletedAt.Should().Be(Now.AddMinutes(3));
        record.AnswerEnvelopeCount.Should().Be(14);
        record.EnvelopesRead.Should().Be(14, "the peer's own claim is kept beside this node's count, never instead of it");
        record.RecordsApplied.Should().Be(2340);
    }

    [Fact]
    public void A_completed_reconcile_whose_counts_disagree_still_reports_both()
    {
        FleetProjectReconcile record = FleetReconcileRules.NewRecord(SiblingNodeId, ProjectId, RequestId, Now);
        FleetReconcileRules.NoteAnswerEnvelopes(record, envelopes: 3, recordsApplied: 40, Now.AddMinutes(1));
        FleetReconcileRules.NoteComplete(record, envelopeCount: 14, Now.AddMinutes(2));

        // An answer partly lost to an outbox squash is exactly this shape, and flattening the two
        // numbers into one would hide it.
        StatusCommand.FleetReconcileLine(record, "arx-platform").Should().Contain("3 of 14 answering envelope(s) read");
    }

    [Fact]
    public void Held_tail_only_streams_are_counted_and_named_rather_than_silently_skipped()
    {
        FleetProjectReconcile record = FleetReconcileRules.NewRecord(SiblingNodeId, ProjectId, RequestId, Now);
        FleetReconcileRules.NoteAnswerEnvelopes(record, envelopes: 14, recordsApplied: 2612, Now.AddMinutes(1));
        FleetReconcileRules.NoteComplete(record, envelopeCount: 14, Now.AddMinutes(2));
        record.HeldTailOnlyStreams = 3;

        string line = StatusCommand.FleetReconcileLine(record, "arx-platform");

        line.Should().Contain("complete");
        line.Should().Contain("3 stream(s) held tail-only");
        line.Should().Contain("held-tail ask", "the count has to point at the ask that can actually fix it");
    }

    [Fact]
    public void A_peer_holding_nothing_says_so_and_the_record_says_so_too()
    {
        FleetProjectReconcile record = FleetReconcileRules.NewRecord(SiblingNodeId, ProjectId, RequestId, Now);

        FleetReconcileRules.NoteUnavailable(record, "nothing held here matches this request", Now.AddMinutes(1));

        record.CompletedAt.Should().Be(Now.AddMinutes(1), "a final answer closes the exchange");
        FleetReconcileRules.NeedsReAsk(record, Retention, Now.AddDays(30)).Should().BeFalse(
            "a peer that already said it holds nothing is not re-asked every two days forever");
        StatusCommand.FleetReconcileLine(record, "bioage-calc").Should()
            .Contain("nothing held here matches this request");
    }

    [Fact]
    public void An_unanswered_reconcile_is_re_asked_once_and_then_reported()
    {
        FleetProjectReconcile record = FleetReconcileRules.NewRecord(SiblingNodeId, ProjectId, RequestId, Now);

        FleetReconcileRules.NeedsReAsk(record, Retention, Now.AddHours(47)).Should().BeFalse(
            "inside the squash window the answer may still be on its way");
        FleetReconcileRules.NeedsReAsk(record, Retention, Now + Retention).Should().BeTrue(
            "past it an unread answer has been pruned, and nothing else would ever ask again");

        FleetReconcileRules.PointAtFreshAsk(record, DomainId.New(), automatic: true, Now + Retention);

        FleetReconcileRules.NeedsReAsk(record, Retention, Now + Retention + Retention).Should().BeFalse(
            "once, not on a loop");
        FleetReconcileRules.IsStalled(record, Retention, Now + Retention + Retention).Should().BeTrue(
            "the re-ask going unanswered too is the point a human has to be told");
    }

    /// <summary>
    /// A record whose peer left this owner's fleet is closed unanswered rather than re-asked and
    /// then reported stalled: nothing this node sends a revoked node will ever be answered, and the
    /// stall line names <c>h9k project reconcile</c>, which walks the CURRENT fleet and so could
    /// never clear that record (independent pre-PR review, cycle 1, adversarial lens, medium).
    /// </summary>
    [Fact]
    public void A_record_whose_peer_left_the_fleet_is_closed_rather_than_re_asked_and_stalled()
    {
        FleetProjectReconcile record = FleetReconcileRules.NewRecord(SiblingNodeId, ProjectId, RequestId, Now);
        HashSet<Guid> fleetWithoutIt = [SecondSiblingNodeId];

        FleetReconcileRules.NeedsRetiring(record, fleetWithoutIt).Should().BeTrue();
        FleetReconcileRules.NotePeerLeftFleet(record, Now.AddDays(1));

        record.PeerLeftFleetAt.Should().Be(Now.AddDays(1));
        record.CompletedAt.Should().BeNull("no answer was ever observed, so nothing here claims one");
        record.UnavailableReason.Should().BeNull("that field holds the peer's own words, and it said nothing");
        FleetReconcileRules.NeedsRetiring(record, fleetWithoutIt).Should().BeFalse("retired once, not every sweep");
        FleetReconcileRules.NeedsReAsk(record, Retention, Now.AddDays(30)).Should().BeFalse(
            "a node that is not a sibling any more is not asked again");
        FleetReconcileRules.IsStalled(record, Retention, Now.AddDays(30)).Should().BeFalse(
            "and is never reported as a stall a human could clear");

        string line = StatusCommand.FleetReconcileLine(record, "arx-platform");
        line.Should().Contain("no longer part of this owner's fleet");
        line.Should().NotContain("h9k project reconcile", "that command walks the current fleet and cannot reach this");
    }

    [Fact]
    public void A_completed_reconcile_is_left_alone_when_its_peer_later_leaves_the_fleet()
    {
        FleetProjectReconcile record = FleetReconcileRules.NewRecord(SiblingNodeId, ProjectId, RequestId, Now);
        FleetReconcileRules.NoteComplete(record, envelopeCount: 9, Now.AddMinutes(2));

        FleetReconcileRules.NeedsRetiring(record, new HashSet<Guid>()).Should().BeFalse(
            "that exchange is a finished fact about history the two nodes did share, and a revoke does not unmake it");
    }

    [Fact]
    public void A_peer_vouched_back_into_the_fleet_starts_its_exchange_over()
    {
        FleetProjectReconcile record = FleetReconcileRules.NewRecord(SiblingNodeId, ProjectId, RequestId, Now);
        FleetReconcileRules.NotePeerLeftFleet(record, Now.AddDays(1));
        HashSet<Guid> fleetWithItBack = [SiblingNodeId, SecondSiblingNodeId];

        FleetReconcileRules.PeerIsBackInFleet(record, fleetWithItBack).Should().BeTrue(
            "the record's own existence is what otherwise keeps this pair from ever being asked again");

        Guid freshRequestId = DomainId.New();
        FleetReconcileRules.PointAtFreshAsk(record, freshRequestId, automatic: false, Now.AddDays(2));

        record.PeerLeftFleetAt.Should().BeNull();
        record.RequestId.Should().Be(freshRequestId);
        record.ReAskedAt.Should().BeNull("a peer's return is a new exchange, not a second strike against the old one");
        FleetReconcileRules.PeerIsBackInFleet(record, fleetWithItBack).Should().BeFalse("restarted once, not every sweep");
    }

    /// <summary>
    /// The one reading of an empty fleet that must NOT retire anything: a chain this read could not
    /// see at all reports no peers for the identical reason a genuinely solo fleet does, and
    /// treating the two the same would close every record on this node the first time a ledger read
    /// came back thin (AGENTS.md: never guess at unobserved facts).
    /// </summary>
    [Fact]
    public void A_fleet_nobody_could_read_is_unknown_rather_than_empty()
    {
        FleetReconcileRules.FleetIsKnown(Chain(), MyOwnerRoot).Should().BeTrue();
        FleetReconcileRules.FleetIsKnown(Chain(), "an-owner-root-nobody-vouched").Should().BeFalse(
            "no chain for this owner proves nothing whatsoever about who its siblings are");
    }

    [Fact]
    public void A_fresh_ask_reports_this_exchanges_counts_rather_than_the_previous_ones()
    {
        FleetProjectReconcile record = FleetReconcileRules.NewRecord(SiblingNodeId, ProjectId, RequestId, Now);
        FleetReconcileRules.NoteAnswerEnvelopes(record, envelopes: 14, recordsApplied: 2612, Now.AddMinutes(1));
        FleetReconcileRules.NoteComplete(record, envelopeCount: 14, Now.AddMinutes(2));

        Guid freshRequestId = DomainId.New();
        FleetReconcileRules.PointAtFreshAsk(record, freshRequestId, automatic: false, Now.AddDays(5));

        record.RequestId.Should().Be(freshRequestId, "a terminal envelope for the superseded ask must not close this one");
        record.CompletedAt.Should().BeNull();
        record.FirstAnswerAt.Should().BeNull();
        record.EnvelopesRead.Should().Be(0);
        record.RecordsApplied.Should().Be(0);
        record.AnswerEnvelopeCount.Should().BeNull();
        record.HeldTailOnlyStreams.Should().Be(0);
        record.ReAskedAt.Should().BeNull("a human asking again restarts the ladder rather than spending its one re-ask");
    }

    [Fact]
    public void The_terminal_envelope_is_a_recognized_replication_protocol_kind()
    {
        MessageKind parsed = MessageKind.Parse("events-answer-complete");

        parsed.Should().Be(MessageKind.EventsAnswerComplete);
        parsed.IsRecognized.Should().BeTrue();
        parsed.IsReplicationProtocol.Should().BeTrue("h9k messages never shows a protocol envelope as though it were a note");

        // What a build predating this kind does with it — an unrecognized kind round-trips as
        // itself rather than being refused, so the envelope is stored and skipped and the reconcile
        // stays incomplete rather than wrongly closed — is MessageKindTests's own
        // Parse_RoundTripsAnUnrecognizedKindRatherThanFailing, through this very seam. Asserting it
        // again here would be the same proof twice (independent pre-PR review, cycle 1, conformance
        // lens, low).
    }

    [Fact]
    public void The_answer_complete_body_round_trips_and_a_malformed_one_is_null()
    {
        string body = EventReplicationCodec.EncodeAnswerComplete(
            new EventReplicationCodec.EventsAnswerCompleteRecord(RequestId, 165));

        EventReplicationCodec.DecodeAnswerComplete(body).Should()
            .Be(new EventReplicationCodec.EventsAnswerCompleteRecord(RequestId, 165));
        EventReplicationCodec.DecodeAnswerComplete("not json at all").Should().BeNull();
    }

    [Fact]
    public void A_reconcile_record_is_keyed_by_the_pair_so_the_same_pair_always_resolves_to_one_document()
    {
        Guid first = EventReplicationStreamId.ForFleetReconcile(SiblingNodeId, ProjectId);
        Guid again = EventReplicationStreamId.ForFleetReconcile(SiblingNodeId, ProjectId);
        Guid otherProject = EventReplicationStreamId.ForFleetReconcile(SiblingNodeId, DomainId.New());
        Guid otherPeer = EventReplicationStreamId.ForFleetReconcile(SecondSiblingNodeId, ProjectId);

        again.Should().Be(first);
        otherProject.Should().NotBe(first, "a reconcile is per project, not per peer");
        otherPeer.Should().NotBe(first, "and per peer, not per project");
        first.Should().NotBe(
            EventReplicationStreamId.ForCatchUpInboxCursor(SiblingNodeId, ProjectId),
            "each bookkeeping document's key is tagged with its own kind");
    }

    private static readonly Guid ProjectId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid RequestId = Guid.Parse("66666666-6666-6666-6666-666666666666");
}
