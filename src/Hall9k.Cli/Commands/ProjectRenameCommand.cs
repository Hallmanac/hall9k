using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Changes a project's name and nothing else (task: a project can be archived, listed as archived,
/// reactivated, and renamed). NAME IS NOT AN IDENTIFIER for anything this platform persists — the
/// id, the recorded home path, the repository, and every task, run, and idea are untouched, and
/// the home directory on disk keeps its old folder name. A handful of call sites (rendered
/// AGENTS.md, the dispatched work prompt, <c>h9k project init</c>'s repair path) still need a bare
/// clone's filename and derive it from the name when nothing is recorded yet to prefer instead —
/// but they go through <see cref="ProjectHomePaths.ResolveBareRepository"/>, which always prefers
/// the recorded <c>RepositoryPath</c> once it already lives inside the home's own <c>repo/</c>
/// directory, so a rename never desyncs the two (independent pre-PR review, cycle 1, adversarial
/// lens — the earlier version of this comment claimed no name-keyed lookup existed at all, which
/// was false: <c>ProjectHomePaths.BareRepository(home, project.Name)</c> was one, called directly
/// at four sites, and every one of them stopped recognising its own clone the moment a project was
/// renamed). Its main use is freeing an archived project's name for a fresh registration
/// (h9k project add offers this same rename inline on a name collision), but nothing here requires
/// the project to be archived — a live project may be renamed too.
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
        StreamState? fence = await session.Events.FetchStreamStateAsync(project.Id, cancellationToken)
            ?? throw new DomainNotFoundException($"No project {project.Id}.");
        ProjectAggregate aggregate = await session.Events.AggregateStreamAsync<ProjectAggregate>(
                project.Id, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No project {project.Id}.");

        await ProjectNameUniqueness.CheckAsync(session, settings.NewName, excludingProjectId: project.Id, cancellationToken);

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        session.Events.Append(
            project.Id, expectedVersion: fence.Version + 1,
            ProjectDecider.Rename(aggregate, settings.NewName, DateTimeOffset.UtcNow, context.OwnerId));
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Project '{project.Name}' changed while renaming it — check h9k status and re-run this "
                + "command with the name it should still become.");
        }

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
