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
/// Ends an archive in place, on the same stream and the same id (task: a project can be archived,
/// listed as archived, reactivated, and renamed). Everything an archive left untouched — settings,
/// tasks, ideas, the recorded home — reads exactly as it did before <c>h9k project remove</c>, and
/// the dispatcher's claim sweep and both daemon sweeps resume for it immediately.
/// </summary>
public sealed class ProjectReactivateCommand : Hall9kAsyncCommand<ProjectReactivateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PROJECT>")]
        [Description("Project name, an unambiguous fragment of it, or the full id (h9k project list --include-archived shows them all)")]
        public string Project { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
        ProjectAggregate aggregate = await session.Events.AggregateStreamAsync<ProjectAggregate>(project.Id, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No project {project.Id}.");

        if (!aggregate.IsArchived)
        {
            throw new DomainValidationException($"Project '{project.Name}' is not archived, so there is nothing to reactivate.");
        }

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        session.Events.Append(
            project.Id, ProjectDecider.Reactivate(aggregate, DateTimeOffset.UtcNow, context.OwnerId));
        await session.SaveChangesAsync(cancellationToken);
        await Doorbell.RingAsync($"project-reactivated:{project.Id}", cancellationToken);

        AnsiConsole.MarkupLine(
            $"[green]Project '{project.Name.EscapeMarkup()}' reactivated.[/] Same id, settings, tasks, "
            + "ideas, and home; the dispatcher's claim sweep and the project-home and auto-pr-review "
            + "sweeps resume for it. This is this install's own record — a registration of the same "
            + "repository on another node is unaffected.");

        ProjectHomeDirectoryStatus.Report(project);
        return ExitCodes.Ok;
    }
}
