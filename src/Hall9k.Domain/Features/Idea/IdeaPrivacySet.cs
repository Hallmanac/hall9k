namespace Hall9k.Domain.Features.Idea;

/// <summary>
/// Flips <see cref="IdeaAggregate.IsPrivate"/> (idea 202383dc, M2a: "drafts and ideas travel, each
/// with a private flag that stops travel until cleared") — the identical idiom
/// <see cref="Hall9k.Domain.Features.Tasks.Events.TaskPrivacySet"/> uses for a task, settable at
/// any point in the idea's life.
/// </summary>
public sealed record IdeaPrivacySet(Guid Id, bool IsPrivate, DateTimeOffset SetAt, Guid SetByOwnerId);
