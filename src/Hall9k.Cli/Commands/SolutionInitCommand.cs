using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Cli.Orchestrator;
using Hall9k.Cli.ProjectHomes;
using Hall9k.Domain.Features.Project;
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

        return await SolutionHomeInit.RunAsync(view.Membership, context, cancellationToken)
            ? ExitCodes.Ok
            : ExitCodes.Error;
    }
}

/// <summary>The home recipe and its report, shared by <c>h9k solution init</c> and <c>h9k solution add</c>.</summary>
internal static class SolutionHomeInit
{
    /// <summary>Builds the home, prints each step, and answers whether every step got through.</summary>
    public static async Task<bool> RunAsync(
        SolutionMembership membership, SolutionContext context, CancellationToken cancellationToken)
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
            AnsiConsole.Profile.Out.Writer.WriteLine(LaunchTextDefaults.Render(
                home, OrchestratorRecipeContext.SolutionOpeningMessage(membership.Name)));
        }

        return succeeded;
    }
}
