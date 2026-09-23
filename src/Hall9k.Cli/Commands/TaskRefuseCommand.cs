using JasperFx.Events;
using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
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
/// h9k task refuse: the holder's own human answers a parked cooperative take request (idea
/// 202383dc, item 5, "a member can ask a holder for a task", <c>take-policy ask</c>) by refusing
/// it, with --reason. Carries no ledger or claim change of its own.
/// </summary>
public sealed class TaskRefuseCommand : Hall9kAsyncCommand<TaskRefuseCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--reason <REASON>")]
        [Description("Why the take request is being refused — required, recorded on the task's own stream.")]
        public string? Reason { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        return await RunAsync(store, session, settings, cancellationToken);
    }

    internal static async Task<int> RunAsync(
        IDocumentStore store, IDocumentSession session, Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Reason.IsBlank())
        {
            throw new DomainValidationException("A cooperative refusal needs --reason: why the take request is being refused.");
        }

        string reason = settings.Reason;

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
                $"Task {taskId} is not held by this node — only the current holder can refuse a "
                + "cooperative take request.");
        }

        if (task.PendingTakeRequestedByNodeId is not { } requesterNodeId
            || task.PendingTakeRequestedByOwnerId is not { } requesterOwnerId)
        {
            throw new DomainConflictException($"Task {taskId} carries no pending take request to refuse.");
        }

        ProjectDetails project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"No project {task.ProjectId}.");
        string? ownerRootFingerprint = await OwnerRootFingerprintResolver.ResolveAsync(session, context.OwnerId, cancellationToken)
            ?? throw new DomainValidationException("This node's owner has not claimed a root fingerprint yet. Run h9k project join first.");

        await ClaimRequestEngine.RefuseAsync(
            store, session, project, taskId, requesterNodeId, requesterOwnerId, reason, context.NodeId,
            ownerRootFingerprint, DateTimeOffset.UtcNow, cancellationToken);

        AnsiConsole.MarkupLine(
            $"[yellow]Refused[/] the take request on task {taskId} from node {DomainId.Short(requesterNodeId)} — "
            + $"reason: {reason.EscapeMarkup()}");
        return ExitCodes.Ok;
    }
}
