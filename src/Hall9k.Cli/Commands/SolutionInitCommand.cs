using System.ComponentModel;
using Hall9k.Cli.ClaudeCode;
using Hall9k.Cli.Infrastructure;
using Hall9k.Cli.Orchestrator;
using Hall9k.Cli.ProjectHomes;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Storage;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Creates, or repairs, a solution's thin home on this node. The same recipe <c>h9k solution add</c>
/// runs for a new solution, reported the way <c>h9k project init</c> reports a project home, so a
/// solution that was created on another node (its membership replicated here) gets a home here too.
/// </summary>
public sealed class SolutionInitCommand : Hall9kAsyncCommand<SolutionInitCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<SOLUTION>")]
        [Description("The solution: its name, or a prefix of its key (h9k solution list shows them)")]
        public string Solution { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        SolutionContext context = await SolutionContext.LoadAsync(session, cancellationToken);
        SolutionView view = SolutionRegistry.Match(context.Solutions, settings.Solution);

        return await SolutionHomeInit.RunAsync(session, view.Membership, context, cancellationToken)
            ? ExitCodes.Ok
            : ExitCodes.Error;
    }
}

/// <summary>The home recipe and its report, shared by <c>h9k solution init</c> and <c>h9k solution add</c>.</summary>
internal static class SolutionHomeInit
{
    /// <summary>Builds the home, prints each step, and answers whether every step got through.</summary>
    public static async Task<bool> RunAsync(
        IQuerySession session, SolutionMembership membership, SolutionContext context, CancellationToken cancellationToken)
    {
        string home = SolutionHomePaths.For(membership.Name, membership.Key);
        bool succeeded = ProjectHomeRecipe.Report(await SolutionHomeRecipe.BuildAsync(
            home, membership, context.Projects, context.RootFingerprintOf, cancellationToken));
        if (succeeded)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Start a solution window in {home}. Its AGENTS.md names every member and where this node holds it. Generate its recipe first with the orchestrator-recipe-generator skill, run from that directory, then open a terminal and run:[/]");

            // Written past the console's word wrap, like the Home line of h9k solution show: a
            // wrapped launch line is not one line the operator can paste.
            // The account every member agrees on, or none when they disagree or none is bound.
            string? configDirectory = await CliClaudeAccount.PrintedSolutionLineDirectoryAsync(
                session, HeldMembers(membership, context), cancellationToken);
            AnsiConsole.Profile.Out.Writer.WriteLine(SolutionLine(home, membership.Name, configDirectory, LaunchTextDefaults.HostShell));
        }

        return succeeded;
    }

    /// <summary>The solution window line, carrying <paramref name="configDirectory"/> when the members agree on one.</summary>
    internal static string SolutionLine(string home, string solutionName, string? configDirectory, LaunchShell shell)
    {
        string line = LaunchTextDefaults.Render(home, OrchestratorRecipeContext.SolutionOpeningMessage(solutionName), shell);
        return LaunchLineAccount.Apply(line, configDirectory, shell) ?? line;
    }

    /// <summary>The members of <paramref name="membership"/> this node holds, whose bindings the solution line follows.</summary>
    internal static IReadOnlyList<ProjectDetails> HeldMembers(SolutionMembership membership, SolutionContext context) =>
        [.. membership.Members
            .Select(member => SolutionDecider.HeldProject(member, context.Projects, context.RootFingerprintOf))
            .OfType<ProjectDetails>()];
}
