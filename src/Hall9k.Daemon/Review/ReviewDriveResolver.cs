using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;

namespace Hall9k.Daemon.Review;

/// <summary>
/// What a pr-review dispatch decides about standing the project's product up, one persona at a
/// time (idea b9b09779, piece 3 for the designer; piece 2 for QA on the same mechanism). Both
/// halves of the answer live here because neither alone is the decision: the project's own drive
/// setting (<see cref="ReviewDriveSetting"/>) says whether it is allowed, and the run skill
/// (<see cref="ProjectRunSkillReader"/>) is what makes it possible.
/// <para>
/// Resolved once, at dispatch, and recorded on the run's own stream: both inputs can move while
/// a review is in flight, and the findings report has to describe the review that ran rather
/// than the settings as they stand when somebody reads it.
/// </para>
/// </summary>
public static class ReviewDriveResolver
{
    /// <summary>What this run will do about <paramref name="persona"/> driving the product.</summary>
    public static async Task<ReviewDriveDecision> ResolveAsync(
        ReviewPersona persona, IQuerySession session, ProjectDetails project, CancellationToken cancellationToken)
    {
        ReviewDriveSetting setting = await ReviewDriveSetting.ResolveAsync(
            persona, session, project.Id, cancellationToken);
        return new ReviewDriveDecision(
            persona, setting.Enabled, ProjectRunSkillReader.Read(project).IsNotBlank());
    }

    /// <summary>
    /// One decision per persona in <paramref name="personas"/> whose review can drive — what a
    /// dispatch resolves before it builds its plan. A persona that cannot drive is skipped
    /// entirely rather than recorded as "off": it has no setting, and a recorded decision for
    /// one would be an answer to a question nobody asked.
    /// </summary>
    public static async Task<IReadOnlyList<ReviewDriveDecision>> ResolveAllAsync(
        IEnumerable<ReviewPersona> personas, IQuerySession session, ProjectDetails project,
        CancellationToken cancellationToken)
    {
        List<ReviewDriveDecision> decisions = [];
        foreach (ReviewPersona persona in personas.Where(persona => ReviewPersonaRegistry.For(persona).CanDriveTheProduct))
        {
            decisions.Add(await ResolveAsync(persona, session, project, cancellationToken));
        }

        return decisions;
    }
}
