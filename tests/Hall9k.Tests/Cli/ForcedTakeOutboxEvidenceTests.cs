using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The clause <c>h9k task take --force</c> prints about the holder's own outbox (task 054d5ab0).
/// It reads the events-replication cursor this node keeps for that sender, because that is the
/// record of what was actually read here: the first forced take between real nodes printed
/// "nothing has ever been heard from its outbox on this node" about a holder whose fourteen
/// envelopes this node had already read and applied, since the view it read counted only envelopes
/// addressed to this node as messages. An operator overriding a live holder acts on this sentence,
/// so the false negative is the defect these cases pin shut.
/// </summary>
public sealed class ForcedTakeOutboxEvidenceTests
{
    private static readonly DateTimeOffset InspectedAt = new(2026, 9, 19, 18, 33, 14, TimeSpan.Zero);
    private static readonly Guid Holder = DomainId.New();
    private static readonly Guid Project = DomainId.New();

    [Fact]
    public void A_holder_whose_outbox_this_node_has_read_is_named_by_seq_and_time()
    {
        EventReplicationInboxCursor cursor = new()
        {
            Id = EventReplicationStreamId.ForInboxCursor(Holder, Project),
            SenderNodeId = Holder,
            ProjectId = Project,
            HighestSeqInspected = 14,
            HighestSeqInspectedAt = InspectedAt,
        };

        string evidence = TaskTakeCommand.DescribeOutboxAsObserved(cursor);

        evidence.Should().Contain("seq 14", "the operator needs to know how far this node got");
        evidence.Should().Contain("2026-09-19 18:33:14Z", "and when it got there");
        evidence.Should().NotContain(
            "never", "fourteen envelopes were read here, which is the opposite of never hearing from it");
    }

    [Fact]
    public void A_holder_this_node_has_never_read_says_so()
    {
        string evidence = TaskTakeCommand.DescribeOutboxAsObserved(null);

        evidence.Should().Be("nothing from its outbox has ever been read on this node");
    }

    [Fact]
    public void A_cursor_standing_at_nothing_read_is_the_same_fact_as_no_cursor_at_all()
    {
        // A sweep that could not vouch for the sender stores a cursor without ever inspecting an
        // envelope. Nothing has been read either way, and the two must not read differently.
        EventReplicationInboxCursor cursor = new()
        {
            Id = EventReplicationStreamId.ForInboxCursor(Holder, Project),
            SenderNodeId = Holder,
            ProjectId = Project,
            HighestSeqInspected = 0,
            SenderIgnored = true,
            IgnoredReason = "no node file vouches for this sender's outbox",
            IgnoredAt = InspectedAt,
        };

        TaskTakeCommand.DescribeOutboxAsObserved(cursor)
            .Should().Be(TaskTakeCommand.DescribeOutboxAsObserved(null));
    }

    [Fact]
    public void A_cursor_written_before_this_node_stamped_one_names_the_seq_and_admits_the_missing_time()
    {
        EventReplicationInboxCursor cursor = new()
        {
            Id = EventReplicationStreamId.ForInboxCursor(Holder, Project),
            SenderNodeId = Holder,
            ProjectId = Project,
            HighestSeqInspected = 9,
            HighestSeqInspectedAt = null,
        };

        string evidence = TaskTakeCommand.DescribeOutboxAsObserved(cursor);

        evidence.Should().Contain("seq 9");
        evidence.Should().Contain(
            "did not record", "an unstamped cursor still proves the outbox was read, so it must not read as never");
    }
}
