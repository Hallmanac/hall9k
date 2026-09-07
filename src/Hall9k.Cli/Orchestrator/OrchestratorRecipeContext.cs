using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Cli.Orchestrator;

/// <summary>
/// The two facts <see cref="LaunchTextDefaults"/> needs to render a scope's default launch line:
/// where the window's working directory is, and the opening message that scope starts with.
/// </summary>
public static class OrchestratorRecipeContext
{
    public const string NodeOpeningMessage = "You are the Hall9k node orchestrator on this machine.";

    public static string NodeWorkingDirectory => PlatformPaths.Home;

    public static string ProjectOpeningMessage(string projectName) =>
        $"You are the {projectName} project orchestrator.";

    /// <summary>
    /// The project's home directory, or a placeholder naming the gap when none is recorded yet —
    /// a default launch line rendered against a placeholder is honestly synthetic rather than a
    /// guess at a path that does not exist.
    /// </summary>
    public static string ProjectWorkingDirectory(ProjectDetails project) =>
        project.HomeDirectory.HasValue
            ? project.HomeDirectory.Value
            : $"<no home recorded yet - run h9k project init {project.Name}>";
}
