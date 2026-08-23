using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Marten;

namespace Hall9k.Cli.ProjectHomes;

/// <summary>
/// One home belongs to one project. This is the check that keeps it that way, run before any
/// command creates a home or records where one is.
/// <para>
/// It exists because the default location is derived from the project name through a lossy slug:
/// <c>My App</c>, <c>my-app</c> and <c>my.app</c> all reduce to <c>my-app</c>, and the name
/// uniqueness check upstream compares names rather than the directories they resolve to. Two
/// projects landing in one home is not a cosmetic collision. They share a bare clone, so the
/// second project's tasks are dispatched into worktrees cut from the first project's code; they
/// share the generated <c>AGENTS.md</c>, so the second registration silently overwrites the
/// first's facts; and every step of the recipe reports success while doing it, because
/// "already existed, completed in place" is exactly what an idempotent re-run looks like.
/// </para>
/// <para>
/// Origin incident (2026-08-23): the pre-PR review of the project-home branch traced both
/// spellings through <c>h9k project add</c> and found nothing anywhere that recorded the two had
/// collided.
/// </para>
/// </summary>
public static class ProjectHomeClaims
{
    /// <summary>
    /// Refuses when another registered project already holds <paramref name="home"/> or
    /// <paramref name="repositoryPath"/>. <paramref name="projectId"/> is the project being
    /// registered or repaired, and is excluded: a project may always re-claim its own.
    /// </summary>
    public static async Task EnsureUnclaimedAsync(
        IDocumentSession session,
        Guid projectId,
        string home,
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ProjectDetails> registered =
            await session.Query<ProjectDetails>().ToListAsync(cancellationToken);

        foreach (ProjectDetails other in registered.Where(project => project.Id != projectId))
        {
            if (ProjectHomePaths.SameDirectory(other.HomeDirectory.Value, home))
            {
                throw new DomainConflictException(
                    $"'{other.Name}' already lives at {home}, and two projects cannot share one home: "
                    + "they would share one bare clone and one generated AGENTS.md, so whichever "
                    + "registered second would quietly overwrite the first. Project names are reduced "
                    + "to directory names (lowercase, non-alphanumerics collapsed to dashes), which is "
                    + "how two different names land on one path. Pass --home <path> to put this project "
                    + "somewhere of its own.");
            }

            if (ProjectHomePaths.SameDirectory(other.RepositoryPath, repositoryPath))
            {
                throw new DomainConflictException(
                    $"'{other.Name}' already cuts its worktrees from {repositoryPath}. Two projects "
                    + "sharing one repository path means each one's tasks are dispatched into the "
                    + "other's code. Pass --home <path> so this project gets its own clone, or --repo "
                    + "<path> to name a different repository.");
            }
        }
    }
}
