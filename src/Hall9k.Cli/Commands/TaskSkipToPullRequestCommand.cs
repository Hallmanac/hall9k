using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Prompts;
using Hall9k.Connectors.Worktrees;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Opens the pull request at once on a task in interactive mode, bypassing any gates still owed, every review
/// track, the final pass, and the settling gate, and records a manual override naming who skipped
/// what (idea 6e6d0200, Card 3). It has two doors.
/// <para>
/// From the build, under the caller's claim, it delivers through <see cref="TaskDeliverCommand"/>'s
/// own code (its refusals, the uncommitted-file check, the push, the handoff, token recording, and
/// the pre-append re-checks) and appends the override in the same transaction as the delivery.
/// From any review park, an interactive boundary or any other, it appends the override on the
/// parked run. Either way the CLI cannot reference the daemon, so it records the event and rings the
/// doorbell; the daemon's stranded-pipeline sweep finds the run UnderReview at merge-ready and hands
/// it to the pull request opener with no gate and no session, as it does for any settled run.
/// </para>
/// <para>
/// Nothing about the override reaches the pull request: whether the description mentions the skip is
/// the human's call, written in their own summary.
/// </para>
/// </summary>
public sealed class TaskSkipToPullRequestCommand : Hall9kAsyncCommand<TaskSkipToPullRequestCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--reason <TEXT>")]
        [Description("Why the rest of the review is being skipped, recorded on the run and shown wherever the override is. Optional, but always asked for: pass this, or --no-reason, or answer the prompt on a terminal.")]
        public string? Reason { get; init; }

        [CommandOption("--no-reason")]
        [Description("Record the override with no reason, skipping the prompt. The record then reads 'no reason given'.")]
        public bool NoReason { get; init; }

        [CommandOption("--reason-by-agent")]
        [Description("Marks --reason as written by the driving session rather than by you, so the record never presents a session's words as your own. Requires --reason.")]
        public bool ReasonByAgent { get; init; }

        [CommandOption("--handoff <TEXT>")]
        [Description("From the build only: what this run hands down to a dependent task or a resuming session, exactly as h9k task deliver takes it.")]
        public string? Handoff { get; init; }

        [CommandOption("--pr-summary <FILE>")]
        [Description("A file holding the pull request's title and description, built into the pull request exactly as on h9k task deliver: a first line reading 'Title: <title>', a blank line, then the description. The override itself adds nothing to the pull request; mention the skip there yourself if you want it said.")]
        public string? PrSummary { get; init; }

        [CommandOption("--force")]
        [Description("From the build only: proceed even though the claim's interactive session was recorded on another machine this one cannot check, attesting you confirmed by hand that it has exited (as on h9k task deliver).")]
        public bool Force { get; init; }

        public override ValidationResult Validate()
        {
            if (NoReason && Reason is not null)
            {
                return ValidationResult.Error(
                    "Pass --reason \"<text>\" or --no-reason, not both: they are two answers to one question.");
            }

            if (Reason is not null && Reason.IsBlank())
            {
                return ValidationResult.Error(
                    "--reason is blank. Say why, or pass --no-reason to record the override with none.");
            }

            return ReasonByAgent && Reason is null
                ? ValidationResult.Error(
                    "--reason-by-agent marks who wrote the --reason text, so it needs --reason. With no reason "
                    + "to record, pass --no-reason alone.")
                : ValidationResult.Success();
        }
    }

    /// <summary>The reason to record and whether the driving session wrote it.</summary>
    internal readonly record struct ResolvedReason(string? Text, bool ByAgent);

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        TaskDetails task = await session.LoadAsync<TaskDetails>(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        Guid runId = task.CurrentRunId
            ?? throw new DomainConflictException(
                $"Task {taskId} has no current run, so there is no build or review park to skip from. "
                + $"h9k task show {taskId} to see where it stands.");

        // Fenced like h9k review proceed: the park door's append carries expectedVersion, so a skip
        // racing the daemon, or a second skip, loses loudly instead of stacking a second override.
        StreamState fence = await session.Events.FetchStreamStateAsync(runId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s run {runId} has no run stream.");
        RunAggregate run = await session.Events.AggregateStreamAsync<RunAggregate>(
                runId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s run {runId} has no run stream.");

        // Admitted before the reason is asked for, so a task this verb refuses is never prompted about.
        ReviewSkipEntry entry = ReviewSkipRules.Admit(task, run);

        // Resolved before anything is pushed: a refusal for want of a reason must cost nothing.
        ResolvedReason reason = ResolveReason(
            settings, AnsiConsole.Profile.Capabilities.Interactive,
            () => AnsiConsole.Prompt(new TextPrompt<string>("Reason (leave blank for none):").AllowEmpty()));

        PrSummaryParser.PrSummary? prSummary = settings.PrSummary is { } prSummaryFile
            ? await TaskDeliverCommand.ReadPrSummaryAsync(prSummaryFile, cancellationToken)
            : null;

        return entry switch
        {
            ReviewSkipEntry.FromBuild => await TaskDeliverCommand.DeliverAsync(
                new TaskDeliverCommand.Settings
                {
                    Id = taskId.ToString(),
                    Handoff = settings.Handoff,
                    PrSummary = settings.PrSummary,
                    Force = settings.Force,
                },
                new TaskDeliverCommand.SkipRequest(reason.Text, reason.ByAgent),
                cancellationToken),
            _ => await SkipFromParkAsync(
                session, task, run, fence, settings, reason, prSummary, cancellationToken),
        };
    }

    /// <summary>
    /// The reason flags, the terminal prompt, and the no-terminal refusal as a pure rule. A flag
    /// answers outright; with neither flag a terminal is asked, and a blank answer records no reason;
    /// with no terminal (an agent shell, stdin closed) there is nobody to ask, so it refuses and
    /// names both flags. A prompted answer is always the human's, never marked agent-written.
    /// </summary>
    internal static ResolvedReason ResolveReason(Settings settings, bool terminalIsInteractive, Func<string> prompt)
    {
        if (settings.NoReason)
        {
            return new ResolvedReason(null, false);
        }

        if (settings.Reason is { } given)
        {
            return new ResolvedReason(given.Trim(), settings.ReasonByAgent);
        }

        if (!terminalIsInteractive)
        {
            throw new DomainValidationException(
                "A reason is always asked for, and there is no terminal to ask on. Pass --reason \"<why>\" to "
                + "record one (add --reason-by-agent if a driving session wrote it), or --no-reason to record "
                + "none.");
        }

        string answer = prompt();
        return new ResolvedReason(answer.IsBlank() ? null : answer.Trim(), false);
    }

    /// <summary>
    /// The park door: the run is review-parked, so the override is appended to it the way
    /// <c>h9k review proceed</c> appends its approval, after the worktree checks that keep the pull
    /// request from opening over work the branch does not hold.
    /// </summary>
    private static async Task<int> SkipFromParkAsync(
        IDocumentSession session, TaskDetails task, RunAggregate run, StreamState fence, Settings settings,
        ResolvedReason reason, PrSummaryParser.PrSummary? prSummary, CancellationToken cancellationToken)
    {
        Guid taskId = task.Id;
        Guid runId = run.Id;
        if (settings.Handoff is not null)
        {
            throw new DomainValidationException(
                "--handoff applies only when skipping from the build. This run has already been delivered, "
                + "and its handoff was recorded then.");
        }

        // Re-read after the reason prompt, which can sit open while someone clears interactive mode;
        // the run-stream fence below does not cover the task stream. The build door makes the same check.
        TaskDetails taskNow = await session.LoadAsync<TaskDetails>(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        if (!taskNow.InteractiveModeEnabled)
        {
            throw new DomainConflictException(
                $"Task {taskId}'s interactive mode was cleared while skipping, so nothing was recorded. "
                + "Skipping the review pipeline is only for a task you are driving by hand; turn interactive "
                + $"mode back on, or let the daemon run the full pipeline (h9k task show {taskId}).");
        }

        RunDetails runDetails = await session.LoadAsync<RunDetails>(runId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s run {runId} has no recorded details.");
        await EnsureWorktreeReadyAsync(taskId, runDetails, cancellationToken);

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        string? fingerprint = await OwnerRootFingerprintResolver.ResolveAsync(session, context.OwnerId, cancellationToken);

        if (prSummary is not null)
        {
            await TaskDeliverCommand.CapturePrSummaryAsync(runDetails.RunDirectory, prSummary, cancellationToken);
        }

        session.Events.Append(runId, expectedVersion: fence.Version + 1, new ReviewSkippedToPullRequest(
            runId, context.OwnerId, DateTimeOffset.UtcNow, reason.Text, reason.ByAgent,
            ReviewSkipPlan.Compute(run), fingerprint));

        // The run is no longer parked, so the sweep's parked-run shield no longer covers this lease;
        // a fresh heartbeat holds the task while the daemon wakes (mirrors h9k review proceed).
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
                $"Run {runId} changed while skipping, so the daemon (or another resolve) got there first. "
                + "Check h9k status; re-run this command only if the run is still ReviewParked.");
        }

        await Doorbell.RingAsync($"task-skip-to-pr:{taskId}", cancellationToken);
        AnsiConsole.MarkupLineInterpolated(
            $"[dim]Task {taskId} skipped to its pull request, recorded as a manual override; the daemon opens it next. h9k task show {taskId} to watch it.[/]");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Refuses when the worktree holds uncommitted work or is checked out somewhere other than the
    /// claim branch, because the opener pushes the branch and the pull request would then open over
    /// less than the owner sees. Git being unreadable warns and proceeds, as on deliver: it is never
    /// guessed at as clean.
    /// </summary>
    private static async Task EnsureWorktreeReadyAsync(
        Guid taskId, RunDetails run, CancellationToken cancellationToken)
    {
        if (run.WorktreePath.IsBlank())
        {
            return;
        }

        (IReadOnlyList<string>? modified, IReadOnlyList<string> untracked) =
            await InteractiveWorktreeGit.ListUncommittedFilesAsync(run.WorktreePath, cancellationToken);
        if (modified is null)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Could not read the worktree's git status at {run.WorktreePath}; skipping the uncommitted-files check.[/]");
        }
        else
        {
            (IReadOnlyList<string> strandable, _) = WorktreeGitStatus.SplitUntracked(untracked);
            IReadOnlyList<string> blocking = [.. modified, .. strandable];
            if (blocking.Count > 0)
            {
                throw new DomainConflictException(
                    $"Task {taskId}'s worktree holds uncommitted work, which the pull request would open without. "
                    + $"Commit it or discard it and run this again. Uncommitted: {string.Join(", ", blocking)}");
            }
        }

        string? currentBranch = await InteractiveWorktreeGit.GetCurrentBranchAsync(run.WorktreePath, cancellationToken);
        if (currentBranch is not null && currentBranch != run.Branch)
        {
            string where = currentBranch.Length == 0 ? "a detached commit" : $"'{currentBranch}'";
            throw new DomainConflictException(
                $"Task {taskId}'s worktree is checked out to {where}, not its claim branch '{run.Branch}', so the "
                + $"pull request would open over a different tree. Check out '{run.Branch}' and run this again.");
        }
    }
}
