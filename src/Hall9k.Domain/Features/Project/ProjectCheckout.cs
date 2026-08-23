using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// Where a session that needs to <em>read</em> a project's files runs.
/// <para>
/// A project's <c>RepositoryPath</c> is what dispatch cuts worktrees from, and once a project
/// owns a home that is the bare clone at <c>&lt;home&gt;/repo/&lt;name&gt;.git</c> — refs and
/// objects, and not one file of the project's code. Git commands are perfectly happy there;
/// a session told to read the repository's skills is not. <c>repo/dev</c> is the checkout the
/// home keeps on the primary branch for exactly this, so it is what a reading session is given.
/// </para>
/// <para>
/// Origin incident (2026-08-23): the pre-PR review of the project-home branch found
/// <c>h9k task push-to-jira</c> spawning its card-authoring session with the bare clone as its
/// working directory, where the prompt's "read the card rules from the repository you are in"
/// could not be followed and nothing said so.
/// </para>
/// </summary>
public static class ProjectCheckout
{
    /// <summary>
    /// The directory to run a reading session in: the home's <c>repo/dev</c> worktree when there
    /// is one, and otherwise the recorded repository path, which is an ordinary clone for every
    /// project registered before homes existed. The answer is not promised to exist — whether it
    /// does is the caller's guard, because what to say about a missing one is the caller's to say.
    /// </summary>
    public static string ForReading(ProjectDetails project)
    {
        if (!project.HomeDirectory.HasValue)
        {
            return project.RepositoryPath;
        }

        string dev = ProjectHomePaths.DevWorktree(project.HomeDirectory.Value);
        return Directory.Exists(dev) ? dev : project.RepositoryPath;
    }

    /// <summary>
    /// Whether <paramref name="checkout"/> is the home's own <c>repo/dev</c>, which is the only
    /// reading checkout the platform may move. It made that one and keeps it on the primary
    /// branch for its own use, so bringing it up to date before a session reads it is housekeeping.
    /// The fallback answer from <see cref="ForReading"/> is somebody's ordinary clone — a project
    /// registered before homes existed points at one — and fast-forwarding that would move a
    /// person's working directory under them for a reason they never asked for.
    /// </summary>
    public static bool IsHomeDevWorktree(ProjectDetails project, string checkout) =>
        project.HomeDirectory.HasValue
        && ProjectHomePaths.SameDirectory(ProjectHomePaths.DevWorktree(project.HomeDirectory.Value), checkout);

    /// <summary>
    /// Whether a directory is a bare clone rather than a working tree. Read off the layout git
    /// itself makes — no <c>.git</c> entry, but <c>objects/</c> and <c>refs/</c> at the top — so
    /// a caller that needs files can refuse before spawning something into a directory that has
    /// none, rather than after the session reports it found nothing.
    /// </summary>
    public static bool IsBare(string directory) =>
        !Directory.Exists(Path.Combine(directory, ".git"))
        && !File.Exists(Path.Combine(directory, ".git"))
        && Directory.Exists(Path.Combine(directory, "objects"))
        && Directory.Exists(Path.Combine(directory, "refs"));
}
