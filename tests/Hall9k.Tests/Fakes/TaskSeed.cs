using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;

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

    /// <summary>The same events, plus the aggregate they leave behind for further Apply calls.</summary>
    public static (TaskAggregate Task, object[] Events) Start(TaskAdded added, Guid ownerId, DateTimeOffset at)
    {
        TaskAggregate task = new();
        task.Apply(added);

        TaskPublished published = TaskDecider.Publish(task, TaskDependencyGraph.Empty, at, ownerId);
        task.Apply(published);

        TaskAssigned assigned = TaskDecider.Assign(task, ownerId, [], at, ownerId);
        task.Apply(assigned);

        return (task, [added, published, assigned]);
    }
}
