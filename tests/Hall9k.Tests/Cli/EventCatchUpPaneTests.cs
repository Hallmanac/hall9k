using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Replication;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// What <c>h9k status</c> says about catch-up. The pane showed outstanding asks only until this
/// suite's subject existed, so a request a peer had declined simply vanished from it — which is
/// how one node came to be told, ten hours after a decline it never printed, that the old ask was
/// still on its way (2026-09-19 23:36).
/// </summary>
public sealed class EventCatchUpPaneTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 35, 0, TimeSpan.Zero);
    private static readonly HeldTailSummary NoHeldTail = new(0, 0);

    private static EventCatchUpRequest StreamRequest(Guid streamId, DateTimeOffset sentAt) => new()
    {
        Id = DomainId.New(),
        ProjectId = DomainId.New(),
        ForStreamId = streamId,
        Candidates = [],
        SentAt = sentAt,
    };

    [Fact]
    public void Says_nothing_when_there_is_nothing_to_say()
    {
        EventCatchUpPane.ComposeLines([], [], NoHeldTail, Now).Should().BeEmpty();
    }

    [Fact]
    public void Lists_an_outstanding_broadcast_beside_the_ask_it_is_waiting_on()
    {
        Guid streamId = DomainId.New();

        IReadOnlyList<string> lines = EventCatchUpPane.ComposeLines(
            [StreamRequest(streamId, Now.AddMinutes(-3))], [], NoHeldTail, Now);

        lines.Should().ContainSingle().Which.Should().Be(
            $"catch-up outstanding for stream {DomainId.Short(streamId)} — broadcast to the whole project "
            + $"(sent {Now.AddMinutes(-3):u})");
    }

    [Fact]
    public void Names_the_node_that_declined_a_closed_request_and_when_it_said_so()
    {
        Guid streamId = DomainId.New();
        Guid mac = DomainId.New();
        DateTimeOffset declinedAt = Now.AddHours(-3);
        EventCatchUpRequest declined = StreamRequest(streamId, Now.AddHours(-4));
        declined.AnsweredAt = declinedAt;
        declined.Declines = [new EventCatchUpDecline(mac, declinedAt, "nothing held here matches this request")];
        declined.ClosedByDecline = declined.Declines[0];

        IReadOnlyList<string> lines = EventCatchUpPane.ComposeLines([], [declined], NoHeldTail, Now);

        lines.Should().ContainSingle().Which.Should().Be(
            $"catch-up for stream {DomainId.Short(streamId)} declined by {DomainId.Short(mac)} "
            + $"at {declinedAt:u} (nothing held here matches this request)");
    }

    /// <summary>
    /// The reason text crossed the wire from another node, and the console renderer escapes
    /// Spectre markup but not control characters — so a newline or an ANSI escape sequence in one
    /// would break this pane's own line structure or restyle everything printed after it
    /// (independent pre-PR review, cycle 1, adversarial lens). Flattened to spaces rather than
    /// dropped, so the words a peer actually sent still read as themselves.
    /// </summary>
    [Fact]
    public void Flattens_a_reason_carrying_control_characters_into_one_printable_line()
    {
        Guid streamId = DomainId.New();
        Guid mac = DomainId.New();
        DateTimeOffset declinedAt = Now.AddHours(-1);
        EventCatchUpRequest declined = StreamRequest(streamId, Now.AddHours(-2));
        declined.AnsweredAt = declinedAt;
        declined.Declines = [new EventCatchUpDecline(mac, declinedAt, "nothing held\r\n\u001b[31mhere")];
        declined.ClosedByDecline = declined.Declines[0];

        string line = EventCatchUpPane.ComposeLines([], [declined], NoHeldTail, Now).Should().ContainSingle().Subject;

        line.Should().Be(
            $"catch-up for stream {DomainId.Short(streamId)} declined by {DomainId.Short(mac)} "
            + $"at {declinedAt:u} (nothing held   [31mhere)");
        line.Should().NotContainAny("\n", "\r", "\u001b");
    }

    [Fact]
    public void Cuts_a_reason_nobody_could_read_off_at_a_length_that_still_fits_a_pane()
    {
        Guid mac = DomainId.New();
        DateTimeOffset declinedAt = Now.AddHours(-1);
        EventCatchUpRequest declined = StreamRequest(DomainId.New(), Now.AddHours(-2));
        declined.AnsweredAt = declinedAt;
        declined.Declines = [new EventCatchUpDecline(mac, declinedAt, new string('x', 5_000))];
        declined.ClosedByDecline = declined.Declines[0];

        string line = EventCatchUpPane.ComposeLines([], [declined], NoHeldTail, Now).Should().ContainSingle().Subject;

        line.Should().EndWith("…)").And.Contain(new string('x', 200));
        line.Should().NotContain(new string('x', 201));
    }

    [Fact]
    public void Counts_the_other_members_that_also_said_they_hold_nothing()
    {
        Guid mac = DomainId.New();
        EventCatchUpRequest declined = StreamRequest(DomainId.New(), Now.AddHours(-4));
        declined.AnsweredAt = Now.AddHours(-3);
        declined.Declines =
        [
            new EventCatchUpDecline(mac, Now.AddHours(-3), "nothing here"),
            new EventCatchUpDecline(DomainId.New(), Now.AddHours(-2), "nothing here"),
            new EventCatchUpDecline(DomainId.New(), Now.AddHours(-2), "nothing here"),
        ];
        declined.ClosedByDecline = declined.Declines[0];

        EventCatchUpPane.ComposeLines([], [declined], NoHeldTail, Now)
            .Should().ContainSingle().Which.Should().EndWith(", and by 2 other members");
    }

    [Fact]
    public void Forgets_a_decline_older_than_a_day()
    {
        EventCatchUpRequest declined = StreamRequest(DomainId.New(), Now.AddDays(-2));
        DateTimeOffset declinedAt = Now - EventCatchUpPane.DeclineWindow - TimeSpan.FromMinutes(1);
        declined.AnsweredAt = declinedAt;
        declined.Declines = [new EventCatchUpDecline(DomainId.New(), declinedAt, "nothing here")];
        declined.ClosedByDecline = declined.Declines[0];

        EventCatchUpPane.ComposeLines([], [declined], NoHeldTail, Now).Should().BeEmpty();
    }

    /// <summary>
    /// AGENTS.md's own rule: the unobserved is labelled, never filled in plausibly. A request
    /// document written before the closing decline was recorded carries no node and no time, so
    /// this pane says nothing about it rather than naming a node it never saw.
    /// </summary>
    [Fact]
    public void Says_nothing_about_a_request_whose_closing_decline_was_never_recorded()
    {
        EventCatchUpRequest legacy = StreamRequest(DomainId.New(), Now.AddHours(-4));
        legacy.AnsweredAt = Now.AddHours(-3);
        legacy.DeclinedReason = "nothing held here matches this request";

        EventCatchUpPane.ComposeLines([], [legacy], NoHeldTail, Now).Should().BeEmpty();
    }

    /// <summary>
    /// The other way a decline ends a request: the last ranked candidate declines, so the cascade
    /// exhausts with no <c>AnsweredAt</c> at all. Self-review, round one: the pane read the
    /// closed set on <c>AnsweredAt</c> alone, so this shape's own decline was recorded on the
    /// document and printed nowhere.
    /// </summary>
    [Fact]
    public void Names_the_last_candidate_that_declined_a_cascade_into_exhaustion()
    {
        Guid lastCandidate = DomainId.New();
        DateTimeOffset declinedAt = Now.AddMinutes(-20);
        EventCatchUpRequest cascade = new()
        {
            Id = DomainId.New(),
            ProjectId = DomainId.New(),
            ForOriginNodeId = DomainId.New(),
            SinceOriginSequence = 7009,
            Candidates = [DomainId.New(), lastCandidate],
            CandidateIndex = 2,
            SentAt = declinedAt,
            Exhausted = true,
        };
        cascade.Declines = [new EventCatchUpDecline(lastCandidate, declinedAt, "nothing held here")];
        cascade.ClosedByDecline = cascade.Declines[0];

        EventCatchUpPane.ComposeLines([], [cascade], NoHeldTail, Now)
            .Should().ContainSingle().Which.Should().Be(
                $"catch-up for a gap from {DomainId.Short(cascade.ForOriginNodeId!.Value)} (since 7009) "
                + $"declined by {DomainId.Short(lastCandidate)} at {declinedAt:u} (nothing held here)");
    }

    [Fact]
    public void Says_nothing_about_a_request_an_answer_actually_closed()
    {
        EventCatchUpRequest answered = StreamRequest(DomainId.New(), Now.AddHours(-4));
        answered.AnsweredAt = Now.AddHours(-3);

        EventCatchUpPane.ComposeLines([], [answered], NoHeldTail, Now).Should().BeEmpty();
    }

    [Fact]
    public void Reports_how_many_streams_are_held_tail_only_and_how_many_it_gave_up_on()
    {
        IReadOnlyList<string> lines = EventCatchUpPane.ComposeLines([], [], new HeldTailSummary(11, 3), Now);

        lines.Should().ContainSingle().Which.Should().Be(
            $"catch-up holds 11 streams tail-only, 3 given up after {EventCatchUpCoordinator.MaxHeldTailAttempts} asks");
    }

    [Fact]
    public void Reads_as_a_sentence_for_one_stream()
    {
        EventCatchUpPane.ComposeLines([], [], new HeldTailSummary(1, 0), Now)
            .Should().ContainSingle().Which.Should().StartWith("catch-up holds 1 stream tail-only, 0 given up");
    }

    [Fact]
    public void Puts_the_outstanding_asks_first_then_the_declines_newest_first_then_the_counts()
    {
        EventCatchUpRequest older = StreamRequest(DomainId.New(), Now.AddHours(-9));
        older.AnsweredAt = Now.AddHours(-8);
        older.Declines = [new EventCatchUpDecline(DomainId.New(), Now.AddHours(-8), "nothing here")];
        older.ClosedByDecline = older.Declines[0];
        EventCatchUpRequest newer = StreamRequest(DomainId.New(), Now.AddHours(-2));
        newer.AnsweredAt = Now.AddHours(-1);
        newer.Declines = [new EventCatchUpDecline(DomainId.New(), Now.AddHours(-1), "nothing here")];
        newer.ClosedByDecline = newer.Declines[0];

        IReadOnlyList<string> lines = EventCatchUpPane.ComposeLines(
            [StreamRequest(DomainId.New(), Now.AddMinutes(-1))], [older, newer], new HeldTailSummary(2, 0), Now);

        lines.Should().HaveCount(4);
        lines[0].Should().StartWith("catch-up outstanding");
        lines[1].Should().Contain($"at {Now.AddHours(-1):u}");
        lines[2].Should().Contain($"at {Now.AddHours(-8):u}");
        lines[3].Should().StartWith("catch-up holds");
    }
}
