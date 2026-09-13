using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx.Events;
using Marten;
using Marten.Events;
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
        // Fenced against the version just read (Copilot review, PR #338), the same reason and
        // the same pattern as h9k project reactivate's own fix: the daemon's sweep reads this
        // stream on its own schedule, so an unfenced cancel racing a concurrent reschedule or
        // reactivate must lose loudly rather than silently land on a lifecycle state it never saw.
        StreamState fence = await session.Events.FetchStreamStateAsync(project.Id, cancellationToken)
            ?? throw new DomainNotFoundException($"No project {project.Id}.");
        ProjectAggregate aggregate = await session.Events.AggregateStreamAsync<ProjectAggregate>(
                project.Id, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No project {project.Id}.");

        if (aggregate.PurgeAt is null)
        {
            throw new DomainValidationException($"Project '{project.Name}' has no purge scheduled.");
        }

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        session.Events.Append(
            project.Id, expectedVersion: fence.Version + 1,
            ProjectDecider.CancelPurge(aggregate, DateTimeOffset.UtcNow, context.OwnerId));
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Project '{project.Name}' changed while cancelling the purge — check h9k project show "
                + "and try again.");
        }

        await Doorbell.RingAsync($"project-purge-cancelled:{project.Id}", cancellationToken);

        AnsiConsole.MarkupLine(
            $"[green]Purge cancelled.[/] Project '{project.Name.EscapeMarkup()}' stays archived — "
            + "nothing was destroyed. Reactivate it: h9k project reactivate "
            + $"{project.Name.EscapeMarkup()}, or schedule another purge any time: h9k project remove "
            + $"{project.Name.EscapeMarkup()} --purge");
        return ExitCodes.Ok;
    }
}
