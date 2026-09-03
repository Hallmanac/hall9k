using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
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
        [Description("Why the follow-up is needed (defaults to a message matching the prompt: review comments, failing checks with --checks, or a base-branch conflict with --rebase)")]
        public string? Reason { get; init; }

        [CommandOption("--checks")]
        [Description("Dispatch the fix-the-CI prompt (the PR's checks are failing) instead of the resolve-review-comments prompt")]
        public bool Checks { get; init; }

        [CommandOption("--rebase")]
        [Description(
            "Dispatch the rebase-onto-main prompt (the PR's branch conflicts with its base) instead of the "
            + "resolve-review-comments prompt — for when you see the conflict before the closeout monitor's "
            + "next inspection does (backlog 44)")]
        public bool Rebase { get; init; }

        public override ValidationResult Validate() =>
            Checks && Rebase
                ? ValidationResult.Error("Pass at most one of --checks and --rebase — they dispatch different follow-up prompts.")
                : ValidationResult.Success();
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
            ?? throw new DomainNotFoundException(MissingRunRecordMessage(taskId, previousRunId, task.PullRequestUrl));

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        DateTimeOffset resolvedAt = DateTimeOffset.UtcNow;
        string reason = settings.Reason ?? (settings.Checks, settings.Rebase) switch
        {
            (true, false) => "CI checks failing on the pull request.",
            (false, true) => "The pull request's branch conflicts with its base branch.",
            _ => "Unresolved review comments on the pull request.",
        };
        FollowUpKind kind = (settings.Checks, settings.Rebase) switch
        {
            (true, false) => FollowUpKind.FailingChecks,
            (false, true) => FollowUpKind.Rebase,
            _ => FollowUpKind.ReviewFeedback,
        };

        // A human-initiated reopen (Automatic: false) also resets the closeout monitor's
        // automatic follow-up budget — the human asking is a fresh grant (log #22). The
        // grant is recorded twice: the reset itself lands here on the task stream, and
        // CloseoutBudgetGranted below records the same grant on the run the human
        // resolved, so the run's own history shows a human touched it (log #80, backlog 45).
        session.Events.Append(taskId, expectedVersion: fence.Version + 1, TaskDecider.Reopen(
            task, previousRunId, previousRun.Branch, reason, kind,
            automatic: false,
            resolvedAt, context.OwnerId));
        session.Events.Append(previousRunId, new CloseoutBudgetGranted(previousRunId, reason, resolvedAt));
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

        // TaskAggregate.Apply(TaskReopened) never touches _unmetDependencies, only Assign does —
        // so a deliberately-claimed Blocked task (h9k task start --acknowledge-unmet-dependencies)
        // that reached Done while still naming an open blocker lands back on Blocked here, not
        // Queued, and no follow-up run dispatches until that blocker closes out — the task sits
        // Blocked rather than silently re-running (conformance review, cycle 4). Apply(TaskReopened)
        // keeps CurrentRunId pointing at previousRunId in this one case rather than nulling it the
        // way every other branch does, so this run stays watched (CloseoutEngine keeps reading its
        // pull request for a merge or close) instead of being superseded and orphaned
        // (adversarial review, cycle 1, on h9k task start).
        int unmetDependencyCount = task.UnmetDependencies.Count;
        if (unmetDependencyCount > 0)
        {
            string dependencyNoun = unmetDependencyCount == 1 ? "dependency" : "dependencies";
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Task {taskId} reopened, but {unmetDependencyCount} unmet {dependencyNoun} still name it Blocked — no follow-up run resumes branch {previousRun.Branch} until those close out.[/]");
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Task {taskId} reopened — a follow-up run will resume branch {previousRun.Branch} for {task.PullRequestUrl}.[/]");
        }

        return ExitCodes.Ok;
    }

    /// <summary>
    /// A task can reach here with no pull request at all — a Claimed task whose run stream has
    /// not started yet (the launcher is still checking out a worktree) hits the same missing-run
    /// record this command refuses on. The advice about the closeout sweep, and the URL it names,
    /// only make sense once a pull request exists (independent pre-PR review, cycle 1, ride-along).
    /// </summary>
    private static string MissingRunRecordMessage(Guid taskId, Guid runId, string? pullRequestUrl)
    {
        string baseMessage =
            $"Task {taskId}'s run {runId} has no run record, so there is no branch to resume — " +
            "pr resolve cannot dispatch a follow-up here.";
        return pullRequestUrl.IsNotBlank()
            ? $"{baseMessage} If the pull request has since merged, the closeout sweep will complete the " +
              $"task on its own; otherwise check {pullRequestUrl} directly."
            : $"{baseMessage} This task has no pull request yet, so it is likely still dispatching its run — " +
              "check h9k status and retry once it does.";
    }
}
