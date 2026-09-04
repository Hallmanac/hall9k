using Hall9k.Domain.Features.Tasks.Projections;
using Marten;
using Marten.Events;
using Marten.Linq.MatchesSql;

namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>
/// Re-projects task streams whose documents were last written before a change to the task
/// projections' shape. The projections are Inline, so a document is only ever rewritten when
/// its stream gets a new event: a task that reached Done last week still carries last week's
/// shape, and nothing else in the platform rebuilds it. Every field the projections learned
/// after a document was written therefore reads as absent on that document, and the code that
/// reads the field cannot tell absent-because-old from absent-because-nothing-was-recorded.
/// The events say the truth in both cases, so replaying them is the whole migration.
///
/// Origin incident (2026-08-20): the lifecycle split (Decisions Log #34) shipped the new claim
/// filter — <c>State == Queued</c> plus <c>AssignedOwnerId == this node's owner</c> — without
/// the rebuild, and a document with no assignedOwnerId key at all never matches it. Every one
/// of the 24 tasks in the dogfooding database was left permanently unclaimable, silently,
/// because an unclaimable task looks exactly like an idle queue.
///
/// Second incident (2026-08-21, caught in review before it shipped): the dead-blocker recovery
/// (Decisions Log #61) added deadDependencyReasons and made the displayed failure reason derive
/// from it alone. On a document written before that change the map reads empty while
/// deadDependencies is still populated, so the first blocker to recover or complete nulls the
/// reason for the blockers that are <em>still</em> dead — and no later sweep restores it, since
/// the aggregate (rebuilt from events, so its map is right) sees nothing new to record. The
/// task sits Blocked with nothing on the board saying why. Every new projection field needs a
/// marker here for the same reason.
/// </summary>
public static class TaskLifecycleProjectionBackfill
{
    /// <summary>
    /// What an out-of-date document looks like: a key the current projections always write —
    /// as a value, an explicit null, or an empty map — is simply absent. One marker per shape
    /// change, because a document can be old enough to be missing several and new enough to
    /// have the earlier ones; the alternation is parenthesised so it stays one predicate
    /// whatever Marten conjoins it with. Written as jsonb_exists rather than the <c>?</c>
    /// operator because <c>?</c> is Marten's parameter placeholder.
    /// <para>
    /// Markers here apply to both <see cref="TaskListItem"/> and <see cref="TaskDetails"/>: every
    /// field named below exists on both projections' current shape. A marker for a field only one
    /// of the two carries belongs on <see cref="StaleDetailsOnlyDocument"/> (details-only) or
    /// <see cref="StaleListOnlyDocument"/> (list-only) instead — mixing it in here would make the
    /// OTHER document type read as permanently stale, since a key that projection never writes at
    /// all is indistinguishable from one an old document is merely missing.
    /// </para>
    /// <para>
    /// <see cref="TaskListItem.EpicId"/> and <see cref="TaskDetails.EpicId"/> (Decisions Log #100)
    /// deliberately have no marker here, unlike every field above: they are nullable and mean
    /// "no epic", which is exactly the truthful reading of an absent key on a document written
    /// before epics existed. There is no dead-blocker-shaped failure mode to repair — a missing
    /// key and an explicit null read identically on every path that consumes this field.
    /// </para>
    /// </summary>
    private const string StaleDocument =
        "(not jsonb_exists(d.data, 'assignedOwnerId')"               // pre-lifecycle-split (log #34)
        + " or not jsonb_exists(d.data, 'deadDependencyReasons')"    // pre-blocker-recovery (log #61)
        + " or not jsonb_exists(d.data, 'assignedAt')"               // pre-concurrency-ceiling (log #64)
        + " or not jsonb_exists(d.data, 'failureReason'))";          // pre-status-redesign (log #66)

    /// <summary>
    /// <see cref="StaleDocument"/>'s markers, plus the fields <see cref="TaskDetails"/> alone
    /// carries (backlog 51): <see cref="TaskDetails.FailedRunId"/> and
    /// <see cref="TaskDetails.ResolvedRunId"/> exist only on the detail document, since only the
    /// daemon's project-home render sweep reads them, so a document written before either landed
    /// never had the key and would otherwise sit un-archived at the top level of <c>tasks/</c>
    /// forever, indistinguishable from a task genuinely still live. <see cref="TaskDetails.UntrackedAttested"/>
    /// (backlog: a task can be published deliberately untracked under a tracking backlog policy)
    /// is this group's marker. This store serializes nullable properties as explicit JSON nulls
    /// (<see cref="MartenConfiguration.ConfigureHall9k"/> sets no null-ignoring option), which is
    /// exactly why <see cref="TaskDetails.ResolvedRunId"/> above works as a marker despite being
    /// nullable — so its siblings <see cref="TaskDetails.UntrackedAttestedAt"/> and
    /// <see cref="TaskDetails.UntrackedAttestedByOwnerId"/> would have served equally well.
    /// <see cref="TaskDetails.UntrackedAttested"/> is used instead only because it is
    /// non-nullable and therefore always present, with no serialization nuance to reason about.
    /// </summary>
    private const string StaleDetailsOnlyDocument =
        "(" + StaleDocument
        + " or not jsonb_exists(d.data, 'failedRunId')"
        + " or not jsonb_exists(d.data, 'resolvedRunId')"
        + " or not jsonb_exists(d.data, 'untrackedAttested'))";

