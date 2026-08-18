using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Run.Projections;
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
/// The human-only exit from Failed (Decisions Log #25): move the task back to Queued for
/// another run without erasing why it failed. The daemon never takes this path itself —
/// a failure that repeats without human eyes is the never-loop-on-judgment rule (log #11).
/// The failed run's branch rides on the event so the launcher resumes surviving work;
/// gone artifacts mean a clean start from the base branch.
/// </summary>
public sealed class TaskRetryCommand : Hall9kAsyncCommand<TaskRetryCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--reason <REASON>")]
        [Description("Why this attempt should go differently (required — recorded on the stream, shown in h9k task show; the failure history stays visible either way)")]
        public string? Reason { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);

        // Fence before aggregating so the append below carries expectedVersion: a second
        // retry racing this one loses loudly instead of stacking a duplicate event.
        StreamState? fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        // The failed run's branch, when one was recorded at dispatch — an observed fact,
        // not a guarantee it still exists; the launcher checks survival at claim time.
        RunDetails? failedRun = task.CurrentRunId is { } runId
            ? await session.LoadAsync<RunDetails>(runId, cancellationToken)
            : null;
        string? branch = failedRun?.Branch.IsNotBlank() == true ? failedRun.Branch : null;

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        session.Events.Append(taskId, expectedVersion: fence.Version + 1, TaskDecider.Retry(
            task, settings.Reason ?? string.Empty, branch, DateTimeOffset.UtcNow, context.OwnerId));
        session.Delete<TaskLease>(taskId);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Task {taskId} changed while retrying — someone else likely retried it first. " +
                "Check h9k status; re-run this command only if the task is still Failed.");
        }
        await Doorbell.RingAsync($"task-retried:{taskId}", cancellationToken);

        if (branch is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]Task {taskId} retried — queued for a clean run from the base branch.[/]");
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Task {taskId} retried — queued; branch {branch} resumes if it survives, else the run starts clean.[/]");
        }
        return ExitCodes.Ok;
    }
}
