using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;

namespace Hall9k.Domain.Features.Epic;

/// <summary>
/// One epic as a person sees it. A project epic is a group of one; a solution epic is the group of
/// every copy this node holds, which all carry one <see cref="EpicDetails.SolutionEpicKey"/> and one
/// <see cref="EpicDetails.SolutionKey"/>. Showing and listing read these groups, so a solution epic is
/// one row however many member projects hold a copy of it. Every copy stays in the group when a project
/// leaves its solution, so its tasks stay on the solution epic; but only the copies of projects that are
/// still members <see cref="Current"/> speak for the epic (its title, its state, what a close or a Jira
/// link reaches), so a project the solution has let go of no longer decides anything about it.
/// </summary>
public sealed record EpicGroup(IReadOnlyList<EpicDetails> Copies, IReadOnlyList<EpicDetails>? MemberCopies = null)
{
    /// <summary>
    /// The copies that speak for the epic: those in a project that is still a member of the copy's solution
    /// when the group was read with the projects (<see cref="Of(IEnumerable{EpicDetails}, IReadOnlyCollection{ProjectDetails}?)"/>),
    /// else, and when no copy is in a member project, every copy.
    /// </summary>
    public IReadOnlyList<EpicDetails> Current => MemberCopies is { Count: > 0 } members ? members : Copies;

    private bool HasMemberCopy => MemberCopies is { Count: > 0 };

    /// <summary>The group's earliest current copy, which names the group: the copies share a title, and this one is the stable pick.</summary>
    public EpicDetails First => Current.OrderBy(copy => copy.AddedAt).ThenBy(copy => copy.Id).First();

    public bool IsSolutionEpic => First.IsSolutionEpic;

    /// <summary>The key every copy shares; null for a project epic.</summary>
    public string? SolutionEpicKey => First.SolutionEpicKey;

    /// <summary>The solution the copies were made for; null for a project epic.</summary>
    public string? SolutionKey => First.SolutionKey;

    public string Title => First.Title;

    public DateTimeOffset AddedAt => First.AddedAt;

    /// <summary>
    /// Closed as soon as any current copy this node holds is closed. Closing a solution epic is one human act
    /// that closes every copy the node holds, so a closed copy beside an open one is that act still
    /// replicating to the copies another node holds, and the epic is already closed.
    /// </summary>
    public EpicState State => Current.Any(copy => copy.State == EpicState.Closed) ? EpicState.Closed : First.State;

    /// <summary>The id a person types for this epic: the copy's id for a project epic, the shared key for a solution epic.</summary>
    public string Reference => SolutionEpicKey ?? First.Id.ToString();

    public EpicDetails? CopyIn(Guid projectId) => Copies.FirstOrDefault(copy => copy.ProjectId == projectId);

    /// <summary>
    /// Whether <paramref name="project"/> still carries the solution <paramref name="copy"/> was made for and its
    /// membership still lists it. A project taken out of its solution fails this, and so does a project the
    /// solution never named: a copy an event wrote into such a project is not the solution's own.
    /// </summary>
    public static bool IsMemberCopy(EpicDetails copy, ProjectDetails? project) =>
        project?.Solution is { } membership
        && copy.SolutionKey is { } solutionKey
        && membership.Key.Equals(solutionKey, StringComparison.OrdinalIgnoreCase)
        && SolutionRegistry.Lists(membership, project);

    /// <summary>
    /// Groups epics into what a person sees: each project epic alone, each solution epic's copies together.
    /// A solution epic's key comes straight from a replicated event, so a copy whose keys are not the ULIDs
    /// the platform mints is left out, and copies are grouped by the epic key and the solution together, so a
    /// copy naming some other solution cannot rename or close the real epic. With <paramref name="projects"/>,
    /// a group also knows which of its copies sit in a project that is still a member, and when several groups
    /// claim one epic key those with such a copy are kept over those with none.
    /// </summary>
    public static IReadOnlyList<EpicGroup> Of(IEnumerable<EpicDetails> epics, IReadOnlyCollection<ProjectDetails>? projects = null)
    {
        Dictionary<Guid, ProjectDetails> byId = projects is null ? [] : projects.ToDictionary(project => project.Id);

        IReadOnlyList<EpicGroup> groups =
        [
            .. epics
                .Where(IsWellFormed)
                .GroupBy(
                    epic => epic.SolutionEpicKey is null ? epic.Id.ToString() : $"{epic.SolutionEpicKey}/{epic.SolutionKey}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => new EpicGroup(
                    [.. group],
                    projects is null
                        ? null
                        : [.. group.Where(copy => IsMemberCopy(copy, byId.GetValueOrDefault(copy.ProjectId)))])),
        ];

        return
        [
            .. groups.Where(group => !group.IsSolutionEpic
                || group.HasMemberCopy
                || !groups.Any(other => other.HasMemberCopy
                    && string.Equals(other.SolutionEpicKey, group.SolutionEpicKey, StringComparison.OrdinalIgnoreCase))),
        ];
    }

    private static bool IsWellFormed(EpicDetails epic) =>
        !epic.IsSolutionEpic
        || (Ulid.TryParse(epic.SolutionEpicKey, out _) && Ulid.TryParse(epic.SolutionKey, out _));
}
