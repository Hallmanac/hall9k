namespace Hall9k.Domain.Features.Idea;

/// <summary>
/// The note rewritten as discovery sharpens it. The whole text is carried rather than a diff,
/// and every revision stays on the stream: what the idea used to say is how the thinking is
/// read back later.
/// </summary>
public sealed record IdeaRevised(
    Guid Id,
    string Text,
    DateTimeOffset RevisedAt,
    Guid RevisedByOwnerId);
