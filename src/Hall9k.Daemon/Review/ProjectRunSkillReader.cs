using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;

namespace Hall9k.Daemon.Review;

/// <summary>
/// The one place anything asks whether a project has a run skill, and what it says (idea
/// b9b09779). A run skill is that project's own account of how it is stood up locally; a review
/// session that drives the product follows it rather than guessing at a command, which is why
/// having one is a precondition of driving at all (<see cref="ReviewDriveResolver"/>) and not
/// merely a convenience.
/// <para>
/// Read off this node's own projection (<see cref="ProjectDetails.RunSkill"/>), which is what
/// piece 4 records from <c>ProjectRunSkillRecorded</c> and what <c>h9k project run-skill show</c>
/// prints — never a live ledger fetch, for the identical reason that command gives: the ledger is
/// the daemon's sweep to write and to pull, and a read here that needed the project's remote
/// reachable would turn every review dispatch into a network call to answer a question the local
/// store already holds.
/// </para>
/// <para>
/// It is deliberately a single function rather than an interface with one implementation: there
/// is one source of run skills and there was never going to be two.
/// </para>
/// </summary>
public static class ProjectRunSkillReader
{
    /// <summary>
    /// This project's run skill, verbatim, or null when it has none to drive with.
    /// <para>
    /// A <see cref="RunSkillShape.NoneDiscoverable"/> record reads as none, not as a skill: that
    /// shape is the platform's own mechanical finding that nothing in the repository says how to
    /// run it (<c>RunSkillSweepEngine</c>), so its content is an account of where the survey
    /// looked rather than a procedure a session could follow. Handing it to a driving session
    /// would be handing over the very statement that there is nothing to follow.
    /// </para>
    /// </summary>
    public static string? Read(ProjectDetails project) =>
        project.RunSkill is { Shape: var shape, Content: var content } && shape != RunSkillShape.NoneDiscoverable
            ? content
            : null;
}
