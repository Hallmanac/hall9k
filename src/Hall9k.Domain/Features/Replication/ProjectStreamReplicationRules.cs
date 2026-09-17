using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;

namespace Hall9k.Domain.Features.Replication;

/// <summary>
/// Which project coordinate a replicated event's own applied copy carries (idea 202383dc, M2a
/// follow-up, PLAN.md §16 #213): a project's own id is minted per install
/// (<c>ProjectAddCommand</c>), never a shared identity — the shared identity is the ledger
/// repository. <see cref="IsProjectAggregateStreamEvent"/> names the subset of
/// <see cref="ProjectAggregate"/>'s own events that are both safe and required to apply to the
/// receiver's own Project stream, so the receiving inbox rewrites the stream it applies to (its own
/// local Project stream) rather than the sender's for exactly those; every other project-scoped
/// event (Task, Idea, Epic, Run, and every OTHER Project-aggregate event —
/// <see cref="IsProjectLifecycleEvent"/>) keeps its own stream id — either because it IS shared
/// across installs, or because applying it to the receiver's own project would let another
/// install's local decision (archiving, renaming, or scheduling this install's own copy for hard
/// deletion) act on this one with no decider in the way — and instead has its own <c>ProjectId</c>
/// field, when it carries one, rewritten to the receiver's own value.
/// <see cref="IsProjectIdentityEvent"/> names the one event that never travels either way: the
/// birth event a foreign id could only ever phantom-stream under.
/// </summary>
public static class ProjectStreamReplicationRules
{
    /// <summary>Never queued by the outbox and never applied by the inbox, in case an older or
    /// misbehaving sender still queues one: <see cref="ProjectRegistered"/> mints this install's own
    /// local project id (<c>ProjectAddCommand</c>) — applying a copy of it under a foreign id could
    /// only ever create a phantom stream, never the local Project stream it actually means.</summary>
    public static bool IsProjectIdentityEvent(Type eventType) => eventType == typeof(ProjectRegistered);

    /// <summary>
    /// True for the Project aggregate's own events that are team-facing state, not a per-install
    /// lifecycle decision: <see cref="ProjectTeamSettingsChanged"/> (criterion 1 and the objective
    /// both require the team half of settings to travel and apply the same way everywhere),
    /// <see cref="MemberVouched"/>/<see cref="MemberRemoved"/> (this node's own audit trail of a
    /// ledger-file write — never consulted for an actual membership decision, so applying a
    /// teammate's copy locally is harmless bookkeeping, not a fenced action), and
    /// <see cref="ProjectPromptAddendumSet"/>/<see cref="ProjectPromptAddendumRemoved"/> (idea
    /// b9b09779, piece 6 — the same tier <see cref="Hall9k.Domain.Infrastructure.Persistence.EventScopeRegistry"/>
    /// classifies them at: team-visible guidance, not a per-install decision). Each one is applied to the receiver's own
    /// Project stream id in place of the sender's: the aggregate's id IS the coordinate being
    /// rewritten, not a field carried inside it. <see cref="IsProjectLifecycleEvent"/> covers
    /// everything else on this same stream that must NOT do that.
    /// </summary>
    public static bool IsProjectAggregateStreamEvent(Type eventType) =>
        eventType == typeof(ProjectTeamSettingsChanged)
        || eventType == typeof(MemberVouched)
        || eventType == typeof(MemberRemoved)
        || eventType == typeof(ProjectPromptAddendumSet)
        || eventType == typeof(ProjectPromptAddendumRemoved);

    /// <summary>
    /// True for the Project aggregate's own per-install lifecycle decisions (independent pre-PR
    /// review, cycle 3, conformance lens: applying these to the receiver's own project let a
    /// teammate's local <c>h9k project remove --purge</c>, rename, or reactivate act on this
    /// install's own copy of the project with no decider in the way — including scheduling this
    /// install's own project, tasks, runs, ideas, and epics for an unrecoverable hard delete).
    /// Archiving, reactivating, renaming, and scheduling or cancelling a purge are each this
    /// install's own decision about its own local copy (<see cref="ProjectArchived"/>'s own doc:
    /// "archived on this install alone"); a teammate's identical action on their own install is a
    /// fact worth keeping, so it still travels and is recorded — under the sender's own foreign
    /// stream id, the same phantom-stream coordinate <see cref="IsProjectIdentityEvent"/>'s own
    /// excluded event would have created, never rewritten onto the receiver's real Project stream.
    /// </summary>
    public static bool IsProjectLifecycleEvent(Type eventType) =>
        eventType == typeof(ProjectArchived)
        || eventType == typeof(ProjectReactivated)
        || eventType == typeof(ProjectRenamed)
        || eventType == typeof(ProjectPurgeScheduled)
        || eventType == typeof(ProjectPurgeCancelled);
}
