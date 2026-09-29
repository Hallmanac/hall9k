using Hall9k.Domain.Features.Decision;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Features.PrReviewPreflight;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;

namespace Hall9k.Connectors.Replication;

/// <summary>Which of <see cref="ReplicationProjectResolver"/>'s own document families actually
/// resolved a stream (<see cref="ReplicationOwnership.Family"/>) — <see cref="Unknown"/> only for a
/// stream no family resolved at all (brand new here). Used by
/// <c>Hall9k.Connectors.Replication.EventReplicationInbox</c>'s own stream-ownership guard to catch
/// an event from one family forged onto another family's already-existing, same-project stream
/// (independent pre-PR review, cycle 4, adversarial lens, medium: a <c>TaskAbandoned</c> aimed at an
/// existing idea stream materialises a phantom <c>TaskDetails</c> row there, and every later
/// legitimate event on that idea is refused for good the moment
/// <see cref="ReplicationProjectResolver"/> resolves the phantom instead) — the identical shape the
/// guard's own lifecycle-vs-Project-stream check already closes for the Project family alone.</summary>
public enum ReplicationStreamFamily
{
    Unknown,
    Project,
    Task,
    Idea,
    Epic,
    Run,
    Decision,
    Learning,
    PrReviewPreflight,
}

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
/// <param name="Family">
/// Which family actually resolved this stream, <see cref="ReplicationStreamFamily.Unknown"/> when
/// none did. Replication's stream-ownership guard is the one caller that needs it; every other
/// caller here predates it and keeps reading <see cref="ProjectId"/>/<see cref="TaskId"/> alone.
/// </param>
public sealed record ReplicationOwnership(
    Guid? ProjectId,
    ReplicationScope Scope,
    bool IsProjectStreamItself = false,
    Guid? TaskId = null,
    ReplicationStreamFamily Family = ReplicationStreamFamily.Unknown)
{
    /// <summary>The pre-8c5993c5 two-valued read, kept for callers that only ever asked "private or not".</summary>
    public bool IsPrivate => Scope == ReplicationScope.Private;
}

/// <summary>
/// Resolves which project owns a raw event's own stream (idea 202383dc, M2a's outbound flush needs
/// this to scope a node-wide event log down to one project's own outbox) and whether the task or
/// idea that stream belongs to is currently private. Every <c>ProjectScoped</c>
/// <c>EventScopeRegistry</c> family is covered: the Project stream itself (settings, membership),
/// Task, Idea, Epic, Run (by way of its own task), and Decision and Learning (by their own scope
/// coordinate — idea d805fd8b, piece 1).
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
            return new ReplicationOwnership(
                project.Id, ReplicationScope.Team, IsProjectStreamItself: true, Family: ReplicationStreamFamily.Project);
        }

        if (await session.LoadAsync<TaskDetails>(streamId, cancellationToken) is { } task)
        {
            return new ReplicationOwnership(task.ProjectId, task.Scope, TaskId: task.Id, Family: ReplicationStreamFamily.Task);
        }

        if (await session.LoadAsync<IdeaDetails>(streamId, cancellationToken) is { } idea)
        {
            return new ReplicationOwnership(idea.ProjectId, idea.Scope, Family: ReplicationStreamFamily.Idea);
        }

        if (await session.LoadAsync<EpicDetails>(streamId, cancellationToken) is { } epic)
        {
            return new ReplicationOwnership(epic.ProjectId, ReplicationScope.Team, Family: ReplicationStreamFamily.Epic);
        }

        if (await session.LoadAsync<RunDetails>(streamId, cancellationToken) is { } run)
        {
            TaskDetails? owningTask = await session.LoadAsync<TaskDetails>(run.TaskId, cancellationToken);
            return new ReplicationOwnership(
                owningTask?.ProjectId, owningTask?.Scope ?? ReplicationScope.Team, TaskId: run.TaskId,
                Family: ReplicationStreamFamily.Run);
        }

        // The pull-request review pre-flight's own stream (idea 6be68ee2, finding 1, phase one) is
        // keyed by the pre-flight's own id, never the task's, the identical "resolve by the owning
        // task" shape Run's own branch just above uses — without this branch these events resolved
        // to no project at all and never reached an outbox, whatever EventScopeRegistry's own
        // ProjectScoped classification said (independent pre-PR review, cycle 1, both lenses).
        if (await session.LoadAsync<PrReviewPreflightDetails>(streamId, cancellationToken) is { } preflight)
        {
            TaskDetails? owningTask = await session.LoadAsync<TaskDetails>(preflight.TaskId, cancellationToken);
            return new ReplicationOwnership(
                owningTask?.ProjectId, owningTask?.Scope ?? ReplicationScope.Team, TaskId: preflight.TaskId,
                Family: ReplicationStreamFamily.PrReviewPreflight);
        }

        // Idea d805fd8b, piece 1. The scope coordinate IS the project for a project-scoped
        // decision or lesson, and there is no project at all for an owner-scoped one: a null
        // ProjectId never equals the project an outbox is flushing for, so an owner-scoped record
        // stays on the node that recorded it without needing an exclusion of its own. Neither
        // stream carries a replication scope to narrow further — a decision is either the
        // project's or the owner's, and there is no private tier over it the way a task or an
        // idea has one.
        //
        // Last, behind Run deliberately (independent pre-PR review, cycle 1, conformance lens):
        // Run is the highest-volume replicated family by a wide margin, and every one of its
        // events pays for whatever misses ahead of it on each outbox flush and catch-up pass. Two
        // lookups that always miss for a run cost more in aggregate than one extra miss costs the
        // two families that record a handful of rows a week.
        if (await session.LoadAsync<DecisionDetails>(streamId, cancellationToken) is { } decision)
        {
            return new ReplicationOwnership(
                decision.Scope == KnowledgeScope.Project ? decision.ScopeId : null, ReplicationScope.Team,
                Family: ReplicationStreamFamily.Decision);
        }

        if (await session.LoadAsync<LearningDetails>(streamId, cancellationToken) is { } learning)
        {
            return new ReplicationOwnership(
                learning.Scope == KnowledgeScope.Project ? learning.ScopeId : null, ReplicationScope.Team,
                Family: ReplicationStreamFamily.Learning);
        }

        return new ReplicationOwnership(null, ReplicationScope.Team);
    }
}
