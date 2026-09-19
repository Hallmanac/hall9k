using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <c>h9k task take --force</c>'s own rollback reading (idea 202383dc, item 4): every path that
/// abandons the command after the tracker take and the ledger override have already landed asks
/// <see cref="TaskTakeCommand.ResolveUndo"/> what to give back, so the two undos can never
/// disagree about who holds the task. Driven here rather than through the command itself because
/// two of these four holder shapes — the stream naming this node, and it naming a third one — are
/// reachable only through a commit that lands and then throws, or a claim racing a rollback; the
/// integration tests cover the other two end to end.
/// </summary>
public sealed class TaskTakeUndoResolutionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ThisNode = DomainId.New();
    private static readonly Guid HolderAtEntry = DomainId.New();
    private static readonly TaskRecordHolder PreviousHolder =
        new("holder-fingerprint", HolderAtEntry, "OLD-NODE", Now.AddHours(-6));

    [Fact]
    public void A_stream_that_already_names_this_node_rolls_nothing_back()
    {
        // The append committed and the commit then failed on the way out, or this node's own
        // daemon claimed on the strength of the override: the takeover is in force, and undoing
        // the ledger would split the two apart with no way back from either side.
        TaskTakeCommand.TakeUndo undo = TaskTakeCommand.ResolveUndo(
            ThisNode, ThisNode, HolderAtEntry, PreviousHolder);

        undo.RollBackLedger.Should().BeFalse();
        undo.KeepTrackerBecause.Should().NotBeNull("the assignment is what a claim on this task now rests on");
    }

    [Fact]
    public void A_stream_that_names_nobody_releases_the_ledger_and_gives_the_assignment_back()
    {
        TaskTakeCommand.TakeUndo undo = TaskTakeCommand.ResolveUndo(
            null, ThisNode, HolderAtEntry, PreviousHolder);

        undo.RollBackLedger.Should().BeTrue();
        undo.LedgerTarget.Should().BeNull(
            "restoring a holder the domain no longer recognises would leave the ledger naming a node nothing can clear");
        undo.KeepTrackerBecause.Should().BeNull("nothing holds this task, so nothing can be passing the gate on that assignment");
    }

    [Fact]
    public void A_stream_still_naming_the_holder_this_take_set_out_to_override_puts_both_halves_back()
    {
        TaskTakeCommand.TakeUndo undo = TaskTakeCommand.ResolveUndo(
            HolderAtEntry, ThisNode, HolderAtEntry, PreviousHolder);

        undo.RollBackLedger.Should().BeTrue();
        undo.LedgerTarget.Should().Be(PreviousHolder);
        undo.KeepTrackerBecause.Should().BeNull("nothing moved, so the item goes back to the unassigned state this take found");
    }

    [Fact]
    public void A_ledger_holder_that_disagrees_with_the_stream_is_released_rather_than_written_back()
    {
        // The ledger's own previous holder named somebody the task's stream does not: writing it
        // back would assert a holder the domain never recorded, the same hazard the null case
        // above avoids.
        TaskRecordHolder someoneElse = new("other-fingerprint", DomainId.New(), "OTHER-NODE", Now.AddHours(-6));

        TaskTakeCommand.TakeUndo undo = TaskTakeCommand.ResolveUndo(
            HolderAtEntry, ThisNode, HolderAtEntry, someoneElse);

        undo.RollBackLedger.Should().BeTrue();
        undo.LedgerTarget.Should().BeNull();
    }

    [Fact]
    public void A_stream_naming_a_holder_this_take_never_wrote_keeps_the_assignment_that_holder_may_be_claiming_on()
    {
        // The high-severity defect this guards against (adversarial pre-PR review, cycle 3): a
        // holder that arrived while this command was running can only have passed a tracker-assignee
        // gate on the very assignment this take wrote, because the item was unassigned before it.
        // Clearing it back off strands that holder with a task nothing can dispatch.
        TaskTakeCommand.TakeUndo undo = TaskTakeCommand.ResolveUndo(
            DomainId.New(), ThisNode, HolderAtEntry, PreviousHolder);

        undo.RollBackLedger.Should().BeTrue();
        undo.LedgerTarget.Should().BeNull();
        undo.KeepTrackerBecause.Should().NotBeNull();
    }
}
