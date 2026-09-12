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
/// Changes a project's name and nothing else (task: a project can be archived, listed as archived,
/// reactivated, and renamed). NAME IS NOT AN IDENTIFIER — the id, the recorded home path, the
/// repository, and every task, run, and idea are untouched, and the home directory on disk keeps
/// its old folder name. Its main use is freeing an archived project's name for a fresh
/// registration (h9k project add offers this same rename inline on a name collision), but nothing
/// here requires the project to be archived — a live project may be renamed too.
/// </summary>
public sealed class ProjectRenameCommand : Hall9kAsyncCommand<ProjectRenameCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PROJECT>")]
        [Description("Project name, an unambiguous fragment of it, or the full id")]
        public string Project { get; init; } = string.Empty;

        [CommandArgument(1, "<NEW_NAME>")]
        [Description("The new name; refused when another project already carries it")]
        public string NewName { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
        ProjectAggregate aggregate = await session.Events.AggregateStreamAsync<ProjectAggregate>(project.Id, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No project {project.Id}.");

        await ProjectNameUniqueness.CheckAsync(session, settings.NewName, excludingProjectId: project.Id, cancellationToken);

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        session.Events.Append(
            project.Id, ProjectDecider.Rename(aggregate, settings.NewName, DateTimeOffset.UtcNow, context.OwnerId));
        await session.SaveChangesAsync(cancellationToken);
        await Doorbell.RingAsync($"project-renamed:{project.Id}", cancellationToken);

        AnsiConsole.MarkupLine(
            $"[green]Project '{project.Name.EscapeMarkup()}' renamed to '{settings.NewName.EscapeMarkup()}'.[/] "
            + "The id, the recorded home path, the repository, and every task, run, and idea are "
            + "untouched. The home directory on disk keeps its old folder name"
            + (project.HomeDirectory.HasValue ? $": {project.HomeDirectory.Value.EscapeMarkup()}" : " (none recorded)")
            + ".");
        return ExitCodes.Ok;
    }
}
