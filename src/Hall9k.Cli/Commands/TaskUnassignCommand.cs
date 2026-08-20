using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
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
/// contract out from under it is the race the lifecycle exists to prevent.
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
            + "omitted, never inferred")]
        public string? Reason { get; init; }
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
        session.Events.Append(taskId, expectedVersion: fence.Version + 1, TaskDecider.Unassign(
            task, settings.Reason, leaseHeld, DateTimeOffset.UtcNow, context.OwnerId));
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Task {taskId} changed while unassigning — a node may have just claimed it. " +
                "Check h9k status; re-run this command only if it is still Queued or Blocked.");
        }

        string shortId = TaskListCommand.ShortId(taskId);
        AnsiConsole.MarkupLine($"[blue]Task {shortId} unassigned[/] — published again, and no node will claim it.");
        AnsiConsole.MarkupLine(
            $"[dim]To edit it:[/] h9k task draft {shortId} [dim]· to start it again:[/] h9k task assign {shortId}");
        return ExitCodes.Ok;
    }
}
