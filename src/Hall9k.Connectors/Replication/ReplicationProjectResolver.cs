using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;

namespace Hall9k.Connectors.Replication;

/// <summary>One project-scoped event's own project (for the outbound flush), the current
/// replication scope of the Task or Idea stream it lives on (directly, or by way of the Run that
/// stream belongs to — idea 8c5993c5), and whether the stream IS the Project aggregate's own stream
/// rather than one that merely belongs to it. The Project and Epic streams themselves have no scope
/// of their own to narrow, so they always resolve <see cref="ReplicationScope.Team"/>.</summary>
/// <param name="TaskId">
/// The task this stream belongs to — itself for a Task stream, the owning task for a Run stream,
/// and null for every other family, which belongs to no single task. Replication has no use for
/// it; the orchestrator feed (idea 89471598, piece 2) groups on it, and resolving it here rather
/// than in a second resolver is what keeps one answer to "which stream is this".
/// </param>
public sealed record ReplicationOwnership(
    Guid? ProjectId,
    ReplicationScope Scope,
    bool IsProjectStreamItself = false,
    Guid? TaskId = null)
{
    /// <summary>The pre-8c5993c5 two-valued read, kept for callers that only ever asked "private or not".</summary>
    public bool IsPrivate => Scope == ReplicationScope.Private;
}

/// <summary>
/// Resolves which project owns a raw event's own stream (idea 202383dc, M2a's outbound flush needs
/// this to scope a node-wide event log down to one project's own outbox) and whether the task or
/// idea that stream belongs to is currently private. Every <c>ProjectScoped</c>
/// <c>EventScopeRegistry</c> family is covered: the Project stream itself (settings, membership),
/// Task, Idea, Epic, and Run (by way of its own task).
/// </summary>
public sealed class ReplicationProjectResolver
{
    public Task<ReplicationOwnership> ResolveAsync(IQuerySession session, IEvent candidate, CancellationToken cancellationToken) =>
        ResolveAsync(session, candidate.StreamId, cancellationToken);

    /// <summary>
    /// The same answer for a stream named directly, for a caller that already has the id and no
    /// <see cref="IEvent"/> to hand (the orchestrator feed reads through its own candidate
    /// record). The stream is all the overload above ever looked at.
    /// </summary>
    public async Task<ReplicationOwnership> ResolveAsync(IQuerySession session, Guid streamId, CancellationToken cancellationToken)
    {
        if (await session.LoadAsync<ProjectDetails>(streamId, cancellationToken) is { } project)
        {
            // The Project aggregate's own id, unlike a Task/Idea/Epic id, is never shared across
            // installs — every node mints its own via DomainId.New() the moment it registers the
            // same real-world project (ProjectAddCommand). Flagged here, rather than resolved
            // silently like every other family below, so the outbox can refuse only its one
            // identity event (ProjectRegistered — a fact appended under the SENDER's own project id
            // could only phantom-stream under a foreign id) while every other project-scoped event
            // on this same stream still travels normally; the inbox rewrites its own stream id to
            // the RECEIVER's own Project stream on apply (ProjectStreamReplicationRules), never the
            // sender's (independent pre-PR review, cycle 1, adversarial lens: the earlier build here
            // wrongly excluded the whole stream, ProjectTeamSettingsChanged included).
            return new ReplicationOwnership(project.Id, ReplicationScope.Team, IsProjectStreamItself: true);
        }

        if (await session.LoadAsync<TaskDetails>(streamId, cancellationToken) is { } task)
        {
            return new ReplicationOwnership(task.ProjectId, task.Scope, TaskId: task.Id);
        }

        if (await session.LoadAsync<IdeaDetails>(streamId, cancellationToken) is { } idea)
        {
            return new ReplicationOwnership(idea.ProjectId, idea.Scope);
        }

        if (await session.LoadAsync<EpicDetails>(streamId, cancellationToken) is { } epic)
        {
            return new ReplicationOwnership(epic.ProjectId, ReplicationScope.Team);
        }

        if (await session.LoadAsync<RunDetails>(streamId, cancellationToken) is { } run)
        {
            TaskDetails? owningTask = await session.LoadAsync<TaskDetails>(run.TaskId, cancellationToken);
            return new ReplicationOwnership(
                owningTask?.ProjectId, owningTask?.Scope ?? ReplicationScope.Team, TaskId: run.TaskId);
        }

        return new ReplicationOwnership(null, ReplicationScope.Team);
    }
}
