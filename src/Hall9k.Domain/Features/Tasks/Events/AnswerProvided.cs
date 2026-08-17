namespace Hall9k.Domain.Features.Tasks.Events;

public sealed record AnswerProvided(
    Guid Id,
    Guid QuestionId,
    string Answer,
    DateTimeOffset AnsweredAt,
    Guid AnsweredByOwnerId);
