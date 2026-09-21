using System.Globalization;
using System.Text.Json;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Tasks.Projections;
using JasperFx.Events;
using Marten;
using Npgsql;

namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>
/// Repairs a headless <see cref="TaskListItem"/>/<see cref="TaskDetails"/> or <see cref="IdeaDetails"/>
/// already sitting on this install — <c>Hall9k.Connectors.Replication.EventReplicationInbox</c>'s own
/// genesis-required guard (a task whose <c>TaskAdded</c> predates the sender's outbox, a run whose
/// parent task never arrived) refuses this shape going forward, but a document this exact bug
/// already produced, on an earlier build, needs its own migration the same way every projection
/// shape change has (see <see cref="TaskLifecycleProjectionBackfill"/>'s own doc): the events are
/// already applied and stamped Inline, so nothing rewrites the document again on its own.
/// <para>
/// The repair reconstructs each affected stream's own applied events back into
/// <see cref="HeldReplicatedEventRecord"/> — exactly the shape a live sweep now holds a genesis-less
/// tail in — using the headers that inbox already stamped on them (origin node, origin event id,
/// origin sequence, the sender that delivered them) and the
/// local project id its own now-deleted <see cref="ReplicatedEventRecord"/> dedupe row
/// still remembers, since neither the headless document nor these particular event types (a task's
/// own branch-pushed or completed, say) carry one. Deletes the headless document and the dedupe
/// rows, then hard-deletes the corrupted local stream itself — the one thing no Marten API reaches
/// (<see cref="Marten.Events.IEventStoreOperations.ArchiveStream(Guid)"/> marks a stream done, it
/// does not free its identity, and <c>StartStream</c> refuses an id already in <c>mt_streams</c>
/// regardless) — through the session's own connection, in code, rather than a hand-run script. Once
/// this runs, the stream reads exactly as it would have had this node never received anything for it
/// at all: nothing to show, and every fact it already received waiting, held, for the genesis a
/// future <c>h9k task pull</c> or catch-up answer still has to actually deliver — the sender's own
/// ledger is never touched, and nothing here re-derives or guesses at a fact this repair did not
/// itself observe on the stream it is fixing.
/// </para>
/// <para>
/// A stream this repair cannot safely reconstruct — an event missing the headers above, or one
/// whose own dedupe row is already gone — is left exactly as it is rather than discarded: the
/// never-guess rule (AGENTS.md) applies to a repair as much as to any other write, and a headless
/// document nothing can safely explain stays headless rather than losing history that could not be
/// faithfully preserved.
/// </para>
/// </summary>
public static class HeadlessReplicatedStreamRepair
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Repairs every headless stream this store currently holds and returns the stream ids it actually fixed.</summary>
    public static async Task<IReadOnlyList<Guid>> RunAsync(IDocumentStore store, CancellationToken cancellationToken)
    {
        Guid[] headless = await HeadlessStreamIdsAsync(store, cancellationToken);
        if (headless.Length == 0)
        {
            return [];
        }

        List<Guid> repaired = [];
        foreach (Guid streamId in headless)
        {
            if (await RepairStreamAsync(store, streamId, cancellationToken))
            {
                repaired.Add(streamId);
            }
        }

        return repaired;
    }

    /// <summary>
    /// A headless <see cref="TaskListItem"/> or <see cref="IdeaDetails"/>: <see cref="TaskDetails"/>
    /// is single-stream on the identical id, so <see cref="TaskListItem"/>'s own headless ids already
    /// name every affected task stream without a second query. The identical tell
    /// <c>Hall9k.Cli.Commands.TaskStatusComposer</c> and <c>Hall9k.Cli.Commands.IdeaRow</c> use for
    /// the board's own row filter — restated here since Domain references no Hall9k project and
    /// cannot call either.
    /// </summary>
    private static async Task<Guid[]> HeadlessStreamIdsAsync(IDocumentStore store, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();

        // Filtered in memory rather than translated to SQL: IdeaState is a value object Marten's
        // Linq provider cannot compare server-side, and both tables are small (h9k status already
        // sweeps the whole of TaskListItem for its own rollups; IdeaDetails is "few and small" by
        // its own doc), so a startup-only migration costs nothing extra by reading them whole.
        IReadOnlyList<TaskListItem> tasks = await session.Query<TaskListItem>().ToListAsync(cancellationToken);
        IReadOnlyList<IdeaDetails> ideas = await session.Query<IdeaDetails>().ToListAsync(cancellationToken);

        Guid[] headlessTasks = [.. tasks
            .Where(task => task.ProjectId == Guid.Empty || task.AddedAt == default)
            .Select(task => task.Id)];
        Guid[] headlessIdeas = [.. ideas
            .Where(idea => idea.State == IdeaState.Unknown || idea.CapturedAt == default)
            .Select(idea => idea.Id)];

        return [.. headlessTasks.Concat(headlessIdeas).Distinct()];
    }

    private static async Task<bool> RepairStreamAsync(IDocumentStore store, Guid streamId, CancellationToken cancellationToken)
    {
        List<HeldReplicatedEventRecord> held;
        await using (IDocumentSession session = store.LightweightSession())
        {
            IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(streamId, token: cancellationToken);
            if (events.Count == 0)
            {
                return false;
            }

            held = [];
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (IEvent @event in events)
            {
                if (!TryReadOrigin(@event, out Guid originNodeId, out Guid originEventId, out long originSequence, out Guid senderNodeId))
                {
                    // Not a replicated event this repair knows how to preserve (a native local
                    // event, or one from a build too old to carry these headers) — nothing safe to
                    // hold, so the whole stream is left exactly as it is.
                    return false;
                }

                ReplicatedEventRecord? dedupe = await session.LoadAsync<ReplicatedEventRecord>(originEventId, cancellationToken);
                if (dedupe is null)
                {
                    // The local project id this record's own dedupe row carried when it was first
                    // (wrongly) applied — the one fact neither this event nor the headless document
                    // can answer, which is exactly why the document reads Guid.Empty/Unknown in the
                    // first place. Absent means this repair cannot say which project the held copy
                    // belongs to, so the never-guess rule leaves the stream exactly as it is.
                    return false;
                }

                string? originProjectKey = @event.GetHeader(ReplicationEventHeaders.OriginProjectKey) as string;
                string originOwnerRootFingerprint = @event.GetHeader(ReplicationEventHeaders.OriginOwnerRootFingerprint) as string ?? string.Empty;
                Guid originProjectId = @event.GetHeader(ReplicationEventHeaders.OriginProjectId) is string originProjectIdText
                    && Guid.TryParse(originProjectIdText, out Guid parsedOriginProjectId)
                        ? parsedOriginProjectId
                        : default;

                EventReplicationCodec.ReplicatedEventRecord record = new(
                    streamId, @event.EventType.FullName!, JsonSerializer.Serialize(@event.Data, JsonOptions),
                    originEventId, originSequence, originNodeId, originOwnerRootFingerprint, @event.Timestamp,
                    originProjectId);

                held.Add(new HeldReplicatedEventRecord
                {
                    Id = originEventId,
                    StreamId = streamId,
                    ProjectId = dedupe.ProjectId,
                    SenderNodeId = senderNodeId,
                    OriginProjectKey = originProjectKey,
                    RecordJson = EventReplicationCodec.EncodeRecord(record),
                    OriginSequence = originSequence,
                    OriginNodeId = originNodeId,
                    HeldAt = now,
                });
            }

            foreach (HeldReplicatedEventRecord record in held)
            {
                session.Store(record);
                session.Delete<ReplicatedEventRecord>(record.Id);
            }

            session.Delete<TaskListItem>(streamId);
            session.Delete<TaskDetails>(streamId);
            session.Delete<IdeaDetails>(streamId);
            await session.SaveChangesAsync(cancellationToken);
        }

        // The corrupted local stream itself, hard-deleted — cascading (fkey_mt_events_stream_id)
        // to remove its own now-orphaned events too — so a future genesis can legitimately
        // StartStream this id as its own version 1. A fresh session, after the document work
        // above has already committed: this repair's one fact worth losing on a crash between the
        // two is a redundant retry next start, never a document deleted with nothing held for it.
        await using IDocumentSession purgeSession = store.LightweightSession();
        await using NpgsqlCommand purge = new(
            $"delete from {store.Options.Events.DatabaseSchemaName}.mt_streams where id = @id");
        purge.Parameters.AddWithValue("id", streamId);
        await purgeSession.ExecuteAsync(purge, cancellationToken);

        return true;
    }

    private static bool TryReadOrigin(
        IEvent @event, out Guid originNodeId, out Guid originEventId, out long originSequence, out Guid senderNodeId)
    {
        originNodeId = default;
        originEventId = default;
        originSequence = default;
        senderNodeId = default;

        return @event.GetHeader(ReplicationEventHeaders.OriginNodeId) is string originNodeIdText
            && Guid.TryParse(originNodeIdText, out originNodeId)
            && @event.GetHeader(ReplicationEventHeaders.OriginEventId) is string originEventIdText
            && Guid.TryParse(originEventIdText, out originEventId)
            && @event.GetHeader(ReplicationEventHeaders.OriginSequence) is string originSequenceText
            && long.TryParse(originSequenceText, NumberStyles.Integer, CultureInfo.InvariantCulture, out originSequence)
            && @event.GetHeader(ReplicationEventHeaders.ReceivedFromNodeId) is string senderNodeIdText
            && Guid.TryParse(senderNodeIdText, out senderNodeId);
    }
}
