using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="RunKillCommand.PartitionSessions"/> and <see cref="RunKillCommand.BuildFailureReason"/>
/// are the pure decisions inside <c>h9k run kill</c>'s otherwise DB-bound execute path: which of a
/// run's recorded sessions this machine may safely terminate, and the free-text reason recorded on
/// the task's own <c>TaskFailed</c> (task: a run can be killed without killing its task).
/// </summary>
public sealed class RunKillCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_session_on_this_machine_with_a_recorded_start_time_is_killable()
    {
        ActiveSession session = new(AgentRole.Build, ReviewLens.Unknown, 4242, Now);

        (IReadOnlyList<ActiveSession> killable, IReadOnlyList<ActiveSession> unreachable) =
            RunKillCommand.PartitionSessions([session], runOnThisMachine: true, thisMachine: "here");

        killable.Should().ContainSingle().Which.Should().BeSameAs(session);
        unreachable.Should().BeEmpty();
    }

    [Fact]
    public void A_session_with_no_recorded_start_time_is_never_killed_blindly()
    {
        // A resumed build session's own event records only a pid (ActiveSession's own doc) — a
        // bare pid is a lie waiting to happen (Decisions Log #2), so this can never be verified
        // as the process this machine actually spawned.
        ActiveSession session = new(AgentRole.Build, ReviewLens.Unknown, 4242, StartedAt: null);

        (IReadOnlyList<ActiveSession> killable, IReadOnlyList<ActiveSession> unreachable) =
            RunKillCommand.PartitionSessions([session], runOnThisMachine: true, thisMachine: "here");

        killable.Should().BeEmpty();
        unreachable.Should().ContainSingle().Which.Should().BeSameAs(session);
    }

    [Fact]
    public void A_run_not_on_this_machine_leaves_every_session_unreachable()
    {
        // Interactive sessions aside (they carry their own MachineName), an ordinary
        // daemon-dispatched session's liveness is only ever answerable on the machine that
        // spawned it — this machine's process table says nothing about another node's pid.
        ActiveSession session = new(AgentRole.Build, ReviewLens.Unknown, 4242, Now);

        (IReadOnlyList<ActiveSession> killable, IReadOnlyList<ActiveSession> unreachable) =
            RunKillCommand.PartitionSessions([session], runOnThisMachine: false, thisMachine: "here");

        killable.Should().BeEmpty();
        unreachable.Should().ContainSingle().Which.Should().BeSameAs(session);
    }

    [Fact]
    public void A_sessions_own_machine_name_overrides_the_runs_when_it_is_recorded()
    {
        // Mirrors TaskStatusComposer.SessionOnThisMachine: an interactive session's own
        // MachineName is authoritative even when the run-level check (NodeId's own sentinel for
        // an interactive claim) would otherwise read false.
        ActiveSession onThisMachine = new(AgentRole.Interactive, ReviewLens.Unknown, 111, Now, MachineName: "here");
        ActiveSession elsewhere = new(AgentRole.Interactive, ReviewLens.Unknown, 222, Now, MachineName: "elsewhere");

        (IReadOnlyList<ActiveSession> killable, IReadOnlyList<ActiveSession> unreachable) =
            RunKillCommand.PartitionSessions([onThisMachine, elsewhere], runOnThisMachine: false, thisMachine: "here");

        killable.Should().ContainSingle().Which.Should().BeSameAs(onThisMachine);
        unreachable.Should().ContainSingle().Which.Should().BeSameAs(elsewhere);
    }

    [Fact]
    public void Two_review_lens_sessions_partition_independently()
    {
        // A review cycle dispatches one pass per active track (Decisions Log #59) — a lingering
        // conformance pass must not block killing an already-verifiable adversarial one, or vice
        // versa.
        ActiveSession killableSession = new(AgentRole.Review, ReviewLens.Adversarial, 1, Now);
        ActiveSession unverifiable = new(AgentRole.Review, ReviewLens.Conformance, 2, StartedAt: null);

        (IReadOnlyList<ActiveSession> killable, IReadOnlyList<ActiveSession> unreachable) =
            RunKillCommand.PartitionSessions([killableSession, unverifiable], runOnThisMachine: true, thisMachine: "here");

        killable.Should().ContainSingle().Which.Should().BeSameAs(killableSession);
        unreachable.Should().ContainSingle().Which.Should().BeSameAs(unverifiable);
    }

    [Fact]
    public void An_ordinary_dispatchs_own_node_id_is_the_physical_node()
    {
        Guid nodeId = Guid.NewGuid();

        RunKillCommand.ResolvePhysicalNodeId(nodeId, dispatchingNodeId: Guid.NewGuid())
            .Should().Be(nodeId);
    }

    [Fact]
    public void A_sentinel_runs_dispatching_node_id_is_the_physical_node()
    {
        // h9k task start's deliberate claim and auto-pr-review's "now" speed both dispatch under
        // the ceiling-exempt Guid.Empty sentinel — NodeId itself names no physical daemon there,
        // so DispatchingNodeId is the only field that does (RunDispatched's own doc).
        Guid dispatchingNodeId = Guid.NewGuid();

        RunKillCommand.ResolvePhysicalNodeId(nodeId: Guid.Empty, dispatchingNodeId)
            .Should().Be(dispatchingNodeId);
    }

    [Fact]
    public void A_blank_reason_records_an_honest_default_rather_than_guessing()
    {
        RunKillCommand.BuildFailureReason(null).Should().Be("Killed by h9k run kill.");
        RunKillCommand.BuildFailureReason("   ").Should().Be("Killed by h9k run kill.");
    }

    [Fact]
    public void A_given_reason_is_carried_onto_the_task_stream()
    {
        RunKillCommand.BuildFailureReason("Shedding load before a reinstall")
            .Should().Be("Killed by h9k run kill: Shedding load before a reinstall");
    }
}
