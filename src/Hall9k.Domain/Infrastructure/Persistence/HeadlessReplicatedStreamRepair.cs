using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Projections;
using JasperFx.Events;
using Marten;

namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>
/// Repairs every PARTIAL replicated stream already sitting on this install: a local stream whose own
/// first event is a copy of a teammate's fact and is not that aggregate's genesis, so the story here
/// starts in the middle (<see cref="PartialReplicatedStreamRules"/> is the rule itself).
/// <c>Hall9k.Connectors.Replication.EventReplicationInbox</c>'s own genesis-required guard refuses
/// this shape going forward, but a stream an earlier build already applied this way needs its own
/// migration the same way every projection shape change has (see
/// <see cref="TaskLifecycleProjectionBackfill"/>'s own doc): the events are already applied and
/// stamped Inline, so nothing rewrites the documents again on its own.
/// <para>
/// The repair reconstructs each affected stream's applied events back into
/// <see cref="HeldReplicatedEventRecord"/> — exactly the shape a live sweep now holds a genesis-less
/// tail in — using the headers that inbox already stamped on them and the local project id each
/// one's own now-deleted <see cref="ReplicatedEventRecord"/> dedupe row still remembers, since
/// neither the projected document nor the event payload carries this install's own project
/// coordinate. Deletes the aggregate's documents and the dedupe rows, then hard-deletes the
/// corrupted local stream itself — the one thing no Marten API reaches
/// (<see cref="Marten.Events.IEventStoreOperations.ArchiveStream(Guid)"/> marks a stream done, it
/// does not free its identity, and <c>StartStream</c> refuses an id already in <c>mt_streams</c>
/// regardless) — through the session's own connection, in code, rather than a hand-run script. Once
/// this runs, the stream reads exactly as it would have had this node never received anything for it
/// at all: nothing to show, and every fact it already received waiting, held, for the genesis a
/// future <c>h9k task pull</c> or catch-up answer still has to actually deliver. The sender's own
/// ledger is never touched, and nothing here re-derives or guesses at a fact this repair did not
/// itself observe on the stream it is fixing.
/// </para>
/// <para>
/// Why the v0.10.20 pass missed the streams actually sitting on this node, in the two ways it did.
/// It selected by the projected document (a <see cref="TaskListItem"/> with no project id or no
/// added-at, an <see cref="IdeaDetails"/> with no state or no captured-at), which only ever asked
/// about the two aggregates whose documents it queried: a partial Run stream, which is what the
/// three on this node are, and a partial Epic stream were invisible to it however plainly the
/// events said so. And it refused any stream the moment a single event on it lacked origin headers,
/// which this node's own dispatcher guarantees the moment it claims a phantom task. The selection
/// here reads the events, across all four replicated aggregates, and the planner reasons about a
/// native event on the stream instead of walking away from it.
/// </para>
/// <para>
/// A stream this repair cannot safely reconstruct (a replicated event missing a header the held
/// shape needs, one whose own dedupe row is already gone, or a native event on the stream outside
/// the doors the dispatch loop reaches a phantom through) is left exactly as it is rather than
/// discarded, and reported by id with its reason: the never-guess rule (AGENTS.md) applies to a
/// repair as much as to any other write, and a stream nothing can safely explain stays as it is
/// rather than losing history that could not be faithfully preserved.
/// </para>
/// </summary>
public static class HeadlessReplicatedStreamRepair
{
    /// <summary>A partial stream left exactly as it is, and why. Logged by the caller, since
    /// Hall9k.Domain takes no logging dependency.</summary>
    public sealed record Skipped(Guid StreamId, string Reason);

    /// <summary>What one pass actually did. <see cref="Repaired"/> carries each freed stream's own
    /// plan, so the caller can log the dropped native events and the run an abandoned claim named
    /// without this type restating either.</summary>
    public sealed record Report(
        IReadOnlyList<PartialReplicatedStreamRepairPlan> Repaired,
        IReadOnlyList<Skipped> Skipped)
    {
        public static readonly Report Nothing = new([], []);
    }

    /// <summary>Repairs every partial replicated stream this store currently holds.
    /// <paramref name="now"/> is the caller's own clock (<see cref="Hall9k.Daemon.Dispatch.DispatchLoop"/> reads
    /// <see cref="DateTimeOffset.UtcNow"/> once and passes it through) rather than read here, so a test can fix it
    /// the same way it already fixes every event's own timestamp, keeping the held rows' <c>HeldAt</c> — and the
    /// replay order it tie-breaks — independent of the host clock.</summary>
    public static async Task<Report> RunAsync(IDocumentStore store, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Guid[] partial = await PartialStreamIdsAsync(store, cancellationToken);
        if (partial.Length == 0)
        {
            return Report.Nothing;
        }

        List<PartialReplicatedStreamRepairPlan> repaired = [];
        List<Skipped> skipped = [];
        foreach (Guid streamId in partial)
        {
            PartialReplicatedStreamRepairPlanner.Decision decision =
                await RepairStreamAsync(store, streamId, now, cancellationToken);
            if (decision.Plan is not null)
            {
                repaired.Add(decision.Plan);
            }
            else if (decision.SkipReason is not null)
            {
                skipped.Add(new Skipped(streamId, decision.SkipReason));
            }
        }

        return new Report(repaired, skipped);
    }

