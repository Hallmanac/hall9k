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
/// Queued or Blocked -> Published, keeping the assignee: stops the go and nothing else, so whoever
/// holds the task still does and <c>h9k task queue</c> puts it back. Refused while a node holds the
/// lease, for the reason <c>h9k task unassign</c> is: pulling the contract out from under a running
/// agent is the race the lifecycle exists to prevent. Recorded as a <see cref="TaskUnassigned"/> marked
/// <see cref="TaskUnassigned.KeepsAssignee"/>; <c>h9k task unassign</c> on a queued task is the same
/// event without the mark, which also lets go of the assignee.
/// </summary>
public sealed class TaskDequeueCommand : Hall9kAsyncCommand<TaskDequeueCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--reason <REASON>")]
        [Description(
            "Why the task is leaving the queue; recorded on TaskUnassigned and left unknown when "
            + "omitted, never inferred. Required with --holder, where it is also recorded as the "
            + "override's reason")]
        public string? Reason { get; init; }

        [CommandOption("--holder <NAME>")]
        [Description(
            "Another owner's task is theirs to dequeue, so this refuses unless this node's owner may "
            + "act on it. An Owner-role member may dequeue it on that owner's behalf by naming the "
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

        // Fenced before aggregating, for the reason h9k task unassign is: reading the state and the
        // lease is not the same instant as appending, and the dispatch loop claims with expectedVersion.
        StreamState? fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        bool leaseHeld = await session.LoadAsync<TaskLease>(taskId, cancellationToken) is not null;

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        TaskOwnerOverrideDecision ownerDecision = await TaskOwnerGuard.AuthorizeAsync(
            session, task, context, "dequeue", settings.Holder, settings.Reason, new GitLedgerChainReader(),
            new NodeKeyStore(), cancellationToken);

        TaskUnassigned dequeued = TaskDecider.Dequeue(
            task, settings.Reason, leaseHeld, DateTimeOffset.UtcNow, context.OwnerId);
        if (ownerDecision.Outcome == TaskOwnerOverrideOutcome.Override)
        {
            dequeued = dequeued with
            {
                OnBehalfOfOwnerRootFingerprint = ownerDecision.OnBehalfOfRootFingerprint,
                OverrideReason = ownerDecision.Reason,
            };
        }

        session.Events.Append(taskId, expectedVersion: fence.Version + 1, dequeued);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Task {taskId} changed while leaving the queue, so a node may have just claimed it. "
                + "Check h9k status; re-run this command only if it is still Queued or Blocked.");
        }

        string shortId = TaskListCommand.ShortId(taskId);
        AnsiConsole.MarkupLine(
            $"[blue]Task {shortId} dequeued[/]: published again and still assigned to its holder, and no node will claim it.");
        AnsiConsole.MarkupLine(
            $"[dim]To queue it again:[/] h9k task queue {shortId} [dim]· to edit it:[/] h9k task draft {shortId} "
            + $"[dim]· to let go of it:[/] h9k task unassign {shortId}");
        TaskOwnerGuard.AnnounceOverride(ownerDecision, "dequeued");
        return ExitCodes.Ok;
    }
}
