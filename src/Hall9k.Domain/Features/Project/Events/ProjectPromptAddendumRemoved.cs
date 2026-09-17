namespace Hall9k.Domain.Features.Project.Events;

/// <summary>
/// A project cleared its own addendum for one prompt builder (idea b9b09779, piece 6), mirroring
/// <see cref="ProjectPromptAddendumSet"/>. The daemon deletes the corresponding ledger file from
/// this — a genuine removal, never a tombstone — the same shape <c>MemberRemoved</c> already uses.
/// </summary>
public sealed record ProjectPromptAddendumRemoved(
    Guid ProjectId,
    string BuilderKey,
    DateTimeOffset RemovedAt,
    Guid RemovedByOwnerId);
