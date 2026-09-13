using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run.Projections;
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
/// as archived, reactivated, and renamed; Decisions Log #182). Kept named "remove"
/// per Brian's own ruling (2026-09-12): what it does is archive, but the escape hatch a stray
/// registration needs is spelled the way an operator reaches for it.
/// <para>
/// <c>--purge</c> (task: an archived project can be purged — the second half of the two-tier
/// design) archives the project if it is not already, then schedules a permanent hard delete of
/// its database footprint 24 hours out (<see cref="ProjectPurge.GracePeriod"/>): the project's own
/// stream, every task, run, and idea stream it owns, and their projection documents. Linked
/// tracker items, the repository, and the home directory on disk are outside its scope; the
/// confirmation and every message here say so. This is the one explicit exception to the
/// platform's nothing-is-deleted doctrine (Brian's ruling, 2026-08-29) — without <c>--purge</c>
/// nothing this command does is ever destructive.
/// </para>
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
    /// act on, and archiving over it would leave live work orphaned mid-flight. A Done task with a
    /// still-open pull request is admitted here on the strength of <c>CloseoutEngine</c> itself
    /// skipping every archived project's tasks (the same skip the render and auto-pr-review sweeps
    /// already had) rather than this predicate refusing over it — see the archived-project checks
    /// beside every <c>ProjectDetails</c> load in <c>CloseoutEngine</c> (independent pre-PR review,
    /// cycle 1, adversarial lens: this predicate's own claim was true only once that skip existed).
    /// The same predicate gates <c>--purge</c> (the acceptance criteria's own words: "the same
    /// refusal as archive applies"), checked fresh every time regardless of whether the project was
    /// archived moments ago or long before — a purge never assumes yesterday's archive check still
    /// holds.
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

        [CommandOption("--purge")]
        [Description(
            "Archive the project (or accept one already archived) and schedule a permanent hard "
            + "delete of its database footprint 24 hours from now: the project's own stream, every "
            + "task, run, and idea stream it owns, and their projection documents. Linked tracker "
            + "items, the repository, and the home directory on disk are never touched by this. "
            + "Cancellable any time before it fires: h9k project cancel-purge <project>. Without "
            + "this flag, nothing is ever deleted.")]
        public bool Purge { get; init; }

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

        if (aggregate.IsArchived && !settings.Purge)
        {
            throw new DomainValidationException(
                $"Project '{project.Name}' is already archived"
                + (aggregate.ArchivedAt is { } at ? $" (since {at:g})" : string.Empty)
                + $". Reactivate it: h9k project reactivate {project.Name}, or schedule a permanent "
                + $"delete: h9k project remove {project.Name} --purge");
        }

        if (settings.Purge && aggregate.PurgeAt is { } existingDeadline)
        {
            throw new DomainValidationException(
                $"Project '{project.Name}' already has a purge scheduled for {existingDeadline:g}. "
                + $"Cancel it first: h9k project cancel-purge {project.Name}");
        }

        IReadOnlyList<TaskListItem> tasks = await session.Query<TaskListItem>()
            .Where(task => task.ProjectId == project.Id)
            .ToListAsync(cancellationToken);

        TaskListItem[] blocking = [.. tasks.Where(task => !IsInertUnderArchive(task.State))];
        if (blocking.Length > 0)
        {
            string verb = settings.Purge ? "purged" : "archived";
            throw new DomainValidationException(
                $"Project '{project.Name}' cannot be {verb}: {blocking.Length} of its task(s) are in a "
                + "state the daemon may still act on — "
                + string.Join(", ", blocking.Select(task => $"{DomainId.Short(task.Id)} ({task.State.Value})"))
                + $". Resolve them first (h9k task show <id>) — unassign a queued or blocked one "
                + "(h9k task unassign), answer a needs-human one, retry or resolve a failed one — then "
                + $"{(settings.Purge ? "purge" : "archive")} again.");
        }

        if (settings.Purge)
        {
            return await ExecutePurgeAsync(session, project, aggregate, tasks, settings, cancellationToken);
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
            + "will not claim its tasks, and the project-home render, closeout, and auto-pr-review sweeps "
            + "skip it. This is this install's own record — a registration of the same repository on "
            + "another node is unaffected.");
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
            + "the dispatcher claiming its tasks, and stops the project-home, closeout, and auto-pr-review "
            + "sweeps visiting it. Nothing is deleted; the home directory on disk is untouched; this is "
            + "reversible.");
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

    /// <summary>
    /// The <c>--purge</c> path: archives the project first if it is not already (in the same
    /// transaction as the schedule itself, so both commit together or neither does), then appends
    /// <see cref="ProjectPurgeScheduled"/>. The scope named in the confirmation and the final
    /// message — every task, run, and idea this purge will destroy — is counted here, the same
    /// division the archive-only path already draws between the pure decider and the database
    /// query only a command can run.
    /// </summary>
    private static async Task<int> ExecutePurgeAsync(
        IDocumentSession session, ProjectDetails project, ProjectAggregate aggregate,
        IReadOnlyList<TaskListItem> tasks, Settings settings, CancellationToken cancellationToken)
    {
        Guid[] taskIds = [.. tasks.Select(task => task.Id)];
        int runCount = taskIds.Length == 0
            ? 0
            : await session.Query<RunListItem>().Where(run => taskIds.Contains(run.TaskId)).CountAsync(cancellationToken);
        int ideaCount = await session.Query<IdeaDetails>()
            .Where(idea => idea.ProjectId == project.Id)
            .CountAsync(cancellationToken);

        DateTimeOffset scheduledAt = DateTimeOffset.UtcNow;
        DateTimeOffset deadline = scheduledAt + ProjectPurge.GracePeriod;

        if (!ConfirmPurge(project, tasks.Count, runCount, ideaCount, deadline, settings.Yes))
        {
            await Console.Error.WriteLineAsync(
                "Refusing to purge without confirmation — nothing was touched. Re-run with --yes to "
                + "skip the prompt in a non-interactive session.");
            return ExitCodes.Error;
        }

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        if (!aggregate.IsArchived)
        {
            ProjectArchived archived = ProjectDecider.Archive(aggregate, settings.Reason, scheduledAt, context.OwnerId);
            session.Events.Append(project.Id, archived);
            aggregate.Apply(archived);
        }

        session.Events.Append(project.Id, ProjectDecider.SchedulePurge(aggregate, scheduledAt, context.OwnerId));
        await session.SaveChangesAsync(cancellationToken);
        await Doorbell.RingAsync($"project-purge-scheduled:{project.Id}", cancellationToken);

        AnsiConsole.MarkupLine(
            $"[red]Project '{project.Name.EscapeMarkup()}' scheduled for permanent deletion at "
            + $"{deadline.ToLocalTime():g}.[/] {tasks.Count} task(s), {runCount} run(s), and {ideaCount} "
            + "idea(s) will be permanently destroyed from this install's own database — the project "
            + "stream and every task, run, and idea stream it owns, with their projection documents. "
            + "Linked tracker items, the repository, and the home directory are outside this operation's "
            + "scope and are never touched.");
        AnsiConsole.MarkupLine(project.HomeDirectory.HasValue
            ? $"[dim]The home directory stays exactly where it is:[/] {project.HomeDirectory.Value.EscapeMarkup()}"
            : "[dim]No home directory was ever recorded for it.[/]");
        AnsiConsole.MarkupLine($"[dim]Cancel any time before then:[/] h9k project cancel-purge {project.Name.EscapeMarkup()}");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The purge confirmation: the scope in numbers and the deadline, exactly as the acceptance
    /// criteria require, plus what is deliberately out of scope so nobody reads a database-only
    /// operation as touching the repository or the tracker. <c>--yes</c> skips it outright for
    /// non-interactive use; a non-interactive session with no <c>--yes</c> refuses rather than
    /// guessing — the same discipline <see cref="Confirm"/> already follows for a plain archive.
    /// </summary>
    private static bool ConfirmPurge(
        ProjectDetails project, int taskCount, int runCount, int ideaCount, DateTimeOffset deadline, bool yes)
    {
        if (yes)
        {
            return true;
        }

        AnsiConsole.MarkupLine(
            $"[red]Purging '{project.Name.EscapeMarkup()}'[/] archives it if it is not already, then "
            + $"permanently destroys {taskCount} task(s), {runCount} run(s), and {ideaCount} idea(s) — "
            + "every stream, event, and projection document this install's database holds for the "
            + $"project and everything it owns — at {deadline.ToLocalTime():g}, 24 hours from now. "
            + "Linked tracker items, the repository, and the home directory on disk are outside this "
            + "operation's scope; they are never touched. This cannot be undone once it fires.");

        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            AnsiConsole.MarkupLine("[red]Refusing[/]: this session cannot prompt for confirmation. Pass --yes to proceed anyway.");
            return false;
        }

        return AnsiConsole.Confirm("Schedule this project for permanent deletion?", defaultValue: false);
    }
}
