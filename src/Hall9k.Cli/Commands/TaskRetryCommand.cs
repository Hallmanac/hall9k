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
/// The human's exit from Failed (Decisions Log #25): requeue a failed task so the daemon
/// dispatches a fresh run. The failure stays on the stream — retry appends, it never
/// erases — and the next run resumes the failed run's branch when it survives, or starts
/// clean from the base branch when the artifacts are gone. Human-only: no monitor drives
/// this path (never loop on judgment, log #11).
/// </summary>
public sealed class TaskRetryCommand : Hall9kAsyncCommand<TaskRetryCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--reason <REASON>")]
        [Description("Why the failure deserves another attempt — recorded on the stream and shown by h9k task show (defaults to a note that the retry was requested via this command)")]
        public string? Reason { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);

        // Fence before aggregating: without expectedVersion a duplicate retry racing the
        // dispatch loop could land after TaskClaimed and yank a claimed task back to
        // Queued — the double-run hazard the lease fencing exists to prevent.
        StreamState? fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        // The failed run's branch, as observed — null when the failure predates any run
        // record. The launcher resumes a surviving branch and starts clean otherwise.
        Guid? previousRunId = task.CurrentRunId;
        RunDetails? previousRun = previousRunId is { } runId
            ? await session.LoadAsync<RunDetails>(runId, cancellationToken)
            : null;
        string? branch = previousRun?.Branch.IsNotBlank() == true ? previousRun.Branch : null;

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        session.Events.Append(taskId, expectedVersion: fence.Version + 1, TaskDecider.Retry(
            task, previousRunId, branch,
            settings.Reason ?? "Retry requested via h9k task retry.",
            DateTimeOffset.UtcNow, context.OwnerId));
        // The failed run's own stream.jsonl, if it ever launched a headless session, is
        // otherwise never read back once this run is left behind by the retry — a start-it-mine
        // claim's only other read of it is h9k task deliver's own, which a failed run never
        // reaches (conformance review, cycle 1, on h9k task start).
        if (previousRun is not null)
        {
            HeadlessTokenRecovery.AppendIfRecorded(session, previousRun, DateTimeOffset.UtcNow);
        }

        session.Delete<TaskLease>(taskId);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Task {taskId} changed while retrying — check h9k status; re-run this command " +
                "only if the task is still Failed.");
        }
        await Doorbell.RingAsync($"task-retried:{taskId}", cancellationToken);

        // TaskAggregate.Apply(TaskRetried) never touches _unmetDependencies, only Assign does —
        // so a deliberately-claimed Blocked task (h9k task start --acknowledge-unmet-dependencies)
        // whose worktree cut failed can still name an open blocker here, landing Blocked rather
        // than Queued; no run dispatches until that blocker closes out (conformance review,
        // cycle 4).
        int unmetDependencyCount = task.UnmetDependencies.Count;
        if (unmetDependencyCount > 0)
        {
            string dependencyNoun = unmetDependencyCount == 1 ? "dependency" : "dependencies";
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Task {taskId} requeued, but {unmetDependencyCount} unmet {dependencyNoun} still name it Blocked — no run dispatches until those close out.[/]");
        }
        else if (branch is null)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Task {taskId} requeued — the next run starts clean from the base branch.[/]");
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Task {taskId} requeued — the next run resumes branch {branch} if it survives, or starts clean.[/]");
        }

        return ExitCodes.Ok;
    }
}
