using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Trust;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Owner;
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
/// h9k task take --force: an owner-role member overrides an absent holder (idea 202383dc, item
/// 4). Absence is never detected — presence detection is dead, never parked — so this command
/// prints whatever evidence it has and proceeds on the operator's own judgment: the holder it
/// currently reads, since when, and the last time anything from that node's own outbox was
/// observed here. The cooperative take (<c>h9k task take &lt;id&gt;</c> with no <c>--force</c>,
/// idea 202383dc, item 5) is a separate, not-yet-built door — this command refuses without
/// <c>--force</c> rather than silently doing nothing.
/// </summary>
public sealed class TaskTakeCommand : Hall9kAsyncCommand<TaskTakeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--force")]
        [Description(
            "Override the current holder unilaterally, on the operator's own judgment — the only "
            + "form this command supports today. The cooperative take (no --force, idea 202383dc, "
            + "item 5) is not built yet.")]
        public bool Force { get; init; }

        [CommandOption("--reason <REASON>")]
        [Description("Why the current holder is being overridden — required with --force, recorded on the task's own stream.")]
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
        if (!settings.Force)
        {
            throw new DomainValidationException(
                "h9k task take needs --force --reason \"<why>\": the cooperative take (idea 202383dc, "
                + "item 5) is not built yet, so a forced override is the only door this command opens today.");
        }

        if (settings.Reason.IsBlank())
        {
            throw new DomainValidationException(
                "A forced take needs --reason: why the current holder is being overridden.");
        }

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
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
            task, context.NodeId, context.OwnerId, ownerFingerprint, settings.Reason!, context.OwnerId, now);

        // Gated projects run the existing tracker take first (idea 202383dc, item 4, criterion 2):
        // its own refusal — DomainBusinessRuleException, Program.cs's own exit 70 mapping — stops
        // the override outright, before the ledger holder is ever touched, with the tracker's own
        // sentence as the reason.
        await TrackerClaimCheck.TakeOrRefuseAsync(
            store, taskId, project, task.ExternalReference?.ToString(), take, cancellationToken);

        TaskRecordHolder candidate = new(ownerFingerprint, context.NodeId, myNode?.MachineName ?? Environment.MachineName, now);

        HolderOverrideResult result = await TaskLedgerHolder.TryOverrideAsync(
            ledger, project.RepositoryPath, taskId, candidate, committer, signingKey, cancellationToken);
        switch (result.Verdict)
        {
            case HolderOverrideVerdict.AlreadyOverridden:
                string winner = result.CurrentHolder is { } currentHolder
                    ? $"node {DomainId.Short(currentHolder.NodeId)} ({currentHolder.OwnerFingerprint}), since {currentHolder.Since:u}"
                    : "another node";
                throw new DomainConflictException(
                    $"Task {taskId}: another override already landed while this one was deciding — the "
                    + $"ledger now names {winner}. Two overriders cannot both win; this one lost the race.");
            case HolderOverrideVerdict.Failed:
                throw new DomainConflictException(
                    $"Task {taskId}: the ledger holder could not be overridden — {result.FailureReason} "
                    + "Nothing was recorded; re-run h9k task take --force once that settles.");
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
                freshTask, context.NodeId, context.OwnerId, ownerFingerprint, settings.Reason!, context.OwnerId, now);
        }
        catch (DomainConflictException)
        {
            // The task moved out of Claimed/NeedsHuman during the tracker take or the ledger
            // override above — the exact race the re-validation above exists to catch. The ledger
            // override already landed, so it is rolled back here exactly as it would be had the
            // append itself lost the race below; RestoreLedgerOverrideBestEffortAsync reports its
            // own outcome to the operator, and the decider's own conflict message (naming the
            // task's actual current state) is left to propagate unchanged.
            await RestoreLedgerOverrideBestEffortAsync(
                ledger, project.RepositoryPath, taskId, context.NodeId, result.PreviousHolder, committer,
                signingKey, cancellationToken);
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
            // whatever the domain aggregate — still naming the ORIGINAL holder, since this append
            // never landed — believes about it. That is the exact double-claim hazard this rollback
            // exists to prevent, not reintroduce.
            bool rolledBack = await RestoreLedgerOverrideBestEffortAsync(
                ledger, project.RepositoryPath, taskId, context.NodeId, result.PreviousHolder, committer,
                signingKey, cancellationToken);
            // The message names what actually happened (conformance pre-PR review, cycle 4) rather
            // than always claiming the rollback landed: on the rollback-failed path,
            // RestoreLedgerOverrideBestEffortAsync has already printed the warning that says so, and
            // an operator reading the thrown error text alone (the CLI's own self-correction
            // standard, AGENTS.md) must not be told the opposite of what just happened.
            throw new DomainConflictException(
                rolledBack
                    ? $"Task {taskId} changed while recording this takeover — the ledger holder override "
                        + "was rolled back rather than left pointing at a node whose own task stream never "
                        + "recorded taking it. Check h9k task show and re-run h9k task take --force once "
                        + "that settles."
                    : $"Task {taskId} changed while recording this takeover, and the ledger holder override "
                        + "could not be rolled back — see the warning above. It still names this node even "
                        + "though this node's own task stream never recorded taking it. Check h9k task show "
                        + "and re-run h9k task take --force once that settles.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Any other SaveChangesAsync failure — a transient Npgsql error, a timeout —
            // (adversarial pre-PR review, cycle 4) leaves the identical split the race above
            // leaves: the ledger already names this node while this node's own stream never
            // recorded taking it. Rolled back here the same way, then the original failure is
            // left to propagate rather than wrapped in a conflict message that would not describe
            // what actually went wrong.
            await RestoreLedgerOverrideBestEffortAsync(
                ledger, project.RepositoryPath, taskId, context.NodeId, result.PreviousHolder, committer,
                signingKey, cancellationToken);
            throw;
        }

        await Doorbell.RingAsync($"task-taken-over:{taskId}", cancellationToken);

        AnsiConsole.MarkupLine(
            $"[green]Task {taskId} taken over[/] from node {DomainId.Short(previousHolderNodeId)} — "
            + $"reason: {settings.Reason!.EscapeMarkup()}");
        AnsiConsole.MarkupLine(
            "[dim]This node claims it on its next dispatch sweep and resumes the branch where the "
            + "previous run left it.[/]");

        return ExitCodes.Ok;
    }

    /// <summary>
    /// Best-effort undo of the ledger override this command just wrote, for the one path where
    /// this command's own final append never lands (a concurrent change on the task's own stream
    /// between the refetch and the commit): restores <paramref name="previousHolder"/> — the
    /// holder <c>TryOverrideAsync</c> actually overwrote — rather than clearing the ledger to no
    /// holder at all, so the record never sits in a state the domain aggregate itself never
    /// recorded (adversarial pre-PR review, cycle 2). A failure to restore here is reported to the
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
