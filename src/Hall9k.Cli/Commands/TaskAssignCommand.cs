using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The dispatch trigger (Decisions Log #34). Publishing says the task is ready; assigning
/// says it should run now, and on whose nodes. It is always a human's explicit act — the
/// platform never assigns on its own.
/// </summary>
public sealed class TaskAssignCommand : Hall9kAsyncCommand<TaskAssignCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandArgument(1, "[OWNER]")]
        [Description(
            "Owner whose nodes may claim the task: their name, an unambiguous fragment of it or of "
            + "their email, or their id. Omit it only when the platform has exactly one owner, "
            + "which is then who it goes to")]
        public string? Owner { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        OwnerDetails owner = settings.Owner.IsNotBlank()
            ? await OwnerResolver.ResolveAsync(session, settings.Owner, cancellationToken)
            : await OwnerResolver.SoleOwnerAsync(session, cancellationToken)
                ?? throw new DomainValidationException(
                    "More than one owner is registered, so who this task is for cannot be inferred. "
                    + "Name them: h9k task assign <id> <owner>");

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        TaskAssigned assigned = await AppendAsync(session, task, owner.Id, context.OwnerId, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        await Doorbell.RingAsync($"task-assigned:{taskId}", cancellationToken);

        await AnnounceAsync(assigned, owner, session, cancellationToken);
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Appends the assignment onto an open session (the caller saves), so h9k task publish can
    /// offer assignment in the same transaction it publishes in. Dependencies are read here
    /// rather than passed in: where the task lands — Queued or Blocked — is decided by whether
    /// each blocker has reached true closeout at this moment.
    /// </summary>
    internal static async Task<TaskAssigned> AppendAsync(
        IDocumentSession session,
        TaskAggregate task,
        Guid assignedOwnerId,
        Guid assignedByOwnerId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TaskDependency> dependencies = await TaskDependencyQuery.LoadAsync(
            session, task.BlockedBy, cancellationToken);
        TaskAssigned assigned = TaskDecider.Assign(
            task, assignedOwnerId, dependencies, DateTimeOffset.UtcNow, assignedByOwnerId);
        session.Events.Append(task.Id, assigned);
        return assigned;
    }

    /// <summary>Says which of the two landings happened, and what the blocked one is waiting on.</summary>
    internal static async Task AnnounceAsync(
        TaskAssigned assigned, OwnerDetails owner, IQuerySession session, CancellationToken cancellationToken)
    {
        string shortId = TaskListCommand.ShortId(assigned.Id);
        if (assigned.UnmetDependencies.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"[green]Task {shortId} assigned to {owner.Name.EscapeMarkup()}[/] — queued; "
                + "the next dispatch cycle on one of their nodes claims it.");
            return;
        }

        IReadOnlyList<TaskDependency> unmet = await TaskDependencyQuery.LoadAsync(
            session, assigned.UnmetDependencies, cancellationToken);
        AnsiConsole.MarkupLine(
            $"[yellow]Task {shortId} assigned to {owner.Name.EscapeMarkup()}[/] — blocked on "
            + $"{assigned.UnmetDependencies.Count} dependency(ies) that have not closed out:");
        foreach (TaskDependency dependency in unmet)
        {
            AnsiConsole.MarkupLine(
                $"  [dim]{TaskListCommand.ShortId(dependency.Id)}[/] "
                + $"{TaskListCommand.Truncate(ExternalText.OneLine(dependency.Objective), 60).EscapeMarkup()} "
                + $"[dim]({dependency.State.Value})[/]");
        }

        AnsiConsole.MarkupLine(
            "[dim]It queues itself the moment the last one's pull request merges — nothing else to do.[/]");
    }
}
