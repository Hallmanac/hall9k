using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Hall9k.Domain.Features.Tasks.Events;
using JasperFx.Events;

namespace Hall9k.Domain.Features.Replication;

/// <summary>
/// Decides, from a stream's own events alone, whether it is a partial replicated stream this repair
/// can free and exactly what freeing it comes to. Pure and store-free on purpose: this is where the
/// judgment lives, so it is testable through its own seam with no database, while
/// <see cref="Hall9k.Domain.Infrastructure.Persistence.HeadlessReplicatedStreamRepair"/> keeps only
/// the reading and the one atomic write.
/// </summary>
public static class PartialReplicatedStreamRepairPlanner
{
    /// <summary>The same web-default shape the inbox reads a wire record's payload back with
    /// (<c>EventReplicationInbox</c>'s own options), so a record rebuilt here decodes there.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// What to do about one stream. <see cref="Plan"/> set means repair it; <see cref="SkipReason"/>
    /// set means a partial stream this repair refuses to touch, which the caller logs by id; both
    /// null means the stream is not a partial replicated stream at all, so there is nothing to
    /// repair and nothing worth a log line either.
    /// </summary>
    public sealed record Decision(PartialReplicatedStreamRepairPlan? Plan, string? SkipReason)
    {
        public static readonly Decision NotACandidate = new(null, null);

        public static Decision Repair(PartialReplicatedStreamRepairPlan plan) => new(plan, null);

        public static Decision Skip(string reason) => new(null, reason);
    }

    /// <summary>
    /// <paramref name="events"/> is the stream in version order, exactly as
    /// <c>FetchStreamAsync</c> returns it. <paramref name="projectIdsByOriginEventId"/> is the local
    /// project id each replicated event's own dedupe row still remembers: the one fact neither the
    /// event nor its document can answer, since a task or run event carries the SENDER's project
    /// coordinate and the projected document may carry none at all. A replicated event whose row is
    /// gone, or which is missing a header the held shape needs, skips the whole stream rather than
    /// having either guessed at.
    /// <para>
    /// Only the FIRST event decides whether a stream is a candidate, which matters for one shape a
    /// build older than the append-only guard could produce: a tail applied first and the genesis
    /// appended behind it, so the stream replays backwards and the task reads as newly created.
    /// That stream is a candidate here, and repairing it is the right answer rather than an
    /// accident. Every replicated event including the genesis is held, and the held tail replays in
    /// ORIGIN order (<c>EventReplicationInbox.ApplyHeldTailAsync</c>), so the genesis goes first and
    /// the stream is rebuilt the right way round. What it costs is that the stream shows nothing
    /// until an ask brings the genesis back, since holding a genesis is not the same as replaying
    /// one; <c>h9k task pull</c> reads the freed stream as absent and can finally ask, which it
    /// could not while the stream read as whole.
    /// </para>
    /// </summary>
    public static Decision Decide(
        Guid streamId,
        IReadOnlyList<IEvent> events,
        IReadOnlyDictionary<Guid, Guid> projectIdsByOriginEventId,
        DateTimeOffset now)
    {
        if (events.Count == 0 || !PartialReplicatedStreamRules.IsPartialStreamHead(events[0]))
        {
            return Decision.NotACandidate;
        }

        ReplicatedAggregate aggregate = PartialReplicatedStreamRules.AggregateOf(events[0].EventType);
        List<HeldReplicatedEventRecord> held = [];
        List<string> droppedNativeEventTypes = [];
        Guid? claimedRunId = null;

        foreach (IEvent @event in events)
        {
            if (!PartialReplicatedStreamRules.CarriesReplicationOrigin(@event))
            {
                if (!PartialReplicatedStreamRules.IsDroppableNativeEvent(@event.EventType))
                {
                    return Decision.Skip(
                        $"it carries a native {@event.EventType.Name} at version {@event.Version}, which is not one "
                        + "of the doors this node's own dispatch loop reaches a phantom task through, so what "
                        + "dropping it would cost cannot be reasoned about here");
                }

                droppedNativeEventTypes.Add(@event.EventType.Name);
                if (@event.Data is TaskClaimed claimed)
                {
                    claimedRunId = claimed.RunId;
                }

                continue;
            }

            if (!TryReadOrigin(@event, out Origin? origin))
            {
                return Decision.Skip(
                    $"its replicated event at version {@event.Version} ({@event.EventType.Name}) is missing an "
                    + "origin header the held shape needs, so it cannot be preserved faithfully");
            }

            if (!projectIdsByOriginEventId.TryGetValue(origin.EventId, out Guid projectId))
            {
                return Decision.Skip(
                    $"the dedupe row for its replicated event {origin.EventId} is already gone, so nothing here "
                    + "can say which local project the held copy belongs to");
            }

            held.Add(HoldOf(streamId, @event, origin, projectId, now));
        }

        return Decision.Repair(
            new PartialReplicatedStreamRepairPlan(streamId, aggregate, held, droppedNativeEventTypes, claimedRunId));
    }

