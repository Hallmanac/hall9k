using System.Text.Json;
using FluentAssertions;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Plan;
using Hall9k.Domain.Features.Plan.Events;
using Hall9k.Domain.Features.Plan.Handlers;
using Hall9k.Domain.Features.Plan.Projections;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// A plan started from an idea, in the record and in the decider: it begins in discovery with no drafts, parks at a gate or on a
/// failed session, is answered by pursue (which names the drafts) or stop (a normal ending), and none of B1's rules over drafts
/// fires on it before it has any.
/// </summary>
public sealed class PlanDiscoveryRecordTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly Guid _projectId = DomainId.New();
    private readonly Guid _ideaId = DomainId.New();
    private readonly Guid _owner = DomainId.New();

    private PlanAggregate InDiscovery()
    {
        PlanAggregate plan = new();
        plan.Apply(new PlanStarted(DomainId.New(), _projectId, [], PlanPhase.Discovery, false, false, _owner, Now, _ideaId));
        return plan;
    }

    private PlanAggregate Running(out Guid session)
    {
        PlanAggregate plan = InDiscovery();
        session = DomainId.New();
        plan.ApplyEvent(new PlanDiscoveryDispatched(plan.Id, _projectId, _ideaId, session, Now));
        return plan;
    }

    private PlanAggregate AtGate()
    {
        PlanAggregate plan = Running(out Guid session);
        plan.ApplyEvent(PlanDecider.ParkDiscovery(plan, PlanParkReason.Gate, session, RefinementFailureReason.Unknown, Now));
        return plan;
    }

    [Fact]
    public void Every_discovery_event_is_node_scoped_and_holds_no_text_from_a_session_or_the_report()
    {
        Type[] events =
        [
            typeof(PlanDiscoveryDispatched), typeof(PlanDiscoveryParked), typeof(PlanDraftsCut), typeof(PlanStopped), typeof(PlanTokensRecorded),
        ];
        foreach (Type type in events)
        {
            EventScopeRegistry.ClassificationOf(type).Should().Be(EventScope.NodeScoped, type.Name);
        }

        foreach (Type type in events.Where(type => type != typeof(PlanStopped)))
        {
            type.GetProperties().Select(property => property.PropertyType).Should().NotContain(typeof(string), type.Name);
        }

        // The one person's text is the reason given at the gate, which is neither a session's nor the report's.
        typeof(PlanStopped).GetProperties().Count(property => property.PropertyType == typeof(string)).Should().Be(1);
        typeof(PlanDiscoveryState).GetProperties().Select(property => property.PropertyType).Should().NotContain(typeof(string));
    }

    [Fact]
    public void A_plan_started_from_an_idea_begins_in_discovery_with_no_drafts_and_none_of_the_rules_over_drafts_fire_on_it()
    {
        PlanAggregate plan = InDiscovery();

        (plan.InDiscovery, plan.IsLive, plan.Drafts.Count, plan.IdeaId).Should().Be((true, true, 0, _ideaId));
        plan.StartPhase.Should().Be(PlanPhase.Discovery);
        PlanDecider.End(plan, Now).Should().BeNull("a plan with no drafts yet has not published or abandoned anything");
        PlanDecider.DraftsToPublish(plan).Should().BeEmpty();
        PlanDecider.NextDiscoveryStep(plan).Should().Be(PlanStepKind.DispatchDiscovery);
    }

    [Fact]
    public void A_dispatched_session_is_awaited_and_a_parked_discovery_has_no_step_of_its_own()
    {
        PlanDecider.NextDiscoveryStep(Running(out _)).Should().Be(PlanStepKind.AwaitSession);
        PlanDecider.NextDiscoveryStep(AtGate()).Should().Be(PlanStepKind.None);
        PlanAggregate failed = Running(out Guid session);
        failed.ApplyEvent(PlanDecider.ParkDiscovery(failed, PlanParkReason.SessionFailed, session, RefinementFailureReason.TimedOut, Now));
        PlanDecider.NextDiscoveryStep(failed).Should().Be(PlanStepKind.None);
        failed.IsParked.Should().BeTrue();
        failed.AtGate.Should().BeFalse();
    }

    [Fact]
    public void A_resume_after_a_failed_session_dispatches_discovery_again_and_a_resume_at_the_gate_is_refused_naming_the_two_answers()
    {
        PlanAggregate failed = Running(out Guid session);
        failed.ApplyEvent(PlanDecider.ParkDiscovery(failed, PlanParkReason.SessionFailed, session, RefinementFailureReason.NoReport, Now));
        failed.ApplyEvent(PlanDecider.Resume(failed, _owner, Now));
        PlanDecider.NextDiscoveryStep(failed).Should().Be(PlanStepKind.DispatchDiscovery);

        PlanAggregate gate = AtGate();
        Action resume = () => PlanDecider.Resume(gate, _owner, Now);
        resume.Should().Throw<DomainConflictException>()
            .Which.Message.Should().Contain("h9k plan pursue").And.Contain("h9k plan stop").And.Contain("gate");
        gate.AtGate.Should().BeTrue("a refused resume changes nothing");
    }

    [Fact]
    public void Stop_is_accepted_only_at_the_gate_ends_the_plan_stopped_and_needs_a_reason()
    {
        Action notAtGate = () => PlanDecider.Stop(Running(out _), "no", _owner, Now);
        notAtGate.Should().Throw<DomainConflictException>().Which.Message.Should().Contain("cannot be stopped");

        PlanAggregate gate = AtGate();
        Action blank = () => PlanDecider.Stop(gate, "  ", _owner, Now);
        blank.Should().Throw<DomainValidationException>();

        PlanStopped stopped = PlanDecider.Stop(gate, " an exploration ", _owner, Now);
        gate.ApplyEvent(stopped);

        (gate.State, gate.IsLive, gate.StopReason, gate.EndedAt).Should().Be((PlanState.Stopped, false, "an exploration", Now));
        stopped.IdeaId.Should().Be(_ideaId);
        Action again = () => PlanDecider.Stop(gate, "again", _owner, Now);
        again.Should().Throw<DomainConflictException>().Which.Message.Should().Contain("Stopped");
    }

    [Fact]
    public void Pursue_is_accepted_only_at_the_gate_and_adds_the_drafts_it_names_in_refinement()
    {
        Guid first = DomainId.New();
        Guid second = DomainId.New();
        Guid epic = DomainId.New();
        Action notAtGate = () => PlanDecider.Pursue(InDiscovery(), [first], null, false, false, _owner, Now);
        notAtGate.Should().Throw<DomainConflictException>();

        PlanAggregate gate = AtGate();
        Action none = () => PlanDecider.Pursue(gate, [], null, false, false, _owner, Now);
        none.Should().Throw<DomainValidationException>();

        PlanDraftsCut cut = PlanDecider.Pursue(gate, [first, second], epic, noExistingItemAttested: true, untrackedAttested: false, _owner, Now);
        gate.ApplyEvent(cut);

        gate.Drafts.Select(draft => draft.DraftId).Should().Equal(first, second);
        gate.Drafts.Should().OnlyContain(draft => draft.Phase == PlanPhase.Refinement && draft.Round == 0);
        (gate.InDiscovery, gate.IsParked, gate.IsLive, gate.EpicId, gate.NoExistingItemAttested).Should().Be((false, false, true, epic, true));
        PlanDecider.NextStep(gate.Drafts[0]).Should().Be(PlanStepKind.DispatchRefinement, "every step after the cut is a plan's ordinary path from refinement");
        Action twice = () => PlanDecider.Pursue(gate, [DomainId.New()], null, false, false, _owner, Now);
        twice.Should().Throw<DomainConflictException>().Which.Message.Should().Contain("already cut its drafts");
    }

    [Fact]
    public void A_plan_cancelled_in_discovery_ends_cancelled_and_cannot_be_pursued()
    {
        PlanAggregate gate = AtGate();
        gate.ApplyEvent(PlanDecider.Cancel(gate, "the idea changed", _owner, Now));

        gate.State.Should().Be(PlanState.Cancelled);
        Action pursue = () => PlanDecider.Pursue(gate, [DomainId.New()], null, false, false, _owner, Now);
        pursue.Should().Throw<DomainConflictException>().Which.Message.Should().Contain("Cancelled");
    }

    [Fact]
    public void The_view_agrees_with_the_aggregate_through_discovery_the_gate_and_the_cut()
    {
        Guid plan = DomainId.New();
        Guid session = DomainId.New();
        Guid draft = DomainId.New();
        object[] events =
        [
            new PlanStarted(plan, _projectId, [], PlanPhase.Discovery, false, false, _owner, Now, _ideaId),
            new PlanDiscoveryDispatched(plan, _projectId, _ideaId, session, Now),
            new PlanDiscoveryParked(plan, _projectId, _ideaId, PlanParkReason.SessionFailed, session, RefinementFailureReason.TimedOut, Now),
            new PlanResumed(plan, _projectId, _owner, Now),
            new PlanDiscoveryDispatched(plan, _projectId, _ideaId, session, Now),
            new PlanDiscoveryParked(plan, _projectId, _ideaId, PlanParkReason.Gate, session, RefinementFailureReason.Unknown, Now),
            new PlanDraftsCut(plan, _projectId, _ideaId, [draft], null, false, true, _owner, Now),
        ];
        PlanAggregate aggregate = new();
        PlanDetailsProjection projection = new();
        PlanDetails view = projection.Create(new FakeEvent<PlanStarted>((PlanStarted)events[0]));
        aggregate.ApplyEvent(events[0]);
        view.AtGate.Should().BeFalse();
        foreach (object @event in events.Skip(1))
        {
            aggregate.ApplyEvent(@event);
            switch (@event)
            {
                case PlanDiscoveryDispatched e:
                    projection.Apply(new FakeEvent<PlanDiscoveryDispatched>(e), view);
                    break;
                case PlanDiscoveryParked e:
                    projection.Apply(new FakeEvent<PlanDiscoveryParked>(e), view);
                    break;
                case PlanResumed e:
                    projection.Apply(new FakeEvent<PlanResumed>(e), view);
                    break;
                case PlanDraftsCut e:
                    projection.Apply(new FakeEvent<PlanDraftsCut>(e), view);
                    break;
                default:
                    throw new InvalidOperationException(@event.GetType().Name);
            }
        }

        JsonSerializer.Serialize(view.Drafts).Should().Be(JsonSerializer.Serialize(aggregate.Drafts));
        JsonSerializer.Serialize(view.Discovery).Should().Be(JsonSerializer.Serialize(aggregate.Discovery));
        (view.IdeaId, view.InDiscovery, view.IsParked, view.UntrackedAttested, view.Phase).Should().Be((_ideaId, false, false, true, PlanPhase.Refinement));
        view.Discovery!.LatestSessionId.Should().Be(session);
    }

    [Fact]
    public void The_view_reads_stopped_as_an_ending_in_discovery_and_the_gate_as_a_park()
    {
        PlanDetailsProjection projection = new();
        Guid plan = DomainId.New();
        Guid session = DomainId.New();
        PlanDetails view = projection.Create(new FakeEvent<PlanStarted>(
            new PlanStarted(plan, _projectId, [], PlanPhase.Discovery, false, false, _owner, Now, _ideaId)));
        projection.Apply(new FakeEvent<PlanDiscoveryParked>(
            new PlanDiscoveryParked(plan, _projectId, _ideaId, PlanParkReason.Gate, session, RefinementFailureReason.Unknown, Now)), view);

        (view.AtGate, view.IsParked, view.Phase).Should().Be((true, true, PlanPhase.Discovery));

        projection.Apply(new FakeEvent<PlanStopped>(new PlanStopped(plan, _projectId, _ideaId, "an exploration", _owner, Now)), view);

        (view.State, view.IsLive, view.AtGate, view.StopReason, view.Phase).Should().Be((PlanState.Stopped, false, false, "an exploration", PlanPhase.Discovery));
    }

    [Fact]
    public void The_gate_and_stopped_round_trip_through_their_stored_form()
    {
        JsonSerializer.Deserialize<PlanParkReason>(JsonSerializer.Serialize(PlanParkReason.Gate)).Should().Be(PlanParkReason.Gate);
        JsonSerializer.Deserialize<PlanState>(JsonSerializer.Serialize(PlanState.Stopped)).Should().Be(PlanState.Stopped);
        JsonSerializer.Deserialize<PlanPhase>(JsonSerializer.Serialize(PlanPhase.Discovery)).Should().Be(PlanPhase.Discovery);
        JsonSerializer.Deserialize<RefinementFailureReason>(JsonSerializer.Serialize(RefinementFailureReason.ReportNotCopied))
            .Should().Be(RefinementFailureReason.ReportNotCopied);
        PlanParkReason.Gate.WayForward.Should().Contain("h9k plan pursue").And.Contain("h9k plan stop");
        PlanState.Stopped.IsLive.Should().BeFalse();
    }

    [Fact]
    public void A_discovery_park_reaches_the_feed_at_a_plan_parks_level_and_at_once_carrying_the_project_and_the_idea_and_stopped_is_an_ordinary_item()
    {
        OrchestratorFeedInterest.BandOf(typeof(PlanDiscoveryParked)).Should().Be(OrchestratorFeedInterest.BandOf(typeof(PlanParked)));
        OrchestratorFeedInterest.BandOf(typeof(PlanDiscoveryParked)).Should().Be(OrchestratorFeedLevel.Actionable);
        OrchestratorFeedInterest.BandOf(typeof(PlanStopped)).Should().Be(OrchestratorFeedLevel.Transitions);
        OrchestratorFeedInterest.BandOf(typeof(PlanDiscoveryDispatched)).Should().BeNull();
        OrchestratorFeedInterest.BandOf(typeof(PlanTokensRecorded)).Should().BeNull();
        OrchestratorFeedInterest.BandOf(typeof(PlanDraftsCut)).Should().BeNull();

        Guid plan = DomainId.New();
        PlanDiscoveryParked gate = new(plan, _projectId, _ideaId, PlanParkReason.Gate, DomainId.New(), RefinementFailureReason.Unknown, Now);
        OrchestratorFeedUrgency.IsUrgent(typeof(PlanDiscoveryParked), gate).Should().BeTrue();
        gate.ProjectId.Should().Be(_projectId);
        gate.IdeaId.Should().Be(_ideaId);

        string line = OrchestratorFeedDescription.Of(gate);
        line.Should().Contain(DomainId.Short(plan)).And.Contain(DomainId.Short(_ideaId)).And.Contain("discovery")
            .And.Contain(PlanParkReason.Gate.Description).And.Contain($"h9k plan pursue {DomainId.Short(plan)}").And.Contain($"h9k plan stop {DomainId.Short(plan)}");
        line.Should().NotContain("h9k plan resume", "a resume is refused at the gate");
        OrchestratorFeedDescription.Of(gate with { Reason = PlanParkReason.SessionFailed }).Should().Contain($"h9k plan resume {DomainId.Short(plan)}");

        string stopped = OrchestratorFeedDescription.Of(new PlanStopped(plan, _projectId, _ideaId, "a private reason", _owner, Now));
        stopped.Should().Contain(DomainId.Short(plan)).And.Contain("stopped").And.NotContain("private");
    }

    [Fact]
    public void A_discovery_sessions_spend_is_a_node_scoped_event_on_the_plan_stream_shaped_like_the_couriers()
    {
        PlanTokensRecorded tokens = new(DomainId.New(), 10, 5, 0.5m, Now, 3, 2, AgentModel.FromInput(AgentModel.ChallengeDefault));

        (tokens.InputTokens + tokens.CacheReadInputTokens + tokens.CacheCreationInputTokens).Should().Be(15);
        EventScopeRegistry.ClassificationOf(typeof(PlanTokensRecorded)).Should().Be(EventScope.NodeScoped);
        EventScopeRegistry.ClassificationOf(typeof(PublicationTokensRecorded)).Should().NotBe(EventScope.NodeScoped, "which is why it cannot serve");
    }
}
