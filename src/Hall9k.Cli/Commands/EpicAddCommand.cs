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
        [Description("The epic's name — a cohesive family of tasks worth naming (Decisions Log #99). Required.")]
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
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        Guid epicId = DomainId.New();
        EpicAdded added = EpicDecider.Add(
            epicId, project.Id, settings.Title ?? string.Empty, DateTimeOffset.UtcNow, context.OwnerId);
        session.Events.StartStream<EpicAggregate>(epicId, added);
        await session.SaveChangesAsync(cancellationToken);

        string shortId = TaskListCommand.ShortId(epicId);
        AnsiConsole.MarkupLine(
            $"[blue]Epic created[/] in '{project.Name.EscapeMarkup()}': {added.Title.EscapeMarkup()} [dim]({shortId})[/]");
        AnsiConsole.MarkupLine(
            $"[dim]Next:[/] h9k task add --project {project.Name.EscapeMarkup()} --objective \"…\" --epic {shortId} "
            + $"[dim]for a new task, or for an existing draft:[/] h9k task revise <id> --epic {shortId} "
            + $"[dim](revision is Draft-only — a Published task returns with[/] h9k task draft <id> "
            + $"[dim]alone; an assigned task (Queued or Blocked) needs[/] h9k task unassign <id> && h9k task draft <id> "
            + $"[dim]first)[/]");
        return ExitCodes.Ok;
    }
}
