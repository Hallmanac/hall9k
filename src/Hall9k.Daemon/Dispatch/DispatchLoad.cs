using Hall9k.Domain.Features.Project;

namespace Hall9k.Daemon.Dispatch;

/// <summary>
/// Everything one sweep admits against: this node's own ceiling (<see cref="NodeLoad"/>,
/// Decisions Log #64, #111) and each interested project's own ceiling (Decisions Log #140).
/// Measured together, in one pass, so the two gates can never be applied against counts taken
/// at different moments.
/// </summary>
/// <param name="Node">What this node is carrying against what it may carry.</param>
/// <param name="Projects">
/// Every project this sweep could have to decide about: one it is carrying a run for, or one a
/// queued candidate belongs to. A project absent from the map is one no candidate named and no
/// run belongs to, which is why <see cref="Project"/> answers for it with an uncapped, idle
/// reading rather than throwing — a missing project document must never wedge the queue.
/// </param>
public sealed record DispatchLoad(NodeLoad Node, IReadOnlyDictionary<Guid, ProjectLoad> Projects)
{
    /// <summary>
    /// One project's measurement, or — for a project this sweep measured nothing about — an
    /// uncapped one carrying nothing, named by its own id since no document answered for it.
    /// Uncapped is the honest fallback: the cap lives on the project document, so a project whose
    /// document could not be read has no cap this sweep can enforce, and inventing one would hold
    /// work back for a rule nobody set (AGENTS.md: never guess at unobserved facts). Its tier
    /// falls back the same way, to the default one: a project nothing answered for rotates like
    /// every other rather than being quietly focused or quietly starved.
    /// </summary>
    public ProjectLoad Project(Guid projectId) =>
        Projects.TryGetValue(projectId, out ProjectLoad? project)
            ? project
            : new ProjectLoad(
                projectId, projectId.ToString(), ProjectRunCeiling.Uncapped(0), ProjectPriority.Normal);
}

/// <summary>
/// One project as a sweep measured it: what it is carrying, the cap it is admitting against, the
/// tier it competes for a free slot in, and the name every operator-facing line about it uses. The
/// name is carried rather than looked up again at log time, so the line an operator reads names
/// the project the decision was actually made about.
/// </summary>
/// <param name="Priority">
/// This project's dispatch tier (Decisions Log #141), read off the same project document the cap
/// is: one read, so the rotation and the cap can never be decided from documents fetched at
/// different moments.
/// </param>
public sealed record ProjectLoad(
    Guid ProjectId, string Name, ProjectRunCeiling Ceiling, ProjectPriority Priority);
