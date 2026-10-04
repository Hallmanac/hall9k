using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Queued or Blocked -> Published: takes a task back out of the dispatcher's sight (Decisions
/// Log #34). Refused while a node holds the lease — that is a running agent, and pulling the
/// contract out from under it is the race the lifecycle exists to prevent. On a Draft, or a
/// Published task that is not queued, it only lets go of the assignee
/// (<see cref="TaskAssigneeCleared"/>), and nothing moves for the dispatcher.
/// </summary>
public sealed class TaskUnassignCommand : Hall9kAsyncCommand<TaskUnassignCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--reason <REASON>")]
        [Description(
            "Why the task is being taken back; recorded on TaskUnassigned and left unknown when "
            + "omitted, never inferred. Required with --holder, where it is also recorded as the "
            + "override's reason")]
        public string? Reason { get; init; }

        [CommandOption("--holder <NAME>")]
        [Description(
            "Another owner's task is theirs to unassign, so this refuses unless this node's owner may "
            + "act on it. An Owner-role member may unassign it on that owner's behalf by naming the "
            + "holder here (their label, which the refusal names, or at least 8 hex characters "
            + "of their root fingerprint; the word 'unknown' when the task's owner cannot be "
            + "resolved on this node) and giving --reason, both required together")]
        public string? Holder { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);

        // Fence before aggregating: reading the state and the lease is not the same instant as
        // appending. The dispatch loop claims with expectedVersion, so without a fence of our own
        // it wins the race and an unfenced TaskUnassigned lands on top of TaskClaimed — a task
        // whose replay says Published while a live agent works it, which is exactly the contract
        // pulled out from under a running agent this command refuses to do.
        StreamState? fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        // The lease document is the honest answer to "is a node running this right now":
        // it exists exactly while a claim is held, and the daemon's heartbeat keeps it.
        bool leaseHeld = await session.LoadAsync<TaskLease>(taskId, cancellationToken) is not null;

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        TaskOwnerOverrideDecision ownerDecision = await TaskOwnerGuard.AuthorizeAsync(
            session, task, context, "unassign", settings.Holder, settings.Reason, new GitLedgerChainReader(),
            new NodeKeyStore(), cancellationToken);
        bool overridden = ownerDecision.Outcome == TaskOwnerOverrideOutcome.Override;

        // Lets go of a hold on a task nothing has queued; a task with no assignee falls through to
        // TaskDecider.Unassign below, which says why there is nothing to do.
        if (ReleasesAssigneeOnly(task))
        {
            session.Events.Append(
                taskId, expectedVersion: fence.Version + 1,
                ClearAssignee(task, settings.Reason, context.OwnerId, ownerDecision, DateTimeOffset.UtcNow));
            await SaveAsync(
                session,
                $"Task {taskId} changed while letting go of it, so nothing was released. Check h9k task show, "
                + "then re-run this command if it still has an assignee.",
                cancellationToken);

            string clearedShortId = TaskListCommand.ShortId(taskId);
            AnsiConsole.MarkupLine(
                $"[blue]Task {clearedShortId} let go[/] - nobody holds it now, so it falls back to its creator.");
            TaskOwnerGuard.AnnounceOverride(ownerDecision, "unassigned");
            return ExitCodes.Ok;
        }

        TaskUnassigned unassigned = TaskDecider.Unassign(
            task, settings.Reason, leaseHeld, DateTimeOffset.UtcNow, context.OwnerId);
        if (overridden)
        {
            unassigned = unassigned with
            {
                OnBehalfOfOwnerRootFingerprint = ownerDecision.OnBehalfOfRootFingerprint,
                OverrideReason = ownerDecision.Reason,
            };
        }

        session.Events.Append(taskId, expectedVersion: fence.Version + 1, unassigned);
        await SaveAsync(
            session,
            $"Task {taskId} changed while unassigning — a node may have just claimed it. "
            + "Check h9k status; re-run this command only if it is still Queued or Blocked.",
            cancellationToken);

        string shortId = TaskListCommand.ShortId(taskId);
        AnsiConsole.MarkupLine($"[blue]Task {shortId} unassigned[/] — published again, and no node will claim it.");
        AnsiConsole.MarkupLine(
            $"[dim]To edit it:[/] h9k task draft {shortId} [dim]· to start it again:[/] h9k task assign {shortId}");
        TaskOwnerGuard.AnnounceOverride(ownerDecision, "unassigned");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Whether this unassign only lets go of a hold: a Draft, or a Published task that is not queued,
    /// that has an assignee. Anything queued unassigns through <see cref="TaskUnassigned"/>, which
    /// clears the owner it is queued for in the same event.
    /// </summary>
    internal static bool ReleasesAssigneeOnly(TaskAggregate task) =>
        task.State.IsPreDispatch && task.AssigneeOwnerId is not null;

    /// <summary>
    /// The event that lets go of the hold, stamped with the override when an Owner-role member did it
    /// to another owner's task (<c>--holder</c> with <c>--reason</c>).
    /// </summary>
    internal static TaskAssigneeCleared ClearAssignee(
        TaskAggregate task, string? reason, Guid clearedByOwnerId, TaskOwnerOverrideDecision ownerDecision,
        DateTimeOffset now)
    {
        TaskAssigneeCleared cleared = TaskDecider.ClearAssignee(task, reason, now, clearedByOwnerId);
        return ownerDecision.Outcome == TaskOwnerOverrideOutcome.Override
            ? cleared with
            {
                OnBehalfOfOwnerRootFingerprint = ownerDecision.OnBehalfOfRootFingerprint,
                OverrideReason = ownerDecision.Reason,
            }
            : cleared;
    }

    private static async Task SaveAsync(IDocumentSession session, string conflictMessage, CancellationToken cancellationToken)
    {
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(conflictMessage);
        }
    }
}
