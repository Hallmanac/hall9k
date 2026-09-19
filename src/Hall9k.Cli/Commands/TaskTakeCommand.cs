using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Trust;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// h9k task take: <c>--force</c> is an owner-role member's own unilateral override of an absent
/// holder (idea 202383dc, item 4). Absence is never detected — presence detection is dead, never
/// parked — so that path prints whatever evidence it has and proceeds on the operator's own
/// judgment: the holder it currently reads, since when, and the last time anything from that
/// node's own outbox was observed here.
/// <para>
/// Without <c>--force</c> this is the cooperative take instead (idea 202383dc, item 5, "a member
/// can ask a holder for a task"): a task with no current holder claims directly through the
/// ordinary lock (nothing to negotiate), a task this node already holds says so, and a task held
/// by another node sends that node a <see cref="MessageKind.ClaimRequest"/> envelope naming this
/// node's own owner and <c>--reason</c>, then prints that it asked and is waiting — this command
/// only ever queues that envelope; the daemon's own message sweep is what actually sends it, and
/// the holder's own <c>ClaimRequestWatchLoop</c> is what actually answers it, exactly the way
/// <c>h9k message send</c> never blocks on the transport either. <c>h9k task show</c> and
/// <c>h9k status</c> are where the answer — granted, refused, or timed out — is read back.
/// </para>
/// </summary>
public sealed class TaskTakeCommand : Hall9kAsyncCommand<TaskTakeCommand.Settings>
{
    /// <summary>The platform default when a project has never set its own <c>--take-timeout</c> — see <see cref="ProjectDetails.TakeTimeoutMinutes"/>'s own doc.</summary>
    internal const int DefaultTakeTimeoutMinutes = 30;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--force")]
        [Description(
            "Override the current holder unilaterally, on the operator's own judgment, rather than "
            + "asking it (idea 202383dc, item 4). Without this flag, the same command asks "
            + "cooperatively instead (idea 202383dc, item 5).")]
        public bool Force { get; init; }

        [CommandOption("--reason <REASON>")]
        [Description(
            "Why the current holder is being overridden (--force) or asked for (cooperative) — "
            + "required either way, recorded on the task's own stream.")]
        public string? Reason { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        return await RunAsync(
            store, session, settings, new GitLedger(new ConsoleWorktreeLogger<GitLedger>()), new GitLedgerChainReader(),
            take: null, new NodeKeyStore(), cancellationToken);
    }

    /// <summary>Testable core — every external seam (the ledger, the chain read, the tracker take, the signing key) is injectable, the same shape <c>ProjectMemberRemoveCommand.RunAsync</c> already gives its own owner-role gate.</summary>
    internal static async Task<int> RunAsync(
        IDocumentStore store,
        IDocumentSession session,
        Settings settings,
        ILedger ledger,
        ILedgerChainReader chainReader,
        TrackerAssignmentTake? take,
        NodeKeyStore keyStore,
        CancellationToken cancellationToken)
    {
        if (settings.Reason.IsBlank())
        {
            throw new DomainValidationException(
                settings.Force
                    ? "A forced take needs --reason: why the current holder is being overridden."
                    : "A cooperative take needs --reason: why this task is being asked for.");
        }

        string reason = settings.Reason;

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);

        if (!settings.Force)
        {
            return await RunCooperativeAsync(store, session, taskId, reason, take, cancellationToken);
        }

