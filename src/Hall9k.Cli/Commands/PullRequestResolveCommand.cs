using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
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
/// The on-demand PR-closeout trigger (Decisions Log #20): reopen a done task so the daemon
/// dispatches a follow-up run on the task's existing pull-request branch to resolve review
/// feedback. The automatic monitor (backlog 04) drives this same reopen path.
/// </summary>
public sealed class PullRequestResolveCommand : Hall9kAsyncCommand<PullRequestResolveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TASK>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Task { get; init; } = string.Empty;

        [CommandOption("--reason <REASON>")]
        [Description("Why the follow-up is needed (defaults to a message matching the prompt: review comments, or failing checks with --checks)")]
        public string? Reason { get; init; }

        [CommandOption("--checks")]
        [Description("Dispatch the fix-the-CI prompt (the PR's checks are failing) instead of the resolve-review-comments prompt")]
        public bool Checks { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Task, cancellationToken);

        // Fence before aggregating: the closeout monitor also writes TaskReopened, so
        // the append below carries expectedVersion and loses loudly instead of stacking
        // a second reopen (and a second budget reset) on a concurrently reopened task.
        StreamState? fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        Guid previousRunId = task.CurrentRunId
            ?? throw new DomainConflictException($"Task {taskId} has no recorded run to follow up on.");
        RunDetails previousRun = await session.LoadAsync<RunDetails>(previousRunId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s run {previousRunId} has no run record.");

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        // A human-initiated reopen (Automatic: false) also resets the closeout monitor's
        // automatic follow-up budget — the human asking is a fresh grant (log #22).
        session.Events.Append(taskId, expectedVersion: fence.Version + 1, TaskDecider.Reopen(
            task, previousRunId, previousRun.Branch,
            settings.Reason ?? (settings.Checks
                ? "CI checks failing on the pull request."
                : "Unresolved review comments on the pull request."),
            settings.Checks ? FollowUpKind.FailingChecks : FollowUpKind.ReviewFeedback,
            automatic: false,
            DateTimeOffset.UtcNow, context.OwnerId));
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Task {taskId} changed while reopening — the closeout monitor likely just dispatched " +
                "a follow-up itself. Check h9k status; re-run this command only if the follow-up you " +
                "wanted is not already in flight.");
        }
        await Doorbell.RingAsync($"pr-resolve:{taskId}", cancellationToken);

        AnsiConsole.MarkupLineInterpolated(
            $"[dim]Task {taskId} reopened — a follow-up run will resume branch {previousRun.Branch} for {task.PullRequestUrl}.[/]");
        return ExitCodes.Ok;
    }
}
