using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;

namespace Hall9k.Domain.Features.Replication;

/// <summary>
/// Which project coordinate a replicated event's own applied copy carries (idea 202383dc, M2a
/// follow-up, PLAN.md §16 PLACEHOLDER-c8dd149c): a project's own id is minted per install
/// (<c>ProjectAddCommand</c>), never a shared identity — the shared identity is the ledger
/// repository. <see cref="IsProjectAggregateStreamEvent"/> names every <see cref="ProjectAggregate"/>
/// event whose own stream IS that per-install id, so the receiving inbox rewrites the stream it
/// applies to (its own local Project stream) rather than the sender's; every other project-scoped
/// event (Task, Idea, Epic, Run) keeps its own stream id — those ARE shared across installs — and
/// instead has its own <c>ProjectId</c> field, when it carries one, rewritten to the receiver's own
/// value. <see cref="IsProjectIdentityEvent"/> names the one event that never travels either way:
/// the birth event a foreign id could only ever phantom-stream under.
/// </summary>
public static class ProjectStreamReplicationRules
{
    /// <summary>Never queued by the outbox and never applied by the inbox, in case an older or
    /// misbehaving sender still queues one: <see cref="ProjectRegistered"/> mints this install's own
    /// local project id (<c>ProjectAddCommand</c>) — applying a copy of it under a foreign id could
    /// only ever create a phantom stream, never the local Project stream it actually means.</summary>
    public static bool IsProjectIdentityEvent(Type eventType) => eventType == typeof(ProjectRegistered);

    /// <summary>
    /// True for every one of <see cref="ProjectAggregate"/>'s own project-scoped events
    /// besides the identity event above — its full <c>Apply</c> overload list, minus
    /// <see cref="ProjectRegistered"/> (identity, excluded) and <see cref="ProjectSettingsChanged"/>
    /// (node-scoped, never reaches this check). Each one is applied to the receiver's own Project
    /// stream id in place of the sender's: the aggregate's id IS the coordinate being rewritten, not
    /// a field carried inside it.
    /// </summary>
    public static bool IsProjectAggregateStreamEvent(Type eventType) =>
        eventType == typeof(ProjectTeamSettingsChanged)
        || eventType == typeof(ProjectArchived)
        || eventType == typeof(ProjectReactivated)
        || eventType == typeof(ProjectRenamed)
        || eventType == typeof(ProjectPurgeScheduled)
        || eventType == typeof(ProjectPurgeCancelled)
        || eventType == typeof(MemberVouched)
        || eventType == typeof(MemberRemoved);
}
