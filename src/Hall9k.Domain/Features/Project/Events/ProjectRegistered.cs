namespace Hall9k.Domain.Features.Project.Events;

/// <summary>
/// HomeDirectory is where the project's home was created on the registering node — the
/// directory holding the generated AGENTS.md, repo/, ideas/, tasks/ and skills/. Appended with
/// a default so streams written before homes existed replay unchanged; those projects read as
/// <see cref="ProjectHome.None"/> until <c>h9k project init</c> gives them one.
/// <para>
/// SkipPermissions is whether this project's dispatched agents launch with
/// <c>--dangerously-skip-permissions</c> from the moment it is registered (Windows field report,
/// 2026-08-31: a newly registered project started with permission prompts live, and a headless
/// run cannot answer one). <c>h9k project add</c> always passes <c>true</c> explicitly; the
/// default here stays <c>false</c>, the same direction <see cref="HomeDirectory"/> takes and for
/// the same reason — a default of <c>true</c> would make every stream written before this field
/// existed deserialize and replay as <c>true</c>, silently flipping every project that already
/// exists, which is the exact outcome this field's own default is chosen to avoid.
/// <c>h9k project set --skip-permissions</c> still overrides whatever this event recorded.
/// </para>
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
    ProjectHome? HomeDirectory = null,
    bool SkipPermissions = false);
