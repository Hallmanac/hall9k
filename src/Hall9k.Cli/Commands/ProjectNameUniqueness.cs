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
    /// Case-insensitive (<see cref="StringComparison.OrdinalIgnoreCase"/>), matching the way
    /// <c>ProjectResolver</c> and <c>ProjectDecider.Rename</c> already treat a project's name — a
    /// database-equality comparison here would let a rename or a fresh registration land a name
    /// that only case-differs from an existing project, which <c>ProjectResolver</c> would then
    /// resolve as an ambiguous exact match for both.
    /// </summary>
    public static async Task CheckAsync(
        IQuerySession session, string name, Guid? excludingProjectId, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProjectDetails> projects = await session.Query<ProjectDetails>().ToListAsync(cancellationToken);
        bool duplicate = projects.Any(p =>
            p.Id != excludingProjectId && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
        {
            throw new DomainConflictException($"A project named '{name}' already exists.");
        }
    }
}
