using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
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
/// Record the Linear issue a task belongs to, from what Linear says rather than from what anybody
/// claims: the sibling of <see cref="TaskLinkJiraCommand"/> and the same observation gate. The
/// identifier or URL is an argument to be checked, and what is written down is the identifier Linear
/// answered with, so <c>eng-123</c>, the bare identifier, and the issue URL all land on one reference.
/// <para>
/// It accepts an issue from any team the key can see, and it applies no state gate: this door
/// records an issue that already exists, whatever state its team's workflow has it in, and the
/// state it observed rides on the link.
/// </para>
/// <para>
/// It is also how a stale link is repaired. Moving an issue to another team gives it a new
/// identifier, so a task's recorded <c>linear:</c> reference stops resolving. A task carries one
/// external item and the platform has no unlink, so the second link is refused unless Linear's own
/// answer for the new identifier lists the recorded one among the issue's previous identifiers,
/// which proves the two name one issue. A branch already recorded on a run is not renamed.
/// </para>
/// </summary>
public sealed class TaskLinkLinearCommand : Hall9kAsyncCommand<TaskLinkLinearCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TASK>")]
        [Description("Task id (full, or an unambiguous prefix)")]
        public string Task { get; init; } = string.Empty;

        [CommandArgument(1, "<IDENTIFIER>")]
        [Description(
            "The Linear issue: its identifier (ENG-123) or its URL. Hall9k reads it through the registered "
            + "connection before recording anything, so this is an identifier to be checked rather than a fact "
            + "to be accepted, and what is recorded is the identifier Linear answers with. An issue from any "
            + "team the key can see is accepted; a URL from a different workspace than the registered one is "
            + "refused, because a stored reference records no workspace. If the task already carries a Linear "
            + "issue that has since moved to another team, pass the new identifier here to replace it: that "
            + "works only when Linear lists the recorded identifier among the issue's previous ones")]
        public string Identifier { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Task, cancellationToken);

        LinearWorkItemProvider provider = await WorkItemConnections.LinearProviderAsync(session, cancellationToken);
        LinearIssueIdentifier asked = provider.ParseIdentifier(settings.Identifier);

        // When the typed identifier is the one the task already carries, this read is of a linked
        // issue, and a not-found answer is the moved-to-another-team case with its own remedy.
        TaskAggregate current = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        string? linkedTask = current.ExternalReference is { } carried
            && carried.Provider == WorkItemProvider.Linear
            && carried.Reference.Equals(asked.Value, StringComparison.OrdinalIgnoreCase)
                ? TaskListCommand.ShortId(taskId)
                : null;

        // Read before deciding, and the read is of the issue as itself: no adoption gate applies to a
        // door that records an issue that already exists.
        LinearIssueRead read = await provider.ReadIssueAsync(asked, linkedTask, cancellationToken);
        ImportedWorkItem issue = read.Item;

        // Fence after the read, as link-jira does: reading is a request to somebody else's service
        // with a deadline on it, and every guard below is about the task as it is now.
        StreamState fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        if (TaskDecider.AlreadyLinkedTo(task, issue.Reference))
        {
            AnsiConsole.MarkupLine(
                $"[green]Already linked[/]: task {TaskListCommand.ShortId(taskId)} carries "
                + $"{issue.Reference.ToString().EscapeMarkup()}. [dim]Nothing to do.[/]");
            return ExitCodes.Ok;
        }

        // One item, one live task: the same rule --from-linear and the Jira doors enforce, applied to
        // this door into the same field.
        await TaskAddCommand.RefuseSecondAdoptionAsync(
            session, issue.Reference, new TaskAddCommand.AdoptionSite(task.ProjectId, task.EpicId),
            ownerRootFingerprint: null, cancellationToken);

        ExternalReference? replaced = task.ExternalReference;
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        WorkItemLinked linked = TaskDecider.LinkWorkItem(
            task,
            issue.Reference,
            issue.Title,
            issue.Status.ToString(),
            issue.ObservedAt,
            DateTimeOffset.UtcNow,
            context.OwnerId,
            read.PreviousIdentifiers);

        session.Events.Append(taskId, expectedVersion: fence.Version + 1, linked);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Task {taskId} changed while {issue.Reference} was being read back from Linear, so "
                + "nothing was linked. The issue is untouched either way: this command only ever reads "
                + "Linear. Check h9k task show and run h9k task link-linear again if the task should still "
                + "carry it.");
        }

        AnsiConsole.MarkupLine(
            $"[green]Linked[/] task {TaskListCommand.ShortId(taskId)} to "
            + $"{issue.Reference.ToString().EscapeMarkup()}: "
            + $"{ExternalText.OneLineMarkup(issue.Title)}");
        if (replaced is not null)
        {
            AnsiConsole.MarkupLine(
                $"[dim]  replaced {replaced.ToString().EscapeMarkup()}, which Linear lists as a previous "
                + "identifier of this issue. A branch already cut for this task keeps the name it has.[/]");
        }

        AnsiConsole.MarkupLine(ObservationMarkup(issue));
        if (provider.WebUrl(issue.Reference) is { } url)
        {
            AnsiConsole.MarkupLine($"[dim]  {url.ToString().EscapeMarkup()}[/]");
        }

        return ExitCodes.Ok;
    }

    /// <summary>
    /// What was observed, and when, said as the one reading it is: Hall9k does not watch the issue
    /// afterwards, so a state with no stamp beside it would read as the issue's current state. The state
    /// is Linear's own workflow text, so it goes through <see cref="ExternalText"/>.
    /// </summary>
    internal static string ObservationMarkup(ImportedWorkItem issue) =>
        $"[dim]  {ExternalText.OneLineMarkup(issue.Status.ToString())} when read at {issue.ObservedStamp}; "
        + "Hall9k took one reading and does not watch the issue afterwards.[/]";
}
