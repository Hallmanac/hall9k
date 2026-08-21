using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Queries;
using Marten;

namespace Hall9k.Tests.Fakes;

/// <summary>
/// The events a task needs before any node may claim it (Decisions Log #34): drafted by Add,
/// through the readiness gate, then explicitly assigned. Seeds that used to start a stream
/// with TaskAdded alone go through here, so every integration test walks the same lifecycle
/// the CLI walks a human through rather than a shortcut the platform does not have.
/// </summary>
internal static class TaskSeed
{
    /// <summary>How many events <see cref="Dispatchable"/> writes — the expected-version arithmetic.</summary>
    public const int EventCount = 3;

    /// <summary>The events that leave a task Queued and assigned to <paramref name="ownerId"/>.</summary>
    public static object[] Dispatchable(TaskAdded added, Guid ownerId, DateTimeOffset at) =>
        Start(added, ownerId, at).Events;

    /// <summary>
    /// The same, for a task with declared dependencies. Publish is the cycle-detection gate,
    /// so it refuses a task whose blockers are absent from the graph it is handed — a seed
    /// with edges must load the real graph (<see cref="DependencyGraphAsync"/>) rather than
    /// pass the empty one.
    /// </summary>
    public static object[] Dispatchable(
        TaskAdded added, Guid ownerId, DateTimeOffset at, TaskDependencyGraph graph) =>
        Start(added, ownerId, at, graph).Events;

    /// <summary>The same events, plus the aggregate they leave behind for further Apply calls.</summary>
    public static (TaskAggregate Task, object[] Events) Start(TaskAdded added, Guid ownerId, DateTimeOffset at) =>
        Start(added, ownerId, at, TaskDependencyGraph.Empty);

    public static (TaskAggregate Task, object[] Events) Start(
        TaskAdded added, Guid ownerId, DateTimeOffset at, TaskDependencyGraph graph)
    {
        TaskAggregate task = new();
        task.Apply(added);

        TaskPublished published = TaskDecider.Publish(task, graph, at, ownerId);
        task.Apply(published);

        // Assign resolves the same graph Publish vetted: a blocker that has not closed out
        // leaves the task Blocked rather than Queued, which is the real behavior a seeded
        // dependent must inherit rather than shortcut.
        TaskAssigned assigned = TaskDecider.Assign(
            task, ownerId, graph.Resolve(task.BlockedBy), at, ownerId);
        task.Apply(assigned);

        return (task, [added, published, assigned]);
    }

    /// <summary>
    /// The dependency graph behind <paramref name="blockedBy"/>, loaded the way Publish loads
    /// it in the CLI. The blockers must already be committed — which is the real ordering too:
    /// a human declares an edge to a task that exists.
    /// </summary>
    public static Task<TaskDependencyGraph> DependencyGraphAsync(
        IQuerySession session, IReadOnlyList<Guid> blockedBy, CancellationToken cancellationToken) =>
        TaskDependencyQuery.LoadGraphAsync(session, blockedBy, cancellationToken);
}
