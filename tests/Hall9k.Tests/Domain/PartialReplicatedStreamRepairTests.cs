using System.Text.Json;
using FluentAssertions;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using JasperFx.Events;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The judgment behind the partial-stream repair, exercised through its own seam with no database
/// in the way: <see cref="PartialReplicatedStreamRules"/> decides which streams are even candidates
/// and <see cref="PartialReplicatedStreamRepairPlanner"/> decides what repairing one comes to.
/// <para>
/// A plan's <see cref="PartialReplicatedStreamRepairPlan.Aggregate"/> is what selects the documents
/// the repair deletes (a Run plan deletes <c>RunListItem</c> and <c>RunDetails</c>, a Task plan
/// <c>TaskListItem</c> and <c>TaskDetails</c>), so asserting it here is asserting that choice;
/// the rows actually disappearing from Postgres is what
/// <c>Hall9k.Tests.Integration.HeadlessReplicatedStreamRepairTests</c> proves against a real store.
/// Nothing here touches a repository or a remote (Brian's testing rule, 2026-09-13).
/// </para>
/// </summary>
public sealed class PartialReplicatedStreamRepairTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 4, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Guid MacNodeId = DomainId.New();
    private static readonly Guid OwnerId = DomainId.New();

    /// <summary>
    /// The reference case: the 13d6b371 tail, which opens at <see cref="TaskReturnedToDraft"/> and
    /// runs through the assigning node's own placement. Every replicated event is held, and the
    /// held records replay the whole story once the genesis finally lands.
    /// </summary>
    [Fact]
    public void A_partial_task_stream_is_planned_for_repair_and_its_held_records_replay_whole_when_the_genesis_arrives()
    {
        Guid taskId = DomainId.New();
        Guid localProjectId = DomainId.New();
        Guid dependencyId = DomainId.New();
        Guid placedOnNodeId = DomainId.New();

        IEvent[] events =
        [
            Replicated(taskId, new TaskReturnedToDraft(taskId, "held for redesign", Now, OwnerId), 48_028, version: 1),
            Replicated(taskId, Revised(taskId), 48_029, version: 2),
            Replicated(taskId, new TaskPublished(taskId, Now.AddMinutes(2), OwnerId), 48_030, version: 3),
            Replicated(
                taskId,
                new TaskAssigned(taskId, OwnerId, [dependencyId], Now.AddMinutes(3), OwnerId, null, placedOnNodeId),
                48_031,
                version: 4),
            Replicated(
                taskId,
                new TaskDependencyCompleted(taskId, dependencyId, [], Now.AddMinutes(4)),
                48_781,
                version: 5),
        ];

        PartialReplicatedStreamRepairPlanner.Decision decision = PartialReplicatedStreamRepairPlanner.Decide(
            taskId, events, DedupeRows(events, localProjectId), Now);

        decision.SkipReason.Should().BeNull();
        decision.Plan.Should().NotBeNull();
        PartialReplicatedStreamRepairPlan plan = decision.Plan!;
        plan.Aggregate.Should().Be(ReplicatedAggregate.Task);
        plan.Held.Should().HaveCount(5, "nothing replicated is lost, only held");
        plan.Held.Should().OnlyContain(record => record.ProjectId == localProjectId,
            "the local project coordinate comes from each event's own dedupe row, which is the only place it exists");
        plan.Held.Should().OnlyContain(record => record.OriginNodeId == MacNodeId);
        plan.DroppedNativeEventTypes.Should().BeEmpty();
        plan.DeletesTaskLease.Should().BeFalse("nothing local ever claimed this one");

        // The genesis finally lands and the held tail replays behind it, in origin order, exactly
        // as EventReplicationInbox.ApplyHeldTailAsync orders it.
        TaskAggregate task = new();
        task.Apply(new TaskAdded(
            taskId, localProjectId, "Repair the partial streams", ["the streams are freed"], TaskType.Feature,
            null, null, null, Now.AddMinutes(-1), OwnerId, BlockedBy: [dependencyId]));
        foreach (object replayed in ReplayOrder(plan))
        {
            Replay(task, replayed);
        }

        task.State.Should().Be(TaskState.Queued, "which is the task's true state, not the Working the phantom read");
        task.PlacedOnNodeId.Should().Be(placedOnNodeId, "so this node's own dispatcher claims it afresh");
        task.Objective.Should().Be("Repair the partial streams");
    }

    /// <summary>
    /// The 13d6b371 shape: this node's own dispatcher claimed the phantom, so the stream carries
    /// two native events with no origin headers and a <c>TaskLease</c> the heartbeat service keeps
    /// alive. The claim is dropped rather than re-held, because replaying it after the genesis
    /// would leave the task Working with no run alive to conclude it, and the lease goes with it
    /// because a lease whose run cannot be found counts as a live slot.
    /// </summary>
    [Fact]
    public void A_partial_stream_carrying_the_dispatchers_native_claim_drops_it_and_takes_the_lease_with_it()
    {
        Guid taskId = DomainId.New();
        Guid localProjectId = DomainId.New();
        Guid dependencyId = DomainId.New();
        Guid abandonedRunId = DomainId.New();
        Guid thisNodeId = DomainId.New();

        IEvent[] events =
        [
            Replicated(taskId, new TaskReturnedToDraft(taskId, "held for redesign", Now, OwnerId), 48_028, version: 1),
            Native(taskId, new TaskDependencyCompleted(taskId, dependencyId, [], Now.AddHours(2)), version: 2),
            Native(taskId, new TaskClaimed(taskId, thisNodeId, OwnerId, 1, abandonedRunId, Now.AddHours(2)), version: 3),
            Replicated(
                taskId,
                new TaskDependencyCompleted(taskId, dependencyId, [], Now.AddHours(3)),
                48_781,
                version: 4),
        ];

        PartialReplicatedStreamRepairPlanner.Decision decision = PartialReplicatedStreamRepairPlanner.Decide(
            taskId, events, DedupeRows(events, localProjectId), Now);

        decision.Plan.Should().NotBeNull();
        PartialReplicatedStreamRepairPlan plan = decision.Plan!;
        plan.Held.Should().HaveCount(2, "only the replicated copies are held");
        plan.DroppedNativeEventTypes.Should().Equal(nameof(TaskDependencyCompleted), nameof(TaskClaimed));
        plan.ClaimedRunId.Should().Be(abandonedRunId, "so the log line can name the run the claim was holding a slot for");
        plan.DeletesTaskLease.Should().BeTrue();
    }

    /// <summary>A requeue with no claim before it leaves nothing to name, which is recorded as
    /// unknown rather than filled in with a plausible run id (AGENTS.md's never-guess rule).</summary>
    [Fact]
    public void A_dropped_requeue_with_no_claim_beside_it_names_no_run()
    {
        Guid taskId = DomainId.New();
        Guid localProjectId = DomainId.New();

        IEvent[] events =
        [
            Replicated(taskId, new TaskAssigned(taskId, OwnerId, [], Now, OwnerId), 100, version: 1),
            Native(taskId, new TaskRequeued(taskId, RequeueReason.LeaseExpired, Now.AddHours(1)), version: 2),
        ];

        PartialReplicatedStreamRepairPlan plan = PartialReplicatedStreamRepairPlanner
            .Decide(taskId, events, DedupeRows(events, localProjectId), Now).Plan!;

        plan.DroppedNativeEventTypes.Should().Equal(nameof(TaskRequeued));
        plan.ClaimedRunId.Should().BeNull();
        plan.DeletesTaskLease.Should().BeTrue("a requeue is one of the doors a phantom claim leaves a lease behind");
    }

    /// <summary>
    /// Every door this node's own dispatch loop actually reaches a phantom through, on one stream,
    /// in the order the dispatcher writes them: the dependency resolver's three verdicts, the
    /// claim gate's evidence riding in the claim's own transaction, and the lease sweep giving the
    /// claim back as the PAIR it always writes. Allowing <see cref="TaskRequeued"/> without
    /// <see cref="TaskHolderReleased"/> made that allowance unreachable — the sweep appends both
    /// whenever the expiring lease and the task's holder are this node, which a native claim
    /// always makes true — so this stream was refused and left partial forever even though
    /// nothing on it came from anywhere but the dispatcher (independent pre-PR review, cycle 1,
    /// adversarial lens).
    /// </summary>
    [Fact]
    public void A_partial_stream_carrying_every_dispatcher_door_is_repaired_rather_than_refused()
    {
        Guid taskId = DomainId.New();
        Guid localProjectId = DomainId.New();
        Guid dependencyId = DomainId.New();
        Guid abandonedRunId = DomainId.New();
        Guid thisNodeId = DomainId.New();

        IEvent[] events =
        [
            Replicated(taskId, new TaskAssigned(taskId, OwnerId, [dependencyId], Now, OwnerId), 55, version: 1),
            Native(taskId, new TaskDependencyFailed(taskId, dependencyId, "blocker abandoned", Now.AddHours(1)), version: 2),
            Native(taskId, new TaskDependencyRecovered(taskId, dependencyId, "blocker retried", Now.AddHours(2)), version: 3),
            Native(taskId, new TaskDependencyCompleted(taskId, dependencyId, [], Now.AddHours(3)), version: 4),
            Native(
                taskId,
                new TrackerAssignmentObserved(taskId, "ARX-1", "account-id", "Brian Hall", Now.AddHours(4)),
                version: 5),
            Native(taskId, new TaskClaimed(taskId, thisNodeId, OwnerId, 1, abandonedRunId, Now.AddHours(4)), version: 6),
            Native(taskId, new TaskRequeued(taskId, RequeueReason.LeaseExpired, Now.AddHours(5)), version: 7),
            Native(taskId, new TaskHolderReleased(taskId, Now.AddHours(5)), version: 8),
        ];

        PartialReplicatedStreamRepairPlanner.Decision decision = PartialReplicatedStreamRepairPlanner.Decide(
            taskId, events, DedupeRows(events, localProjectId), Now);

        decision.SkipReason.Should().BeNull();
        PartialReplicatedStreamRepairPlan plan = decision.Plan!;
        plan.Held.Should().ContainSingle("only the one replicated copy is held");
        plan.DroppedNativeEventTypes.Should().Equal(
            nameof(TaskDependencyFailed),
            nameof(TaskDependencyRecovered),
            nameof(TaskDependencyCompleted),
            nameof(TrackerAssignmentObserved),
            nameof(TaskClaimed),
            nameof(TaskRequeued),
            nameof(TaskHolderReleased));
        plan.ClaimedRunId.Should().Be(abandonedRunId);
        plan.DeletesTaskLease.Should().BeTrue();
    }

    /// <summary>
    /// A run tail that opens at <see cref="TokensRecorded"/>: the three partial streams actually on
    /// this node are runs, and the v0.10.20 selection never queried a run document at all. The Run
    /// classification here is what makes the repair delete <c>RunListItem</c> and <c>RunDetails</c>.
    /// </summary>
    [Fact]
    public void A_partial_run_stream_is_planned_for_repair_as_a_run()
    {
        Guid runId = DomainId.New();
        Guid localProjectId = DomainId.New();

        IEvent[] events =
        [
            Replicated(
                runId,
                new TokensRecorded(runId, 118, 4_200, 0.42m, Now, 8_239_942, 196_080, AgentModel.Unknown),
                91_000,
                version: 1),
            Replicated(runId, new RunCompleted(runId, Now.AddMinutes(1)), 91_001, version: 2),
        ];

        PartialReplicatedStreamRepairPlan plan = PartialReplicatedStreamRepairPlanner
            .Decide(runId, events, DedupeRows(events, localProjectId), Now).Plan!;

        plan.Aggregate.Should().Be(ReplicatedAggregate.Run);
        plan.Held.Should().HaveCount(2);
        plan.DeletesTaskLease.Should().BeFalse("only a task stream has a lease");
    }

    /// <summary>A stream that starts from its own genesis holds the whole story, so there is
    /// nothing to free and nothing worth reporting either.</summary>
    [Fact]
    public void A_whole_replicated_stream_is_not_a_candidate()
    {
        Guid taskId = DomainId.New();
        IEvent genesis = Replicated(
            taskId,
            new TaskAdded(
                taskId, DomainId.New(), "Whole story", ["it is whole"], TaskType.Feature, null, null, null, Now,
                OwnerId),
            500,
            version: 1);

        PartialReplicatedStreamRules.IsPartialStreamHead(genesis).Should().BeFalse();
        PartialReplicatedStreamRepairPlanner
            .Decide(taskId, [genesis], DedupeRows([genesis], DomainId.New()), Now)
            .Should().Be(PartialReplicatedStreamRepairPlanner.Decision.NotACandidate);
    }

    /// <summary>A stream this node created itself carries no origin headers on its first event, so
    /// the selection never reaches it whatever its shape: the never-guess boundary.</summary>
    [Fact]
    public void A_native_stream_is_not_a_candidate_however_mid_story_it_starts()
    {
        Guid taskId = DomainId.New();
        IEvent native = Native(
            taskId, new TaskCompleted(taskId, DomainId.New(), "https://github.com/x/y/pull/1", Now), version: 1);

        PartialReplicatedStreamRules.IsPartialStreamHead(native).Should().BeFalse();
        PartialReplicatedStreamRepairPlanner.Decide(taskId, [native], new Dictionary<Guid, Guid>(), Now)
            .Should().Be(PartialReplicatedStreamRepairPlanner.Decision.NotACandidate);
    }

    /// <summary>
    /// A teammate's own per-install project lifecycle decision is MEANT to start a stream with no
    /// genesis, under a project id this install does not own
    /// (<see cref="ProjectStreamReplicationRules.IsProjectLifecycleEvent"/>), so freeing one would
    /// destroy the record of it rather than repair anything.
    /// </summary>
    [Fact]
    public void A_phantom_project_lifecycle_stream_is_not_a_candidate()
    {
        Guid foreignProjectId = DomainId.New();
        IEvent archived = Replicated(
            foreignProjectId, new ProjectArchived(foreignProjectId, "done with it", Now, OwnerId), 7, version: 1);

        PartialReplicatedStreamRules.IsPartialStreamHead(archived).Should().BeFalse();
        PartialReplicatedStreamRepairPlanner
            .Decide(foreignProjectId, [archived], DedupeRows([archived], DomainId.New()), Now)
            .Should().Be(PartialReplicatedStreamRepairPlanner.Decision.NotACandidate);
    }

    /// <summary>A native event outside the doors the dispatch loop reaches a phantom through is a
    /// stream this repair cannot reason about, so it is reported by id and left exactly as it is.</summary>
    [Fact]
    public void A_partial_stream_carrying_an_unexpected_native_event_is_skipped_with_a_reason()
    {
        Guid taskId = DomainId.New();
        Guid localProjectId = DomainId.New();

        IEvent[] events =
        [
            Replicated(taskId, new TaskAssigned(taskId, OwnerId, [], Now, OwnerId), 55, version: 1),
            Native(taskId, new TaskAbandoned(taskId, "given up on", Now.AddHours(1), OwnerId), version: 2),
        ];

        PartialReplicatedStreamRepairPlanner.Decision decision = PartialReplicatedStreamRepairPlanner.Decide(
            taskId, events, DedupeRows(events, localProjectId), Now);

        decision.Plan.Should().BeNull();
        decision.SkipReason.Should().Contain(nameof(TaskAbandoned));
    }

    /// <summary>A replicated event whose dedupe row is already gone takes the local project id with
    /// it, and nothing else on the stream can answer for it, so the whole stream is left alone.</summary>
    [Fact]
    public void A_partial_stream_whose_dedupe_row_is_gone_is_skipped_with_a_reason()
    {
        Guid taskId = DomainId.New();

        IEvent[] events =
        [
            Replicated(taskId, new TaskAssigned(taskId, OwnerId, [], Now, OwnerId), 55, version: 1),
        ];

        PartialReplicatedStreamRepairPlanner.Decision decision = PartialReplicatedStreamRepairPlanner.Decide(
            taskId, events, new Dictionary<Guid, Guid>(), Now);

        decision.Plan.Should().BeNull();
        decision.SkipReason.Should().Contain("dedupe row");
    }

    /// <summary>A replicated event missing a header the held shape needs cannot be preserved
    /// faithfully, and a partial preservation is worse than none.</summary>
    [Fact]
    public void A_partial_stream_missing_an_origin_header_is_skipped_with_a_reason()
    {
        Guid taskId = DomainId.New();
        Guid originEventId = DomainId.New();
        FakeEvent<TaskAssigned> incomplete = new(new TaskAssigned(taskId, OwnerId, [], Now, OwnerId))
        {
            StreamId = taskId, Version = 1, Timestamp = Now,
        };
        incomplete.SetHeader(ReplicationEventHeaders.OriginNodeId, MacNodeId.ToString());
        incomplete.SetHeader(ReplicationEventHeaders.OriginEventId, originEventId.ToString());
        // No OriginSequence and no ReceivedFromNodeId: a held record cannot be rebuilt without the
        // sequence it replays in or the sender it arrived from.

        PartialReplicatedStreamRepairPlanner.Decision decision = PartialReplicatedStreamRepairPlanner.Decide(
            taskId, [incomplete], new Dictionary<Guid, Guid> { [originEventId] = DomainId.New() }, Now);

        decision.Plan.Should().BeNull();
        decision.SkipReason.Should().Contain("origin header");
    }

    /// <summary>
    /// The planner's own empty-stream guard, which is what makes the repair idempotent: a freed
    /// stream has no events under its id at all. That second-pass property itself is proved
    /// against a real store, after a real first pass, by
    /// <c>Hall9k.Tests.Integration.HeadlessReplicatedStreamRepairTests.A_second_pass_right_after_the_first_repairs_nothing</c>;
    /// this covers only the guard the planner reaches it through.
    /// </summary>
    [Fact]
    public void An_empty_stream_is_not_a_candidate()
    {
        Guid taskId = DomainId.New();

        PartialReplicatedStreamRepairPlanner.Decide(taskId, [], new Dictionary<Guid, Guid>(), Now)
            .Should().Be(PartialReplicatedStreamRepairPlanner.Decision.NotACandidate);
    }

    private static TaskRevised Revised(Guid taskId) => new(
        taskId,
        Optional<string>.None,
        Optional<IReadOnlyList<string>>.None,
        Optional<string>.None,
        Optional<IReadOnlyList<Guid>>.None,
        Optional<TaskType>.None,
        Optional<AgentModel>.None,
        Now.AddMinutes(1),
        OwnerId);

    /// <summary>The held tail in the order <c>EventReplicationInbox.ApplyHeldTailAsync</c> replays
    /// it, decoded back out of the wire records the plan rebuilt.</summary>
    private static IEnumerable<object> ReplayOrder(PartialReplicatedStreamRepairPlan plan) => plan.Held
        .OrderBy(record => record.OriginSequence)
        .ThenBy(record => record.HeldAt)
        .Select(record => EventReplicationCodec.DecodeRecord(record.RecordJson)!)
        .Select(record => JsonSerializer.Deserialize(
            record.EventDataJson, ReplicationEventTypeCatalog.Resolve(record.EventTypeName)!, JsonOptions)!);

    private static void Replay(TaskAggregate task, object @event)
    {
        switch (@event)
        {
            case TaskReturnedToDraft returned:
                task.Apply(returned);
                break;
            case TaskRevised revised:
                task.Apply(revised);
                break;
            case TaskPublished published:
                task.Apply(published);
                break;
            case TaskAssigned assigned:
                task.Apply(assigned);
                break;
            case TaskDependencyCompleted dependencyCompleted:
                task.Apply(dependencyCompleted);
                break;
            default:
                throw new InvalidOperationException(
                    $"the held tail decoded to an unexpected {@event.GetType().Name}");
        }
    }

    /// <summary>The dedupe row each replicated event on the stream still has, all pointing at this
    /// install's own local project id.</summary>
    private static IReadOnlyDictionary<Guid, Guid> DedupeRows(IReadOnlyList<IEvent> events, Guid localProjectId) =>
        events
            .Where(PartialReplicatedStreamRules.CarriesReplicationOrigin)
            .ToDictionary(
                @event => Guid.Parse((string)@event.GetHeader(ReplicationEventHeaders.OriginEventId)!),
                _ => localProjectId);

    private static IEvent Replicated<T>(Guid streamId, T data, long originSequence, long version)
        where T : notnull
    {
        FakeEvent<T> @event = new(data)
        {
            StreamId = streamId, Version = version, Timestamp = Now.AddSeconds(version),
        };
        @event.SetHeader(ReplicationEventHeaders.OriginNodeId, MacNodeId.ToString());
        @event.SetHeader(ReplicationEventHeaders.OriginOwnerRootFingerprint, "owner-a-fingerprint");
        @event.SetHeader(ReplicationEventHeaders.OriginEventId, DomainId.New().ToString());
        @event.SetHeader(ReplicationEventHeaders.OriginSequence, originSequence.ToString());
        @event.SetHeader(ReplicationEventHeaders.ReceivedFromNodeId, MacNodeId.ToString());
        @event.SetHeader(ReplicationEventHeaders.ReceivedAt, Now.ToString("O"));
        return @event;
    }

    private static IEvent Native<T>(Guid streamId, T data, long version)
        where T : notnull =>
        new FakeEvent<T>(data) { StreamId = streamId, Version = version, Timestamp = Now.AddSeconds(version) };
}
