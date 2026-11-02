using System.Text.Json;
using FluentAssertions;
using Hall9k.Connectors.Replication;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Plan;
using Hall9k.Domain.Features.Plan.Events;
using Hall9k.Domain.Features.Plan.Handlers;
using Hall9k.Domain.Features.Plan.Projections;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using JasperFx.Events;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>How a plan sits in the record: what its events hold, what travels, what the feed says, and that the view and the aggregate agree.</summary>
public sealed class PlanRecordTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly Type[] PlanEvents =
    [
        typeof(PlanStarted), typeof(PlanRefinementDispatched), typeof(PlanChallengeRequested), typeof(PlanDraftMoved), typeof(PlanParked),
        typeof(PlanResumed), typeof(PlanDraftPublished), typeof(PlanDraftAbandoned), typeof(PlanPublished), typeof(PlanCancelled),
    ];

    [Fact]
    public void Every_plan_event_is_node_scoped_so_the_outbox_never_carries_one_and_the_inbox_refuses_one_that_arrives()
    {
        foreach (Type type in PlanEvents)
        {
            EventScopeRegistry.ClassificationOf(type).Should().Be(EventScope.NodeScoped, type.Name);
        }

        // The inbox's own gate: anything not project-scoped is never eligible to travel.
        EventScopeRegistry.ClassificationOf(typeof(PlanStarted)).Should().NotBe(EventScope.ProjectScoped);
    }

    [Fact]
    public void A_plan_holds_ids_phases_rounds_and_closed_outcomes_and_no_prose_not_even_a_title()
    {
        foreach (Type type in PlanEvents.Where(type => type != typeof(PlanCancelled)))
        {
            type.GetProperties().Select(property => property.PropertyType)
                .Should().NotContain(typeof(string), $"{type.Name} holds nothing a draft or a session wrote");
        }

        // The one person's text on the stream is the reason given at cancel, which is neither a draft's nor a session's.
        typeof(PlanCancelled).GetProperties().Count(property => property.PropertyType == typeof(string)).Should().Be(1);
        typeof(PlanAggregate).GetProperties().Select(property => property.Name).Should().NotContain("Title");
        typeof(PlanDraftState).GetProperties().Select(property => property.PropertyType).Should().NotContain(typeof(string));
    }

    [Fact]
    public void The_refinement_dispatch_and_outcome_travel_with_the_node_and_no_machine_path_and_are_conditional_acts()
    {
        EventScopeRegistry.ClassificationOf(typeof(DraftRefinementDispatched)).Should().Be(EventScope.ProjectScoped);
        EventScopeRegistry.ClassificationOf(typeof(DraftRefinementRecorded)).Should().Be(EventScope.ProjectScoped);
        TaskActClassificationRegistry.TryClassificationOf(typeof(DraftRefinementDispatched)).Should().Be(TaskActClassification.Conditional);
        TaskActClassificationRegistry.TryClassificationOf(typeof(DraftRefinementRecorded)).Should().Be(TaskActClassification.Conditional);

        // Pre-assignment capable: a creator's outcome on their own unassigned draft applies, and a stranger's does not.
        Guid taskId = DomainId.New();
        Guid nodeId = DomainId.New();
        TaskAggregate draft = new();
        draft.Apply(new TaskAdded(taskId, DomainId.New(), "Ship it", ["it ships"], TaskType.Feature, null, null, null, Now, Guid.NewGuid()));
        EventReplicationInbox.SenderResolution sender = new("member-root", MembershipRole.Member, new HashSet<Guid> { nodeId });
        foreach ((Type type, object data) in new (Type, object)[]
                 {
                     (typeof(DraftRefinementDispatched), new DraftRefinementDispatched(
                         taskId, DomainId.New(), nodeId, Now, AgentModel.FromInput(AgentModel.ChallengeDefault), AgentEffort.High, RefinementPurpose.Refine)),
                     (typeof(DraftRefinementRecorded), new DraftRefinementRecorded(
                         taskId, DomainId.New(), nodeId, RefinementOutcome.Revised, RefinementFailureReason.Unknown, RefinementPurpose.Refine,
                         AgentModel.FromInput(AgentModel.ChallengeDefault), AgentEffort.High, Now)),
                 })
        {
            EventReplicationInbox.EvaluateTaskActVerdict(
                    TaskActClassification.Conditional, type, data, draft, sender, nodeId, nodeId, creatorRootFingerprint: "member-root")
                .Should().Be(EventReplicationInbox.TaskActVerdict.Allowed, $"{type.Name} from the draft's own creator applies");
            EventReplicationInbox.EvaluateTaskActVerdict(
                    TaskActClassification.Conditional, type, data, draft, sender, nodeId, nodeId, creatorRootFingerprint: "someone-else")
                .Should().Be(EventReplicationInbox.TaskActVerdict.DroppedAndRefusedPermanently, $"{type.Name} on a draft the sender did not create is refused");
        }

        typeof(DraftRefinementDispatched).GetProperties().Select(property => property.PropertyType).Should().NotContain(typeof(string));
        typeof(DraftRefinementRecorded).GetProperties().Select(property => property.PropertyType).Should().NotContain(typeof(string));
        typeof(DraftRefinementDispatched).GetProperties().Select(property => property.Name).Should().Contain("NodeId");
    }

    [Fact]
    public void A_refinement_dispatch_counts_as_a_session_in_the_tasks_passage_and_its_spend_rides_the_existing_tokens_event()
    {
        Guid taskId = DomainId.New();
        List<IEvent> events =
        [
            new FakeEvent<DraftRefinementDispatched>(new DraftRefinementDispatched(
                taskId, DomainId.New(), DomainId.New(), Now, AgentModel.FromInput(AgentModel.ChallengeDefault), AgentEffort.High, RefinementPurpose.Refine)),
            new FakeEvent<DraftChallengeDispatched>(new DraftChallengeDispatched(
                taskId, DomainId.New(), DomainId.New(), Now, AgentModel.ChallengeDefault, AgentEffort.High)),
        ];

        TaskPassageQuery.Compute(events, [], TaskType.Feature, taskConcluded: false, Now).Sessions.Should().Be(2);
    }

    [Fact]
    public void The_closed_lists_round_trip_through_their_stored_form_and_an_unknown_value_reads_as_unknown()
    {
        foreach (PlanParkReason reason in new[]
                 {
                     PlanParkReason.RoundsExhausted, PlanParkReason.Question, PlanParkReason.VerdictNotApplied, PlanParkReason.DraftChanged,
                     PlanParkReason.RevisionRefused, PlanParkReason.SessionFailed, PlanParkReason.ChallengeFailed, PlanParkReason.PublishRefused,
                 })
        {
            JsonSerializer.Deserialize<PlanParkReason>(JsonSerializer.Serialize(reason)).Should().Be(reason);
            reason.WayForward.Should().NotBeEmpty();
        }

        ((PlanParkReason)"nonsense").Should().Be(PlanParkReason.Unknown);
        ((RefinementOutcome)"nonsense").Should().Be(RefinementOutcome.Unknown);
        ((RefinementFailureReason)"nonsense").Should().Be(RefinementFailureReason.Unknown);
        ((PlanOutcome)"nonsense").Should().Be(PlanOutcome.None);
        JsonSerializer.Deserialize<RefinementFailureReason>(JsonSerializer.Serialize(RefinementFailureReason.DispatchExpired))!.Description
            .Should().Be(RefinementFailureReason.DispatchExpired.Description);
    }

    [Fact]
    public void A_park_reaches_the_feed_at_a_review_parks_level_and_at_once_a_phase_move_does_not_arrive_and_the_end_is_an_ordinary_item()
    {
        OrchestratorFeedInterest.BandOf(typeof(PlanParked)).Should().Be(OrchestratorFeedInterest.BandOf(typeof(ReviewParked)));
        OrchestratorFeedInterest.BandOf(typeof(PlanParked)).Should().Be(OrchestratorFeedLevel.Actionable);
        OrchestratorFeedInterest.BandOf(typeof(PlanDraftMoved)).Should().BeNull();
        OrchestratorFeedInterest.BandOf(typeof(PlanRefinementDispatched)).Should().BeNull();
        OrchestratorFeedInterest.BandOf(typeof(PlanChallengeRequested)).Should().BeNull();
        OrchestratorFeedInterest.BandOf(typeof(PlanPublished)).Should().Be(OrchestratorFeedLevel.Transitions);
        OrchestratorFeedInterest.BandOf(typeof(PlanCancelled)).Should().Be(OrchestratorFeedLevel.Transitions);

        Guid plan = DomainId.New();
        Guid project = DomainId.New();
        PlanParked parked = new(plan, project, DomainId.New(), PlanParkReason.Question, DomainId.New(), Now);
        OrchestratorFeedUrgency.IsUrgent(typeof(PlanParked), parked).Should().BeTrue();
        OrchestratorFeedUrgency.IsUrgent(typeof(PlanPublished), new PlanPublished(plan, project, Now, [])).Should().BeFalse();
    }

    [Fact]
    public void The_feed_text_carries_only_ids_and_the_reason_and_never_a_cancel_reason()
    {
        Guid plan = DomainId.New();
        Guid project = DomainId.New();
        Guid draft = DomainId.New();

        string parked = OrchestratorFeedDescription.Of(new PlanParked(plan, project, draft, PlanParkReason.RoundsExhausted, null, Now));
        parked.Should().Contain(DomainId.Short(plan)).And.Contain(DomainId.Short(draft)).And.Contain(PlanParkReason.RoundsExhausted.Description)
            .And.Contain($"h9k plan show {DomainId.Short(plan)}").And.Contain($"h9k plan resume {DomainId.Short(plan)}");

        string published = OrchestratorFeedDescription.Of(new PlanPublished(plan, project, Now, [draft]));
        published.Should().Contain(DomainId.Short(plan)).And.Contain("each draft is published or was abandoned").And.Contain(DomainId.Short(draft))
            .And.Contain("no tracker item");
        OrchestratorFeedDescription.Of(new PlanPublished(plan, project, Now, [])).Should().NotContain("tracker");

        string cancelled = OrchestratorFeedDescription.Of(new PlanCancelled(plan, project, "a secret reason", DomainId.New(), Now));
        cancelled.Should().Contain(DomainId.Short(plan)).And.NotContain("secret");
    }

    [Fact]
    public void The_tracker_gap_is_a_published_draft_on_a_tracking_project_with_no_item_none_on_its_way_and_not_published_untracked()
    {
        TaskDetails published = new() { State = TaskState.Published };

        TrackerGap.Exists(BacklogPolicy.GitHubIssues, published).Should().BeTrue();
        TrackerGap.Exists(BacklogPolicy.None, published).Should().BeFalse("the project tracks nothing");
        TrackerGap.Exists(BacklogPolicy.Jira, new TaskDetails { State = TaskState.Published, ExternalReference = "jira:ABC-1" }).Should().BeFalse();
        TrackerGap.Exists(BacklogPolicy.Jira, new TaskDetails { State = TaskState.Published, PendingPublicationProvider = "jira" }).Should().BeFalse();
        TrackerGap.Exists(BacklogPolicy.Jira, new TaskDetails { State = TaskState.Published, UntrackedAttested = true }).Should().BeFalse();
        TrackerGap.Exists(BacklogPolicy.Jira, new TaskDetails { State = TaskState.Draft }).Should().BeFalse();
    }

    [Fact]
    public void The_view_the_cli_reads_agrees_with_the_aggregate_the_daemon_decides_over_after_a_whole_plans_events()
    {
        Guid first = DomainId.New();
        Guid second = DomainId.New();
        Guid plan = DomainId.New();
        Guid project = DomainId.New();
        Guid owner = DomainId.New();
        Guid session = DomainId.New();
        object[] events =
        [
            new PlanStarted(plan, project, [first, second], PlanPhase.Refinement, true, false, owner, Now),
            new PlanRefinementDispatched(plan, project, first, session, RefinementPurpose.Refine, Now),
            new PlanDraftMoved(plan, project, first, PlanPhase.Challenge, 1, PlanOutcome.Revised, RefinementPurpose.Unknown, false, session, Now),
            new PlanChallengeRequested(plan, project, first, 1, Now),
            new PlanDraftMoved(plan, project, first, PlanPhase.Challenge, 1, PlanOutcome.DoNotPublish, RefinementPurpose.Unknown, false, session, Now),
            new PlanParked(plan, project, first, PlanParkReason.RoundsExhausted, session, Now),
            new PlanParked(plan, project, second, PlanParkReason.Question, session, Now),
            new PlanResumed(plan, project, owner, Now),
            new PlanDraftPublished(plan, project, first, true, Now),
            new PlanDraftAbandoned(plan, project, second, Now),
            new PlanPublished(plan, project, Now, []),
        ];

        PlanAggregate aggregate = new();
        PlanDetailsProjection projection = new();
        PlanDetails view = projection.Create(new FakeEvent<PlanStarted>((PlanStarted)events[0]));
        aggregate.ApplyEvent(events[0]);
        foreach (object @event in events.Skip(1))
        {
            aggregate.ApplyEvent(@event);
            switch (@event)
            {
                case PlanRefinementDispatched e:
                    projection.Apply(new FakeEvent<PlanRefinementDispatched>(e), view);
                    break;
                case PlanChallengeRequested e:
                    projection.Apply(new FakeEvent<PlanChallengeRequested>(e), view);
                    break;
                case PlanDraftMoved e:
                    projection.Apply(new FakeEvent<PlanDraftMoved>(e), view);
                    break;
                case PlanParked e:
                    projection.Apply(new FakeEvent<PlanParked>(e), view);
                    break;
                case PlanResumed e:
                    projection.Apply(new FakeEvent<PlanResumed>(e), view);
                    break;
                case PlanDraftPublished e:
                    projection.Apply(new FakeEvent<PlanDraftPublished>(e), view);
                    break;
                case PlanDraftAbandoned e:
                    projection.Apply(new FakeEvent<PlanDraftAbandoned>(e), view);
                    break;
                case PlanPublished e:
                    projection.Apply(new FakeEvent<PlanPublished>(e), view);
                    break;
                default:
                    throw new InvalidOperationException(@event.GetType().Name);
            }
        }

        view.State.Should().Be(aggregate.State).And.Be(PlanState.Published);
        view.NoExistingItemAttested.Should().BeTrue();
        JsonSerializer.Serialize(view.Drafts).Should().Be(JsonSerializer.Serialize(aggregate.Drafts));
        view.Drafts[0].Published.Should().BeTrue();
        view.Drafts[0].ExtraRounds.Should().Be(1, "the first draft's park was rounds exhausted, answered by the resume");
        view.Drafts[1].Abandoned.Should().BeTrue();
        view.IsParked.Should().BeFalse();
    }

    [Fact]
    public void A_refinement_dispatch_and_outcome_move_the_tasks_view_and_a_revision_while_it_runs_is_noticed_only_for_what_the_prompt_carries()
    {
        Guid taskId = DomainId.New();
        Guid node = DomainId.New();
        Guid session = DomainId.New();
        TaskDetailsProjection projection = new();
        TaskDetails view = projection.Create(new FakeEvent<TaskAdded>(
            new TaskAdded(taskId, DomainId.New(), "Ship it", ["it ships"], TaskType.Feature, null, null, null, Now, Guid.NewGuid())));
        TaskAggregate aggregate = new();
        aggregate.Apply(new TaskAdded(taskId, DomainId.New(), "Ship it", ["it ships"], TaskType.Feature, null, null, null, Now, Guid.NewGuid()));
        DraftRefinementDispatched dispatched = new(
            taskId, session, node, Now, AgentModel.FromInput(AgentModel.ChallengeDefault), AgentEffort.High, RefinementPurpose.Refine);

        projection.Apply(new FakeEvent<DraftRefinementDispatched>(dispatched), view);
        aggregate.Apply(dispatched);
        view.RunningRefinementSessionId.Should().Be(session);
        view.RunningRefinementNodeId.Should().Be(node);
        aggregate.RunningRefinementSessionId.Should().Be(session);

        TaskRevised effortOnly = new(
            taskId, default, default, default, default, default, default, Now, Guid.NewGuid(), Effort: Optional<AgentEffort>.Of(AgentEffort.High));
        projection.Apply(new FakeEvent<TaskRevised>(effortOnly), view);
        aggregate.Apply(effortOnly);
        view.RevisedWhileRefinementRan.Should().BeFalse("effort is not something the prompt carries");
        aggregate.RevisedWhileRefinementRan.Should().BeFalse();
        view.RevisedSinceLastDraftSession.Should().BeFalse();
        aggregate.RevisedSinceLastDraftSession.Should().BeFalse();

        TaskRevised text = effortOnly with { Objective = Optional<string>.Of("Ship it, revised") };
        projection.Apply(new FakeEvent<TaskRevised>(text), view);
        aggregate.Apply(text);
        view.RevisedWhileRefinementRan.Should().BeTrue();
        aggregate.RevisedWhileRefinementRan.Should().BeTrue();
        view.RevisedSinceLastDraftSession.Should().BeTrue();
        aggregate.RevisedSinceLastDraftSession.Should().BeTrue();

        DraftRefinementRecorded recorded = new(
            taskId, session, node, RefinementOutcome.Revised, RefinementFailureReason.DraftChanged, RefinementPurpose.Refine,
            AgentModel.FromInput(AgentModel.ChallengeDefault), AgentEffort.High, Now);
        projection.Apply(new FakeEvent<DraftRefinementRecorded>(recorded), view);
        aggregate.Apply(recorded);
        view.RunningRefinementSessionId.Should().BeNull();
        view.LatestRefinementOutcome.Should().Be(recorded);
        view.RevisedWhileRefinementRan.Should().BeFalse();
        view.RevisedSinceLastDraftSession.Should().BeTrue("a person's revision made while the session ran is text no session has read");
        aggregate.RevisedSinceLastDraftSession.Should().BeTrue();
        aggregate.LatestRefinementOutcome.Should().Be(recorded);
        aggregate.RunningRefinementSessionId.Should().BeNull();
    }

    [Fact]
    public void A_revision_made_on_the_running_sessions_behalf_is_its_own_and_leaves_no_trace_but_a_person_revising_after_it_is_still_noticed()
    {
        Guid taskId = DomainId.New();
        Guid node = DomainId.New();
        Guid session = DomainId.New();
        TaskDetailsProjection projection = new();
        TaskAdded added = new(taskId, DomainId.New(), "Ship it", ["it ships"], TaskType.Feature, null, null, null, Now, Guid.NewGuid());
        TaskDetails view = projection.Create(new FakeEvent<TaskAdded>(added));
        TaskAggregate aggregate = new();
        aggregate.Apply(added);
        DraftRefinementDispatched dispatched = new(
            taskId, session, node, Now, AgentModel.FromInput(AgentModel.ChallengeDefault), AgentEffort.High, RefinementPurpose.Refine);
        projection.Apply(new FakeEvent<DraftRefinementDispatched>(dispatched), view);
        aggregate.Apply(dispatched);

        TaskRevised own = new(
            taskId, Optional<string>.Of("Ship it, as the session wrote it"), default, default, default, default, default, Now,
            Guid.NewGuid(), RefinementSessionId: session);
        projection.Apply(new FakeEvent<TaskRevised>(own), view);
        aggregate.Apply(own);
        view.RevisedWhileRefinementRan.Should().BeFalse("the session's own revision is not a change it never read");
        aggregate.RevisedWhileRefinementRan.Should().BeFalse();
        view.RevisedSinceLastDraftSession.Should().BeFalse();
        aggregate.RevisedSinceLastDraftSession.Should().BeFalse();

        // The window between the session's revision and its outcome being recorded: a person's revise cannot be told
        // apart from the session's by the flag alone, which is why the session's is stamped.
        TaskRevised person = own with { Objective = Optional<string>.Of("Ship it, as a person wrote it"), RefinementSessionId = null };
        projection.Apply(new FakeEvent<TaskRevised>(person), view);
        aggregate.Apply(person);
        view.RevisedWhileRefinementRan.Should().BeTrue();
        aggregate.RevisedWhileRefinementRan.Should().BeTrue();

        DraftRefinementRecorded recorded = new(
            taskId, session, node, RefinementOutcome.Revised, RefinementFailureReason.Unknown, RefinementPurpose.Refine,
            AgentModel.FromInput(AgentModel.ChallengeDefault), AgentEffort.High, Now);
        projection.Apply(new FakeEvent<DraftRefinementRecorded>(recorded), view);
        aggregate.Apply(recorded);
        view.RevisedSinceLastDraftSession.Should().BeTrue("the person's revision after the session's own is still standing, unread");
        aggregate.RevisedSinceLastDraftSession.Should().BeTrue();
        view.RevisedWhileRefinementRan.Should().BeFalse();
    }

    [Fact]
    public void A_verdict_leaves_a_revision_made_while_its_challenge_ran_unread_but_clears_one_made_before_it()
    {
        Guid taskId = DomainId.New();
        TaskDetailsProjection projection = new();
        TaskAdded added = new(taskId, DomainId.New(), "Ship it", ["it ships"], TaskType.Feature, null, null, null, Now, Guid.NewGuid());
        TaskDetails view = projection.Create(new FakeEvent<TaskAdded>(added));
        TaskAggregate aggregate = new();
        aggregate.Apply(added);
        AgentModel model = AgentModel.FromInput(AgentModel.ChallengeDefault);
        TaskRevised revise = new(
            taskId, Optional<string>.Of("Ship it, revised"), default, default, default, default, default, Now, Guid.NewGuid());

        // Revised before the challenge was dispatched: the challenge reads it, so its verdict clears the flag.
        projection.Apply(new FakeEvent<TaskRevised>(revise), view);
        aggregate.Apply(revise);
        DraftChallengeDispatched dispatched = new(taskId, DomainId.New(), DomainId.New(), Now, model, AgentEffort.High);
        projection.Apply(new FakeEvent<DraftChallengeDispatched>(dispatched), view);
        aggregate.Apply(dispatched);
        DraftChallengeRecorded verdict = new(
            taskId, dispatched.SessionId, dispatched.NodeId, ChallengeVerdict.PublishAsRevised, ChallengeFailureReason.Unknown, 0, 0, 0,
            model, AgentEffort.High, Now);
        projection.Apply(new FakeEvent<DraftChallengeRecorded>(verdict), view);
        aggregate.Apply(verdict);
        view.RevisedSinceLastDraftSession.Should().BeFalse();
        aggregate.RevisedSinceLastDraftSession.Should().BeFalse();

        // Revised while the next challenge ran: its verdict never read that text, so the flag stands.
        projection.Apply(new FakeEvent<DraftChallengeDispatched>(dispatched), view);
        aggregate.Apply(dispatched);
        projection.Apply(new FakeEvent<TaskRevised>(revise), view);
        aggregate.Apply(revise);
        projection.Apply(new FakeEvent<DraftChallengeRecorded>(verdict), view);
        aggregate.Apply(verdict);
        view.RevisedSinceLastDraftSession.Should().BeTrue();
        aggregate.RevisedSinceLastDraftSession.Should().BeTrue();
        view.LatestChallengeVerdictJudgedOlderDraft.Should().BeTrue();
        aggregate.LatestChallengeVerdictJudgedOlderDraft.Should().BeTrue();
    }

    [Fact]
    public void A_late_outcome_for_a_session_that_is_no_longer_the_running_one_leaves_the_running_sessions_standing_alone()
    {
        Guid taskId = DomainId.New();
        Guid node = DomainId.New();
        Guid expired = DomainId.New();
        Guid replacement = DomainId.New();
        TaskDetailsProjection projection = new();
        TaskAdded added = new(taskId, DomainId.New(), "Ship it", ["it ships"], TaskType.Feature, null, null, null, Now, Guid.NewGuid());
        TaskDetails view = projection.Create(new FakeEvent<TaskAdded>(added));
        TaskAggregate aggregate = new();
        aggregate.Apply(added);
        AgentModel model = AgentModel.FromInput(AgentModel.ChallengeDefault);

        // The first session's dispatch was expired and a second was dispatched, and a person revised while the second ran.
        DraftRefinementDispatched first = new(taskId, expired, node, Now, model, AgentEffort.High, RefinementPurpose.Refine);
        DraftRefinementRecorded expiry = new(
            taskId, expired, node, RefinementOutcome.Unknown, RefinementFailureReason.DispatchExpired, RefinementPurpose.Unknown, model,
            AgentEffort.High, Now);
        DraftRefinementDispatched second = new(taskId, replacement, node, Now, model, AgentEffort.High, RefinementPurpose.Refine);
        TaskRevised person = new(
            taskId, Optional<string>.Of("Ship it, revised"), default, default, default, default, default, Now, Guid.NewGuid());
        foreach (object step in new object[] { first, expiry, second, person })
        {
            switch (step)
            {
                case DraftRefinementDispatched dispatched:
                    projection.Apply(new FakeEvent<DraftRefinementDispatched>(dispatched), view);
                    aggregate.Apply(dispatched);
                    break;
                case DraftRefinementRecorded recorded:
                    projection.Apply(new FakeEvent<DraftRefinementRecorded>(recorded), view);
                    aggregate.Apply(recorded);
                    break;
                case TaskRevised revised:
                    projection.Apply(new FakeEvent<TaskRevised>(revised), view);
                    aggregate.Apply(revised);
                    break;
            }
        }

        DraftRefinementRecorded late = new(
            taskId, expired, node, RefinementOutcome.Revised, RefinementFailureReason.Unknown, RefinementPurpose.Refine, model, AgentEffort.High, Now);
        projection.Apply(new FakeEvent<DraftRefinementRecorded>(late), view);
        aggregate.Apply(late);

        view.RunningRefinementSessionId.Should().Be(replacement);
        aggregate.RunningRefinementSessionId.Should().Be(replacement);
        view.RunningRefinementNodeId.Should().Be(node, "the replacement's node and model are still on record");
        view.RevisedWhileRefinementRan.Should().BeTrue("the draft check of the replacement session still sees the revision");
        aggregate.RevisedWhileRefinementRan.Should().BeTrue();
        view.LatestRefinementOutcome.Should().Be(expiry);
        aggregate.LatestRefinementOutcome.Should().Be(expiry);
    }

    [Fact]
    public void A_refinement_dispatch_refused_before_any_session_ran_leaves_a_revision_no_session_has_read_standing()
    {
        Guid taskId = DomainId.New();
        TaskDetailsProjection projection = new();
        TaskAdded added = new(taskId, DomainId.New(), "Ship it", ["it ships"], TaskType.Feature, null, null, null, Now, Guid.NewGuid());
        TaskDetails view = projection.Create(new FakeEvent<TaskAdded>(added));
        TaskAggregate aggregate = new();
        aggregate.Apply(added);
        TaskRevised person = new(
            taskId, Optional<string>.Of("Ship it, revised"), default, default, default, default, default, Now, Guid.NewGuid());
        projection.Apply(new FakeEvent<TaskRevised>(person), view);
        aggregate.Apply(person);

        DraftRefinementRecorded refused = new(
            taskId, null, DomainId.New(), RefinementOutcome.Unknown, RefinementFailureReason.StartFailed, RefinementPurpose.ApplyVerdict,
            AgentModel.FromInput(AgentModel.ChallengeDefault), AgentEffort.High, Now);
        projection.Apply(new FakeEvent<DraftRefinementRecorded>(refused), view);
        aggregate.Apply(refused);

        view.RevisedSinceLastDraftSession.Should().BeTrue("no session ran, so none read the person's text");
        aggregate.RevisedSinceLastDraftSession.Should().BeTrue();
        view.LatestRefinementOutcome.Should().Be(refused);
    }
}
