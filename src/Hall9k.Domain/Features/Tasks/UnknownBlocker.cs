using Hall9k.Domain.Infrastructure.Ids;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// How a node reports a blocker it has no record of. The dependency sweep leaves such a task
/// Blocked rather than recording a failure from a partial view (<c>TaskDependencyResolver</c>),
/// so the node says what it does not know instead: the blocker is named by its short id only,
/// never an objective this node never saw, with the most likely reason and who to ask.
/// <c>h9k status</c>, <c>h9k task show</c>, and the orchestrator feed all read this one sentence,
/// so they cannot describe the same blocker differently.
/// </summary>
public static class UnknownBlocker
{
    /// <summary>
    /// The ids among <paramref name="blockerIds"/> that name no task in <paramref name="knownTaskIds"/>
    /// and that the dependent has not already recorded as dead (<paramref name="deadBlockerIds"/>).
    /// A blocker with a recorded failure is explained by that failure's own reason, which is
    /// the case for one this node purged itself and for one a teammate's node failed and
    /// replicated here, so calling it unknown too would send the reader to a teammate about a
    /// deletion the recorded reason already accounts for.
    /// </summary>
    public static IReadOnlyList<Guid> Among(
        IEnumerable<Guid> blockerIds, IReadOnlySet<Guid> knownTaskIds, IReadOnlyCollection<Guid> deadBlockerIds) =>
        [.. blockerIds.Distinct().Where(id => !knownTaskIds.Contains(id) && !deadBlockerIds.Contains(id))];

    /// <summary>
    /// The one sentence, in the register of the existing blocked lines (lower case, no trailing
    /// period), or null when every blocker is known.
    /// </summary>
    public static string? Line(IReadOnlyList<Guid> unknownIds) => unknownIds switch
    {
        [] => null,
        [Guid only] =>
            $"unknown blocker {DomainId.Short(only)}: it most likely belongs to a project in a solution held on a "
            + "teammate's node, so ask that teammate about it",
        _ =>
            $"unknown blockers {string.Join(", ", unknownIds.Select(DomainId.Short))}: they most likely belong to "
            + "projects in a solution held on a teammate's node, so ask that teammate about them",
    };
}
