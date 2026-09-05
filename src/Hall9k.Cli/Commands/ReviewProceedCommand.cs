using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The bare-proceed lever for interactive mode's own routine phase boundaries (task: interactive
/// mode becomes a recorded property of the task, design rulings R2, R5, R9): a boundary with no
/// dispute needs only this — no verdict, no reason, just the human's recorded go to continue
/// exactly where the park interrupted the loop. <c>h9k review resolve --merge-ready</c>/
/// <c>--needs-fixes</c> keeps its exact existing meaning alongside this command for a boundary the
/// human wants to redirect instead of merely approve (the review-verdict-to-fix boundary is the
/// one where that actually applies); this command is refused outright on a run parked for a
/// genuine dispute or a cap/budget reason, which still take only <c>h9k review resolve</c>. Usable
/// from any door — the operator's own session, the orchestrator window, or a bare terminal — and
/// the boundary advances identically whichever one it came through, since the daemon's resume
/// sweep is what actually continues the loop once this appends.
/// </summary>
public sealed class ReviewProceedCommand : Hall9kAsyncCommand<ReviewProceedCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TASK>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Task { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Task, cancellationToken);
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        Guid runId = task.CurrentRunId
            ?? throw new DomainConflictException(
                $"Task {taskId} has no current run — nothing is review-parked here.");

        // Fence before aggregating, the h9k review resolve manner: the append below carries
        // expectedVersion so a proceed racing the daemon (or a duplicate invocation) loses loudly
        // instead of stacking a second approval.
        StreamState? fence = await session.Events.FetchStreamStateAsync(runId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s run {runId} has no run stream.");
        RunAggregate run = await session.Events.AggregateStreamAsync<RunAggregate>(
                runId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s run {runId} has no run stream.");

        if (run.State != RunState.ReviewParked)
        {
            throw new DomainConflictException(
                $"Task {taskId}'s current run is {run.State.Value}, not ReviewParked — only a " +
                "review-parked run takes a proceed. (A parked pull request is resolved with h9k pr resolve instead.)");
        }

        if (!run.ParkedIsInteractiveGate)
        {
            throw new DomainConflictException(
                $"Task {taskId}'s park is not one of interactive mode's own routine boundaries — h9k review " +
                "proceed only clears one of those. h9k review resolve --merge-ready or --needs-fixes \"<reason>\" " +
                "is the lever for this park.");
        }

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        session.Events.Append(runId, expectedVersion: fence.Version + 1, new ReviewBoundaryApproved(
            runId, DateTimeOffset.UtcNow, context.OwnerId));

        // The run is no longer parked, so the sweep's parked-run shield no longer covers this
        // lease; a fresh heartbeat holds the task while the daemon wakes (mirrors h9k review resolve).
        TaskLease? lease = await session.LoadAsync<TaskLease>(taskId, cancellationToken);
        if (lease is not null)
        {
            lease.HeartbeatAt = DateTimeOffset.UtcNow;
            session.Store(lease);
        }

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Run {runId} changed while proceeding — the daemon (or another proceed/resolve) got there " +
                "first. Check h9k status; re-run this command only if the run is still ReviewParked.");
        }

        await Doorbell.RingAsync($"review-proceed:{taskId}", cancellationToken);
        AnsiConsole.MarkupLineInterpolated(
            $"[dim]Task {taskId} proceeds — the daemon continues the review loop exactly where it parked.[/]");
        return ExitCodes.Ok;
    }
}
