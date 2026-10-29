using Hall9k.Domain.Features.Project.Projections;

namespace Hall9k.Domain.Features.Epic;

/// <summary>
/// The pure half of solution epics. A solution epic is one ordinary epic per member project, all
/// carrying one shared key, so creating one is minting that key and one <see cref="EpicAdded"/> per
/// project, and bringing a project into a solution is giving it the copies it missed. Nothing here
/// touches replication: each copy is a plain epic stream that replicates through its own project's
/// ledger exactly as an epic does today.
/// </summary>
public static class SolutionEpicDecider
{
    /// <summary>A new solution epic's key: a 26-character Crockford-base32 ULID, minted once and shared by every copy.</summary>
    public static string NewKey() => Ulid.NewUlid().ToString();

    /// <summary>
    /// One copy of a new solution epic for each of <paramref name="projectIds"/>, all sharing a fresh
    /// key and the title. Callers pass the member projects this node holds; a member it does not hold
    /// is not here and receives no copy from this node.
    /// </summary>
    public static IReadOnlyList<EpicAdded> Add(
        string solutionKey,
        string title,
        IReadOnlyList<Guid> projectIds,
        Func<Guid> newId,
        DateTimeOffset addedAt,
        Guid addedByOwnerId)
    {
        string epicKey = NewKey();
        return [.. projectIds.Select(projectId =>
            EpicDecider.Add(newId(), projectId, title, addedAt, addedByOwnerId, epicKey, solutionKey))];
    }

    /// <summary>
    /// The copies that bring <paramref name="newProjectIds"/> up to date with the open solution epics
    /// <paramref name="epics"/> holds for <paramref name="solutionKey"/>: one per open solution epic the
    /// project has no copy of, carrying that epic's key and title. A closed solution epic is not copied
    /// (a task can no longer join it), and a project that kept a copy from an earlier membership is not
    /// given a second one. <paramref name="projects"/> tells which copies sit in a project that is still a member,
    /// so a copy a project outside the solution holds cannot close the epic for the others.
    /// </summary>
    public static IReadOnlyList<EpicAdded> CopiesForNewMembers(
        IReadOnlyList<EpicDetails> epics,
        IReadOnlyCollection<ProjectDetails> projects,
        string solutionKey,
        IReadOnlyList<Guid> newProjectIds,
        Func<Guid> newId,
        DateTimeOffset addedAt,
        Guid addedByOwnerId)
    {
        IReadOnlyList<EpicGroup> open =
        [
            .. EpicGroup.Of(epics.Where(epic =>
                    epic.IsSolutionEpic && string.Equals(epic.SolutionKey, solutionKey, StringComparison.OrdinalIgnoreCase)),
                projects)
                .Where(group => group.State == EpicState.Open)
                .OrderBy(group => group.AddedAt),
        ];

        return
        [
            .. newProjectIds.SelectMany(projectId => open
                .Where(group => group.CopyIn(projectId) is null)
                .Select(group => EpicDecider.Add(
                    newId(), projectId, group.Title, addedAt, addedByOwnerId, group.SolutionEpicKey, solutionKey))),
        ];
    }
}
