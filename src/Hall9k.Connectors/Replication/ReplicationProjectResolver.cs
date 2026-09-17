using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using JasperFx.Events;
using Marten;

namespace Hall9k.Connectors.Replication;

/// <summary>One project-scoped event's own project (for the outbound flush) and, when it lives on
/// a Task or Idea stream (directly, or by way of the Run that stream belongs to), whether that
/// owning task or idea is currently private (idea 202383dc, M2a).</summary>
public sealed record ReplicationOwnership(Guid? ProjectId, bool IsPrivate);

/// <summary>
/// Resolves which project owns a raw event's own stream (idea 202383dc, M2a's outbound flush needs
/// this to scope a node-wide event log down to one project's own outbox) and whether the task or
/// idea that stream belongs to is currently private. Every <c>ProjectScoped</c>
/// <c>EventScopeRegistry</c> family is covered: the Project stream itself (settings, membership),
/// Task, Idea, Epic, and Run (by way of its own task).
/// </summary>
public sealed class ReplicationProjectResolver
{
    public async Task<ReplicationOwnership> ResolveAsync(IQuerySession session, IEvent candidate, CancellationToken cancellationToken)
    {
        Guid streamId = candidate.StreamId;

        if (await session.LoadAsync<ProjectDetails>(streamId, cancellationToken) is { } project)
        {
            return new ReplicationOwnership(project.Id, IsPrivate: false);
        }

        if (await session.LoadAsync<TaskDetails>(streamId, cancellationToken) is { } task)
        {
            return new ReplicationOwnership(task.ProjectId, task.IsPrivate);
        }

        if (await session.LoadAsync<IdeaDetails>(streamId, cancellationToken) is { } idea)
        {
            return new ReplicationOwnership(idea.ProjectId, idea.IsPrivate);
        }

        if (await session.LoadAsync<EpicDetails>(streamId, cancellationToken) is { } epic)
        {
            return new ReplicationOwnership(epic.ProjectId, IsPrivate: false);
        }

        if (await session.LoadAsync<RunDetails>(streamId, cancellationToken) is { } run)
        {
            TaskDetails? owningTask = await session.LoadAsync<TaskDetails>(run.TaskId, cancellationToken);
            return new ReplicationOwnership(owningTask?.ProjectId, owningTask?.IsPrivate ?? false);
        }

        return new ReplicationOwnership(null, IsPrivate: false);
    }
}
