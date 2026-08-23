namespace Hall9k.Domain.Features.Project.Events;

/// <summary>
/// HomeDirectory is where the project's home was created on the registering node — the
/// directory holding the generated AGENTS.md, repo/, ideas/, tasks/ and skills/. Appended with
/// a default so streams written before homes existed replay unchanged; those projects read as
/// <see cref="ProjectHome.None"/> until <c>h9k project init</c> gives them one.
/// </summary>
public sealed record ProjectRegistered(
    Guid Id,
    Guid OwnerId,
    Guid ConnectionId,
    string Name,
    string RepositoryPath,
    Uri? RepositoryUrl,
    string BaseBranch,
    DateTimeOffset RegisteredAt,
    ProjectHome? HomeDirectory = null);
