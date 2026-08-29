using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
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
/// Hand an interactive claim (h9k task work) to a headless agent partway through: the operator
/// is present to commit, so an uncommitted file refuses this the same way h9k task deliver
/// refuses on one, naming it. Releases the human claim and queues the task through normal
/// dispatch — mechanically the existing follow-up resume-existing-branch flow
/// (RunLauncher.CheckoutFreshOrRetryAsync reads the branch this records exactly as a
/// human-requested retry's surviving branch, Decisions Log #25), so the next headless run
/// continues from the branch state rather than starting clean.
/// </summary>
public sealed class TaskHandbackCommand : Hall9kAsyncCommand<TaskHandbackCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--reason <REASON>")]
        [Description("Why a headless agent is finishing this — recorded on the stream and carried into the follow-up's context")]
        public string? Reason { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        StreamState? fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        if (task.State != TaskState.Claimed || !task.IsInteractiveClaim || task.CurrentRunId is not { } runId)
        {
            throw new DomainConflictException(
                $"Task {taskId} is {task.State.Value} — only a task with an active interactive claim hands back this way.");
        }

        RunDetails run = await session.LoadAsync<RunDetails>(runId, cancellationToken)
            ?? throw new DomainConflictException($"Task {taskId} is claimed interactively but run {runId} has no record.");

        // An operator's own session, still attached in another terminal, owns this worktree right
        // now — handing it to a headless agent out from under it would double-book the same files
        // (adversarial review, cycle 1).
        InteractiveSessionLiveness.EnsureNotAttachedElsewhere(run, taskId, "hand back");

        // Mirrors TaskWorkCommand.ReenterAsync's own guard: once h9k task deliver hands the run
        // to the standard pipeline, the task can still read Claimed+interactive for the whole
        // review loop, so the state check above alone would let this requeue and re-dispatch a
        // headless agent into the very worktree the delivered run's gates and review sessions
        // are still reading (adversarial review, cycle 1).
        if (run.State != RunState.Dispatched && run.State != RunState.Running)
        {
            throw new DomainConflictException(
                $"Task {taskId}'s run {runId} is already {run.State.Value} — it was handed off with "
                + $"h9k task deliver and is now in the standard pipeline. h9k task show {taskId} "
                + "to see where it stands.");
        }

        (IReadOnlyList<string>? modified, _) = await InteractiveWorktreeGit.ListUncommittedFilesAsync(run.WorktreePath, cancellationToken);
        if (modified is null)
        {
            // Never guessed at as clean (InteractiveWorktreeGit's own contract): git could not
            // be asked, so the operator is told the check was skipped rather than handback
            // silently proceeding over a tree nobody actually looked at.
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Could not read the worktree's git status at {run.WorktreePath}; skipping the uncommitted-files check.[/]");
        }
        else if (modified.Count > 0)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]Task {taskId}'s worktree has uncommitted file(s); commit or discard them first:[/]");
            foreach (string file in modified)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]  {file}[/]");
            }

            return ExitCodes.Conflict;
        }

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        session.Events.Append(taskId, expectedVersion: fence.Version + 1, TaskDecider.HandBack(
            task, runId, run.Branch, settings.Reason, DateTimeOffset.UtcNow, context.OwnerId));
        // The next headless claim resumes the branch under a fresh run id (RunLauncher mints
        // one per launch); this run otherwise reads Running forever — it holds no TaskLease
        // and its NodeId is the Guid.Empty sentinel, so neither AdoptOrphansAsync's NodeId
        // filter nor SweepExpiredLeasesAsync's lease scan will ever retire it (conformance and
        // adversarial review, cycle 1).
        session.Events.Append(runId, new RunSuperseded(runId, task.LeaseGeneration + 1, DateTimeOffset.UtcNow));
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Task {taskId} changed while handing it back — check h9k status and try again.");
        }

        await Doorbell.RingAsync($"task-handed-back:{taskId}", cancellationToken);
        AnsiConsole.MarkupLineInterpolated(
            $"[dim]Task {taskId} handed back — the next headless run resumes branch {run.Branch}.[/]");
        return ExitCodes.Ok;
    }
}
