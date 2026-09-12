namespace Hall9k.Domain.Features.Project.Events;

/// <summary>
/// Changes a project's name and nothing else — NAME IS NOT AN IDENTIFIER (verified 2026-09-12):
/// every task, run, idea, setting, and home directory references the project's own <see cref="Id"/>,
/// never its name, and the recorded home path (<see cref="Projections.ProjectDetails.HomeDirectory"/>)
/// is never re-derived from it, so the directory on disk keeps its old folder name after this event
/// lands. The only name-keyed lookup in the platform is the duplicate-name check
/// <c>h9k project add</c> already ran before this event existed; a name this event frees is accepted
/// by that same check the moment this lands. There is no rename of any kind before this event — it
/// exists so an archived project's name can be freed for a new registration without losing the
/// archive's own history (<see cref="ProjectArchived"/>, <see cref="ProjectReactivated"/>).
/// </summary>
public sealed record ProjectRenamed(Guid Id, string PreviousName, string NewName, DateTimeOffset RenamedAt, Guid RenamedByOwnerId);
