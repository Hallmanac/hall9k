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
/// The human's unpark lever for the pre-PR review loop (Decisions Log #24 deferred it;
/// its absence left a parked run with no path forward except abandonment): record a
/// human verdict on a review-parked run. --merge-ready sends the run on to its pull
/// request; --needs-fixes dispatches a fix session with the stated reason as its
/// findings and, like h9k pr resolve, restores the automatic fix budget — the human
/// asking is a fresh grant.
/// </summary>
public sealed class ReviewResolveCommand : Hall9kAsyncCommand<ReviewResolveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TASK>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Task { get; init; } = string.Empty;

        [CommandOption("--merge-ready")]
        [Description("Your verdict: the diff is sound — the run proceeds to open its pull request")]
        public bool MergeReady { get; init; }

        [CommandOption("--needs-fixes <REASON>")]
        [Description("Your verdict: the stated defects are real — a fix session is dispatched with this reason as its findings")]
        public string? NeedsFixes { get; init; }

        public override ValidationResult Validate() =>
            MergeReady == NeedsFixes.IsNotBlank()
                ? ValidationResult.Error(
                    "Pass exactly one verdict: --merge-ready, or --needs-fixes <reason>. " +
                    "The park exists because the platform refused to guess; this command records " +
                    "YOUR judgment (PLAN.md log #24).")
                : ValidationResult.Success();
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

        // Fence before aggregating (the h9k pr resolve manner): the append below carries
        // expectedVersion so a resolve racing the daemon (or a duplicate invocation)
        // loses loudly instead of stacking a second verdict.
        StreamState? fence = await session.Events.FetchStreamStateAsync(runId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s run {runId} has no run stream.");
        RunAggregate run = await session.Events.AggregateStreamAsync<RunAggregate>(
                runId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s run {runId} has no run stream.");

        if (run.State != RunState.ReviewParked)
        {
            throw new DomainConflictException(
                $"Task {taskId}'s current run is {run.State.Value}, not ReviewParked — only a " +
                "review-parked run takes a human verdict. (A parked pull request is resolved " +
                "with h9k pr resolve instead.)");
        }

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        ReviewVerdict verdict = settings.MergeReady ? ReviewVerdict.MergeReady : ReviewVerdict.NeedsFixes;
        session.Events.Append(runId, expectedVersion: fence.Version + 1, new ReviewParkResolved(
            runId, verdict, settings.NeedsFixes, DateTimeOffset.UtcNow, context.OwnerId));

        // The run is no longer parked, so the sweep's parked-run shield no longer covers
        // this lease; a fresh heartbeat holds the task while the daemon wakes.
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
                $"Run {runId} changed while resolving — the daemon (or another resolve) got there " +
                "first. Check h9k status; re-run this command only if the run is still ReviewParked.");
        }
        await Doorbell.RingAsync($"review-resolve:{taskId}", cancellationToken);

        AnsiConsole.MarkupLineInterpolated(settings.MergeReady
            ? (FormattableString)$"[dim]Run {runId} resolved merge-ready — the daemon resumes it and opens the pull request.[/]"
            : $"[dim]Run {runId} resolved needs-fixes — the daemon dispatches a fix session with your reason as its findings.[/]");
        return ExitCodes.Ok;
    }
}
