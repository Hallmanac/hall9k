namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// Draft -> Published: the readiness gate passed (Decisions Log #34). Publishing promises
/// two things about the state it produces — the task satisfies the readiness contract, and
/// a human may assign it at any moment — which is why validation and cycle detection live
/// here alone and revision stops here.
/// </summary>
public sealed record TaskPublished(
    Guid Id,
    DateTimeOffset PublishedAt,
    Guid PublishedByOwnerId);
