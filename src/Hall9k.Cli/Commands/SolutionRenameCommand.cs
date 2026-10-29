using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>Renames a solution on every member in one command: an explicit human act, never automatic.</summary>
public sealed class SolutionRenameCommand : Hall9kAsyncCommand<SolutionRenameCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<SOLUTION>")]
        [Description("The solution: its name, or a prefix of its key (h9k solution list shows them)")]
        public string Solution { get; init; } = string.Empty;

        [CommandArgument(1, "<NEW_NAME>")]
        [Description("The new name. Follows the rules a project name follows: unique on this node, case-insensitive")]
        public string NewName { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        SolutionContext context = await SolutionContext.LoadAsync(session, cancellationToken);
        SolutionView view = SolutionRegistry.Match(context.Solutions, settings.Solution);

        SolutionRegistry.RequireUniqueName(context.Solutions, settings.NewName, view.Membership.Key);
        SolutionMembership renamed = SolutionDecider.Rename(view.Membership, settings.NewName);
        SolutionDecider.RequireEveryMemberHeld(view.Membership, context.Projects, context.RootFingerprintOf, "rename");

        await SolutionWriter.WriteAsync(session, SolutionWriter.Rewrite(renamed, context, removed: []), cancellationToken);

        AnsiConsole.MarkupLineInterpolated($"[green]Solution '{view.Membership.Name}' renamed to '{renamed.Name}'.[/] Its key and members are unchanged.");
        return ExitCodes.Ok;
    }
}
