using Hall9k.Domain.Features.Project.Projections;

namespace Hall9k.Cli.Commands;

/// <summary>What <c>h9k solution join</c> does for one bundle member, decided from the projects this node has.</summary>
internal enum SolutionJoinAction
{
    /// <summary>No local project is the member: register it from the bundle's name and URL, then join it.</summary>
    Register,

    /// <summary>A local project is the member and has not joined its ledger: join it with the member's secret.</summary>
    Join,

    /// <summary>A local project is the member and has joined: leave it alone and do not spend the secret.</summary>
    AlreadyJoined,

    /// <summary>No local project is the member, but another one already has its name, so registering it would collide.</summary>
    NameCollision,
}

/// <summary>One member's action, with the local project it concerns (null for <see cref="SolutionJoinAction.Register"/>).</summary>
internal sealed record SolutionJoinStep(SolutionInviteMember Entry, SolutionJoinAction Action, ProjectDetails? Local);

/// <summary>The pure half of <c>h9k solution join</c>: the bundle and this node's projects in, one action per member out.</summary>
internal static class SolutionJoinPlanner
{
    public static IReadOnlyList<SolutionJoinStep> Plan(
        SolutionInviteBundle bundle, IReadOnlyList<ProjectDetails> projects, Func<Guid, string?> rootFingerprintOf) =>
    [
        .. bundle.Members.Select(entry => PlanMember(entry, projects, rootFingerprintOf)),
    ];

    private static SolutionJoinStep PlanMember(
        SolutionInviteMember entry, IReadOnlyList<ProjectDetails> projects, Func<Guid, string?> rootFingerprintOf)
    {
        // An archived project is not a project this node still works on; it still holds its name, which a registration would collide with.
        IReadOnlyList<ProjectDetails> live = [.. projects.Where(project => !project.IsArchived)];

        // By project key first, which names the project on every node, then by repository URL, the only thing an unjoined project has.
        ProjectDetails? local =
            live.FirstOrDefault(project => project.ProjectKey is { } key
                && key.Equals(entry.Member.ProjectKey, StringComparison.OrdinalIgnoreCase))
            ?? live.FirstOrDefault(project => project.RepositoryUrl is { } url
                && RepositoryIdentity(url) == RepositoryIdentity(entry.Member.RepositoryUrl));

        if (local is not null)
        {
            return ProjectJoinStatus.NotJoined(local, rootFingerprintOf(local.OwnerId))
                ? new SolutionJoinStep(entry, SolutionJoinAction.Join, local)
                : new SolutionJoinStep(entry, SolutionJoinAction.AlreadyJoined, local);
        }

        ProjectDetails? collision = projects.FirstOrDefault(
            project => project.Name.Equals(entry.Member.Name, StringComparison.OrdinalIgnoreCase));
        return collision is null
            ? new SolutionJoinStep(entry, SolutionJoinAction.Register, null)
            : new SolutionJoinStep(entry, SolutionJoinAction.NameCollision, collision);
    }

    /// <summary>
    /// What names a repository regardless of how it is reached: host, path, case, a trailing slash or
    /// <c>.git</c>, and not the scheme or the user (an ssh and an https URL for one repository are one).
    /// </summary>
    internal static string RepositoryIdentity(Uri repositoryUrl)
    {
        string path = repositoryUrl.AbsolutePath.Trim('/');
        path = path.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;
        return $"{repositoryUrl.Host}/{path}".ToLowerInvariant();
    }
}
