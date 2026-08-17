namespace Hall9k.Domain.Features.Tasks.Events;

public sealed record QuestionAsked(
    Guid Id,
    Guid QuestionId,
    Guid RunId,
    string Question,
    DateTimeOffset AskedAt);