    private static HeldReplicatedEventRecord HoldOf(
        Guid streamId, IEvent @event, Origin origin, Guid projectId, DateTimeOffset now)
    {
        EventReplicationCodec.ReplicatedEventRecord record = new(
            streamId,
            @event.EventType.FullName!,
            JsonSerializer.Serialize(@event.Data, @event.EventType, JsonOptions),
            origin.EventId,
            origin.Sequence,
            origin.NodeId,
            origin.OwnerRootFingerprint,
            @event.Timestamp,
            origin.ProjectId);

        return new HeldReplicatedEventRecord
        {
            Id = origin.EventId,
            StreamId = streamId,
            ProjectId = projectId,
            SenderNodeId = origin.SenderNodeId,
            OriginProjectKey = origin.ProjectKey,
            RecordJson = EventReplicationCodec.EncodeRecord(record),
            OriginSequence = origin.Sequence,
            OriginNodeId = origin.NodeId,
            HeldAt = now,
        };
    }

    /// <summary>The origin metadata an applied copy carries, read back off its own headers.</summary>
    private sealed record Origin(
        Guid NodeId,
        Guid EventId,
        long Sequence,
        Guid SenderNodeId,
        string OwnerRootFingerprint,
        Guid ProjectId,
        string? ProjectKey);

    private static bool TryReadOrigin(IEvent @event, [NotNullWhen(true)] out Origin? origin)
    {
        origin = null;
        if (@event.GetHeader(ReplicationEventHeaders.OriginNodeId) is not string originNodeIdText
            || !Guid.TryParse(originNodeIdText, out Guid originNodeId)
            || @event.GetHeader(ReplicationEventHeaders.OriginEventId) is not string originEventIdText
            || !Guid.TryParse(originEventIdText, out Guid originEventId)
            || @event.GetHeader(ReplicationEventHeaders.OriginSequence) is not string originSequenceText
            || !long.TryParse(originSequenceText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long originSequence)
            || @event.GetHeader(ReplicationEventHeaders.ReceivedFromNodeId) is not string senderNodeIdText
            || !Guid.TryParse(senderNodeIdText, out Guid senderNodeId))
        {
            return false;
        }

        origin = new Origin(
            originNodeId,
            originEventId,
            originSequence,
            senderNodeId,
            @event.GetHeader(ReplicationEventHeaders.OriginOwnerRootFingerprint) as string ?? string.Empty,
            @event.GetHeader(ReplicationEventHeaders.OriginProjectId) is string originProjectIdText
                && Guid.TryParse(originProjectIdText, out Guid originProjectId)
                    ? originProjectId
                    : default,
            @event.GetHeader(ReplicationEventHeaders.OriginProjectKey) as string);
        return true;
    }
}
