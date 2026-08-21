namespace Hall9k.Domain.Features.Idea;

/// <summary>
/// Discovery gave the idea intent, and an idea with intent is a task (Decisions Log #35).
/// Provenance runs both ways: this names the draft the idea became, and that draft's
/// TaskAdded names this idea as its source. Objective is the seed the draft was created with,
/// recorded here because it is what promotion decided — taken mechanically from the idea's
/// first sentence, or typed by the human as --objective, never inferred.
/// </summary>
public sealed record IdeaPromoted(
    Guid Id,
    Guid TaskId,
    Guid ProjectId,
    string Objective,
    DateTimeOffset PromotedAt,
    Guid PromotedByOwnerId);
