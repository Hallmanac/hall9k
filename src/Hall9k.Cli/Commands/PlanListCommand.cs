using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Plan.Projections;
using Hall9k.Domain.Features.Project.Projections;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>A project's plans on this node, newest first, live ones by default. Plans never leave the node they were started on.</summary>
public sealed class PlanListCommand : Hall9kAsyncCommand<PlanListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--project <PROJECT>")]
        [Description("Only this project's plans: its name, an unambiguous fragment, or its id. Without it, every project's on this node")]
        public string? Project { get; init; }

        [CommandOption("--all")]
        [Description("Include plans that have ended (published or cancelled). By default only live plans, parked or not, are listed")]
        public bool All { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using DocumentStore store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        ProjectDetails? project = settings.Project is null
            ? null
            : await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
        IReadOnlyList<PlanDetails> plans = [.. (await session.Query<PlanDetails>().ToListAsync(cancellationToken))
            .Where(plan => (project is null || plan.ProjectId == project.Id) && (settings.All || plan.IsLive))
            .OrderByDescending(plan => plan.StartedAt)];
        if (plans.Count == 0)
        {
            string scope = settings.All
                ? string.Empty
                : " are live";
            AnsiConsole.MarkupLine($"[dim]No plans{scope} on this node. Start one with:[/] h9k plan start <draft> [[<draft>...]]  [dim]or, from an idea:[/] h9k plan start --idea <idea>");
            return ExitCodes.Ok;
        }

        Dictionary<Guid, string> names = (await session.Query<ProjectDetails>().ToListAsync(cancellationToken))
            .ToDictionary(candidate => candidate.Id, candidate => candidate.Name);
        Table table = new Table().Border(TableBorder.Rounded).AddColumns("Plan", "Project", "Idea", "State", "Drafts", "Started");
        foreach (PlanDetails plan in plans)
        {
            // Stopped at the gate is a normal ending and reads as one; a plan waiting at the gate says so.
            string state = plan.AtGate
                ? "[red]parked (gate)[/]"
                : plan.IsLive && plan.IsParked
                    ? "[red]parked[/]"
                    : plan.State.Value.ToLowerInvariant();
            int closed = plan.Drafts.Count(draft => draft.IsClosed);
            string drafts = plan.Discovery is not null && plan.Drafts.Count == 0
                ? plan.IsLive
                    ? "in discovery"
                    : "none"
                : $"{closed} of {plan.Drafts.Count} closed";
            table.AddRow(
                TaskListCommand.ShortId(plan.Id),
                names.GetValueOrDefault(plan.ProjectId, "?").EscapeMarkup(),
                plan.IdeaId is { } ideaId
                    ? TaskListCommand.ShortId(ideaId)
                    : "[dim]-[/]",
                state,
                drafts,
                $"{plan.StartedAt:u}");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[dim]Read one with:[/] h9k plan show <plan>");
        return ExitCodes.Ok;
    }
}
