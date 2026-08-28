using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Record the GitHub issue a task belongs to, from what <c>gh</c> reports rather than from what
/// anybody claims — the same observation gate <see cref="TaskLinkJiraCommand"/> is (backlog:
/// every published task is tracked automatically). An issue's number is read back through gh
/// before anything is written, so a number that was mistyped or does not exist writes nothing.
/// <para>
/// Usable by a human linking an issue made by hand, and reused internally by
/// <see cref="TaskPublishCommand"/> for a project whose backlog policy is github-issues: the
/// platform authors the issue itself there (an issue's shape is uniform enough to render
/// deterministically), but the claim its own <c>gh issue create</c> call makes is verified
/// through this exact same read-back-and-decide path rather than trusted, so there is one way an
/// issue becomes a task's external reference regardless of who created it.
/// </para>
/// </summary>
public sealed class TaskLinkIssueCommand : Hall9kAsyncCommand<TaskLinkIssueCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TASK>")]
        [Description("Task id (full, or an unambiguous prefix)")]
        public string Task { get; init; } = string.Empty;

        [CommandArgument(1, "<ISSUE>")]
        [Description(
            "The GitHub issue: its number (42 or #42), the owner/repo#42 shorthand, or its URL. Hall9k "
            + "reads it through gh before recording anything, so this is an issue to be checked rather "
            + "than a fact to be accepted — if it does not resolve, nothing is written and the error "
            + "says what to look at")]
        public string Issue { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Task, cancellationToken);

        // ProjectId never changes once a task exists, so reading it ahead of the fence below is
        // safe: it is only here to give gh a directory to resolve a bare issue number against.
        TaskDetails details = await session.LoadAsync<TaskDetails>(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        ProjectDetails project = await session.LoadAsync<ProjectDetails>(details.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId} names a project that is not registered.");

        // Read before deciding, the link-jira order: gh is asked before the task is fenced, so an
        // issue that does not resolve costs nothing and teaches something, whatever state the task
        // happens to be in.
        ImportedWorkItem issue = await new GitHubWorkItemProvider().ImportAsync(
            new WorkItemImportRequest(WorkItemProvider.GitHub, settings.Issue, project.RepositoryPath),
            cancellationToken);

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        LinkOutcome outcome = await LinkAsync(session, taskId, issue, context.OwnerId, cancellationToken);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Task {taskId} changed while {issue.Reference} was being read back from GitHub, so "
                + "nothing was linked. The issue is untouched either way — this command only ever reads "
                + "gh. Check h9k task show and run h9k task link-issue again if the task should still "
                + "carry it.");
        }

        if (outcome == LinkOutcome.AlreadyLinked)
        {
            AnsiConsole.MarkupLine(
                $"[green]Already linked[/] — task {TaskListCommand.ShortId(taskId)} carries "
                + $"{issue.Reference.ToString().EscapeMarkup()}. [dim]Nothing to do.[/]");
            return ExitCodes.Ok;
        }

        AnsiConsole.MarkupLine(
            $"[green]Linked[/] task {TaskListCommand.ShortId(taskId)} to "
            + $"{issue.Reference.ToString().EscapeMarkup()}: "
            + $"{ExternalText.OneLineMarkup(issue.Title)}");
        AnsiConsole.MarkupLine(TaskLinkJiraCommand.ObservationMarkup(issue));
        return ExitCodes.Ok;
    }

    /// <summary>What appending the link came to, for a caller that has no terminal to print to (h9k task publish).</summary>
    internal enum LinkOutcome
    {
        AlreadyLinked,
        Linked,
    }

    /// <summary>
    /// Fence, decide, and append onto an already-open session — the caller saves, the
    /// <see cref="TaskAssignCommand.AppendAsync"/> shape — so <see cref="TaskPublishCommand"/> can
    /// link the issue it just created without a second round trip to the store. The fence is taken
    /// here rather than passed in, deliberately: this always runs after the slow gh call that
    /// produced <paramref name="issue"/>, and the whole point of fencing there instead of earlier
    /// is that nothing between "gh answered" and "the event is appended" is trusted to still be true.
    /// </summary>
    internal static async Task<LinkOutcome> LinkAsync(
        IDocumentSession session,
        Guid taskId,
        ImportedWorkItem issue,
        Guid linkedByOwnerId,
        CancellationToken cancellationToken)
    {
        StreamState fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        if (TaskDecider.AlreadyLinkedTo(task, issue.Reference))
        {
            return LinkOutcome.AlreadyLinked;
        }

        // A publication request outstanding for another provider (h9k task push-to-jira, run by
        // hand or by TaskPublishCommand.TrackInBacklogAsync against a project bound to a Jira
        // board) is a session already writing this task's one external item. Linking this issue
        // on top of it would clear PendingPublicationProvider from under that session
        // (TaskAggregate.Apply(WorkItemLinked)), so CardPublicationEngine stops watching it,
        // WorkItemPublicationCompleted never gets appended, and the session's own h9k task
        // link-jira is refused once it finishes because the task now carries this GitHub
        // reference instead — the card it wrote ends up orphaned with nothing recording or
        // cleaning it up. Checked here, on the fenced aggregate, so both this command's own
        // human-facing entry point and TrackInBacklogAsync's internal call are covered by the one
        // guard rather than only the caller that remembers to ask first.
        if (task.PendingPublicationProvider is { } pending)
        {
            throw new DomainConflictException(
                $"Task {taskId} has a {pending.Value} publication request outstanding"
                + (task.PublicationSessionDispatched ? " and its session is running" : " and is waiting for the daemon")
                + $". Linking {issue.Reference} now would strand that session's card with nothing to record "
                + $"or clean it up. Wait for it to finish, or watch it with h9k task show {taskId}.");
        }

        await TaskAddCommand.RefuseSecondAdoptionAsync(session, issue.Reference, cancellationToken);

        WorkItemLinked linked = TaskDecider.LinkWorkItem(
            task, issue.Reference, issue.Title, issue.Status.ToString(), issue.ObservedAt,
            DateTimeOffset.UtcNow, linkedByOwnerId);
        session.Events.Append(taskId, expectedVersion: fence.Version + 1, linked);
        return LinkOutcome.Linked;
    }
}