        StreamState? fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        if (task.HolderNodeId is not { } previousHolderNodeId)
        {
            throw new DomainConflictException(
                $"Task {taskId} carries no ledger holder to take over — an interactive claim "
                + "(h9k task work) never writes one, and a task with no live claim at all has "
                + "nothing here for --force to override.");
        }

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"No project {task.ProjectId}.");
        await AssertOwnerRoleAsync(session, context, project, chainReader, keyStore, cancellationToken);

        await PrintEvidenceAsync(session, previousHolderNodeId, task.HolderSince, cancellationToken);

        (LedgerCommitter committer, LedgerSigningKey signingKey, string ownerFingerprint) =
            await TaskRecordPublication.ResolveIdentityAsync(session, context, cancellationToken);
        NodeDetails? myNode = await session.LoadAsync<NodeDetails>(context.NodeId, cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // The state/reason guard runs before anything external is written (adversarial +
        // conformance pre-PR review, cycle 1): a task this decider refuses has no holder for a
        // forced take to override, and finding that out only after the tracker take and the
        // ledger override have already changed two external systems leaves both stuck with no
        // event ever recorded to show for it — unrecoverable by re-running, since neither write is
        // idempotent against a state that never changes. This call's own event is discarded: the
        // tracker take and the ledger override below both take real time doing real I/O, so the
        // event actually appended is rebuilt from a fresh read right before that append instead of
        // reused from here (adversarial + conformance pre-PR review, cycle 4 — see the re-fence
        // below).
        _ = TaskDecider.TakeOver(
            task, context.NodeId, context.OwnerId, ownerFingerprint, reason, context.OwnerId, now);

        // Gated projects run the existing tracker take first (idea 202383dc, item 4, criterion 2):
        // its own refusal — DomainBusinessRuleException, Program.cs's own exit 70 mapping — stops
        // the override outright, before the ledger holder is ever touched, with the tracker's own
        // sentence as the reason. The taker instance is kept, not just its result: if the ledger
        // override below fails after this write already landed on the tracker, undoing it needs
        // the same taker (adversarial pre-PR review, cycle 1).
        TrackerAssignmentTake tracker = take ?? new TrackerAssignmentTake(new ProjectScopedGitHubRunner(store).Runner);
        TrackerTake trackerTake = await TrackerClaimCheck.TakeOrRefuseAsync(
            store, taskId, project, task.ExternalReference?.ToString(), tracker, cancellationToken);

        TaskRecordHolder candidate = new(ownerFingerprint, context.NodeId, myNode?.MachineName ?? Environment.MachineName, now);

        HolderOverrideResult result = await TaskLedgerHolder.TryOverrideAsync(
            ledger, project.RepositoryPath, taskId, candidate, committer, signingKey, cancellationToken);
        switch (result.Verdict)
        {
            case HolderOverrideVerdict.AlreadyOverridden:
                string winner = result.CurrentHolder is { } currentHolder
                    ? $"node {DomainId.Short(currentHolder.NodeId)} ({currentHolder.OwnerFingerprint}), since {currentHolder.Since:u}"
                    : "another node";
                // The tracker assignment this command just wrote stays on the item, and this is the
                // one verdict where that is decided without a fresh read of the task's own stream
                // (adversarial + conformance pre-PR review, cycle 3): the ledger has already
                // answered that another node holds this task, which is better evidence than any
                // re-read, and on a gated project that winner's own claim gate can only be passing
                // on this very assignment — the item was unassigned before this command ran, which
                // is what TrackerTake.Wrote means, so the only way a competing holder passed the
                // gate at all is by sharing this install's tracker identity. Clearing it back off
                // would leave the node that won holding a task its own dispatch sweep then reads as
                // Unassigned, which holds, and never claims.
                string alreadyOverriddenTrackerNote = SettledTrackerTakeNote(
                    trackerTake,
                    "the override that won this race holds the task now, and on a claim-gated project "
                    + "its own claim gate passes on this very assignment");
                throw new DomainConflictException(
                    $"Task {taskId}: another override already landed while this one was deciding — the "
                    + $"ledger now names {winner}. Two overriders cannot both win; this one lost the race."
                    + alreadyOverriddenTrackerNote);
            case HolderOverrideVerdict.Failed:
                // Nothing landed on the ledger, so this command changed exactly one thing in the
                // world — the tracker assignment — and the fresh read decides whether giving it
                // back is still safe: a holder that arrived while this override was failing may be
                // passing the gate on it (see ResolveUndo).
                TakeUndo failedUndo = await ResolveUndoAsync(
                    store, taskId, context.NodeId, previousHolderNodeId, result.PreviousHolder, cancellationToken);
                string failedTrackerNote = await SettleTrackerTakeBestEffortAsync(
                    store, tracker, project, task.ExternalReference, trackerTake, failedUndo.KeepTrackerBecause,
                    cancellationToken);
                throw new DomainConflictException(
                    $"Task {taskId}: the ledger holder could not be overridden — {result.FailureReason} "
                    + "Nothing was recorded on the ledger; re-run h9k task take --force once that settles."
                    + failedTrackerNote);
            case HolderOverrideVerdict.NoRecord:
            case HolderOverrideVerdict.Overridden:
            default:
                break;
        }

        // Re-aggregated and re-validated immediately before the append (adversarial + conformance
        // pre-PR review, cycle 4): the tracker take and the ledger override above are both real
        // I/O — a gh round trip, a git fetch and push — wide enough for this task's own stream to
        // move underneath this command (a lease-expiry requeue, a closeout, another takeover).
        // Refetching only the fence's version number here (the previous shape of this fix) would
        // append the event built against the ORIGINAL read over whatever landed in that window,
        // silently dropping TaskDecider.TakeOver's own state guard at the one moment it needs to
        // hold. DispatchEngine.TryClaimAsync does not need this same re-read-and-re-validate: it
        // keeps the fence from its own FIRST read (state.Version + events.Length) all the way to
        // its own append, because nothing external happens in its own window between that read and
        // that commit. This method's window does, so both the fence and the state it is validated
        // against have to be as fresh as the append itself.
        StreamState? freshFence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        TaskAggregate freshTask = await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: freshFence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        TaskHolderTakenOver takenOver;
        try
        {
            takenOver = TaskDecider.TakeOver(
                freshTask, context.NodeId, context.OwnerId, ownerFingerprint, reason, context.OwnerId, now);
        }
        catch (DomainConflictException)
        {
            // The task moved out of Claimed/NeedsHuman during the tracker take or the ledger
            // override above — the exact race the re-validation above exists to catch. Unlike the
            // append-race path below, freshTask is already in hand here, so ResolveUndo is asked
            // directly rather than through its own re-reading wrapper; the reading it applies is
            // the same one both SaveChangesAsync catches below apply, and its own doc comment owns
            // why each holder shape rolls back the way it does.
            TakeUndo undo = ResolveUndo(
                freshTask.HolderNodeId, context.NodeId, previousHolderNodeId, result.PreviousHolder);
            if (undo.RollBackLedger)
            {
                await RestoreLedgerOverrideBestEffortAsync(
                    ledger, project.RepositoryPath, taskId, context.NodeId, undo.LedgerTarget, committer,
                    signingKey, cancellationToken);
            }

            // Printed rather than appended to a message, unlike the two verdict paths above: the
            // decider's own refusal is rethrown verbatim here, so there is no sentence of this
            // command's own to carry the tracker's outcome (conformance pre-PR review, cycle 3 —
            // this path wrote to the tracker too, and left it behind).
            PrintTrackerNote(await SettleTrackerTakeBestEffortAsync(
                store, tracker, project, task.ExternalReference, trackerTake, undo.KeepTrackerBecause,
                cancellationToken));
            throw;
        }

        session.Events.Append(taskId, expectedVersion: freshFence.Version + 1, takenOver);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            // The ledger override above already landed — pushed and confirmed — before this
            // commit ever ran, and a takeover that loses this race here never happened:
            // TaskHolderTakenOver was never appended, so nothing else this platform ships will
            // ever release a holder this node's own aggregate never recorded taking (adversarial
            // pre-PR review, cycle 1 — the same hazard DispatchEngine.TryClaimAsync's own
            // ReleaseLedgerHolderBestEffortAsync exists to close for the ordinary claim path).
            // Best effort: this call is safe even if the restore itself cannot land, since a
            // failure here is reported rather than silently swallowed.
            //
            // Restores the ledger's PREVIOUS holder rather than releasing to null (adversarial
            // pre-PR review, cycle 2): a null holder is freely claimable by any node's ordinary
            // dispatch sweep (TaskLedgerHolder.TryClaimAsync treats a null Holder as unowned),
            // which would let some other node claim and start running this task concurrently with
            // whatever the domain aggregate believes about it — PROVIDED the aggregate still
            // legitimately names a holder. It does not always: the event that won this race is not
            // necessarily unrelated to the holder at all — it can itself be a lease-expiry
            // TaskRequeued/TaskHolderReleased pair from the previous holder's own node, which
            // leaves the aggregate naming no holder at all. Restoring result.PreviousHolder there
            // would leave the ledger naming a node the domain no longer recognises, which nothing
            // can ever clear again — the exact hazard the cycle-6 fix closed for the re-validation
            // path above. So the target is resolved fresh, the same way, rather than assumed
            // (conformance pre-PR review, cycle 8), and that same fresh read decides the tracker
            // assignment's fate too (cycle 3).
            TakeUndo undo = await ResolveUndoAsync(
                store, taskId, context.NodeId, previousHolderNodeId, result.PreviousHolder, cancellationToken);
            bool rolledBack = undo.RollBackLedger && await RestoreLedgerOverrideBestEffortAsync(
                ledger, project.RepositoryPath, taskId, context.NodeId, undo.LedgerTarget, committer,
                signingKey, cancellationToken);
            string trackerNote = await SettleTrackerTakeBestEffortAsync(
                store, tracker, project, task.ExternalReference, trackerTake, undo.KeepTrackerBecause,
                cancellationToken);
            // The message names what actually happened (conformance pre-PR review, cycle 4) rather
            // than always claiming the rollback landed: on the rollback-failed path,
            // RestoreLedgerOverrideBestEffortAsync has already printed the warning that says so, and
            // an operator reading the thrown error text alone (the CLI's own self-correction
            // standard, AGENTS.md) must not be told the opposite of what just happened.
            throw new DomainConflictException(
                (undo.RollBackLedger switch
                {
                    false =>
                        $"Task {taskId} changed while recording this takeover, and this takeover is not "
                        + "what changed it — the task's own stream already names this node as its holder "
                        + "through some other event, so the ledger holder was left naming this node to "
                        + "match rather than rolled back onto a holder the stream no longer names. Check "
                        + "h9k task show.",
                    true when rolledBack =>
                        $"Task {taskId} changed while recording this takeover — the ledger holder override "
                        + "was rolled back rather than left pointing at a node whose own task stream never "
                        + "recorded taking it. Check h9k task show and re-run h9k task take --force once "
                        + "that settles.",
                    true =>
                        $"Task {taskId} changed while recording this takeover, and the ledger holder override "
                        + "could not be rolled back — see the warning above. It still names this node even "
                        + "though this node's own task stream never recorded taking it. Check h9k task show "
                        + "and re-run h9k task take --force once that settles.",
                })
                + trackerNote);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Any other SaveChangesAsync failure — a transient Npgsql error, a timeout —
            // (adversarial pre-PR review, cycle 4) leaves the identical split the race above
            // leaves: the ledger already names this node while this node's own stream never
            // recorded taking it. Rolled back here the same way, including the same fresh-target
            // resolution (conformance pre-PR review, cycle 8) — a transient failure here is no
            // narrower a window for the previous holder's own lease expiry to have landed than the
            // conflict caught above — then the original failure is left to propagate rather than
            // wrapped in a conflict message that would not describe what actually went wrong.
            // This is also the one catch a COMMITTED append can reach — Npgsql can drop the
            // connection after COMMIT has already landed — which is exactly why the rollback is
            // decided by the fresh read rather than by the fact that this method is failing
            // (conformance pre-PR review, cycle 3): a rollback run against an append that did
            // commit would leave the stream saying this node took the task over while the ledger
            // says somebody else holds it, and neither side can be re-taken from the other.
            TakeUndo undo = await ResolveUndoAsync(
                store, taskId, context.NodeId, previousHolderNodeId, result.PreviousHolder, cancellationToken);
            if (undo.RollBackLedger)
            {
                await RestoreLedgerOverrideBestEffortAsync(
                    ledger, project.RepositoryPath, taskId, context.NodeId, undo.LedgerTarget, committer,
                    signingKey, cancellationToken);
            }
            else
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Note:[/] task {taskId}'s own stream already names this node as its holder, so "
                    + "the ledger holder override was left in place rather than rolled back onto a holder "
                    + "the stream no longer names. Check h9k task show.");
            }

            PrintTrackerNote(await SettleTrackerTakeBestEffortAsync(
                store, tracker, project, task.ExternalReference, trackerTake, undo.KeepTrackerBecause,
                cancellationToken));
            throw;
        }

        await Doorbell.RingAsync($"task-taken-over:{taskId}", cancellationToken);

        // Named off the event actually appended, not the first read at the top of this method
        // (conformance + adversarial pre-PR review, cycle 6): a re-claim landing during the
        // tracker take or the ledger override moves takenOver.PreviousHolderNodeId away from
        // previousHolderNodeId, and the durable record — not the stale first read — is what this
        // line must agree with.
        string takenFrom = takenOver.PreviousHolderNodeId is { } takenFromNodeId
            ? $"node {DomainId.Short(takenFromNodeId)}"
            : "no node";
        AnsiConsole.MarkupLine(
            $"[green]Task {taskId} taken over[/] from {takenFrom} — "
            + $"reason: {reason.EscapeMarkup()}");
        AnsiConsole.MarkupLine(
            "[dim]This node claims it on its next dispatch sweep and resumes the branch where the "
            + "previous run left it.[/]");

        return ExitCodes.Ok;
    }

    /// <summary>
    /// What an abandoned take should give back — of the two things it has already changed in the
    /// world by the time any of this command's own failure paths run: the ledger holder it
    /// overrode, and the tracker assignment it wrote.
    /// </summary>
    /// <param name="RollBackLedger">
    /// Whether the ledger override should be undone at all. False only when the task's own stream
    /// already names this node as its holder, which means the takeover is in force whatever this
    /// command is about to throw — an append that committed and then failed on the way out, or
    /// this node's own daemon claiming on the strength of the override. Rolling back there would
    /// leave the stream saying this node took the task and the ledger saying somebody else holds
    /// it, a split neither side can be re-taken from (conformance pre-PR review, cycle 3).
    /// </param>
    /// <param name="LedgerTarget">What the ledger holder goes back to — the holder the override overwrote, or null to release it outright.</param>
    /// <param name="KeepTrackerBecause">
    /// Why the tracker assignment is being left on the item, or null to clear it back off. The
    /// sentence is carried rather than derived because each path has its own reason and an
    /// operator deciding whether to unassign the card by hand needs the one that actually applies.
    /// </param>
    internal sealed record TakeUndo(bool RollBackLedger, TaskRecordHolder? LedgerTarget, string? KeepTrackerBecause)
    {
        /// <summary>The takeover stands: nothing to roll back, and the assignment is what a claim here now rests on.</summary>
        public static readonly TakeUndo Nothing = new(
            RollBackLedger: false,
            LedgerTarget: null,
            KeepTrackerBecause:
                "this task's own stream already names this node as its holder, so a claim on it here "
                + "passes the claim gate on this very assignment");

        /// <summary>Roll the ledger back to <paramref name="target"/>, and give the tracker assignment back too.</summary>
        public static TakeUndo RollBackTo(TaskRecordHolder? target) => new(true, target, KeepTrackerBecause: null);

        /// <summary>Roll the ledger back, but leave the tracker assignment where it is, for the stated reason.</summary>
        public static TakeUndo RollBackKeepingTracker(TaskRecordHolder? target, string because) =>
            new(true, target, because);
    }

    /// <summary>
    /// The fresh-read half of <see cref="ResolveUndo"/>, for the two <c>SaveChangesAsync</c>
    /// catches, which — unlike the re-validation refusal above — have no fresh aggregate in hand
    /// and cannot get one from the session whose own commit just failed.
    /// <para>
    /// Best effort about the read itself: a fetch that fails, or a stream that reads back as
    /// nothing at all, answers with the ledger rolled back to <paramref name="previousHolder"/>
    /// unchanged — the restore this rollback always made before any of these checks existed — and
    /// with the tracker assignment left alone, because a read that answered nothing is not
    /// evidence that nobody has claimed on the strength of that assignment (AGENTS.md: never
    /// guess at unobserved facts).
    /// </para>
    /// </summary>
    private static async Task<TakeUndo> ResolveUndoAsync(
        IDocumentStore store, Guid taskId, Guid thisNodeId, Guid holderAtEntryNodeId,
        TaskRecordHolder? previousHolder, CancellationToken cancellationToken)
    {
        const string unreadable =
            "this task's own stream could not be re-read to tell whether anything has since claimed "
            + "on the strength of that assignment";

        try
        {
            await using IDocumentSession session = store.LightweightSession();
            TaskAggregate? raceTask = await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, token: cancellationToken);
            return raceTask is null
                ? TakeUndo.RollBackKeepingTracker(previousHolder, unreadable)
                : ResolveUndo(raceTask.HolderNodeId, thisNodeId, holderAtEntryNodeId, previousHolder);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return TakeUndo.RollBackKeepingTracker(previousHolder, unreadable);
        }
    }

    /// <summary>
    /// The one reading of a fresh holder every abandoned-take path shares, so the ledger rollback
    /// and the tracker undo can never disagree about who holds this task.
    /// <para>
    /// The tracker half turns on a single asymmetry (adversarial + conformance pre-PR review,
    /// cycle 3). <see cref="TrackerTake.Wrote"/> is only ever true for an item the gate read as
    /// <em>unassigned</em>, and an unassigned item holds the claim gate for everybody — so
    /// leaving the assignment on it can never block a node that was not already blocked before
    /// this command ran, while clearing it can strip the gate from a holder that has since passed
    /// it on the strength of that very assignment. The harm runs one way, so the assignment is
    /// given back only where nothing can have come to depend on it: nobody holds the task, or the
    /// holder is still the one this command set out to override.
    /// </para>
    /// </summary>
    /// <param name="domainHolderNodeId">Who the task's own stream names as its holder, read fresh.</param>
    /// <param name="thisNodeId">This node, the one that wrote both the override and the assignment.</param>
    /// <param name="holderAtEntryNodeId">The holder this command read at the top, the one the override set out to take the task from.</param>
    /// <param name="previousHolder">The holder <c>TryOverrideAsync</c> actually overwrote on the ledger.</param>
    internal static TakeUndo ResolveUndo(
        Guid? domainHolderNodeId, Guid thisNodeId, Guid holderAtEntryNodeId, TaskRecordHolder? previousHolder) =>
        domainHolderNodeId switch
        {
            // The takeover is in force after all, whatever this command is failing with.
            { } held when held == thisNodeId => TakeUndo.Nothing,

            // Nobody holds it — a release, a requeue, a closeout landed underneath. Restoring the
            // previous holder here would leave the ledger naming a node the domain no longer
            // recognises, which nothing can ever clear again (adversarial pre-PR review, cycle 6),
            // and nothing can be leaning on the assignment either, because a holder leaning on the
            // gate would have to be a holder.
            null => TakeUndo.RollBackTo(null),

            // Still the holder this command set out to take the task from: nothing moved, so both
            // halves go back exactly as they were. The ledger's own previous holder is only
            // written back when it names that same node — a ledger that named somebody else, or
            // nobody, is not evidence for a holder the domain does not agree with.
            { } held when held == holderAtEntryNodeId =>
                TakeUndo.RollBackTo(previousHolder?.NodeId == held ? previousHolder : null),

            // A holder that is neither this node nor the one this command read at entry arrived
            // while this command was running, so it claimed or took over after the assignment was
            // written and may well have passed the gate on it. The ledger is released rather than
            // restored, for the same reason the null case above releases: the previous holder is
            // not who the domain names.
            _ => TakeUndo.RollBackKeepingTracker(
                null,
                "this task's own stream now names a holder this take never wrote, and on a claim-gated "
                + "project that holder's own claim gate passes on this very assignment"),
        };

    /// <summary>
    /// Settles the tracker assignment <see cref="TrackerClaimCheck.TakeOrRefuseAsync"/> already
    /// wrote, for every path that abandons this command after that write has landed (adversarial
    /// pre-PR review, cycle 1; extended to the re-validation refusal and both
    /// <c>SaveChangesAsync</c> catches by conformance pre-PR review, cycle 3, which found three of
    /// the five paths rolling the ledger back and leaving the item assigned). Either clears it
    /// back off, or leaves it and says why — <see cref="ResolveUndo"/> owns that call. Only
    /// <see cref="TrackerTake.Wrote"/> actually put anything there: <c>AlreadyMine</c>/<c>NotGated</c>
    /// wrote nothing and have nothing to settle.
    /// <para>
    /// Returns the sentence the caller appends to its own thrown message, or prints when it
    /// rethrows somebody else's — empty when there was nothing to settle — rather than throwing
    /// itself, since a failure here must not hide the conflict that is the actual reason this
    /// command is failing.
    /// </para>
    /// </summary>
    private static async Task<string> SettleTrackerTakeBestEffortAsync(
        IDocumentStore store, TrackerAssignmentTake tracker, ProjectDetails project,
        ExternalReference? externalReference, TrackerTake trackerTake, string? keepBecause,
        CancellationToken cancellationToken)
    {
        if (!trackerTake.Wrote || keepBecause is not null)
        {
            return SettledTrackerTakeNote(trackerTake, keepBecause);
        }

        try
        {
            TrackerRelease release = await tracker.ReleaseAsync(
                store, project.ClaimGate, externalReference, project.RepositoryPath, cancellationToken);
            return (release.Succeeded, release.Wrote) switch
            {
                (true, true) =>
                    $" {trackerTake.Decision.Tracker} showed {trackerTake.Decision.Item} assigned to this "
                    + "install from that take — it has been cleared back off the item.",
                // Nothing of this install's was there to clear, which is not the same thing as
                // having cleared it and must not be reported as though it were (AGENTS.md, never
                // guess at unobserved facts — conformance pre-PR review, cycle 3). Something moved
                // the assignee between that take and now, and the operator reading this is the one
                // positioned to find out what.
                (true, false) =>
                    $" {trackerTake.Decision.Tracker} no longer shows {trackerTake.Decision.Item} assigned "
                    + "to this install, so there was nothing left of that take to clear — something else "
                    + "moved the item's assignee in between, which is worth a look before anything is "
                    + "assumed about who holds it.",
                _ =>
                    $" {trackerTake.Decision.Tracker} still shows {trackerTake.Decision.Item} assigned to "
                    + $"this install from that take, and clearing it back off failed — {release.FailureReason} "
                    + "An owner-role member will need to unassign it by hand.",
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return $" {trackerTake.Decision.Tracker} still shows {trackerTake.Decision.Item} assigned to "
                + $"this install from that take, and clearing it back off failed — {exception.Message} An "
                + "owner-role member will need to unassign it by hand.";
        }
    }

    /// <summary>
    /// The sentence for an assignment deliberately left on the item, for the reason
    /// <paramref name="keepBecause"/> states — and the empty string whenever there is nothing to
    /// say, which is a take that wrote nothing or a caller that means to clear.
    /// </summary>
    private static string SettledTrackerTakeNote(TrackerTake trackerTake, string? keepBecause) =>
        !trackerTake.Wrote || keepBecause is null
            ? string.Empty
            : $" {trackerTake.Decision.Tracker} still shows {trackerTake.Decision.Item} assigned to this "
                + $"install from that take, and it has deliberately been left there: {keepBecause}, so "
                + "clearing it back off would leave that holder unable to claim this task at all. Unassign "
                + "it by hand only once you know who is actually doing the work.";

    /// <summary>
    /// Says a tracker settlement out loud, for the two paths that rethrow somebody else's
    /// exception and so have no message of their own to carry it. Silent when there is nothing to
    /// report.
    /// </summary>
    private static void PrintTrackerNote(string note)
    {
        if (note.IsBlank())
        {
            return;
        }

        AnsiConsole.MarkupLine($"[yellow]Tracker:[/] {note.Trim().EscapeMarkup()}");
    }

    /// <summary>
    /// The cooperative take (idea 202383dc, item 5): a task with no current holder claims directly
    /// through the ordinary lock (nothing to negotiate — the daemon's own dispatch sweep is what
    /// actually claims it, exactly as a forced takeover's own final message already defers to that
    /// sweep), a task this node already holds says so, and a task held by another node gets a
    /// <see cref="MessageKind.ClaimRequest"/> envelope queued for it. Queues only — never flushes:
    /// the daemon's own message sweep sends it, the same <c>h9k message send</c> convention.
    /// </summary>
    private static async Task<int> RunCooperativeAsync(
        IDocumentStore store, IDocumentSession session, Guid taskId, string reason, TrackerAssignmentTake? take,
        CancellationToken cancellationToken)
    {
        StreamState? fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        if (task.HolderNodeId is not { } holderNodeId)
        {
            // The ordinary dispatch sweep only ever claims a Queued task whose own AssignedOwnerId
            // matches the claiming node's owner (DispatchEngine's own queue query, Decisions Log
            // #34) — a fact this command has to check rather than assume, since a task with no
            // ledger holder can still be assigned to somebody else's owner (independent pre-PR
            // review, cycle 1, conformance lens: the earlier version promised the sweep would claim
            // it regardless, which is simply false when the owners differ, and nothing here
            // reassigns it).
            if (task.AssignedOwnerId == context.OwnerId)
            {
                if (task.State == TaskState.Queued)
                {
                    AnsiConsole.MarkupLine(
                        $"[green]Task {taskId} has no current holder[/] — nothing to ask. It claims through "
                        + "the ordinary lock on this node's next dispatch sweep.");
                }
                else
                {
                    // The dispatch sweep's own queue query reads State == Queued as well as
                    // AssignedOwnerId (DispatchEngine.cs's own ReadQueueAsync) — a no-holder task
                    // assigned to this node's own owner is not automatically headed for a claim
                    // unless it is also Queued (independent pre-PR review, cycle 3, conformance
                    // lens: this branch previously promised the sweep would pick it up regardless
                    // of state).
                    AnsiConsole.MarkupLine(
                        $"[yellow]Task {taskId} has no current holder[/] and is assigned to this node's own "
                        + $"owner, but it is {task.State.Value}, not Queued — the dispatch sweep only claims a "
                        + "Queued task, so there is nothing for it to pick up yet.");
                }
            }
            else
            {
                OwnerDetails? assignedOwner = task.AssignedOwnerId is { } assignedOwnerId
                    ? await session.LoadAsync<OwnerDetails>(assignedOwnerId, cancellationToken)
                    : null;
                string assignedOwnerLabel = assignedOwner?.Name
                    ?? task.AssignedOwnerId?.ToString()
                    ?? "no owner";
                if (task.AssignedOwnerId is null)
                {
                    // TaskDecider.Unassign refuses anything that is not Queued or Blocked
                    // (TaskDecider.cs:1091) — a Published, unassigned task needs no unassign step
                    // first (independent pre-PR review, cycle 3, conformance lens: the unassign step
                    // this branch used to suggest fails on the very state that reaches it).
                    AnsiConsole.MarkupLine(
                        $"[yellow]Task {taskId} has no current holder[/] and is already unassigned — this node's "
                        + "dispatch sweep will never claim it, and there is no cooperative lever here for a task "
                        + $"nobody holds yet. Assign it first: h9k task assign {taskId} <owner>.");
                }
                else if (task.State.IsAssigned)
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]Task {taskId} has no current holder[/], but it is assigned to "
                        + $"{assignedOwnerLabel.EscapeMarkup()}, not this node's own owner — this node's dispatch "
                        + "sweep will never claim it, and there is no cooperative lever here for a task nobody "
                        + $"holds yet. Move it first: h9k task unassign {taskId} && h9k task assign {taskId} <owner>.");
                }
                else if (task.State.IsTerminal)
                {
                    // task.State.IsAssigned (Queued||Blocked) is not the same fact as "AssignedOwnerId is
                    // null": a closeout path (Abandon, the terminal Done) appends its own ordinary
                    // TaskHolderReleased alongside the closeout event, clearing HolderNodeId but leaving
                    // AssignedOwnerId and the terminal State untouched (independent pre-PR review, cycle 4,
                    // adversarial lens) — so a task can reach here with a non-null AssignedOwnerId while its
                    // State is neither Queued/Blocked nor unassigned. Neither remedy applies: TaskDecider.Assign
                    // and TaskDecider.Unassign both refuse anything but Published/Queued/Blocked
                    // (TaskDecider.cs:1044, 1091) — its story has already ended. TaskState.IsTerminal (Done or
                    // Abandoned) is the actual boundary here, not merely "not Queued/Blocked" (independent
                    // pre-PR review, cycle 5, conformance lens): Claimed, NeedsHuman, AwaitingAuthor and Failed
                    // all reach this branch too, and none of them has ended.
                    AnsiConsole.MarkupLine(
                        $"[yellow]Task {taskId} has no current holder[/] — it is {task.State.Value}, and its "
                        + $"story has already ended. It is still recorded as assigned to "
                        + $"{assignedOwnerLabel.EscapeMarkup()}, but nothing here can move or reclaim it.");
                }
                else
                {
                    // Claimed, NeedsHuman, AwaitingAuthor and Failed all reach here with no ledger holder:
                    // an interactive claim (h9k task work/start) records TaskClaimed with the Guid.Empty
                    // sentinel, which TaskAggregate.Apply(TaskClaimed) skips the holder write for
                    // — so the task can be under active human work, or waiting on that human's own next
                    // decision, without ever naming a ledger holder to ask (independent pre-PR review, cycle
                    // 5, conformance lens: the terminal wording above wrongly told a reader that a live claim
                    // was over).
                    AnsiConsole.MarkupLine(
                        $"[yellow]Task {taskId} has no current holder[/] — it is {task.State.Value}, which "
                        + "records no ledger holder for an interactive claim. It is still recorded as assigned "
                        + $"to {assignedOwnerLabel.EscapeMarkup()}; there is no cooperative lever here, since "
                        + "nothing holds it to ask.");
                }
            }

            return ExitCodes.Ok;
        }

        if (holderNodeId == context.NodeId)
        {
            AnsiConsole.MarkupLine($"[green]This node already holds task {taskId}.[/]");
            return ExitCodes.Ok;
        }

        ProjectDetails project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"No project {task.ProjectId}.");
        string? ownerRootFingerprint = await OwnerRootFingerprintResolver.ResolveAsync(session, context.OwnerId, cancellationToken);
        if (ownerRootFingerprint is null)
        {
            throw new DomainValidationException(
                "This node's owner has not claimed a root fingerprint yet, so a claim request's own "
                + "from-owner field has nothing to carry. Run h9k project join first.");
        }

        // The requester's own tracker identity, read locally and carried on the envelope, because
        // the holder's own node has no other way to learn it: every teammate's tracker credentials
        // are local to their own install (ClaimGate's own doc). Best effort — a project that is not
        // gated, or a task with no gated item, or a read that fails, all carry null, which the
        // grant's own gated tracker move reads as "assign it to the requester by hand" rather than
        // as a reason to refuse the request outright.
        string? trackerIdentity = null;
        if (project.ClaimGate != ClaimGate.Off && task.ExternalReference is not null)
        {
            try
            {
                TrackerAssignmentTake tracking = take ?? new TrackerAssignmentTake(new ProjectScopedGitHubRunner(store).Runner);
                trackerIdentity = await tracking.ResolveOwnIdentityAsync(
                    store, project.ClaimGate, task.ExternalReference, project.RepositoryPath, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Warning:[/] this install's own tracker identity could not be read — "
                    + $"{exception.Message.EscapeMarkup()} If node {DomainId.Short(holderNodeId)} grants this "
                    + "request, the gated tracker move will need to be done by hand.");
            }
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        ClaimEnvelopeCodec.ClaimRequestRecord request = new(
            taskId, context.NodeId, context.OwnerId, ownerRootFingerprint, reason.Trim(), trackerIdentity);
        await MessageOutbox.QueueAsync(
            session, context.NodeId, project.Id, ownerRootFingerprint, MessageAudience.Node(holderNodeId),
            taskId.ToString(), MessageKind.ClaimRequest, ClaimEnvelopeCodec.Encode(request), now, cancellationToken);

        int timeoutMinutes = project.TakeTimeoutMinutes ?? DefaultTakeTimeoutMinutes;
        AnsiConsole.MarkupLine(
            $"[blue]Asked[/] node {DomainId.Short(holderNodeId)} for task {taskId} — reason: "
            + $"{reason.Trim().EscapeMarkup()}");
        AnsiConsole.MarkupLine(
            "[dim]Waiting — the daemon's next message sweep sends it, and its own reaction on node "
            + $"{DomainId.Short(holderNodeId)} answers it. Check h9k task show {taskId} or h9k status for the "
            + $"answer; no answer within {timeoutMinutes} minute(s) means --force is the way on.[/]");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Best-effort undo of the ledger override this command just wrote, for the paths where this
    /// command's own final append never lands (a concurrent change on the task's own stream
    /// between the refetch and the commit, a refusal at that refetch, a transient failure on the
    /// commit itself): writes <paramref name="previousHolder"/> back — the holder
    /// <c>TryOverrideAsync</c> actually overwrote — rather than clearing the ledger to no holder
    /// at all, so the record never sits in a state the domain aggregate itself never recorded
    /// (adversarial pre-PR review, cycle 2). Which of the two that is, is
    /// <see cref="ResolveUndo"/>'s call rather than this method's: it reads a fresh holder and
    /// this method writes what it decided. A failure to restore here is reported to the
    /// operator rather than left silent, since — unlike
    /// <c>DispatchEngine.ReleaseLedgerHolderBestEffortAsync</c>'s own retry sweep — this CLI
    /// process has no later tick to retry it on.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the ledger no longer names this node afterward (the restore
    /// landed, or there was nothing left to restore); <see langword="false"/> if it still does —
    /// the caller's own thrown message reads accordingly (conformance pre-PR review, cycle 4)
    /// rather than always claiming the rollback landed.
    /// </returns>
    private static async Task<bool> RestoreLedgerOverrideBestEffortAsync(
        ILedger ledger, string repositoryPath, Guid taskId, Guid nodeId, TaskRecordHolder? previousHolder,
        LedgerCommitter committer, LedgerSigningKey signingKey, CancellationToken cancellationToken)
    {
        try
        {
            HolderReleaseResult restore = await TaskLedgerHolder.TryRestoreAsync(
                ledger, repositoryPath, taskId, nodeId, previousHolder, committer, signingKey, cancellationToken);
            if (restore.Verdict == HolderReleaseVerdict.Failed)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Warning:[/] the ledger holder override for task {taskId} could not be rolled "
                    + $"back — {restore.FailureReason} It still names this node; an owner-role member "
                    + "will need to force a takeover away from it again.");
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Warning:[/] rolling back the ledger holder override for task {taskId} failed — "
                + $"{exception.Message} It still names this node; an owner-role member will need to force "
                + "a takeover away from it again.");
            return false;
        }
    }

    /// <summary>
    /// Owner role required (idea 202383dc, item 4) — the identical gate <c>h9k project member
    /// remove</c> and <c>h9k project invite</c> already apply: this node's own root must currently
    /// hold the owner role in the task's own project chain.
    /// </summary>
    private static async Task AssertOwnerRoleAsync(
        IDocumentSession session, BootstrapContext context, ProjectDetails project, ILedgerChainReader chainReader,
        NodeKeyStore keyStore, CancellationToken cancellationToken)
    {
        OwnerAggregate owner = await session.Events.AggregateStreamAsync<OwnerAggregate>(context.OwnerId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No owner {context.OwnerId}.");
        if (owner.RootFingerprint is not { } myRoot)
        {
            throw new DomainValidationException("This node has no root fingerprint yet — run h9k project join first.");
        }

        NodeSigningKey key = await keyStore.EnsureAsync(context.NodeId, cancellationToken);
        TrustChain chain;
        try
        {
            chain = await chainReader.ComputeAsync(project.RepositoryPath, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            throw new DomainValidationException(
                $"Could not read '{project.Name}'s own ledger chain: {exception.Message} Re-run "
                + "h9k task take --force --reason \"...\" once the remote is reachable again.");
        }

        if (chain.RoleOf(myRoot) != MembershipRole.Owner || !chain.IsEnrolledInOwner(key.Fingerprint, myRoot))
        {
            throw new DomainValidationException(
                $"This node's own owner ({myRoot}) does not currently hold the owner role in "
                + $"'{project.Name}' — only an owner-role member's own node may force a takeover "
                + "(idea 202383dc, item 4).");
        }
    }

    /// <summary>
    /// The evidence this command prints and then proceeds past (idea 202383dc, item 4: "the
    /// command prints the evidence it has ... and proceeds on the operator's judgment, never on
    /// detected absence"). "When that node's outbox last moved" is read from the most recent
    /// message this node has ever received FROM the holder's own node — the one durable, already-
    /// replicated proxy this platform has for "when did we last hear anything from them"; there is
    /// no stored commit timestamp for a sender's outbox ref anywhere in the existing message or
    /// replication seams, and this command is not the place to add one.
    /// </summary>
    private static async Task PrintEvidenceAsync(
        IDocumentSession session, Guid holderNodeId, DateTimeOffset? holderSince, CancellationToken cancellationToken)
    {
        NodeDetails? holderNode = await session.LoadAsync<NodeDetails>(holderNodeId, cancellationToken);
        string holderName = holderNode?.MachineName.IsNotBlank() == true
            ? holderNode.MachineName
            : $"node {DomainId.Short(holderNodeId)}";
        string since = holderSince is { } sinceAt ? sinceAt.ToString("u") : "an unrecorded time";

        MessageDetails? lastMessage = await session.Query<MessageDetails>()
            .Where(message => message.FromNodeId == holderNodeId && message.ReceivedAt != null)
            .OrderByDescending(message => message.ReceivedAt)
            .FirstOrDefaultAsync(cancellationToken);
        string outboxFact = lastMessage?.ReceivedAt is { } heardAt
            ? $"last heard from at {heardAt:u}"
            : "nothing has ever been heard from its outbox on this node";

        AnsiConsole.MarkupLine(
            $"[yellow]Evidence:[/] currently held by {holderName.EscapeMarkup()}, since {since} — {outboxFact}.");
        AnsiConsole.MarkupLine(
            "[yellow]This is not a detected absence[/] — nothing here confirms the holder is actually "
            + "gone. Proceeding overrides it on your own judgment.");
        AnsiConsole.MarkupLine(
            $"[yellow]If {holderName.EscapeMarkup()} still has a live run for this task, that node stops it "
            + "the next time this takeover replicates there — recorded as superseded by takeover, its "
            + "transcript kept, no pull request action follows from it.[/]");
    }
}
