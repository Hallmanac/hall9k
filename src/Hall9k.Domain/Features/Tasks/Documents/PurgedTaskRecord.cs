namespace Hall9k.Domain.Features.Tasks.Documents;

/// <summary>
/// One task this node destroyed while purging a project (<c>ProjectPurgeEngine</c>), keyed by the
/// task's own id. It exists because nothing else tells a purged blocker from one this node has
/// simply not seen: a dependency edge is a bare id that records no project for its blocker, and a
/// purge deletes the blocker project's own record along with its tasks.
/// <see cref="Handlers.TaskDependencyResolver"/> therefore records a missing blocker as failed only
/// when this record names it, and otherwise leaves the dependent Blocked.
/// <para>
/// A plain document rather than an event, the same standing <see cref="Hall9k.Domain.Features.Run.PurgedSpendRecord"/>
/// has, and for a stronger reason: it states what this node did to its own database, so it must
/// never replicate. Another node that never purged the project has no business believing this
/// node's deletion. It is written in the same save that destroys the task, so a purge that rolls
/// back leaves no record and a purge that commits always leaves one.
/// </para>
/// </summary>
public sealed record PurgedTaskRecord(Guid Id, Guid ProjectId, DateTimeOffset PurgedAt);