    /// <summary>
    /// Every stream whose own version 1 makes it partial, read from the events and nothing else
    /// (<see cref="PartialReplicatedStreamRules.IsPartialStreamHead"/>). One query for every
    /// stream's first event, filtered in memory: the tell is a pair of event HEADERS and a .NET
    /// type, neither of which Marten's Linq provider reaches, and the alternative, naming every
    /// non-genesis event type of four aggregates in the query, would stop covering a new event
    /// type the moment one shipped, which is the exact class of miss this repair exists to clean up
    /// after. A startup-only pass over one row per stream is the cheaper mistake.
    /// </summary>
    private static async Task<Guid[]> PartialStreamIdsAsync(IDocumentStore store, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        IReadOnlyList<IEvent> heads = await session.Events.QueryAllRawEvents()
            .Where(@event => @event.Version == 1)
            .ToListAsync(cancellationToken);

        return [.. heads
            .Where(PartialReplicatedStreamRules.IsPartialStreamHead)
            .Select(head => head.StreamId)
            .Distinct()];
    }

    private static async Task<PartialReplicatedStreamRepairPlanner.Decision> RepairStreamAsync(
        IDocumentStore store, Guid streamId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(streamId, token: cancellationToken);
        PartialReplicatedStreamRepairPlanner.Decision decision = PartialReplicatedStreamRepairPlanner.Decide(
            streamId, events, await DedupeProjectIdsAsync(session, events, cancellationToken), now);
        if (decision.Plan is not PartialReplicatedStreamRepairPlan plan)
        {
            return decision;
        }

        foreach (HeldReplicatedEventRecord record in plan.Held)
        {
            session.Store(record);
            // A held record's id IS the origin event id dedupe is keyed on, so this is that same
            // row: gone, so a future genesis and the replay behind it are never blocked by the
            // applied copy this repair is undoing.
            session.Delete<ReplicatedEventRecord>(record.Id);
        }

        DeleteProjectedDocuments(session, plan.Aggregate, streamId);
        if (plan.DeletesTaskLease)
        {
            // Stays deleted only because the daemon's lease heartbeat PATCHES a lease rather than
            // storing the whole document back: this repair runs in DispatchLoop and the heartbeat
            // is a hosted service of its own on no shared gate, so a tick that queried its leases
            // just before this commit would, as an upsert, re-insert the very lease being freed
            // here — and then keep refreshing the resurrected row inside the timeout forever, so
            // the slot this repair exists to free stays counted (independent pre-PR review, cycle
            // 1, adversarial lens, medium; Hall9k.Daemon.Dispatch.LeaseHeartbeatService.RefreshAsync
            // carries the rule).
            session.Delete<TaskLease>(streamId);
        }

        // The corrupted local stream itself, hard-deleted, cascading (fkey_mt_events_stream_id) to
        // remove its own now-orphaned events too, so a future genesis can legitimately StartStream
        // this id as its own version 1. Queued into this SAME session as the document and
        // dedupe-row work above, rather than purged through a second session afterward: a second,
        // separate commit left this repair's own retry undetectable the moment the first one alone
        // landed, orphaning the held rows forever (independent pre-PR review, cycle 1, both lenses,
        // medium). Queuing the delete here makes the whole repair, the held rows and the dropped
        // claim's lease included, one atomic commit: either all of it lands or none of it does, and
        // the next daemon start retries the entire stream from scratch.
        session.QueueSqlCommand(
            $"delete from {store.Options.Events.DatabaseSchemaName}.mt_streams where id = ?", streamId);
        await session.SaveChangesAsync(cancellationToken);

        return decision;
    }

    /// <summary>The local project id each replicated event's own dedupe row remembers, loaded in one
    /// round trip for the whole stream.</summary>
    private static async Task<IReadOnlyDictionary<Guid, Guid>> DedupeProjectIdsAsync(
        IQuerySession session, IReadOnlyList<IEvent> events, CancellationToken cancellationToken)
    {
        HashSet<Guid> originEventIds = [];
        foreach (IEvent @event in events.Where(PartialReplicatedStreamRules.CarriesReplicationOrigin))
        {
            if (@event.GetHeader(ReplicationEventHeaders.OriginEventId) is string originEventIdText
                && Guid.TryParse(originEventIdText, out Guid originEventId))
            {
                originEventIds.Add(originEventId);
            }
        }

        if (originEventIds.Count == 0)
        {
            return new Dictionary<Guid, Guid>();
        }

        IReadOnlyList<ReplicatedEventRecord> rows =
            await session.LoadManyAsync<ReplicatedEventRecord>(cancellationToken, [.. originEventIds]);
        return rows.ToDictionary(row => row.Id, row => row.ProjectId);
    }

    /// <summary>
    /// The documents projected from the stream, by the aggregate it belongs to. Deleted by
    /// aggregate rather than blanket-deleted across every candidate table, so the delete says which
    /// aggregate this repair actually decided the stream belongs to instead of hedging across all
    /// of them.
    /// </summary>
    private static void DeleteProjectedDocuments(IDocumentSession session, ReplicatedAggregate aggregate, Guid streamId)
    {
        switch (aggregate)
        {
            case ReplicatedAggregate.Task:
                session.Delete<TaskListItem>(streamId);
                session.Delete<TaskDetails>(streamId);
                break;
            case ReplicatedAggregate.Idea:
                session.Delete<IdeaDetails>(streamId);
                break;
            case ReplicatedAggregate.Epic:
                session.Delete<EpicDetails>(streamId);
                break;
            case ReplicatedAggregate.Run:
                session.Delete<RunListItem>(streamId);
                session.Delete<RunDetails>(streamId);
                break;
            default:
                // Unreachable: a plan only ever exists for one of the four, because
                // PartialReplicatedStreamRules.IsPartialStreamHead is what admitted the stream.
                break;
        }
    }
}
