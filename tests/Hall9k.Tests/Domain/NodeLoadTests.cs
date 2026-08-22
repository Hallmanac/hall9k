using FluentAssertions;
using Hall9k.Daemon.Dispatch;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The concurrency ceiling's counting rule (Decisions Log #64), which decides how many agent
/// sessions this machine is asked to hold at once. It is pure, so it is read here without a
/// database.
/// </summary>
public sealed class NodeLoadTests
{
    private static readonly Guid ThisNode = DomainId.New();
    private static readonly Guid AnotherNode = DomainId.New();

    [Fact]
    public void A_live_run_holds_a_slot_and_a_finished_one_does_not()
    {
        Guid running = DomainId.New();
        Guid verifying = DomainId.New();
        Guid completed = DomainId.New();
        RunListItem live = Run(running, RunState.Running);
        RunListItem underGates = Run(verifying, RunState.Verifying);

        IReadOnlyCollection<LiveSlot> held = NodeLoad.LiveSlots(
            ThisNode,
            [Lease(running), Lease(verifying), Lease(completed)],
            [live, underGates, Run(completed, RunState.Completed)]);

        held.Should().BeEquivalentTo([
            new LiveSlot(running, live.Id),
            new LiveSlot(verifying, underGates.Id),
        ]);
    }

    [Fact]
    public void Two_live_runs_of_one_task_hold_two_slots_because_they_are_two_session_trees()
    {
        // Reachable on an ordinary restart: startup adoption resumes a run's review cycle, the
        // sweep that follows finds the lease stale and requeues the task without failing that
        // run (its build process is long gone), and the next claim launches a second session
        // tree for the same task. Counting per task read that as one slot and let the node go a
        // session tree over its ceiling (pre-PR review of this branch, 2026-08-22).
        Guid taskId = DomainId.New();
        RunListItem resumed = Run(taskId, RunState.UnderReview);
        RunListItem fresh = Run(taskId, RunState.Dispatched);
        fresh.LeaseGeneration = 2;

        NodeLoad.LiveSlots(ThisNode, [Lease(taskId, generation: 2)], [resumed, fresh])
            .Should().BeEquivalentTo([new LiveSlot(taskId, resumed.Id), new LiveSlot(taskId, fresh.Id)]);
    }

    [Fact]
    public void A_review_session_costs_its_own_runs_slot_and_not_a_second_one()
    {
        // The whole reason the number can be trusted: a review cycle spawns review and fix
        // sessions inside the run that owns them, so a ceiling of three means at most three
        // session trees rather than three builds plus however many reviews they spawn.
        Guid taskId = DomainId.New();
        RunListItem run = Run(taskId, RunState.UnderReview);

        NodeLoad.LiveSlots(ThisNode, [Lease(taskId)], [run])
            .Should().ContainSingle().Which.Should().Be(new LiveSlot(taskId, run.Id));
    }

    [Fact]
    public void A_parked_run_releases_its_slot_even_though_it_keeps_its_lease()
    {
        // A park is waiting on a human with no process resident. The lease deliberately stays
        // alive so the expiry sweep cannot requeue the task out from under the worktree
        // (Decisions Log #24), which is exactly why counting leases would have held the machine
        // hostage to work nobody is doing.
        Guid parked = DomainId.New();

        NodeLoad.LiveSlots(ThisNode, [Lease(parked)], [Run(parked, RunState.ReviewParked)])
            .Should().BeEmpty();
    }

    [Fact]
    public void A_task_waiting_on_a_merge_observation_holds_no_slot()
    {
        // Closeout deletes the lease when the pull request opens, and the run states there are
        // not live states: nothing of that task is resident on the machine while GitHub thinks.
        Guid awaiting = DomainId.New();

        NodeLoad.LiveSlots(ThisNode, [], [Run(awaiting, RunState.AwaitingReview)])
            .Should().BeEmpty();
    }

    [Fact]
    public void A_claim_whose_run_document_has_not_landed_yet_already_holds_its_slot()
    {
        // The dispatch handoff: TaskClaimed commits in its own transaction and RunDispatched
        // follows seconds later. A session about to exist has to hold a slot, or a sweep that
        // runs inside that window claims straight over the ceiling.
        Guid claimed = DomainId.New();

        NodeLoad.LiveSlots(ThisNode, [Lease(claimed)], [])
            .Should().ContainSingle().Which.Should().Be(new LiveSlot(claimed, RunId: null));
    }

