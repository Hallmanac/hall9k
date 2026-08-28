using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Marten;

namespace Hall9k.Domain.Features.Tasks.Queries;

/// <summary>
/// Reads BlockedBy edges off the task projection and decides, per dependency, whether it has
/// reached true closeout (Decisions Log #34). Completion is deliberately expensive to claim:
/// the task must be Done <em>and</em> its current run must have reached RunCompleted, which
/// only the closeout monitor's merge observation produces. Nothing weaker is accepted.
/// </summary>
public static class TaskDependencyQuery
{
    /// <summary>The declared dependencies of one task, in declared order.</summary>
    public static async Task<IReadOnlyList<TaskDependency>> LoadAsync(
        IQuerySession session, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        TaskDependencyGraph graph = new(await SnapshotAsync(session, ids, cancellationToken));
        return [.. ids.Select(graph.Node).OfType<TaskDependency>()];
    }

    /// <summary>
    /// Everything reachable from <paramref name="blockedBy"/> through BlockedBy edges — the
    /// closure publish needs to see a cycle anywhere in the chain, not just at the first hop.
    /// Breadth-first in batches, so a chain of depth n costs n queries rather than one per task.
    /// </summary>
    public static async Task<TaskDependencyGraph> LoadGraphAsync(
        IQuerySession session, IReadOnlyList<Guid> blockedBy, CancellationToken cancellationToken)
    {
        Dictionary<Guid, TaskDependency> known = [];
        List<Guid> frontier = [.. blockedBy.Distinct()];

        while (frontier.Count > 0)
        {
            IReadOnlyList<TaskDependency> batch = await SnapshotAsync(session, frontier, cancellationToken);
            foreach (TaskDependency dependency in batch)
            {
                known[dependency.Id] = dependency;
            }

            frontier =
            [
                .. batch.SelectMany(dependency => dependency.BlockedBy)
                    .Distinct()
                    .Where(id => !known.ContainsKey(id)),
            ];
        }

        return new TaskDependencyGraph(known.Values);
    }

    private static async Task<IReadOnlyList<TaskDependency>> SnapshotAsync(
        IQuerySession session, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        Guid[] wanted = [.. ids];
        IReadOnlyList<TaskListItem> tasks = await session.Query<TaskListItem>()
            .Where(task => task.Id.IsOneOf(wanted))
            .ToListAsync(cancellationToken);

        Guid[] runIds = [.. tasks.Select(task => task.CurrentRunId).OfType<Guid>()];
        Dictionary<Guid, RunState> runStates = runIds.Length == 0
            ? []
            : (await session.Query<RunListItem>()
                    .Where(run => run.Id.IsOneOf(runIds))
                    .ToListAsync(cancellationToken))
                .ToDictionary(run => run.Id, run => run.State);

        return [.. tasks.Select(task => Snapshot(task, CurrentRunStateOf(task, runStates)))];
    }

    private static TaskDependency Snapshot(TaskListItem task, RunState? currentRunState) => new(
        task.Id,
        task.Objective,
        task.State,
        IsClosedOut(task, currentRunState),
        currentRunState,
        task.PullRequestUrl,
        task.Type,
        task.BlockedBy);

    /// <summary>
    /// The state of the run the task currently hangs on, or null when it has none — a task
    /// that never claimed, one requeued back to nothing, or (defensively) one pointing at a
    /// run whose projection is missing. Null carries "no run to observe" rather than dressing
    /// the gap up as a run in some state.
    /// </summary>
    private static RunState? CurrentRunStateOf(
        TaskListItem task, IReadOnlyDictionary<Guid, RunState> runStates) =>
        task.CurrentRunId is { } runId && runStates.TryGetValue(runId, out RunState? state)
            ? state
            : null;

    /// <summary>
    /// True closeout: the task is Done and the run that carried it reached Completed, which
    /// the closeout monitor appends only once it observes the merge. A Done task whose pull
    /// request is still open, or whose run was superseded by a follow-up still in flight, has
    /// not closed out — so it still blocks.
    /// </summary>
    private static bool IsClosedOut(TaskListItem task, RunState? currentRunState) =>
        task.State == TaskState.Done && currentRunState == RunState.Completed;
}
