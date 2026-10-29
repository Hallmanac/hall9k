using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

public sealed class EpicAddCommand : Hall9kAsyncCommand<EpicAddCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--project <PROJECT>")]
        [Description(
            "Project the epic belongs to: its name, an unambiguous fragment of it, or its full id "
            + "(h9k project list shows them all). Required.")]
        public string? Project { get; init; }

        [CommandOption("--title <TITLE>")]
        [Description(
            "The epic's name — a cohesive family of tasks worth naming (Decisions Log #100). Required. In a "
            + "project that belongs to a solution every epic is a solution epic: one copy in each member project "
            + "this node holds, sharing this title and one solution epic key, open to tasks from any member")]
        public string? Title { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Project.IsBlank())
        {
            throw new DomainValidationException(
                "An epic belongs to a project: h9k epic add --project <name> --title \"<name>\".");
        }

        if (settings.Title.IsBlank())
        {
            throw new DomainValidationException(
                "An epic needs a title: h9k epic add --project <name> --title \"<name>\".");
        }

        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
        if (project.PurgeAt is { } purgeDeadline)
        {
            // Same reasoning as h9k task add's own refusal: the sweep re-queries ownership at
            // fire time, so an epic created after scheduling is destroyed along with everything
            // else rather than orphaned — refused here so new work does not accrue on a project
            // already scheduled for destruction.
            throw new DomainValidationException(
                $"Project '{project.Name}' is scheduled for permanent deletion at "
                + $"{purgeDeadline.ToLocalTime():g}, so it cannot take a new epic. Cancel the purge "
                + $"first: h9k project cancel-purge {project.Name}");
        }

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        if (await SolutionEpicAddition.PlanAsync(session, project, settings.Title ?? string.Empty, context.OwnerId, cancellationToken) is { } addition)
        {
            return await AddSolutionEpicAsync(session, addition, project.Name, cancellationToken);
        }

        Guid epicId = DomainId.New();
        EpicAdded added = EpicDecider.Add(
            epicId, project.Id, settings.Title ?? string.Empty, DateTimeOffset.UtcNow, context.OwnerId);
        session.Events.StartStream<EpicAggregate>(epicId, added);
        await session.SaveChangesAsync(cancellationToken);

        string shortId = TaskListCommand.ShortId(epicId);
        AnsiConsole.MarkupLine(
            $"[blue]Epic created[/] in '{project.Name.EscapeMarkup()}': {added.Title.EscapeMarkup()} [dim]({shortId})[/]");
        PrintNext(project.Name, shortId);
        return ExitCodes.Ok;
    }

    private static async Task<int> AddSolutionEpicAsync(
        IDocumentSession session, SolutionEpicAddition addition, string projectName, CancellationToken cancellationToken)
    {
        foreach (EpicAdded copy in addition.Copies)
        {
            session.Events.StartStream<EpicAggregate>(copy.Id, copy);
        }

        await session.SaveChangesAsync(cancellationToken);

        string key = addition.Copies[0].SolutionEpicKey ?? string.Empty;
        string shortKey = EpicIdResolver.ReferenceOf(addition.Copies[0].Id, key);
        AnsiConsole.MarkupLineInterpolated(
            $"[blue]Solution epic created[/] in '{addition.View.Membership.Name}': {addition.Copies[0].Title} [dim]({shortKey})[/]");
        AnsiConsole.MarkupLineInterpolated(
            $"[dim]A copy in each of {string.Join(", ", addition.Targets.Select(member => member.Name))}; the copies share the key {key}.[/]");
        if (addition.Purging.Count > 0)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]No copy in {string.Join(", ", addition.Purging.Select(member => member.Name))}[/] [dim](scheduled for permanent deletion).[/]");
        }

        if (addition.NotHeld.Count > 0)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]No copy in {string.Join(", ", addition.NotHeld.Select(member => member.Name))}[/] [dim](not registered and joined on this node; this node makes copies only in the projects it holds).[/]");
        }

        PrintNext(projectName, shortKey);
        return ExitCodes.Ok;
    }

    private static void PrintNext(string projectName, string reference) =>
        AnsiConsole.MarkupLine(
            $"[dim]Next:[/] h9k task add --project {projectName.EscapeMarkup()} --objective \"…\" --epic {reference} "
            + $"[dim]for a new task, or for an existing draft:[/] h9k task revise <id> --epic {reference} "
            + $"[dim](revision is Draft-only — a Published task returns with[/] h9k task draft <id> "
            + $"[dim]alone; an assigned task (Queued or Blocked) needs[/] h9k task dequeue <id> && h9k task draft <id> "
            + $"[dim]first)[/]");
}
