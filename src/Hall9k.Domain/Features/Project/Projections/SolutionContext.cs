using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Handlers;
using Marten;

namespace Hall9k.Domain.Features.Project.Projections;

/// <summary>What every solution command reads first: the registered projects, the solutions they carry, and each owner's claimed root.</summary>
public sealed record SolutionContext(
    IReadOnlyList<ProjectDetails> Projects,
    IReadOnlyList<SolutionView> Solutions,
    IReadOnlyDictionary<Guid, string?> RootFingerprints)
{
    public string? RootFingerprintOf(Guid ownerId) => RootFingerprints.GetValueOrDefault(ownerId);

    /// <summary>The solution with this key as this node reads it, or null when no project it lists carries it here.</summary>
    public SolutionView? ViewOf(string solutionKey) =>
        Solutions.FirstOrDefault(view => view.Membership.Key.Equals(solutionKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The member projects of <paramref name="view"/> this node holds: registered, joined, and not
    /// since moved to another solution. A member this node does not hold is not here, and so receives
    /// nothing a command on this node writes.
    /// </summary>
    public IReadOnlyList<ProjectDetails> HeldMembers(SolutionView view)
    {
        IReadOnlyList<ProjectDetails> candidates = [.. Projects.Where(view.NotInAnotherSolution)];
        return
        [
            .. view.Membership.Members
                .Select(member => SolutionDecider.HeldProject(member, candidates, RootFingerprintOf))
                .OfType<ProjectDetails>(),
        ];
    }

    /// <summary>Whether this node has joined <paramref name="project"/>'s ledger, so its streams are ones this node holds.</summary>
    public bool IsJoined(ProjectDetails project) => !ProjectJoinStatus.NotJoined(project, RootFingerprintOf(project.OwnerId));

    public static async Task<SolutionContext> LoadAsync(IQuerySession session, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProjectDetails> projects = await session.Query<ProjectDetails>().ToListAsync(cancellationToken);
        Dictionary<Guid, string?> roots = [];
        foreach (Guid ownerId in projects.Select(project => project.OwnerId).Distinct())
        {
            roots[ownerId] = await OwnerRootFingerprintResolver.ResolveAsync(session, ownerId, cancellationToken);
        }

        return new SolutionContext(projects, SolutionRegistry.Read(projects), roots);
    }
}
