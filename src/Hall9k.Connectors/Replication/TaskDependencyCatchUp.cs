using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Extensions;
using Marten;

namespace Hall9k.Connectors.Replication;

/// <summary>
/// The dependency half of "a pull brings a task's whole story with it" (task 9eb5b245): a task that
/// lands here by replication — pulled explicitly, adopted from a ledger record, or arriving in an
/// ordinary flush — may name blocked-by or stacked-on ids whose own streams this node does not
/// hold, and nothing asked for those. <c>TaskDecider.Assign</c> then refuses the assignment outright
/// ("depends on N task(s) the platform does not know"), which is a refusal for a stream the platform
/// could have fetched itself, so this mints exactly that ask: one broadcast stream request per
/// missing dependency, recorded as a dependency ask (<see cref="EventCatchUpRequest.ForDependencyOfTaskId"/>)
/// so <c>h9k status</c> can say whose dependency it is fetching.
/// <para>
/// Recursive by arrival rather than by guessing: the walk descends only through dependencies this
/// node actually HOLDS, because a stream that is not here has no edges to read yet — each landing
/// runs this again, so a chain of blockers is asked for one hop per landing until the whole graph is
/// here. Bounded to the one project whose landing triggered it: the project of a stream this node
/// does not hold is unknowable, and a request has to name one, so the naming task's own project is
/// the only honest answer available.
/// </para>
/// </summary>
public static class TaskDependencyCatchUp
{
    /// <summary>
    /// How long a closed dependency ask holds off a fresh one. A human's own <c>h9k task pull</c>
    /// never cools down, but this caller runs on every landing of any task in the project, and a
    /// dependency no member of the project holds is declined immediately by every one of them — so
    /// without this, one permanently unholdable blocker would mint a signed commit and push per
    /// landing, forever, the identical unbounded-loop shape
    /// <c>EventCatchUpCoordinator.RequestGapFillAsync</c>'s own cooldown already exists to stop.
    /// </summary>
    public static readonly TimeSpan ReMintCooldown = TimeSpan.FromHours(6);

    /// <summary>One task's own dependency edges, as the pure walk below reads them — both kinds
    /// together, since a stacked-on parent is a dependency whose stream is needed exactly as much
    /// as a blocker's (Decisions Log #144).</summary>
    public sealed record DependencyEdges(
        Guid TaskId, Guid ProjectId, IReadOnlyList<Guid> BlockedBy, Guid? StackedOnTaskId)
    {
        public IEnumerable<Guid> Dependencies =>
            StackedOnTaskId is { } parent ? BlockedBy.Append(parent) : BlockedBy;
    }

    /// <summary>A dependency stream this node does not hold, and the task whose own edges named it.</summary>
    public sealed record MissingDependency(Guid StreamId, Guid NamedByTaskId);

    /// <summary>
    /// Every dependency stream reachable from <paramref name="landedTaskIds"/> that
    /// <paramref name="held"/> does not contain, in the order the walk first met each one and
    /// deduplicated to one entry per stream (a diamond in the graph is one missing stream, not two
    /// asks for it). <paramref name="landedTaskIds"/> may name streams that are not tasks at all —
    /// a read applies whatever a batch carried — and an id with no entry in
    /// <paramref name="held"/> contributes nothing rather than being reported as missing: it is the
    /// EDGES of held tasks that name a dependency, never a root.
    /// </summary>
    public static IReadOnlyList<MissingDependency> MissingDependencies(
        IEnumerable<Guid> landedTaskIds, Guid projectId, IReadOnlyDictionary<Guid, DependencyEdges> held)
    {
        List<MissingDependency> missing = [];
        HashSet<Guid> alreadyMissing = [];
        HashSet<Guid> walked = [];
        Queue<Guid> frontier = new(landedTaskIds);

        while (frontier.Count > 0)
        {
            Guid taskId = frontier.Dequeue();
            if (!walked.Add(taskId)
                || !held.TryGetValue(taskId, out DependencyEdges? edges)
                || edges.ProjectId != projectId)
            {
                continue;
            }

            foreach (Guid dependency in edges.Dependencies)
            {
                if (held.ContainsKey(dependency))
                {
                    frontier.Enqueue(dependency);
                }
                else if (alreadyMissing.Add(dependency))
                {
                    missing.Add(new MissingDependency(dependency, taskId));
                }
            }
        }

        return missing;
    }

