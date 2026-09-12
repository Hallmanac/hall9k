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

        // Otherwise an abandoned claim's run reads Running (or Dispatched) forever from this
        // command's own point of view: an interactive claim holds no TaskLease (it writes none)
        // and its NodeId is the Guid.Empty sentinel, so neither AdoptOrphansAsync's NodeId filter
        // nor SweepExpiredLeasesAsync's lease scan will ever retire it — mirrors
        // TaskReleaseCommand and TaskHandbackCommand's own retirement of the run they displace
        // (conformance review, cycle 4). A daemon-dispatched headless claim does not have that
        // specific orphaning gap, but leaving it live here still cost several million tokens of
        // Opus review on abandoned work in one afternoon (origin incident 2026-08-27, task
        // ab484e89): the review engine's own GenerationFence check retires it eventually, on its
        // very next dispatch attempt, but this stamps the conclusion immediately instead of
        // waiting on that next poll. Scoped to a claim still sitting exactly where the run was
        // last dispatched or actively running: once the review loop has moved a headless run past
        // that (into Verifying, UnderReview, and beyond), the fence above is what retires it —
        // this command has no business racing that pipeline for a run already past Dispatched or
        // Running. Excludes a pr-review task outright, unlike the state check above:
        // PrReviewSentinelClaim's own IsLive branch names "h9k task abandon" as the honest way
        // out of a live sentinel run, so abandon must keep working there, but that run is
        // launched through RunLauncher and monitored by RunSupervisor exactly like an ordinary
        // headless dispatch — RunSupervisor.CompleteRunAsync appends its own TokensRecorded from
        // the identical stream.jsonl the moment the agent's result line lands, and its own
        // AgentSessionCompleted append later resurrects a run this command marked Superseded
        // (RunDetails.Apply unconditionally sets RunState.Verifying) — a cosmetic bounce the
        // terminal-task check every dispatch site now runs (task: abandoning a task halts its
        // in-flight run entirely) makes harmless, but recovering tokens twice for the same session
        // would still double-book them, so that stays scoped to the interactive lane below.
        if (task.State == TaskState.Claimed && task.Type != TaskType.PrReview
            && task.CurrentRunId is { } currentRunId)
        {
            RunDetails? run = await session.LoadAsync<RunDetails>(currentRunId, cancellationToken);
            if (run is not null && (run.State == RunState.Dispatched || run.State == RunState.Running))
            {
                DateTimeOffset supersededAt = DateTimeOffset.UtcNow;
                if (task.IsInteractiveClaim)
                {
                    // A start-it-mine claim abandoned mid-run had already spent tokens its own
                    // stream.jsonl is the only record of — otherwise never read back once this
                    // run is retired (conformance review, cycle 1, on h9k task start). A
                    // daemon-dispatched headless run's tokens are instead recorded normally by
                    // RunSupervisor.CompleteRunAsync's own TokensRecorded append once the agent
                    // process actually exits, so recovering them here too would double-book them.
                    HeadlessTokenRecovery.AppendIfRecorded(session, run, supersededAt);
                    HeadlessTokenRecovery.AppendDelegatedPhaseTokens(session, run, supersededAt);
                }

                session.Events.Append(currentRunId, new RunSuperseded(currentRunId, task.LeaseGeneration, supersededAt));
            }
        }

        await session.SaveChangesAsync(cancellationToken);
        await Doorbell.RingAsync($"task-abandoned:{taskId}", cancellationToken);

        AnsiConsole.MarkupLine($"[dim]Task {taskId} abandoned.[/]");
        return ExitCodes.Ok;
    }
}
