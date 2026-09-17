using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Documents;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// Idea 202383dc, M2a: a replicated claim by another owner renders as HeldElsewhere in h9k status
/// and h9k task show — both surfaces compose through <see cref="TaskStatusComposer"/>, so this is
/// the one place the rendering rule needs a test. <see cref="StatusFixtures.Context"/> only ever
/// populates <see cref="TaskStatusContext.NodeMachines"/> from a fixture's own run's
/// <see cref="Hall9k.Domain.Features.Run.Projections.RunDetails.NodeId"/> — exactly the real
/// shape a replicated claim leaves behind, since the claiming node's own <c>NodeDetails</c> never
/// replicates and this node never dispatched a run for a task it does not hold.
/// </summary>
public sealed class HeldElsewhereRenderingTests
{
    [Fact]
    public void A_claim_by_a_node_this_install_has_never_registered_renders_HeldElsewhere()
    {
        Guid foreignNodeId = DomainId.New();
        TaskListItem task = StatusFixtures.Task(TaskState.Claimed, claimedByNodeId: foreignNodeId);

        TaskStatusRow row = StatusFixtures.Compose(task, run: null);

        row.State.Should().Be(LifecycleState.HeldElsewhere);
        row.Group.Should().Be(AttentionBucket.HeldElsewhere);
        row.DetailMarkup.Should().Contain(line => line.Contains("held by") && line.Contains("since"));
    }

    [Fact]
    public void An_interactive_claim_is_never_read_as_held_elsewhere()
    {
        // The interactive-claim sentinel (Guid.Empty) is a human's own claim on this node, never a
        // foreign one — TaskListItem.IsInteractiveClaim's own discriminator.
        TaskListItem task = StatusFixtures.Task(TaskState.Claimed, claimedByNodeId: Guid.Empty);

        TaskStatusRow row = StatusFixtures.Compose(task, run: null);

        row.State.Should().NotBe(LifecycleState.HeldElsewhere);
    }

    [Fact]
    public void A_claim_by_a_node_this_install_has_registered_is_not_held_elsewhere()
    {
        // A run belonging to the claiming node makes StatusFixtures.Context register that node in
        // NodeMachines — the local-claim shape, never a replicated one.
        Guid localNodeId = DomainId.New();
        TaskListItem task = StatusFixtures.Task(
            TaskState.Claimed, claimedByNodeId: localNodeId, runId: DomainId.New());
        RunDetails run = StatusFixtures.Run(task.CurrentRunId!.Value, RunState.Running);
        run.NodeId = localNodeId;

        TaskStatusRow row = StatusFixtures.Compose(task, run);

        row.State.Should().NotBe(LifecycleState.HeldElsewhere);
    }

    /// <summary>
    /// A replicated Run event can land locally for a task another node holds (Run events are
    /// project-scoped too), so a HeldElsewhere row's own <c>run</c> may carry a real, if stale,
    /// state — Failed here, deliberately, since that is exactly the shape that would otherwise
    /// read NeedsYou. Attention must stay quiet regardless: the row belongs to another node, and
    /// no lever this column could offer (retry, resolve, answer a question) is this node's to
    /// pull. Built directly against <see cref="TaskStatusContext"/> rather than
    /// <see cref="StatusFixtures.Compose"/>: that helper derives <c>NodeMachines</c> from the run's
    /// own <see cref="RunDetails.NodeId"/> for fixture convenience, which would register the
    /// foreign node as if this install had it — production builds <c>NodeMachines</c> from a real,
    /// separate query of this install's own <c>NodeDetails</c> rows, never from a run's own field.
    /// </summary>
    [Fact]
    public void A_held_elsewhere_row_never_reads_needs_you_from_a_replicated_runs_own_state()
    {
        Guid foreignNodeId = DomainId.New();
        Guid runId = DomainId.New();
        TaskListItem task = StatusFixtures.Task(TaskState.Claimed, runId: runId, claimedByNodeId: foreignNodeId);
        RunDetails run = StatusFixtures.Run(runId, RunState.Failed);
        run.NodeId = foreignNodeId;

        TaskStatusContext context = new(
            new Dictionary<Guid, RunDetails> { [runId] = run },
            new Dictionary<Guid, RunActivity>(),
            new Dictionary<Guid, string>(),
            new Dictionary<Guid, string>(),
            new Dictionary<Guid, string>(), // this install has never registered the foreign node
            new StubSessionObserver(SessionLiveness.NotApplicable),
            StatusFixtures.ThisMachine);

        TaskStatusRow row = TaskStatusComposer.Compose(task, context, StatusFixtures.Now);

        row.State.Should().Be(LifecycleState.HeldElsewhere);
        row.Attention.Should().Be(TaskAttention.None);
        row.Group.Should().Be(AttentionBucket.HeldElsewhere);
    }
}
