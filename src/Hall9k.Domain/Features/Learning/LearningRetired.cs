namespace Hall9k.Domain.Features.Learning;

/// <summary>
/// A lesson stopped being worth carrying, and why (idea d805fd8b, piece 1; backlog 55).
/// Appended, never a deletion: the lesson and its provenance stay queryable, and only its
/// standing changes. The reason is required because retirement is the one cheap noise control
/// this design has, and a retirement with no stated reason teaches the next reader nothing about
/// whether the lesson was wrong, absorbed, or graduated into a harder rule.
/// </summary>
public sealed record LearningRetired(
    Guid Id,
    string Reason,
    Guid RetiredByOwnerId,
    DateTimeOffset RetiredAt);
