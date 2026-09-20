using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Idea;

/// <summary>
/// Sets <see cref="IdeaAggregate.Scope"/> (idea 8c5993c5) — the current home for every door onto an
/// idea's own replication scope: <c>h9k idea scope</c>, <c>h9k idea share</c> (sugar for
/// <see cref="ReplicationScope.Team"/>), and <c>h9k idea set-private</c> (the pre-8c5993c5 alias,
/// now sugar for <see cref="ReplicationScope.Private"/> or <see cref="ReplicationScope.Fleet"/>).
/// Supersedes <see cref="IdeaPrivacySet"/> for every write going forward — that event stays only for
/// a stream that already carries one, replayed by <see cref="IdeaAggregate.Apply(IdeaPrivacySet)"/>
/// into the same field. Settable in any state, exactly as the two-valued flag it replaces was: a
/// draft is exactly the case this exists for.
/// </summary>
public sealed record IdeaScopeSet(Guid Id, ReplicationScope Scope, DateTimeOffset SetAt, Guid SetByOwnerId);
