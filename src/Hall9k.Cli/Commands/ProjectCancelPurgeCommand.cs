using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Ends a pending purge before it fires (task: an archived project can be purged) — the inverse
/// of <c>h9k project remove --purge</c>'s schedule. Leaves the project exactly as archiving left
/// it: archived, never reactivated. Reactivate separately with <c>h9k project reactivate</c> once
/// nothing is scheduled to destroy it.
/// </summary>
public sealed class ProjectCancelPurgeCommand : Hall9kAsyncCommand<ProjectCancelPurgeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PROJECT>")]
        [Description("Project name, an unambiguous fragment of it, or the full id (h9k project list --include-archived shows a pending purge and its deadline)")]
        public string Project { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
        ProjectAggregate aggregate = await session.Events.AggregateStreamAsync<ProjectAggregate>(project.Id, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No project {project.Id}.");

        if (aggregate.PurgeAt is null)
        {
            throw new DomainValidationException($"Project '{project.Name}' has no purge scheduled.");
        }

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        session.Events.Append(
            project.Id, ProjectDecider.CancelPurge(aggregate, DateTimeOffset.UtcNow, context.OwnerId));
        await session.SaveChangesAsync(cancellationToken);
        await Doorbell.RingAsync($"project-purge-cancelled:{project.Id}", cancellationToken);

        AnsiConsole.MarkupLine(
            $"[green]Purge cancelled.[/] Project '{project.Name.EscapeMarkup()}' stays archived — "
            + "nothing was destroyed. Reactivate it: h9k project reactivate "
            + $"{project.Name.EscapeMarkup()}, or schedule another purge any time: h9k project remove "
            + $"{project.Name.EscapeMarkup()} --purge");
        return ExitCodes.Ok;
    }
}
