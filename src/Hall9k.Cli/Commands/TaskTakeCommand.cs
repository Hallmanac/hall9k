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

        // Gated projects run the existing tracker take first (idea 202383dc, item 4, criterion 2):
        // its own refusal — DomainBusinessRuleException, Program.cs's own exit 70 mapping — stops
        // the override outright, before the ledger holder is ever touched, with the tracker's own
        // sentence as the reason.
        await TrackerClaimCheck.TakeOrRefuseAsync(
            store, taskId, project, task.ExternalReference?.ToString(), take, cancellationToken);

        (LedgerCommitter committer, LedgerSigningKey signingKey, string ownerFingerprint) =
            await TaskRecordPublication.ResolveIdentityAsync(session, context, cancellationToken);
        NodeDetails? myNode = await session.LoadAsync<NodeDetails>(context.NodeId, cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
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

        TaskHolderTakenOver takenOver = TaskDecider.TakeOver(
            task, context.NodeId, context.OwnerId, ownerFingerprint, settings.Reason!, context.OwnerId, now);
        session.Events.Append(taskId, expectedVersion: fence.Version + 1, takenOver);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Task {taskId} changed while recording this takeover — the ledger holder now names this "
                + "node regardless, so re-running h9k task take --force will pick up cleanly. Check "
                + "h9k task show and try again.");
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
