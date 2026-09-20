using Hall9k.Domain.Features.Tasks.Events;
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
    /// <see cref="TaskListItem.PreApproval"/> / <see cref="TaskDetails.PreApproval"/> (task: the
    /// people a pull request is waiting on are named, and pre-approval gains a mode that waits for
    /// human review) is the newest marker. A document written before pre-approval became
    /// three-valued carries only the boolean, which is why both projections resolve the mode
    /// through <see cref="TaskListItem.EffectivePreApproval"/> rather than reading the field raw —
    /// so the missing key is already read honestly, and this marker is what eventually retires
    /// the fallback rather than what keeps the surfaces correct in the meantime.
    /// </para>
    /// <para>
    /// <see cref="TaskListItem.EpicId"/> and <see cref="TaskDetails.EpicId"/> (Decisions Log #100)
    /// deliberately have no marker here, unlike every field above: they are nullable and mean
    /// "no epic", which is exactly the truthful reading of an absent key on a document written
    /// before epics existed. There is no dead-blocker-shaped failure mode to repair — a missing
    /// key and an explicit null read identically on every path that consumes this field.
    /// </para>
    /// <para>
    /// <see cref="TaskListItem.AssignedOwnerFingerprint"/> and
    /// <see cref="TaskDetails.AssignedOwnerFingerprint"/> genuinely have no <c>jsonb_exists</c>
    /// marker here, unlike every field above: the key is present, as an explicit null, on every
    /// document this backfill would otherwise need to catch, so <c>jsonb_exists</c> cannot tell
    /// "no fingerprint was ever recorded" from "a fingerprint was discarded by the projection that
    /// used to run" — both read as the same present-but-null key. A missing key is still the
    /// truthful "no fingerprint" reading for a document written before the field existed at all, or
    /// last written by a grant-clearing door that legitimately has none to carry forward.
    /// <see cref="StaleFingerprintStreamsAsync"/> is the marker instead, for exactly the one shape
    /// this class of check cannot reach: idea f72138e1's own origin incident, a document whose
    /// stream's own <see cref="TaskAssigned"/> event does carry
    /// <see cref="TaskAssigned.AssignedOwnerRootFingerprint"/> but whose document was last written
    /// by the projection version that discarded it. It reads the raw event, not the document, which
    /// is the only way to tell the two apart.
    /// </para>
    /// <para>
    /// <see cref="TaskListItem.PlacedOnNodeId"/> and <see cref="TaskDetails.PlacedOnNodeId"/> (idea
    /// 202383dc: an owner can place a task on one of their own nodes) belong to the
    /// <see cref="TaskListItem.EpicId"/> class, not the fingerprint class above: they are nullable
    /// and mean "unplaced", which is exactly the truthful reading of an absent key on a document
    /// written before placement existed, an explicit null left by an unassign or an interactive
    /// claim unassign, and a task that was never placed at all. There is no dead-blocker-shaped
    /// failure mode to repair — a missing key, an explicit null, and "never placed" all read
    /// identically on every path that consumes this field, which
    /// <c>DispatchEngineNodePlacementGateTests</c>' unplaced-admits-everyone case already proves.
    /// </para>
    /// </summary>
    private const string StaleDocument =
        "(not jsonb_exists(d.data, 'assignedOwnerId')"               // pre-lifecycle-split (log #34)
        + " or not jsonb_exists(d.data, 'deadDependencyReasons')"    // pre-blocker-recovery (log #61)
        + " or not jsonb_exists(d.data, 'assignedAt')"               // pre-concurrency-ceiling (log #64)
        + " or not jsonb_exists(d.data, 'failureReason')"            // pre-status-redesign (log #66)
        + " or not jsonb_exists(d.data, 'preApproval'))";           // pre-three-valued pre-approval

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
    /// <see cref="TaskDetails.RetryPending"/> (task: a headless retry's reason reaches the
    /// resumed session) joins this group for the identical reason: it is non-nullable, so it is
    /// always present on a document the current projection wrote, and its absent-key reading —
    /// "no retry pending" — is exactly wrong for a task that was retried on the pre-marker build
    /// and has not been claimed since (independent pre-PR review, cycle 3, both lenses).
    /// </summary>
    private const string StaleDetailsOnlyDocument =
        "(" + StaleDocument
        + " or not jsonb_exists(d.data, 'failedRunId')"
        + " or not jsonb_exists(d.data, 'resolvedRunId')"
        + " or not jsonb_exists(d.data, 'untrackedAttested')"
        + " or not jsonb_exists(d.data, 'retryPending'))";

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
    /// <para>
    /// <see cref="TaskListItem.FollowUpBranch"/> and <see cref="TaskListItem.RetryPending"/> (task:
    /// the dispatcher ranks the ready queue by lifecycle position before age) join this group for
    /// the identical class of defect the ordering markers above already cover: a document written
    /// before the queue read widened to carry them has no key for either, and an absent key reads
    /// as null/false exactly like a task that genuinely has no pending lap or retry. Both fields
    /// already exist on <see cref="TaskDetails"/> (they predate this task, and
    /// <see cref="TaskDetails.RetryPending"/> already has its own marker in
    /// <see cref="StaleDetailsOnlyDocument"/>), so the marker only belongs here — mixing it into
    /// <see cref="StaleDocument"/> would read every <see cref="TaskDetails"/> document ever written
    /// as permanently stale, since that projection never lacked the key.
    /// Without it, a task sitting in the queue with a genuine follow-up or retry pending since
    /// before this build reads as a plain first claim until something else appends to its stream —
    /// which for a queued task, waiting to be claimed, may be a very long time — and is ranked
    /// behind newer work it should have outranked. <see cref="TaskListItem.RetryPending"/>
    /// (Copilot review, PR #346) replaced <c>retryBranch</c> as this group's retry marker: a
    /// retried task's branch is null both when nothing is pending and when the failure predated
    /// any run record, so the branch key alone could not tell a clean-start retry's stale document
    /// from a task that never had one pending.
    /// </para>
    /// </summary>
    private const string StaleListOnlyDocument =
        "(" + StaleDocument
        + " or not jsonb_exists(d.data, 'queuePriorityMarked')"
        + " or not jsonb_exists(d.data, 'followUpBranch')"
        + " or not jsonb_exists(d.data, 'retryPending'))";

    /// <summary>
    /// Rebuilds every task stream still carrying an out-of-date document and returns the ids it
    /// actually rebuilt. Idempotent, and self-terminating for every <c>jsonb_exists</c> marker in
    /// <see cref="StaleStreamsAsync"/>: a rebuilt document has every key, so the next call finds
    /// nothing. <see cref="StaleFingerprintStreamsAsync"/> is looser — it can, rarely, re-select a
    /// document that a later cooperative grant has already correctly nulled back out, since it
    /// reasons from the stream's own <see cref="TaskAssigned"/> history rather than a key on the
    /// document — but a repeat rebuild there only redoes idempotent work, never leaves a genuinely
    /// stale document behind. Both task projections are single-stream on the task's own id, so one
    /// pass over the union of the stale sets repairs both.
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
        IReadOnlyList<Guid> fingerprints = await StaleFingerprintStreamsAsync(session, cancellationToken);

        return [.. rows.Concat(details).Concat(fingerprints).Distinct()];
    }

    /// <summary>
    /// Idea f72138e1's own repair path: a document whose stream's own latest <see cref="TaskAssigned"/>
    /// event carries a non-null <see cref="TaskAssigned.AssignedOwnerRootFingerprint"/>, but whose
    /// document still reads null for it — the exact shape the pre-fix
    /// <c>TaskListItemProjection.Apply(IEvent&lt;TaskAssigned&gt;)</c> /
    /// <c>TaskDetailsProjection.Apply(IEvent&lt;TaskAssigned&gt;)</c> left behind by discarding the
    /// event's own field. No <c>jsonb_exists</c> marker can find this: the key is present on these
    /// documents, as an explicit null, exactly as it is on a document that has never carried a
    /// fingerprint for a genuine reason (an event older than the field, or a task never reassigned
    /// since a fingerprint-clearing door last touched it) — the two are indistinguishable from the
    /// document alone. Reading the raw event is the only way to tell them apart, so this reasons
    /// from <see cref="TaskAssigned"/>'s own history per stream rather than from a document key:
    /// grouped by task id, the latest assignment by <see cref="TaskAssigned.AssignedAt"/> is the
    /// one whose fingerprint (or lack of one) the current document should carry, since a later
    /// assignment always supersedes an earlier one's fingerprint exactly as
    /// <see cref="TaskAggregate.Apply(Events.TaskAssigned)"/> does.
    /// <para>
    /// A forced takeover (<see cref="Events.TaskHolderTakenOver"/>, <see cref="TaskDecider.TakeOver"/>)
    /// is the one door that legitimately clears the fingerprint back to null <em>after</em> a
    /// fingerprinted assignment, without appending a fresh <see cref="TaskAssigned"/> of its own —
    /// the taker's own local reassignment, never a cross-node grant, so it carries no fingerprint to
    /// mirror. Left unaccounted for, a stream the assignment's own latest event still names as
    /// fingerprinted would be re-selected at every daemon start forever, even though the document's
    /// null is already the true current answer (independent pre-PR review, cycle 1, both lenses).
    /// A stream is excluded here whenever its latest takeover lands no earlier than its latest
    /// fingerprinted assignment.
    /// </para>
    /// <para>
    /// Restricted throughout to the candidate document ids that could possibly need this repair — a
    /// non-null <see cref="TaskListItem.AssignedOwnerId"/> beside a null
    /// <see cref="TaskListItem.AssignedOwnerFingerprint"/> — read first and used to bound every event
    /// query below, rather than materializing this store's entire <see cref="TaskAssigned"/> (and
    /// <see cref="Events.TaskHolderTakenOver"/>) history on every daemon start regardless of how many
    /// documents could possibly be stale (independent pre-PR review, cycle 1, adversarial lens).
    /// </para>
    /// </summary>
    private static async Task<Guid[]> StaleFingerprintStreamsAsync(
        IQuerySession session, CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> candidateRows = await session.Query<TaskListItem>()
            .Where(task => task.AssignedOwnerId != null && task.AssignedOwnerFingerprint == null)
            .Select(task => task.Id)
            .ToListAsync(cancellationToken);
        IReadOnlyList<Guid> candidateDetails = await session.Query<TaskDetails>()
            .Where(task => task.AssignedOwnerId != null && task.AssignedOwnerFingerprint == null)
            .Select(task => task.Id)
            .ToListAsync(cancellationToken);
        Guid[] candidates = [.. candidateRows.Concat(candidateDetails).Distinct()];
        if (candidates.Length == 0)
        {
            return [];
        }

        IReadOnlyList<TaskAssigned> assignments = await session.Events
            .QueryRawEventDataOnly<TaskAssigned>()
            .Where(assigned => candidates.Contains(assigned.Id))
            .ToListAsync(cancellationToken);
        if (assignments.Count == 0)
        {
            return [];
        }

        Dictionary<Guid, DateTimeOffset> latestFingerprintedAssignmentAt = assignments
            .GroupBy(assigned => assigned.Id)
            .Select(stream => stream.OrderByDescending(assigned => assigned.AssignedAt).First())
            .Where(latest => latest.AssignedOwnerRootFingerprint is not null)
            .ToDictionary(latest => latest.Id, latest => latest.AssignedAt);
        if (latestFingerprintedAssignmentAt.Count == 0)
        {
            return [];
        }

        Guid[] fingerprintedIds = [.. latestFingerprintedAssignmentAt.Keys];
        IReadOnlyList<TaskHolderTakenOver> takeovers = await session.Events
            .QueryRawEventDataOnly<TaskHolderTakenOver>()
            .Where(takenOver => fingerprintedIds.Contains(takenOver.Id))
            .ToListAsync(cancellationToken);
        Dictionary<Guid, DateTimeOffset> latestTakeoverAt = takeovers
            .GroupBy(takenOver => takenOver.Id)
            .ToDictionary(stream => stream.Key, stream => stream.Max(takenOver => takenOver.TakenAt));

        Guid[] currentlyFingerprinted = [.. latestFingerprintedAssignmentAt
            .Where(entry => !latestTakeoverAt.TryGetValue(entry.Key, out DateTimeOffset takenAt)
                || takenAt <= entry.Value)
            .Select(entry => entry.Key)];
        if (currentlyFingerprinted.Length == 0)
        {
            return [];
        }

        IReadOnlyList<Guid> staleRows = await session.Query<TaskListItem>()
            .Where(task => task.AssignedOwnerId != null && task.AssignedOwnerFingerprint == null)
            .Where(task => currentlyFingerprinted.Contains(task.Id))
            .Select(task => task.Id)
            .ToListAsync(cancellationToken);
        IReadOnlyList<Guid> staleDetails = await session.Query<TaskDetails>()
            .Where(task => task.AssignedOwnerId != null && task.AssignedOwnerFingerprint == null)
            .Where(task => currentlyFingerprinted.Contains(task.Id))
            .Select(task => task.Id)
            .ToListAsync(cancellationToken);

        return [.. staleRows.Concat(staleDetails).Distinct()];
    }
}
