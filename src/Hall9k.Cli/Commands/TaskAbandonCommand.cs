using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

public sealed class TaskAbandonCommand : Hall9kAsyncCommand<TaskAbandonCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--reason <REASON>")]
        [Description(
            "Why this task is being walked away from; recorded on TaskAbandoned and left unknown "
            + "when omitted, never inferred (Decisions Log #27)")]
        public string? Reason { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        session.Events.Append(taskId, TaskDecider.Abandon(task, settings.Reason, DateTimeOffset.UtcNow, context.OwnerId));
        session.Delete<TaskLease>(taskId);

        // Otherwise an abandoned interactive claim's run reads Running forever: it holds no
        // TaskLease (an interactive claim writes none) and its NodeId is the Guid.Empty
        // sentinel, so neither AdoptOrphansAsync's NodeId filter nor SweepExpiredLeasesAsync's
        // lease scan will ever retire it — mirrors TaskReleaseCommand and TaskHandbackCommand's
        // own retirement of the run they displace (conformance review, cycle 4). Scoped to a
        // claim still sitting exactly where h9k task work left it (Dispatched or Running): once
        // h9k task deliver (or handback) has handed the run into the standard pipeline, that
        // pipeline owns the run's lifecycle and this command has no business retiring it out
        // from under it.
        if (task.State == TaskState.Claimed && task.IsInteractiveClaim && task.CurrentRunId is { } currentRunId)
        {
            RunDetails? run = await session.LoadAsync<RunDetails>(currentRunId, cancellationToken);
            if (run is not null && (run.State == RunState.Dispatched || run.State == RunState.Running))
            {
                session.Events.Append(currentRunId, new RunSuperseded(currentRunId, task.LeaseGeneration, DateTimeOffset.UtcNow));
            }
        }

        await session.SaveChangesAsync(cancellationToken);
        await Doorbell.RingAsync($"task-abandoned:{taskId}", cancellationToken);

        AnsiConsole.MarkupLine($"[dim]Task {taskId} abandoned.[/]");
        return ExitCodes.Ok;
    }
}
