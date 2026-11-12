using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Another node's <c>DispatchExpired</c> for a challenge or refinement session and the dispatching node's own outcome for
/// it reach every node in either order, and a node keeps no per-session history. The dispatching node's outcome wins, and
/// an expiry or outcome for a session that is no longer the draft's concern changes nothing (DraftSessionOutcomeRule).
/// Each test compares the aggregate and the projection against the same stream without the event that must change nothing.
/// </summary>
public sealed class ForeignExpiryOutcomeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 11, 45, 28, TimeSpan.Zero);
    private static readonly AgentModel Model = AgentModel.FromInput(AgentModel.ChallengeDefault);
    private static readonly AgentModel LaterModel = AgentModel.Sonnet;

    private readonly Guid _taskId = DomainId.New();
    private readonly Guid _dispatchingNode = DomainId.New();
    private readonly Guid _expiringNode = DomainId.New();
    private readonly Guid _session = DomainId.New();
    private readonly Guid _later = DomainId.New();

    [Fact]
    public void A_challenge_verdict_followed_by_another_nodes_expiry_ends_as_though_the_expiry_never_landed()
    {
        object[] stream = [ChallengeDispatched(_session, _dispatchingNode), PersonRevised(), Verdict(_session), PersonRevised()];

        AssertChangesNothing(stream, ChallengeExpiry(_session));
    }

    [Fact]
    public void Another_nodes_challenge_expiry_followed_by_the_verdict_ends_as_though_the_expiry_never_landed()
    {
        object[] withExpiry = [ChallengeDispatched(_session, _dispatchingNode), PersonRevised(), ChallengeExpiry(_session), Verdict(_session)];
        object[] without = [ChallengeDispatched(_session, _dispatchingNode), PersonRevised(), Verdict(_session)];

        AssertSameEnd(withExpiry, without);
        (TaskAggregate aggregate, TaskDetails view) = Replay(withExpiry);
        aggregate.LatestChallengeOutcome.Should().Be(Verdict(_session), "the dispatching node's outcome wins over the expiry");
        view.RunningChallengeSessionId.Should().BeNull();
        ChallengeGate.Shortfall(aggregate, required: true).Should().BeNull("the publish gate reads the verdict");
        ChallengeGate.Shortfall(view, required: true).Should().BeNull();
    }

    [Fact]
    public void A_refinement_outcome_followed_by_another_nodes_expiry_ends_as_though_the_expiry_never_landed()
    {
        object[] stream =
        [
            .. VerdictStanding(), RefinementDispatched(_session, _dispatchingNode), PersonRevised(), SessionRevised(_session),
            RefinementOutcome(_session),
        ];

        AssertChangesNothing(stream, RefinementExpiry(_session));
    }

    [Fact]
    public void Another_nodes_refinement_expiry_followed_by_the_outcome_ends_as_though_the_expiry_never_landed()
    {
        object[] withExpiry =
        [
            .. VerdictStanding(), RefinementDispatched(_session, _dispatchingNode), PersonRevised(), RefinementExpiry(_session),
            SessionRevised(_session), RefinementOutcome(_session),
        ];
        object[] without =
        [
            .. VerdictStanding(), RefinementDispatched(_session, _dispatchingNode), PersonRevised(), SessionRevised(_session),
            RefinementOutcome(_session),
        ];

        AssertSameEnd(withExpiry, without);
        Replay(withExpiry).Aggregate.LatestRefinementOutcome.Should().Be(RefinementOutcome(_session));
    }

    [Fact]
    public void A_challenge_expiry_that_lands_while_a_later_challenge_runs_changes_nothing()
    {
        object[] stream =
        [
            ChallengeDispatched(_session, _dispatchingNode), Verdict(_session), ChallengeDispatched(_later, _expiringNode, LaterModel),
            PersonRevised(), ChallengeRequested(),
        ];

        AssertChangesNothing(stream, ChallengeExpiry(_session));
    }

    [Fact]
    public void A_challenge_expiry_that_lands_after_a_later_challenge_recorded_its_verdict_changes_nothing()
    {
        object[] stream =
        [
            ChallengeDispatched(_session, _dispatchingNode), Verdict(_session), PersonRevised(),
            ChallengeDispatched(_later, _expiringNode, LaterModel), Verdict(_later, _expiringNode), PersonRevised(), ChallengeRequested(),
        ];

        AssertChangesNothing(stream, ChallengeExpiry(_session));
    }

    [Fact]
    public void The_dispatching_nodes_verdict_that_lands_while_a_later_challenge_runs_changes_nothing()
    {
        object[] stream =
        [
            ChallengeDispatched(_session, _dispatchingNode), ChallengeExpiry(_session), ChallengeDispatched(_later, _expiringNode, LaterModel),
            PersonRevised(), ChallengeRequested(),
        ];

        AssertChangesNothing(stream, Verdict(_session));
    }

    [Fact]
    public void The_dispatching_nodes_verdict_that_lands_after_a_later_challenge_recorded_changes_nothing()
    {
        object[] stream =
        [
            ChallengeDispatched(_session, _dispatchingNode), ChallengeExpiry(_session), ChallengeDispatched(_later, _expiringNode, LaterModel),
            PersonRevised(), Verdict(_later, _expiringNode), PersonRevised(), ChallengeRequested(),
        ];

        AssertChangesNothing(stream, Verdict(_session));
    }

    [Fact]
    public void A_refinement_expiry_that_lands_while_a_later_refinement_runs_changes_nothing()
    {
        object[] stream =
        [
            RefinementDispatched(_session, _dispatchingNode), RefinementOutcome(_session),
            RefinementDispatched(_later, _expiringNode, LaterModel), PersonRevised(),
        ];

        AssertChangesNothing(stream, RefinementExpiry(_session));
    }

    [Fact]
    public void A_refinement_expiry_that_lands_after_a_later_refinement_recorded_changes_nothing()
    {
        object[] stream =
        [
            RefinementDispatched(_session, _dispatchingNode), RefinementOutcome(_session),
            RefinementDispatched(_later, _expiringNode, LaterModel), PersonRevised(), RefinementOutcome(_later, _expiringNode),
            PersonRevised(),
        ];

        AssertChangesNothing(stream, RefinementExpiry(_session));
    }

    [Fact]
    public void The_dispatching_nodes_refinement_outcome_that_lands_while_a_later_refinement_runs_changes_nothing()
    {
        object[] stream =
        [
            RefinementDispatched(_session, _dispatchingNode), RefinementExpiry(_session),
            RefinementDispatched(_later, _expiringNode, LaterModel), PersonRevised(),
        ];

        AssertChangesNothing(stream, RefinementOutcome(_session));
    }

    [Fact]
    public void The_dispatching_nodes_refinement_outcome_that_lands_after_a_later_refinement_recorded_changes_nothing()
    {
        object[] stream =
        [
            RefinementDispatched(_session, _dispatchingNode), RefinementExpiry(_session),
            RefinementDispatched(_later, _expiringNode, LaterModel), PersonRevised(), RefinementOutcome(_later, _expiringNode),
            PersonRevised(),
        ];

        AssertChangesNothing(stream, RefinementOutcome(_session));
    }

    [Fact]
    public void An_expiry_for_the_running_session_ends_its_standing_and_leaves_the_revision_facts_for_its_own_outcome()
    {
        (TaskAggregate aggregate, TaskDetails view) = Replay(
        [
            ChallengeDispatched(_session, _dispatchingNode), RefinementDispatched(_later, _dispatchingNode), PersonRevised(),
            ChallengeExpiry(_session), RefinementExpiry(_later),
        ]);

        aggregate.RunningChallengeSessionId.Should().BeNull();
        aggregate.RunningRefinementSessionId.Should().BeNull();
        view.RunningChallengeNodeId.Should().BeNull();
        view.RunningRefinementNodeId.Should().BeNull();
        aggregate.LatestChallengeOutcome.Should().Be(ChallengeExpiry(_session));
        view.LatestRefinementOutcome.Should().Be(RefinementExpiry(_later));
        aggregate.RevisedWhileChallengeRan.Should().BeTrue();
        aggregate.RevisedWhileRefinementRan.Should().BeTrue();
        view.RevisedWhileChallengeRan.Should().BeTrue();
        view.RevisedWhileRefinementRan.Should().BeTrue();
        aggregate.RevisedSinceLastDraftSession.Should().BeTrue();
        view.RevisedSinceLastDraftSession.Should().BeTrue();
    }

    private void AssertChangesNothing(object[] stream, object late) => AssertSameEnd([.. stream, late], stream);

    private void AssertSameEnd(object[] actual, object[] expected)
    {
        (TaskAggregate actualAggregate, TaskDetails actualView) = Replay(actual);
        (TaskAggregate expectedAggregate, TaskDetails expectedView) = Replay(expected);

        AggregateFacts.Of(actualAggregate).Should().Be(AggregateFacts.Of(expectedAggregate));
        DetailsFacts.Of(actualView).Should().Be(DetailsFacts.Of(expectedView));
    }

    private (TaskAggregate Aggregate, TaskDetails View) Replay(object[] stream)
    {
        TaskDetailsProjection projection = new();
        TaskAdded added = new(_taskId, DomainId.New(), "Ship it", ["it ships"], TaskType.Feature, null, null, null, Now, DomainId.New());
        TaskDetails view = projection.Create(new FakeEvent<TaskAdded>(added));
        TaskAggregate aggregate = new();
        aggregate.Apply(added);
        foreach (object step in stream)
        {
            switch (step)
            {
                case TaskRevised revised:
                    projection.Apply(new FakeEvent<TaskRevised>(revised), view);
                    aggregate.Apply(revised);
                    break;
                case DraftChallengeRequested requested:
                    projection.Apply(new FakeEvent<DraftChallengeRequested>(requested), view);
                    aggregate.Apply(requested);
                    break;
                case DraftChallengeDispatched dispatched:
                    projection.Apply(new FakeEvent<DraftChallengeDispatched>(dispatched), view);
                    aggregate.Apply(dispatched);
                    break;
                case DraftChallengeRecorded recorded:
                    projection.Apply(new FakeEvent<DraftChallengeRecorded>(recorded), view);
                    aggregate.Apply(recorded);
                    break;
                case DraftRefinementDispatched dispatched:
                    projection.Apply(new FakeEvent<DraftRefinementDispatched>(dispatched), view);
                    aggregate.Apply(dispatched);
                    break;
                case DraftRefinementRecorded recorded:
                    projection.Apply(new FakeEvent<DraftRefinementRecorded>(recorded), view);
                    aggregate.Apply(recorded);
                    break;
                default:
                    throw new InvalidOperationException($"no replay for {step.GetType().Name}");
            }
        }

        return (aggregate, view);
    }

    private object[] VerdictStanding() => [ChallengeDispatched(_later, _dispatchingNode), Verdict(_later)];

    private TaskRevised PersonRevised() =>
        new(_taskId, Optional<string>.Of("Ship it, revised"), default, default, default, default, default, Now, DomainId.New());

    private TaskRevised SessionRevised(Guid session) =>
        new(_taskId, Optional<string>.Of("Ship it, refined"), default, default, default, default, default, Now, DomainId.New(),
            RefinementSessionId: session);

    private DraftChallengeRequested ChallengeRequested() => new(_taskId, Now.AddMinutes(30), DomainId.New());

    private DraftChallengeDispatched ChallengeDispatched(Guid session, Guid node, AgentModel? model = null) =>
        new(_taskId, session, node, Now, model ?? Model, AgentEffort.High);

    private DraftChallengeRecorded Verdict(Guid session, Guid? node = null) =>
        new(_taskId, session, node ?? _dispatchingNode, ChallengeVerdict.PublishAsWritten, ChallengeFailureReason.Unknown, 0, 1, 0, Model,
            AgentEffort.High, Now.AddMinutes(3));

    private DraftChallengeRecorded ChallengeExpiry(Guid session) =>
        new(_taskId, session, _dispatchingNode, ChallengeVerdict.Unknown, ChallengeFailureReason.DispatchExpired, 0, 0, 0, Model,
            AgentEffort.High, Now.AddMinutes(85));

    private DraftRefinementDispatched RefinementDispatched(Guid session, Guid node, AgentModel? model = null) =>
        new(_taskId, session, node, Now, model ?? Model, AgentEffort.High, RefinementPurpose.JudgeVerdict);

    private DraftRefinementRecorded RefinementOutcome(Guid session, Guid? node = null) =>
        new(_taskId, session, node ?? _dispatchingNode, Hall9k.Domain.Features.Tasks.RefinementOutcome.Unchanged,
            RefinementFailureReason.Unknown, RefinementPurpose.JudgeVerdict, Model, AgentEffort.High, Now.AddMinutes(5));

    private DraftRefinementRecorded RefinementExpiry(Guid session) =>
        new(_taskId, session, _dispatchingNode, Hall9k.Domain.Features.Tasks.RefinementOutcome.Unknown,
            RefinementFailureReason.DispatchExpired, RefinementPurpose.Unknown, Model, AgentEffort.High, Now.AddMinutes(85));

    private sealed record AggregateFacts(
        bool ChallengeRequested,
        Guid? RunningChallengeSessionId,
        DraftChallengeRecorded? LatestChallengeOutcome,
        DraftChallengeRecorded? LatestChallengeVerdict,
        bool RevisedSinceChallengeVerdict,
        bool JudgedSinceChallengeVerdict,
        bool LatestChallengeVerdictJudgedOlderDraft,
        bool RevisedWhileChallengeRan,
        bool RevisedSinceLastDraftSession,
        Guid? RunningRefinementSessionId,
        DraftRefinementRecorded? LatestRefinementOutcome,
        bool RevisedWhileRefinementRan)
    {
        public static AggregateFacts Of(TaskAggregate task) => new(
            task.ChallengeRequested, task.RunningChallengeSessionId, task.LatestChallengeOutcome, task.LatestChallengeVerdict,
            task.RevisedSinceChallengeVerdict, task.JudgedSinceChallengeVerdict, task.LatestChallengeVerdictJudgedOlderDraft,
            task.RevisedWhileChallengeRan, task.RevisedSinceLastDraftSession, task.RunningRefinementSessionId,
            task.LatestRefinementOutcome, task.RevisedWhileRefinementRan);
    }

    private sealed record DetailsFacts(
        bool ChallengeRequested,
        DateTimeOffset? ChallengeRequestedAt,
        Guid? RunningChallengeSessionId,
        Guid? RunningChallengeNodeId,
        DateTimeOffset? RunningChallengeDispatchedAt,
        AgentModel? RunningChallengeModel,
        AgentEffort? RunningChallengeEffort,
        DraftChallengeRecorded? LatestChallengeOutcome,
        DraftChallengeRecorded? LatestChallengeVerdict,
        bool RevisedSinceChallengeVerdict,
        bool JudgedSinceChallengeVerdict,
        bool LatestChallengeVerdictJudgedOlderDraft,
        bool RevisedWhileChallengeRan,
        bool RevisedSinceLastDraftSession,
        Guid? RunningRefinementSessionId,
        Guid? RunningRefinementNodeId,
        DateTimeOffset? RunningRefinementDispatchedAt,
        AgentModel? RunningRefinementModel,
        AgentEffort? RunningRefinementEffort,
        DraftRefinementRecorded? LatestRefinementOutcome,
        bool RevisedWhileRefinementRan)
    {
        public static DetailsFacts Of(TaskDetails task) => new(
            task.ChallengeRequested, task.ChallengeRequestedAt, task.RunningChallengeSessionId, task.RunningChallengeNodeId,
            task.RunningChallengeDispatchedAt, task.RunningChallengeModel, task.RunningChallengeEffort, task.LatestChallengeOutcome,
            task.LatestChallengeVerdict, task.RevisedSinceChallengeVerdict, task.JudgedSinceChallengeVerdict,
            task.LatestChallengeVerdictJudgedOlderDraft, task.RevisedWhileChallengeRan, task.RevisedSinceLastDraftSession,
            task.RunningRefinementSessionId, task.RunningRefinementNodeId, task.RunningRefinementDispatchedAt,
            task.RunningRefinementModel, task.RunningRefinementEffort, task.LatestRefinementOutcome, task.RevisedWhileRefinementRan);
    }
}
