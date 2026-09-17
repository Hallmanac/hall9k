namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// Flips <see cref="TaskAggregate.IsPrivate"/> (idea 202383dc, M2a: "drafts and ideas travel, each
/// with a private flag that stops travel until cleared"). Settable at any point in the task's
/// life — a draft is exactly the case this exists for, since a task can be private from the moment
/// it is captured, before it has anything else worth gating.
/// </summary>
public sealed record TaskPrivacySet(Guid Id, bool IsPrivate, DateTimeOffset SetAt, Guid SetByOwnerId);
