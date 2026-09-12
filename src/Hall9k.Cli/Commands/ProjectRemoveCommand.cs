using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Archives a project on this install — reversible (h9k project reactivate), never deletes
/// anything, and never touches the home directory on disk (task: a project can be archived, listed
/// as archived, reactivated, and renamed; Decisions Log #PLACEHOLDER-7228d4c7). Kept named "remove"
/// per Brian's own ruling (2026-09-12): what it does is archive, but the escape hatch a stray
/// registration needs is spelled the way an operator reaches for it.
/// </summary>
public sealed class ProjectRemoveCommand : Hall9kAsyncCommand<ProjectRemoveCommand.Settings>
{
    /// <summary>
    /// The only states a task may sit in while its project is archived: Draft and Published are
    /// both pre-dispatch and, for Published, always unassigned (Decisions Log #34 — there is no
    /// "Published-unassigned" state distinct from Published itself); Done and Abandoned are the
    /// platform's own two terminal states (TaskState.IsTerminal) — a human calls an abandoned task
    /// "closed". Everything else (Queued, Blocked, Claimed, NeedsHuman, AwaitingAuthor, Failed) is a
    /// state the daemon, the closeout monitor, or a park a human still owes an answer to may still
    /// act on, and archiving over it would leave live work orphaned mid-flight.
    /// </summary>
    internal static bool IsInertUnderArchive(TaskState state) =>
        state == TaskState.Draft || state == TaskState.Published
        || state == TaskState.Done || state == TaskState.Abandoned;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PROJECT>")]
        [Description("Project name, an unambiguous fragment of it, or the full id")]
        public string Project { get; init; } = string.Empty;

        [CommandOption("--reason <REASON>")]
        [Description(
            "Why this project is being archived; recorded on ProjectArchived and left unknown when "
            + "omitted, never inferred (the same discipline h9k task abandon's own --reason follows)")]
        public string? Reason { get; init; }

        [CommandOption("--yes")]
        [Description("Skip the confirmation prompt — required in a non-interactive session, since there is no terminal to ask")]
        public bool Yes { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
        ProjectAggregate aggregate = await session.Events.AggregateStreamAsync<ProjectAggregate>(project.Id, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No project {project.Id}.");

        if (aggregate.IsArchived)
        {
            throw new DomainValidationException(
                $"Project '{project.Name}' is already archived"
                + (aggregate.ArchivedAt is { } at ? $" (since {at:g})" : string.Empty)
                + $". Reactivate it: h9k project reactivate {project.Name}");
        }

        IReadOnlyList<TaskListItem> tasks = await session.Query<TaskListItem>()
            .Where(task => task.ProjectId == project.Id)
            .ToListAsync(cancellationToken);

        TaskListItem[] blocking = [.. tasks.Where(task => !IsInertUnderArchive(task.State))];
        if (blocking.Length > 0)
        {
            throw new DomainValidationException(
                $"Project '{project.Name}' cannot be archived: {blocking.Length} of its task(s) are in a "
                + "state the daemon may still act on — "
                + string.Join(", ", blocking.Select(task => $"{DomainId.Short(task.Id)} ({task.State.Value})"))
                + $". Resolve them first (h9k task show <id>) — unassign a queued or blocked one "
                + "(h9k task unassign), answer a needs-human one, retry or resolve a failed one — then "
                + "archive again.");
        }

        TaskListItem[] stayingAsIs = [.. tasks.Where(task => task.State == TaskState.Draft || task.State == TaskState.Published)];
        if (!Confirm(project, stayingAsIs, settings.Yes))
        {
            await Console.Error.WriteLineAsync(
                "Refusing to archive without confirmation — nothing was touched. Re-run with --yes to "
                + "skip the prompt in a non-interactive session.");
            return ExitCodes.Error;
        }

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        session.Events.Append(
            project.Id, ProjectDecider.Archive(aggregate, settings.Reason, DateTimeOffset.UtcNow, context.OwnerId));
        await session.SaveChangesAsync(cancellationToken);
        await Doorbell.RingAsync($"project-archived:{project.Id}", cancellationToken);

        AnsiConsole.MarkupLine($"[yellow]Project '{project.Name.EscapeMarkup()}' archived.[/] The dispatcher "
            + "will not claim its tasks, and the project-home render and auto-pr-review sweeps skip it. "
            + "This is this install's own record — a registration of the same repository on another node "
            + "is unaffected.");
        if (stayingAsIs.Length > 0)
        {
            AnsiConsole.MarkupLine(
                $"[dim]{stayingAsIs.Length} draft/unassigned task(s) stay exactly as they are, hidden with "
                + "the project.[/]");
        }

        AnsiConsole.MarkupLine(project.HomeDirectory.HasValue
            ? $"[dim]The home directory is left exactly as it is:[/] {project.HomeDirectory.Value.EscapeMarkup()}"
            : "[dim]No home directory was ever recorded for it.[/]");
        AnsiConsole.MarkupLine($"[dim]Reactivate any time:[/] h9k project reactivate {project.Name.EscapeMarkup()}");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The archive confirmation, naming what will happen — the drafts and unassigned published
    /// tasks that stay as they are, hidden with the project, exactly as the acceptance criteria
    /// require they be named. <c>--yes</c> skips it outright; a non-interactive session with no
    /// <c>--yes</c> refuses rather than guessing.
    /// </summary>
    private static bool Confirm(ProjectDetails project, IReadOnlyList<TaskListItem> stayingAsIs, bool yes)
    {
        if (yes)
        {
            return true;
        }

        AnsiConsole.MarkupLine(
            $"[yellow]Archiving '{project.Name.EscapeMarkup()}'[/] hides it from h9k project list, stops "
            + "the dispatcher claiming its tasks, and stops the project-home and auto-pr-review sweeps "
            + "visiting it. Nothing is deleted; the home directory on disk is untouched; this is reversible.");
        if (stayingAsIs.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[dim]{stayingAsIs.Count} draft/unassigned task(s) stay exactly as they are, hidden with "
                + $"the project:[/] {string.Join(", ", stayingAsIs.Select(task => DomainId.Short(task.Id)))}");
        }

        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            AnsiConsole.MarkupLine("[red]Refusing[/]: this session cannot prompt for confirmation. Pass --yes to proceed anyway.");
            return false;
        }

        return AnsiConsole.Confirm("Archive this project?", defaultValue: false);
    }
}