    /// <summary>
    /// Queues one broadcast stream request per missing dependency reachable from
    /// <paramref name="landedTaskIds"/>, and returns the streams actually asked for — the ones a
    /// caller reports. Silent about a dependency whose ask is still outstanding or inside
    /// <paramref name="reMintCooldown"/> (<see cref="StreamRequestDecision"/>'s own rule decides
    /// which), and silent about a node with no owner root fingerprint, which has no identity to
    /// address an envelope from at all.
    /// </summary>
    public static async Task<IReadOnlyList<MissingDependency>> QueueMissingAsync(
        IDocumentSession session,
        Guid projectId,
        IReadOnlyCollection<Guid> landedTaskIds,
        Guid myNodeId,
        string? myOwnerFingerprint,
        DateTimeOffset now,
        TimeSpan? reMintCooldown,
        CancellationToken cancellationToken)
    {
        if (landedTaskIds.Count == 0 || myOwnerFingerprint.IsBlank())
        {
            return [];
        }

        IReadOnlyDictionary<Guid, DependencyEdges> held = await LoadHeldEdgesAsync(
            session, landedTaskIds, cancellationToken);
        IReadOnlyList<MissingDependency> missing = MissingDependencies(landedTaskIds, projectId, held);
        if (missing.Count == 0)
        {
            return [];
        }

        EventCatchUpCoordinator coordinator = new();
        List<MissingDependency> asked = [];
        foreach (MissingDependency dependency in missing)
        {
            // A dependency whose stream exists here but has no TaskListItem is a TAIL-only hold —
            // the post-switch-on half an ordinary flush shipped, with no TaskAdded on it — and an
            // answer's older events would land behind the newer ones already here, which
            // EventReplicationInbox refuses on arrival rather than replay a stream backwards
            // (EventStreamCatchUp.PartiallyHeldRefusal, the same shape h9k task pull refuses up
            // front). Asking for it could only ever earn that refusal, so this asks for a stream
            // that is genuinely absent and nothing else.
            if (await session.Events.FetchStreamStateAsync(dependency.StreamId, cancellationToken) is not null)
            {
                continue;
            }

            StreamRequestOutcome outcome = await coordinator.RequestStreamBroadcastAsync(
                session, projectId, dependency.StreamId, myNodeId, myOwnerFingerprint, now, cancellationToken,
                again: false, reMintCooldown, forDependencyOfTaskId: dependency.NamedByTaskId);
            if (outcome.Queues())
            {
                asked.Add(dependency);
            }
        }

        return asked;
    }

    /// <summary>
    /// The dependency closure of <paramref name="rootTaskIds"/>, as far as this node actually holds
    /// it: loaded by id in waves rather than by scanning every task on the node, since a landing
    /// read touches a handful of streams and a store holds thousands of tasks. A wave loads only
    /// ids no earlier wave already asked for, so a cycle in the graph (legal in Draft — Decisions
    /// Log #34) terminates rather than looping.
    /// </summary>
    private static async Task<IReadOnlyDictionary<Guid, DependencyEdges>> LoadHeldEdgesAsync(
        IQuerySession session, IEnumerable<Guid> rootTaskIds, CancellationToken cancellationToken)
    {
        Dictionary<Guid, DependencyEdges> held = [];
        HashSet<Guid> requested = [.. rootTaskIds];
        HashSet<Guid> wave = [.. requested];

        while (wave.Count > 0)
        {
            IReadOnlyList<TaskListItem> loaded = await session.LoadManyAsync<TaskListItem>(
                cancellationToken, [.. wave]);
            wave = [];
            foreach (TaskListItem task in loaded)
            {
                DependencyEdges edges = new(task.Id, task.ProjectId, task.BlockedBy, task.StackedOnTaskId);
                held[task.Id] = edges;
                foreach (Guid dependency in edges.Dependencies)
                {
                    if (requested.Add(dependency))
                    {
                        wave.Add(dependency);
                    }
                }
            }
        }

        return held;
    }
}
