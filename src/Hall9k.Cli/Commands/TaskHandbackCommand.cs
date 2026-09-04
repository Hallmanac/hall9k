using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
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
/// <para>
/// Pickup speed is a three-way choice (task 45136b29, idea fcaded0b's R7 ruling). No flag: the
/// normal rotation, byte-for-byte today's behavior — the task falls where its assignment age
/// puts it. <c>--first</c>: records the queue-first marker (the same task-level fact
/// <c>h9k task revise --queue-first</c> sets directly), so the next free slot takes this task
/// regardless of age. <c>--now</c>: dispatches it immediately, ceiling-exempt, by reusing
/// <see cref="TaskStartCommand.RunDeliberateStartAsync"/> — the identical mechanism
/// <c>h9k task start</c> runs, not a second implementation of it — against the task this
/// handback just landed Queued or Blocked. The two are refused together: one hands the next
/// free slot to this task, the other skips waiting for one, and nothing about the wording of
/// either survives being asked for both at once.
/// </para>
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

        [CommandOption("--force")]
        [Description("Hand back even though the claim's interactive session was recorded on another machine this one cannot check — attests you confirmed by hand that it has exited")]
        public bool Force { get; init; }

        [CommandOption("--first")]
        [Description(
            "Record the queue-first marker (task 45136b29): the next free dispatch slot takes this task "
            + "regardless of assignment age, instead of falling where its age puts it. Refused together with "
            + "--now — pass one")]
        public bool First { get; init; }

        [CommandOption("--now")]
        [Description(
            "Dispatch this task immediately after handing it back, ceiling-exempt, through the same mechanism "
            + "h9k task start uses. Refused together with --first — pass one")]
        public bool Now { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (settings.First && settings.Now)
        {
            throw new DomainValidationException(
                "--first and --now say different things about how this hands back: --first waits for the next "
                + "free slot but takes it regardless of age, --now skips waiting for one entirely. Pass one.");
        }

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
            ?? throw new DomainConflictException(
                $"Task {taskId} is claimed interactively but run {runId} has no record — the process likely died "
                + $"while preparing the worktree. h9k task release {taskId} to give the claim back to the "
                + "dispatch queue.");

        // An operator's own session, still attached in another terminal, owns this worktree right
        // now — handing it to a headless agent out from under it would double-book the same files
        // (adversarial review, cycle 1). Skipped when this invocation is that very session handing
        // itself back on the operator's own go (InteractiveSessionLiveness.IsSelfInvocation's own
        // doc has both signals) — the same reasoning h9k task verify's own exemption already rests
        // on: it is waiting on this command, not racing it. WorkPromptBuilder.AppendSelfDeliveryRule
        // is what tells a self-invoking session to stop editing this worktree the instant this
        // succeeds — the follow-on headless run takes it over immediately (independent pre-PR
        // review, conformance lens, cycle 1).
        if (!InteractiveSessionLiveness.IsSelfInvocation(run))
        {
            InteractiveSessionLiveness.EnsureNotAttachedElsewhere(run, taskId, "hand back", settings.Force);
        }

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

        TaskHandedBack handedBack = TaskDecider.HandBack(
            task, runId, run.Branch, settings.Reason, DateTimeOffset.UtcNow, context.OwnerId);
        task.Apply(handedBack);

        // --first records the same task-level fact h9k task revise --queue-first sets directly
        // (task 45136b29, idea fcaded0b's R7 ruling) — appended alongside the handback itself,
        // in the same atomic commit, rather than as a second round trip.
        TaskRevised? markedFirst = null;
        if (settings.First)
        {
            markedFirst = TaskDecider.Revise(
                task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
                Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None,
                DateTimeOffset.UtcNow, context.OwnerId, epicId: Optional<Guid?>.None,
                queuePriority: Optional<bool>.Of(true));
            task.Apply(markedFirst);
        }

        long taskVersionAfterHandback = fence.Version + (markedFirst is null ? 1 : 2);
        if (markedFirst is null)
        {
            session.Events.Append(taskId, expectedVersion: taskVersionAfterHandback, handedBack);
        }
        else
        {
            session.Events.Append(taskId, expectedVersion: taskVersionAfterHandback, handedBack, markedFirst);
        }

        // The next headless claim resumes the branch under a fresh run id (RunLauncher mints
        // one per launch); this run otherwise reads Running forever — it holds no TaskLease
        // and its NodeId is the Guid.Empty sentinel, so neither AdoptOrphansAsync's NodeId
        // filter nor SweepExpiredLeasesAsync's lease scan will ever retire it (conformance and
        // adversarial review, cycle 1).
        DateTimeOffset supersededAt = DateTimeOffset.UtcNow;
        // A start-it-mine claim's own stream.jsonl is otherwise never read back once its run is
        // retired this way — h9k task deliver is the only other lever that reads it, so a
        // headless session handed back mid-run had its token spend discarded permanently
        // (conformance review, cycle 1, on h9k task start).
        HeadlessTokenRecovery.AppendIfRecorded(session, run, supersededAt);
        session.Events.Append(runId, new RunSuperseded(runId, task.LeaseGeneration + 1, supersededAt));
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

        // --now dispatches the handed-back task immediately, ceiling-exempt, reusing exactly the
        // mechanism h9k task start runs — not a second implementation of it (task 45136b29, R7):
        // the aggregate is re-read fresh off the stream this handback just committed, landed
        // Queued (the ordinary case) or Blocked (only when every still-open blocker was already
        // acknowledged at the claim this handback is releasing — TaskStartCommand.ClaimAndCutAsync's
        // own carried-forward branch, which is always true here since a claim's unmet-dependency
        // set never changes between being acknowledged and being handed back).
        if (settings.Now)
        {
            StreamState? nowFence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken)
                ?? throw new DomainConflictException(
                    $"Task {taskId} changed while handing it back — check h9k status and try again.");
            TaskAggregate nowTask = await session.Events.AggregateStreamAsync<TaskAggregate>(
                    taskId, version: nowFence.Version, token: cancellationToken)
                ?? throw new DomainConflictException(
                    $"Task {taskId} changed while handing it back — check h9k status and try again.");

            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Task {taskId} handed back — dispatching immediately (--now).[/]");
            return await TaskStartCommand.RunDeliberateStartAsync(
                store, session, nowTask, nowFence, context, acknowledgeUnmetDependencies: true, cancellationToken);
        }

        // TaskAggregate.Apply(TaskHandedBack) clears the claim but never touches
        // _unmetDependencies, only Assign does — so a handback out of a deliberate
        // start-it-mine override (h9k task start --acknowledge-unmet-dependencies) still naming
        // an open blocker lands Blocked, not Queued, and no headless run dispatches until that
        // blocker closes out (conformance review, cycle 4).
        int unmetDependencyCount = task.UnmetDependencies.Count;
        if (unmetDependencyCount == 0)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Task {taskId} handed back — the next headless run resumes branch {run.Branch}.[/]");
        }
        else
        {
            string dependencyNoun = unmetDependencyCount == 1 ? "dependency" : "dependencies";
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Task {taskId} handed back, but {unmetDependencyCount} unmet {dependencyNoun} still name it Blocked — no headless run resumes branch {run.Branch} until those close out.[/]");
        }

        if (settings.First)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[blue]Task {taskId} marked queue-first[/] — it takes the next free dispatch slot regardless of assignment age.");
        }

        return ExitCodes.Ok;
    }
}
