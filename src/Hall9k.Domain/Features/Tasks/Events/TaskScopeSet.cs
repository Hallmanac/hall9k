using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// Sets <see cref="TaskAggregate.Scope"/> (idea 8c5993c5) — the current home for every door onto a
/// task's own replication scope: <c>h9k task scope</c>, <c>h9k task share</c> (sugar for
/// <see cref="ReplicationScope.Team"/>), and <c>h9k task set-private</c> (the pre-8c5993c5 alias, now
/// sugar for <see cref="ReplicationScope.Private"/> or <see cref="ReplicationScope.Fleet"/>).
/// Supersedes <see cref="TaskPrivacySet"/> for every write going forward — that event stays only for
/// a stream that already carries one, replayed by <see cref="TaskAggregate.Apply(TaskPrivacySet)"/>
/// into the same field. Settable in any state, exactly as the two-valued flag it replaces was: a
/// draft is exactly the case this exists for. Publishing (<see cref="TaskPublished"/>) sets
/// <see cref="ReplicationScope.Team"/> on its own, unconditionally, and needs no event of this shape
/// to do it — <see cref="TaskAggregate.Apply(TaskPublished)"/> is the one place that rule lives.
/// </summary>
public sealed record TaskScopeSet(Guid Id, ReplicationScope Scope, DateTimeOffset SetAt, Guid SetByOwnerId);
