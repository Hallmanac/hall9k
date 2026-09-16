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
            settings.Reason ?? TaskDecider.DefaultRetryReason,
            DateTimeOffset.UtcNow, context.OwnerId));
        // The failed run's own stream.jsonl, if it ever launched a headless session, is
        // otherwise never read back once this run is left behind by the retry — a start-it-mine
        // claim's only other read of it is h9k task deliver's own, which a failed run never
        // reaches (conformance review, cycle 1, on h9k task start). Scoped to the Guid.Empty
        // NodeId sentinel a start-it-mine (or h9k task work) claim's RunDispatched carries and
        // never loses unless h9k task deliver hands it a real node id: an ordinary daemon-dispatched
        // run already carries a real NodeId from the start and already had RunSupervisor.
        // CompleteRunAsync append its TokensRecorded from this exact stream.jsonl line when it
        // completed, so re-appending here for that run double-books its spend under today's date
        // and can hold the node's spend budget closed on tokens it never actually burned this
        // period (conformance and adversarial review, cycle 3). A pr-review task's own
        // Claimed+sentinel state is never this start-it-mine shape — TaskWorkCommand and
        // TaskStartCommand both refuse to create one — it is auto-pr-review's now-speed deliberate
        // claim (AutoPrReviewEngine.CreateOneAsync), launched through RunLauncher and monitored by
        // RunSupervisor exactly like an ordinary headless dispatch, so CompleteRunAsync already
        // appended its TokensRecorded from this same stream.jsonl when the run failed; recovering
        // it again here is the identical double-booking this whole guard exists to prevent
        // (independent pre-PR review, cycle 1, adversarial lens).
        if (previousRun is not null && previousRun.NodeId == Guid.Empty && task.Type != TaskType.PrReview)
        {
            HeadlessTokenRecovery.AppendIfRecorded(session, previousRun, DateTimeOffset.UtcNow);
            HeadlessTokenRecovery.AppendDelegatedPhaseTokens(session, previousRun, DateTimeOffset.UtcNow);
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
        // Stated here, not verified: only RunLauncher's own live read of the branch — at the moment
        // it actually matters, on both origin and the local ref in the reused worktree — can say
        // the pushed tip still holds, and this command has no worktree of its own to ask (AGENTS.md's
        // never-guess rule). It also can't observe whether the failed run's worktree still exists on
        // whichever node dispatches next, or whether that run's own branch still names the retry
        // branch — RunLauncher checks both before it ever reads a tip. What is observed already, on
        // the stream, is the failed run's own step, this task's still-blank pull request, and the tip
        // this task last pushed — enough to say which path the daemon means to take and why, with
        // every condition this command cannot itself verify named alongside it as a caveat rather
        // than asserted as fact (conformance review, cycle 1).
        bool resumesAtPullRequestOpen = task.PullRequestUrl.IsBlank()
            && previousRun?.FailedDuringPullRequestOpen == true
            && branch is not null
            && task.LastPushedBranch == branch
            && task.LastPushedBranchTip is not null;
        if (unmetDependencyCount > 0)
        {
            string dependencyNoun = unmetDependencyCount == 1 ? "dependency" : "dependencies";
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Task {taskId} requeued, but {unmetDependencyCount} unmet {dependencyNoun} still name it Blocked — no run dispatches until those close out.[/]");
        }
        else if (resumesAtPullRequestOpen)
        {
            // "was pushed" deliberately does not say the FAILED run itself pushed it — the flag
            // also covers a push refusal (conformance review, cycle 1, low): branch's own recorded
            // tip can belong to an earlier run, with this one having failed before ever reaching
            // origin. Either way, the tip named here is the one already on record and already
            // reviewed, and that is what the next run re-opens against.
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Task {taskId} requeued — the last run failed only at opening the pull request, after branch {branch}'s build and review had already settled, so the next run means to re-attempt the pull-request open directly against that same branch, with no build or review session. That still depends on the failed run's worktree surviving, its own branch still matching, and the branch's tip not having moved on origin or in that worktree since — this command can't check any of those, so it falls back to a full build instead if one doesn't hold.[/]");
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