    /// <summary>
    /// <see cref="StaleDocument"/>'s markers, plus the field <see cref="TaskListItem"/> alone
    /// carries: <see cref="TaskListItem.QueuePriorityMarked"/> (task 45136b29) exists only on the
    /// list item — <see cref="TaskDetails"/> never gained it, since the dispatcher's claim query
    /// and <c>h9k status</c>'s queued-section ordering are its only two readers and both already
    /// work from <see cref="TaskListItem"/>. Mixing it into <see cref="StaleDocument"/> itself
    /// would make every <see cref="TaskDetails"/> document read as permanently stale, since that
    /// projection never writes the key at all — the same hazard <see cref="StaleDetailsOnlyDocument"/>
    /// exists to avoid for the fields only <see cref="TaskDetails"/> carries.
    /// <para>
    /// Without this marker, a <see cref="TaskListItem"/> document written before the field
    /// existed sorts <c>NULL</c> for <c>OrderByDescending(QueuePriorityMarked)</c>
    /// (<c>Hall9k.Daemon.Dispatch.DispatchEngine.ClaimEligibleAsync</c> — not linked as a
    /// <c>cref</c> since <c>Hall9k.Domain</c> references no Hall9k project and cannot resolve
    /// it), and PostgreSQL's default <c>DESC</c> ordering puts <c>NULL</c> <em>first</em> —
    /// ahead of a genuinely marked row — which is exactly backwards from what the marker is for.
    /// </para>
    /// </summary>
    private const string StaleListOnlyDocument =
        "(" + StaleDocument
        + " or not jsonb_exists(d.data, 'queuePriorityMarked'))";

    /// <summary>
    /// Rebuilds every task stream still carrying an out-of-date document and returns the ids it
    /// actually rebuilt. Idempotent and self-terminating: a rebuilt document has every key, so
    /// the next call finds nothing. Both task projections are single-stream on the task's own
    /// id, so one pass over the union of the two stale sets repairs both.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> RunAsync(
        IDocumentStore store, CancellationToken cancellationToken)
    {
        Guid[] stale = await StaleStreamsAsync(store, cancellationToken);
        if (stale.Length == 0)
        {
            return [];
        }

        List<Guid> rebuilt = [];
        foreach (Guid streamId in stale)
        {
            if (await RebuildAsync(store, streamId, cancellationToken))
            {
                rebuilt.Add(streamId);
            }
        }

        return rebuilt;
    }

    /// <summary>
    /// Replays one stream into both task documents, restored at the stream's own version, and
    /// says whether it did. A document whose stream cannot be replayed is left exactly as it is
    /// and reported as not rebuilt, rather than counted as repaired work nobody did.
    /// <para>
    /// The version is the whole reason this is hand-rolled rather than two calls to Marten's
    /// <c>Advanced.RebuildSingleStreamAsync</c>. An Inline projection skips an event whose
    /// version is not newer than the document it is applying to, and that convenience method
    /// stores the rebuilt document at <em>one past</em> the stream's version (repeated calls
    /// walk it up by one each time). The next event the stream ever receives is therefore
    /// silently dropped from the document — and in the case this backfill exists for, that next
    /// event is precisely the one the repair was needed for: the sweep's TaskDependencyRecovered
    /// on a task whose dead-blocker reasons were just restored. Deleting and re-storing at the
    /// version the events actually reach leaves the document where ordinary Inline projection
    /// left it, so the stream carries on normally. Both operations go in one transaction, so a
    /// crash mid-repair cannot leave a task with no document at all — a state this backfill
    /// could not even find again, since it queries the documents.
    /// </para>
    /// </summary>
    private static async Task<bool> RebuildAsync(
        IDocumentStore store, Guid streamId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();

        StreamState? state = await session.Events.FetchStreamStateAsync(streamId, cancellationToken);
        TaskListItem? row = state is null
            ? null
            : await session.Events.AggregateStreamAsync<TaskListItem>(streamId, token: cancellationToken);
        TaskDetails? details = row is null
            ? null
            : await session.Events.AggregateStreamAsync<TaskDetails>(streamId, token: cancellationToken);
        if (state is null || row is null || details is null)
        {
            return false;
        }

        session.Delete<TaskListItem>(streamId);
        session.Delete<TaskDetails>(streamId);
        session.UpdateRevision(row, (int)state.Version);
        session.UpdateRevision(details, (int)state.Version);

        await session.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static async Task<Guid[]> StaleStreamsAsync(
        IDocumentStore store, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();

        IReadOnlyList<Guid> rows = await session.Query<TaskListItem>()
            .Where(task => task.MatchesSql(StaleListOnlyDocument))
            .Select(task => task.Id)
            .ToListAsync(cancellationToken);
        IReadOnlyList<Guid> details = await session.Query<TaskDetails>()
            .Where(task => task.MatchesSql(StaleDetailsOnlyDocument))
            .Select(task => task.Id)
            .ToListAsync(cancellationToken);

        return [.. rows.Concat(details).Distinct()];
    }
}
