using FluentAssertions;
using Hall9k.Connectors.Replication;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using JasperFx.Events;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// How a draft challenge sits in the record: which of its events replicate and how a member's are judged, what the
/// orchestrator feed says of one, and that its session counts. The event itself holds nothing a session wrote.
/// </summary>
public sealed class DraftChallengeRecordTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private const string MemberRoot = "member-root-fingerprint";

    [Fact]
    public void The_request_never_leaves_its_node_and_the_dispatch_and_outcome_travel()
    {
        EventScopeRegistry.ClassificationOf(typeof(DraftChallengeRequested)).Should().Be(EventScope.NodeScoped);
        EventScopeRegistry.ClassificationOf(typeof(DraftChallengeDispatched)).Should().Be(EventScope.ProjectScoped);
        EventScopeRegistry.ClassificationOf(typeof(DraftChallengeRecorded)).Should().Be(EventScope.ProjectScoped);
    }

    [Fact]
    public void The_dispatch_and_outcome_are_conditional_acts_of_a_member()
    {
        TaskActClassificationRegistry.TryClassificationOf(typeof(DraftChallengeDispatched))
            .Should().Be(TaskActClassification.Conditional);
        TaskActClassificationRegistry.TryClassificationOf(typeof(DraftChallengeRecorded))
            .Should().Be(TaskActClassification.Conditional);
    }

    [Fact]
    public void A_creators_verdict_on_their_own_unassigned_draft_applies_and_a_strangers_is_dropped()
    {
        Guid taskId = DomainId.New();
        Guid nodeId = DomainId.New();
        TaskAggregate draft = new();
        draft.Apply(new TaskAdded(taskId, DomainId.New(), "Ship it", ["it ships"], TaskType.Feature, null, null, null, Now, Guid.NewGuid()));
        EventReplicationInbox.SenderResolution sender = new(MemberRoot, MembershipRole.Member, new HashSet<Guid> { nodeId });
        DraftChallengeRecorded recorded = new(
            taskId, DomainId.New(), nodeId, ChallengeVerdict.PublishAsWritten, ChallengeFailureReason.Unknown, 0, 0, 0,
            AgentModel.ChallengeDefault, AgentEffort.High, Now);
        DraftChallengeDispatched dispatched = new(
            taskId, DomainId.New(), nodeId, Now, AgentModel.ChallengeDefault, AgentEffort.High);

        foreach ((Type type, object data) in new (Type, object)[]
                 { (typeof(DraftChallengeRecorded), recorded), (typeof(DraftChallengeDispatched), dispatched) })
        {
            EventReplicationInbox.EvaluateTaskActVerdict(
                    TaskActClassification.Conditional, type, data, draft, sender, nodeId, nodeId, creatorRootFingerprint: MemberRoot)
                .Should().Be(EventReplicationInbox.TaskActVerdict.Allowed, $"{type.Name} from the draft's own creator applies");
            EventReplicationInbox.EvaluateTaskActVerdict(
                    TaskActClassification.Conditional, type, data, draft, sender, nodeId, nodeId, creatorRootFingerprint: "someone-else")
                .Should().Be(
                    EventReplicationInbox.TaskActVerdict.DroppedAndRefusedPermanently,
                    $"{type.Name} from a member on a draft they did not create is refused");
        }
    }

    [Fact]
    public void An_outcome_reaches_the_feed_as_an_ordinary_item_carrying_only_the_verdict_and_counts_or_the_reason()
    {
        OrchestratorFeedInterest.BandOf(typeof(DraftChallengeRecorded)).Should().Be(OrchestratorFeedLevel.Transitions);
        OrchestratorFeedUrgency.IsUrgent(typeof(DraftChallengeRecorded), System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(DraftChallengeRecorded))).Should().BeFalse();

        DraftChallengeRecorded verdict = new(
            DomainId.New(), DomainId.New(), DomainId.New(), ChallengeVerdict.PublishAsRevised, ChallengeFailureReason.Unknown,
            2, 3, 1, AgentModel.ChallengeDefault, AgentEffort.High, Now);
        OrchestratorFeedDescription.Of(verdict).Should().Contain("publish as revised").And.Contain("2 must")
            .And.Contain("3 should").And.Contain("1 park");

        DraftChallengeRecorded failure = verdict with
        {
            Verdict = ChallengeVerdict.Unknown, Failure = ChallengeFailureReason.TimedOut, MustCount = 0, ShouldCount = 0, ParkCount = 0,
        };
        OrchestratorFeedDescription.Of(failure).Should().Contain("no verdict").And.Contain(ChallengeFailureReason.TimedOut.Description);
    }

    [Fact]
    public void The_read_model_ages_a_verdict_by_a_text_revision_while_the_session_ran_and_by_no_other()
    {
        Guid taskId = DomainId.New();
        Guid nodeId = DomainId.New();
        TaskDetailsProjection projection = new();
        TaskDetails view = projection.Create(new FakeEvent<TaskAdded>(
            new TaskAdded(taskId, DomainId.New(), "Ship it", ["it ships"], TaskType.Feature, null, null, null, Now, Guid.NewGuid())));
        DraftChallengeRecorded verdict = new(
            taskId, DomainId.New(), nodeId, ChallengeVerdict.PublishAsRevised, ChallengeFailureReason.Unknown, 1, 0, 0,
            AgentModel.ChallengeDefault, AgentEffort.High, Now);
        TaskRevised EffortOnly() => new(
            taskId, default, default, default, default, default, default, Now, Guid.NewGuid(),
            Effort: Optional<AgentEffort>.Of(AgentEffort.High));

        projection.Apply(new FakeEvent<DraftChallengeDispatched>(
            new DraftChallengeDispatched(taskId, DomainId.New(), nodeId, Now, AgentModel.ChallengeDefault, AgentEffort.High)), view);
        projection.Apply(new FakeEvent<TaskRevised>(EffortOnly()), view);
        projection.Apply(new FakeEvent<DraftChallengeRecorded>(verdict), view);
        view.RevisedSinceChallengeVerdict.Should().BeFalse("an effort change is not a revision of the text");

        projection.Apply(new FakeEvent<TaskRevised>(EffortOnly() with { Objective = Optional<string>.Of("Ship it, revised") }), view);
        view.RevisedSinceChallengeVerdict.Should().BeTrue("a revision of the text after a verdict");

        projection.Apply(new FakeEvent<DraftChallengeDispatched>(
            new DraftChallengeDispatched(taskId, DomainId.New(), nodeId, Now, AgentModel.ChallengeDefault, AgentEffort.High)), view);
        projection.Apply(new FakeEvent<TaskRevised>(EffortOnly() with { Objective = Optional<string>.Of("Ship it, revised again") }), view);
        projection.Apply(new FakeEvent<DraftChallengeRecorded>(verdict), view);
        view.LatestChallengeVerdictJudgedOlderDraft.Should().BeTrue("the verdict judged the text as it stood at dispatch");
        view.RevisedSinceChallengeVerdict.Should().BeFalse("that revision came before the verdict existed");

        projection.Apply(new FakeEvent<DraftChallengeDispatched>(
            new DraftChallengeDispatched(taskId, DomainId.New(), nodeId, Now, AgentModel.ChallengeDefault, AgentEffort.High)), view);
        projection.Apply(new FakeEvent<DraftChallengeRecorded>(verdict), view);
        view.RevisedSinceChallengeVerdict.Should().BeFalse();
        view.LatestChallengeVerdictJudgedOlderDraft.Should().BeFalse();
    }

    [Fact]
    public void The_recorded_event_holds_no_prose_and_no_machine_path()
    {
        typeof(DraftChallengeRecorded).GetProperties().Select(property => property.PropertyType)
            .Should().NotContain(typeof(string), "nothing the session wrote verbatim and no path reaches the stream");
        typeof(DraftChallengeDispatched).GetProperties().Select(property => property.PropertyType)
            .Should().NotContain(typeof(string));
    }

    [Fact]
    public void A_dispatch_counts_as_a_session_in_the_tasks_passage()
    {
        Guid taskId = DomainId.New();
        List<IEvent> events =
        [
            new FakeEvent<DraftChallengeDispatched>(new DraftChallengeDispatched(
                taskId, DomainId.New(), DomainId.New(), Now, AgentModel.ChallengeDefault, AgentEffort.High)),
            new FakeEvent<DraftChallengeDispatched>(new DraftChallengeDispatched(
                taskId, DomainId.New(), DomainId.New(), Now, AgentModel.ChallengeDefault, AgentEffort.High)),
        ];

        TaskPassageQuery.Compute(events, [], TaskType.Feature, taskConcluded: false, Now).Sessions.Should().Be(2);
    }

    [Fact]
    public void The_verdict_and_the_failure_reason_round_trip_through_their_stored_form()
    {
        ((ChallengeVerdict)(string)ChallengeVerdict.DoNotPublish).Should().Be(ChallengeVerdict.DoNotPublish);
        ((ChallengeFailureReason)(string)ChallengeFailureReason.UnreadableReport).Description
            .Should().Be(ChallengeFailureReason.UnreadableReport.Description);
        ((ChallengeVerdict)"nonsense").Should().Be(ChallengeVerdict.Unknown);
    }
}