    [Fact]
    public void A_lease_at_a_newer_generation_than_its_recorded_run_is_the_handoff_again()
    {
        // A follow-up claim increments the generation, so the predecessor's run document says
        // nothing about whether the successor's session exists yet.
        Guid taskId = DomainId.New();
        RunListItem predecessor = Run(taskId, RunState.Superseded);
        predecessor.LeaseGeneration = 1;

        NodeLoad.LiveSlots(ThisNode, [Lease(taskId, generation: 2)], [predecessor])
            .Should().ContainSingle().Which.Should().Be(new LiveSlot(taskId, RunId: null));
    }

    [Fact]
    public void Another_nodes_work_is_another_nodes_memory()
    {
        Guid theirs = DomainId.New();
        RunListItem run = Run(theirs, RunState.Running);
        run.NodeId = AnotherNode;

        NodeLoad.LiveSlots(ThisNode, [Lease(theirs, nodeId: AnotherNode)], [run]).Should().BeEmpty();
    }

    [Fact]
    public void Capacity_never_reads_negative_when_a_resumed_park_puts_the_node_over()
    {
        // Resolving a park hands a worktree back to a session while the node is already full;
        // refusing the human's explicit resume would be the worse trade, so the overshoot is
        // allowed and simply reads as no capacity.
        NodeLoad over = new(LiveRuns: 4, MaxConcurrentAgentSessions: 3 * NodeLoad.PeakAgentSessionsPerRun);

        over.Capacity.Should().Be(0);
        over.AtCeiling.Should().BeTrue();
    }

    [Fact]
    public void A_run_reserves_every_session_its_tree_can_hold_at_once()
    {
        // The ceiling is set in sessions because sessions are what the machine runs out of, and
        // a run tree is not one session: a review cycle spawns every active lens together and
        // waits on them together (log #59). A run ceiling derived by ignoring that let a node
        // configured for three sessions hold six Opus processes — more than the four that caused
        // the origin OOM (pre-PR review of this branch, 2026-08-22).
        NodeLoad.PeakAgentSessionsPerRun.Should().Be(ReviewLens.CycleLenses.Count,
            "a run under review is one resident session per lens, and appending a third lens has "
            + "to tighten the ceiling rather than quietly enlarge what the machine is asked to hold");

        NodeLoad node = new(LiveRuns: 1, MaxConcurrentAgentSessions: 6);

        node.LiveAgentSessions.Should().Be(NodeLoad.PeakAgentSessionsPerRun,
            "the peak is reserved from the moment the run is claimed, not counted once it is "
            + "reached — a run that is building today reviews on this same machine");
        node.MaxConcurrentRuns.Should().Be(6 / NodeLoad.PeakAgentSessionsPerRun);
        node.Capacity.Should().Be((6 / NodeLoad.PeakAgentSessionsPerRun) - 1);
    }

    [Fact]
    public void A_session_budget_too_small_for_one_run_still_dispatches_one()
    {
        // A budget below one run's peak cannot be honoured by any schedule that does work at
        // all, and a node that silently dispatches nothing is the worse of the two answers: the
        // human sees a queue that never moves with no failure to point at.
        NodeLoad starved = new(LiveRuns: 0, MaxConcurrentAgentSessions: 1);

        starved.MaxConcurrentRuns.Should().Be(1);
        starved.Capacity.Should().Be(1);
        starved.AtCeiling.Should().BeFalse();
    }

    private static TaskLease Lease(Guid taskId, int generation = 1, Guid? nodeId = null) => new()
    {
        Id = taskId,
        NodeId = nodeId ?? ThisNode,
        LeaseGeneration = generation,
        HeartbeatAt = DateTimeOffset.UtcNow,
    };

    private static RunListItem Run(Guid taskId, RunState state) => new()
    {
        Id = DomainId.New(),
        TaskId = taskId,
        NodeId = ThisNode,
        LeaseGeneration = 1,
        State = state,
    };
}
