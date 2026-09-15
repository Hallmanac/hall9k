using FluentAssertions;
using Hall9k.Connectors.Messaging;
using Hall9k.Daemon.Messaging;
using Hall9k.Domain.Features.Node;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The probe's own skip decision (idea 202383dc, M1b): <see cref="MessageSweepEngine.SendersToRead"/>
/// is the pure logic behind "the probe skips the fetch when nothing moved" — no document store, no
/// transport, no sweep, just the tip comparison itself.
/// </summary>
public sealed class MessageSweepEngineTests
{
    private const string RepositoryPath = "repo-under-test";
    private static readonly Guid MyNodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SenderNodeId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void A_sender_whose_tip_never_moved_is_skipped()
    {
        Dictionary<(string, Guid), string> lastKnownTips = new() { [(RepositoryPath, SenderNodeId)] = "tip-1" };
        MessageOutboxTip[] tips = [new MessageOutboxTip(SenderNodeId, "tip-1")];

        IReadOnlyList<MessageOutboxTip> toRead =
            MessageSweepEngine.SendersToRead(tips, MyNodeId, RepositoryPath, lastKnownTips);

        toRead.Should().BeEmpty("the tip is identical to what the last sweep already saw");
    }

    [Fact]
    public void A_sender_whose_tip_moved_is_read()
    {
        Dictionary<(string, Guid), string> lastKnownTips = new() { [(RepositoryPath, SenderNodeId)] = "tip-1" };
        MessageOutboxTip[] tips = [new MessageOutboxTip(SenderNodeId, "tip-2")];

        IReadOnlyList<MessageOutboxTip> toRead =
            MessageSweepEngine.SendersToRead(tips, MyNodeId, RepositoryPath, lastKnownTips);

        toRead.Should().ContainSingle(tip => tip.SenderNodeId == SenderNodeId && tip.Tip == "tip-2");
    }

    [Fact]
    public void A_sender_never_probed_before_is_read()
    {
        Dictionary<(string, Guid), string> lastKnownTips = [];
        MessageOutboxTip[] tips = [new MessageOutboxTip(SenderNodeId, "tip-1")];

        IReadOnlyList<MessageOutboxTip> toRead =
            MessageSweepEngine.SendersToRead(tips, MyNodeId, RepositoryPath, lastKnownTips);

        toRead.Should().ContainSingle();
    }

    [Fact]
    public void This_nodes_own_outbox_is_never_read_back_as_an_inbox_target()
    {
        Dictionary<(string, Guid), string> lastKnownTips = [];
        MessageOutboxTip[] tips = [new MessageOutboxTip(MyNodeId, "tip-1")];

        IReadOnlyList<MessageOutboxTip> toRead =
            MessageSweepEngine.SendersToRead(tips, MyNodeId, RepositoryPath, lastKnownTips);

        toRead.Should().BeEmpty("a project-addressed broadcast is not something the sender itself unreads and handles");
    }

    [Fact]
    public void A_different_repository_path_never_reuses_another_repository_s_known_tip()
    {
        Dictionary<(string, Guid), string> lastKnownTips = new() { [("some-other-repo", SenderNodeId)] = "tip-1" };
        MessageOutboxTip[] tips = [new MessageOutboxTip(SenderNodeId, "tip-1")];

        IReadOnlyList<MessageOutboxTip> toRead =
            MessageSweepEngine.SendersToRead(tips, MyNodeId, RepositoryPath, lastKnownTips);

        toRead.Should().ContainSingle("the known tip belongs to a different repository entirely");
    }

    [Fact]
    public void A_registered_node_with_no_hold_standing_and_nothing_pending_is_idle()
    {
        // LaunchHoldEngine.CurrentHoldAsync returns the node's own NodeDetails once it has ever
        // registered, whether or not a hold is standing — testing that result for non-null alone
        // used to make every registered node read as active forever (independent pre-PR review,
        // cycle 1, both lenses).
        NodeDetails registeredNoHold = new() { LaunchHoldActive = false };

        bool activeCadence = MessageSweepEngine.ComputeActiveCadence(hasUnflushedOrUnread: false, registeredNoHold);

        activeCadence.Should().BeFalse("a registered node with nothing pending and no standing hold is idle");
    }

    [Fact]
    public void A_standing_launch_hold_forces_the_active_cadence_even_with_nothing_pending()
    {
        NodeDetails withHold = new() { LaunchHoldActive = true };

        bool activeCadence = MessageSweepEngine.ComputeActiveCadence(hasUnflushedOrUnread: false, withHold);

        activeCadence.Should().BeTrue("a node-wide launch hold is held work, even with nothing unflushed or unread");
    }

    [Fact]
    public void Unflushed_or_unread_work_forces_the_active_cadence_with_no_hold_at_all()
    {
        bool activeCadence = MessageSweepEngine.ComputeActiveCadence(hasUnflushedOrUnread: true, currentHold: null);

        activeCadence.Should().BeTrue();
    }
}
