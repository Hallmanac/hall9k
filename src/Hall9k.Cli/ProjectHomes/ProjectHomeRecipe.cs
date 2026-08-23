using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Storage;
using Spectre.Console;

namespace Hall9k.Cli.ProjectHomes;

/// <summary>
/// Creates a project's home directory in the shape every machine lays it out in: the generated
/// <c>AGENTS.md</c>, <c>repo/</c> materialised from the recorded remote, empty <c>ideas/</c> and
/// <c>tasks/</c>, <c>skills/</c> seeded from the install's canonical set, and the
/// <c>.claude/</c> adapter generated beside it.
/// <para>
/// This is deterministic platform code end to end, which is the ruling it exists under: "this is
/// a recipe, not an agent" (the installation sitting, restated at the project-home discovery).
/// Nothing here judges anything, so nothing here needs a session. The two judgment moments the
/// discovery did reserve for agents — migrating a nonconforming project into a home, and
/// configuration-laden tracker authoring — are both somewhere else.
/// </para>
/// <para>
/// It is idempotent throughout, so <c>h9k project init</c> is also the repair command: run it
/// against a half-made home and it reports what was already there and makes the rest.
/// </para>
/// </summary>
public static class ProjectHomeRecipe
{
    /// <summary>
    /// Builds (or repairs) the home at <paramref name="home"/> for <paramref name="project"/>.
    /// Every step reports; nothing throws for an ordinary failure, because a home with five of
    /// its six parts in place is worth reporting honestly rather than rolling back.
    /// </summary>
    /// <param name="materialiseRepository">
    /// Whether to clone <c>repo/</c>. False when the project is registered against a repository
    /// that already exists elsewhere (<c>h9k project add --repo</c>): the recorded path is what
    /// dispatch cuts worktrees from, so a clone here would be a second copy nothing ever reads,
    /// and the option promises it is not made. Every other part of the shape is still built, so
    /// the home is a home either way, and <c>h9k project init</c> is what changes the answer.
    /// </param>
    public static async Task<IReadOnlyList<ProjectHomeStep>> BuildAsync(
        string home,
        ProjectDetails project,
        CancellationToken cancellationToken,
        bool materialiseRepository = true)
    {
        List<ProjectHomeStep> steps = [];

        if (ProjectAgentsDocument.Guard(home, project.Name) is { } foreignAgentsFile)
        {
            steps.Add(foreignAgentsFile);
            return steps;
        }

        bool existed = Directory.Exists(home);
        foreach (string directory in ProjectHomePaths.Directories(home))
        {
            Directory.CreateDirectory(directory);
        }

        steps.Add(existed
            ? ProjectHomeStep.AlreadyThere($"home directory {home} already existed — the shape was completed in place")
            : ProjectHomeStep.Created($"home directory created at {home} (repo/ ideas/ tasks/ skills/ .claude/)"));

        if (materialiseRepository)
        {
            steps.AddRange(await RepoMaterialiser.MaterialiseAsync(
                home, project.Name, project.RepositoryUrl, project.BaseBranch, cancellationToken));
        }
        else
        {
            steps.Add(ProjectHomeStep.Skipped(
                $"repo/ left unmaterialised: this project cuts its worktrees from {project.RepositoryPath}, "
                + "so a clone here would be a second copy nothing dispatches against. "
                + $"h9k project init {project.Name} materialises repo/ and re-points the project at it."));
        }

        steps.AddRange(SkillSeeder.Seed(home));
        steps.Add(ProjectAgentsDocument.Write(home, project));

        return steps;
    }

    /// <summary>
    /// Prints the report, one plain line per step, and answers whether every step got through.
    /// The colour carries the outcome so a scan finds the one red line; the words carry the
    /// whole story either way, because this output is read by agents through a redirected stdout
    /// as often as by a human in a terminal.
    /// </summary>
    public static bool Report(IReadOnlyList<ProjectHomeStep> steps)
    {
        foreach (ProjectHomeStep step in steps)
        {
            string message = step.Message.EscapeMarkup();
            AnsiConsole.MarkupLine(step.Outcome switch
            {
                ProjectHomeOutcome.Created => $"[green]·[/] {message}",
                ProjectHomeOutcome.AlreadyThere => $"[dim]·[/] [dim]{message}[/]",
                ProjectHomeOutcome.Skipped => $"[yellow]·[/] {message}",
                _ => $"[red]·[/] {message}",
            });
        }

        return steps.All(step => step.Outcome != ProjectHomeOutcome.Failed);
    }
}
