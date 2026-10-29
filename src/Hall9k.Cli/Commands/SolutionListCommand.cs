using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>Every solution this node knows, read from the memberships its member projects carry.</summary>
public sealed class SolutionListCommand : Hall9kAsyncCommand<SolutionListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--state <STATE>")]
        [Description("open | closed | all. Defaults to open")]
        public string? State { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        SolutionState? state = ParseState(settings.State);

        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        SolutionContext context = await SolutionContext.LoadAsync(session, cancellationToken);
        if (context.Solutions.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No solutions yet. Group projects:[/] h9k solution add <name> --project <project> --project <project>");
            return ExitCodes.Ok;
        }

        List<SolutionView> matched = [.. context.Solutions.Where(view => state is null || view.Membership.State == state)];
        if (matched.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No solutions match that state. See everything:[/] h9k solution list --state all");
            return ExitCodes.Ok;
        }

        Table table = new Table().Border(TableBorder.Rounded);
        table.AddColumns("Key", "Name", "State", "Members", "Here");
        foreach (SolutionView view in matched)
        {
            int here = view.Membership.Members.Count(member =>
                SolutionDecider.HeldProject(member, context.Projects, context.RootFingerprintOf) is not null);
            table.AddRow(
                $"[dim]{view.Membership.Key.EscapeMarkup()}[/]",
                view.Membership.Name.EscapeMarkup(),
                view.Membership.State == SolutionState.Closed ? "[dim]Closed[/]" : "[green]Open[/]",
                string.Join(", ", view.Membership.Members.Select(member => member.Name.EscapeMarkup())),
                $"{here}/{view.Membership.Members.Count}");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            $"[dim]{matched.Count} solution{(matched.Count == 1 ? string.Empty : "s")}. Here is how many members this node has joined. See one's board:[/] "
            + $"h9k solution show \"{matched[0].Membership.Name.EscapeMarkup()}\"");
        return ExitCodes.Ok;
    }

    internal static SolutionState? ParseState(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "open" => SolutionState.Open,
        "closed" => SolutionState.Closed,
        "all" => null,
        _ => throw new DomainValidationException($"Unknown solution state '{value}'. Use one of: open, closed, all."),
    };
}
