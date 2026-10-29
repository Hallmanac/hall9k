namespace Hall9k.Domain.Features.Epic;

/// <summary>
/// One epic as a person sees it. A project epic is a group of one; a solution epic is the group of
/// every copy this node holds, which all carry one <see cref="EpicDetails.SolutionEpicKey"/>. Showing
/// and listing read these groups, so a solution epic is one row however many member projects hold a
/// copy of it. The grouping is by key and never by current membership: a project taken out of its
/// solution keeps its copies, and they stay on the solution epic.
/// </summary>
public sealed record EpicGroup(IReadOnlyList<EpicDetails> Copies)
{
    /// <summary>The group's earliest copy, which names the group: the copies share a title, and this one is the stable pick.</summary>
    public EpicDetails First => Copies.OrderBy(copy => copy.AddedAt).ThenBy(copy => copy.Id).First();

    public bool IsSolutionEpic => First.IsSolutionEpic;

    /// <summary>The key every copy shares; null for a project epic.</summary>
    public string? SolutionEpicKey => First.SolutionEpicKey;

    /// <summary>The solution the copies were made for; null for a project epic.</summary>
    public string? SolutionKey => First.SolutionKey;

    public string Title => First.Title;

    public DateTimeOffset AddedAt => First.AddedAt;

    /// <summary>
    /// Closed as soon as any copy this node holds is closed. Closing a solution epic is one human act
    /// that closes every copy the node holds, so a closed copy beside an open one is that act still
    /// replicating to the copies another node holds, and the epic is already closed.
    /// </summary>
    public EpicState State => Copies.Any(copy => copy.State == EpicState.Closed) ? EpicState.Closed : First.State;

    /// <summary>The id a person types for this epic: the copy's id for a project epic, the shared key for a solution epic.</summary>
    public string Reference => SolutionEpicKey ?? First.Id.ToString();

    public EpicDetails? CopyIn(Guid projectId) => Copies.FirstOrDefault(copy => copy.ProjectId == projectId);

    /// <summary>Groups epics into what a person sees: each project epic alone, each solution epic's copies together.</summary>
    public static IReadOnlyList<EpicGroup> Of(IEnumerable<EpicDetails> epics) =>
    [
        .. epics
            .GroupBy(epic => epic.SolutionEpicKey ?? epic.Id.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new EpicGroup([.. group])),
    ];
}
