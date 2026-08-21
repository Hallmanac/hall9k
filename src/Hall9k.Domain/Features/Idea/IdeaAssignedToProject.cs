namespace Hall9k.Domain.Features.Idea;

/// <summary>
/// The project an idea turned out to belong to, set or changed after capture.
/// PreviousProjectId is what it was assigned to before — null when this is the first
/// assignment, which is the common case since capture rarely knows.
/// </summary>
public sealed record IdeaAssignedToProject(
    Guid Id,
    Guid ProjectId,
    Guid? PreviousProjectId,
    DateTimeOffset AssignedAt,
    Guid AssignedByOwnerId);
