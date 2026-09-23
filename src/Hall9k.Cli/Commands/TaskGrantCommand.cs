using JasperFx.Events;
using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Marten.Events;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// h9k task grant: the holder's own human answers a parked cooperative take request (idea
/// 202383dc, item 5, "a member can ask a holder for a task", <c>take-policy ask</c>) by granting
/// it — through <see cref="ClaimRequestEngine.GrantAsync"/>, the identical method a
/// <c>take-policy auto</c> grant runs on receipt ("grant runs the auto release").
/// </summary>
public sealed class TaskGrantCommand : Hall9kAsyncCommand<TaskGrantCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        return await RunAsync(
            store, session, settings, new GitLedger(new ConsoleWorktreeLogger<GitLedger>()), take: null,
            new NodeKeyStore(), cancellationToken);
    }

    internal static async Task<int> RunAsync(
        IDocumentStore store,
        IDocumentSession session,
        Settings settings,
        ILedger ledger,
        TrackerAssignmentTake? take,
        NodeKeyStore keyStore,
        CancellationToken cancellationToken)
    {
        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        StreamState? fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        if (task.HolderNodeId != context.NodeId)
        {
            throw new DomainConflictException(
                $"Task {taskId} is not held by this node — only the current holder can grant a "
                + "cooperative take request.");
        }

        if (task.PendingTakeRequestedByNodeId is not { } requesterNodeId
            || task.PendingTakeRequestedByOwnerId is not { } requesterOwnerId)
        {
            throw new DomainConflictException($"Task {taskId} carries no pending take request to grant.");
        }

        ProjectDetails project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"No project {task.ProjectId}.");
        (LedgerCommitter committer, LedgerSigningKey signingKey, string ownerFingerprint) =
            await TaskRecordPublication.ResolveIdentityAsync(session, context, cancellationToken);

        // The automatic take-policy-auto path refuses a grant while a run is live for this task on
        // this node (ClaimRequestEngine.ReceiveRequestAsync's own FindLiveRunAsync check) rather
        // than release the ledger holder out from under it — this human-driven door has to make the
        // identical check itself, since GrantAsync carries no such guard of its own: releasing the
        // holder here would land the task Queued while this node's own agent process is still
        // working the branch, and the requester's next dispatch sweep would launch a second run
        // against it (conformance review, cycle 5, high).
        RunListItem? liveRun = await ClaimRequestEngine.FindLiveRunAsync(session, taskId, context.NodeId, cancellationToken);
        if (liveRun is not null)
        {
            throw new DomainConflictException(
                $"Task {taskId} has a run live on this node, started at {liveRun.DispatchedAt:u} — granting now "
                + "would release the ledger holder while that run is still working the branch, and the "
                + "requester's own next dispatch sweep would launch a second run against it. Stop it first with "
                + $"h9k run kill {taskId}, then retry h9k task grant.");
        }

        ClaimRequestOutcome outcome = await ClaimRequestEngine.GrantAsync(
            store, session, project, taskId, requesterNodeId, requesterOwnerId,
            task.PendingTakeRequesterTrackerIdentity, ledger, committer, signingKey, take, context.NodeId,
            ownerFingerprint, DateTimeOffset.UtcNow, cancellationToken);

        AnsiConsole.MarkupLine($"[green]Granted[/] task {taskId} to node {DomainId.Short(requesterNodeId)}.");
        if (outcome.TrackerFailureReason is { } trackerFailure)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning:[/] {trackerFailure.EscapeMarkup()}");
        }

        // TaskAggregate.Apply(TaskHolderReleased) lands a grant on Queued only when no unmet
        // dependency remains, Blocked otherwise — and the dispatch sweep's own queue query
        // (DispatchEngine.cs's own ReadQueueAsync) reads State == Queued, so a grant on a task
        // still carrying one leaves nothing for that sweep to claim yet (class sweep, independent
        // pre-PR review, cycle 3, adversarial lens: the same "promises a claim the dispatch sweep
        // will not make" shape the no-holder path's own message was fixed for). UnmetDependencies
        // itself is untouched by the grant, so the pre-grant aggregate already answers this.
        AnsiConsole.MarkupLine(task.UnmetDependencies.Count == 0
            ? "[dim]The requester's own node claims it through the ordinary lock on its next dispatch sweep.[/]"
            : "[yellow]The requester's own node cannot claim it yet[/] — it still carries an unmet dependency, "
                + "so it lands Blocked rather than Queued until that clears.");
        return ExitCodes.Ok;
    }
}
