using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The only name-keyed query in the platform (verified 2026-09-12: every task, run, idea, setting,
/// and home references a project's own id, never its name). <c>ProjectAddCommand</c> owned this
/// check alone before <c>h9k project rename</c> existed; factored out here so both refuse the
/// identical collision the identical way.
/// </summary>
internal static class ProjectNameUniqueness
{
    /// <summary>
    /// Refuses when another project already carries <paramref name="name"/> — "another" meaning
    /// any project other than <paramref name="excludingProjectId"/>, so a rename that keeps a
    /// project's own name case-different, or a fresh registration (excludingProjectId null, which
    /// matches nothing), both check against every other project's name exactly as they did before.
    /// </summary>
    public static async Task CheckAsync(
        IQuerySession session, string name, Guid? excludingProjectId, CancellationToken cancellationToken)
    {
        bool duplicate = await session.Query<ProjectDetails>()
            .Where(p => p.Name == name)
            .AnyAsync(cancellationToken);
        if (!duplicate)
        {
            return;
        }

        if (excludingProjectId is { } projectId)
        {
            bool onlySelf = !await session.Query<ProjectDetails>()
                .Where(p => p.Name == name && p.Id != projectId)
                .AnyAsync(cancellationToken);
            if (onlySelf)
            {
                return;
            }
        }

        throw new DomainConflictException($"A project named '{name}' already exists.");
    }
}
