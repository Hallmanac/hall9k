using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Replication;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Extensions;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Asks every node of this owner's own fleet for everything it holds of one project, by hand (task
/// 252bc5cf). The daemon's own sweep already does this once per (peer, project) on its own, so this
/// command is the lever for the cases that rule cannot reach: a reconcile whose answer was squashed
/// out of the answering node's outbox before this node read it and which <c>h9k status</c> now
/// reports stalled, a fleet that changed shape mid-exchange, or simply wanting the exchange to run
/// again now rather than on the next tick.
/// <para>
/// Unlike <c>h9k project pull</c>, which broadcasts one ask to every project member, this addresses
/// one ask per fleet sibling by node id: only a sibling of this owner owes this node everything it
/// holds, and every other member would otherwise download a whole project's history it cannot
/// apply. The asks carry the same explicit bound <c>--since all</c> does, so each sibling answers
/// from the start of its own log rather than from its replication switch-on point.
/// </para>
/// <para>
/// Reads this project's own ledger chain to learn the fleet, the same live read
/// <c>h9k project members</c> performs and for the same reason: a node vouched in five minutes ago
/// is a sibling, and no local cache would know it yet. Everything after that read is local — the
/// asks are queued into this node's own store and the daemon's next message sweep is what actually
/// sends them.
/// </para>
/// </summary>
public sealed class ProjectReconcileCommand : Hall9kAsyncCommand<ProjectReconcileCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PROJECT>")]
        [Description("Project name, an unambiguous fragment of it, or its id.")]
        public string Project { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        return await RunAsync(session, settings, new GitLedgerChainReader(), DateTimeOffset.UtcNow, cancellationToken);
    }

    /// <summary>The whole command body, with its session and its chain reader handed in rather than
    /// constructed — this codebase's CLI commands have no other test seam.</summary>
    internal static async Task<int> RunAsync(
        IDocumentSession session, Settings settings, ILedgerChainReader chainReader, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        string? ownerRootFingerprint = await OwnerRootFingerprintResolver.ResolveAsync(
            session, context.OwnerId, cancellationToken);
        if (ownerRootFingerprint.IsBlank())
        {
            throw new DomainValidationException(EventStreamCatchUp.ProjectReconcileBlockedRefusal(project.Name));
        }

        TrustChain chain;
        try
        {
            chain = await chainReader.ComputeAsync(project.RepositoryPath, cancellationToken);
        }
        // The identical reporting h9k project members already does for the identical read: a genuine
        // network or credential failure is named with its real cause rather than folded into "this
        // owner has no fleet", which would read as a settled fact about the ledger.
        catch (InvalidOperationException exception)
        {
            throw new DomainValidationException(
                $"Could not read '{project.Name}'s own ledger chain, so this owner's fleet is unknown and nothing "
                + $"was queued: {exception.Message} Re-run h9k project reconcile {project.Name} once the remote is "
                + "reachable again.");
        }

        IReadOnlyList<Guid> peers = FleetReconcileRules.FleetPeers(chain, ownerRootFingerprint, context.NodeId);
        if (peers.Count == 0)
        {
            string nobody =
                $"'{project.Name}'s own ledger names no other node in this owner's fleet, so there is nobody "
                + "to reconcile with and nothing was queued. h9k node vouch adds a node to a fleet; "
                + "h9k project members shows what this ledger currently sees.";
            AnsiConsole.MarkupLineInterpolated($"[dim]{nobody}[/]");
            return ExitCodes.Ok;
        }

        foreach (Guid peer in peers)
        {
            await EventCatchUpCoordinator.RequestFleetReconcileByHandAsync(
                session, project.Id, peer, context.NodeId, ownerRootFingerprint, now, cancellationToken);
        }

        await session.SaveChangesAsync(cancellationToken);

        string standing = project.IsEligibleForMessaging()
            ? "the daemon's next message sweep sends them"
            : "they stay queued until this project is eligible for messaging (not archived, with a repository) — "
                + "the daemon's sweep cannot send them yet";
        string asked = $"{peers.Count} node(s) in this owner's fleet for everything they hold of {project.Name}";
        AnsiConsole.MarkupLineInterpolated($"[blue]Asked[/] {asked} [dim]({standing})[/].");
        AnsiConsole.MarkupLine(
            "[dim]Each ask starts that peer's reconcile over, so the counts h9k status shows for it are this "
            + "exchange's rather than a previous one's. Answers apply idempotently over history this node already "
            + "holds, and a private task or idea is never served. A reconcile completes only when the answering "
            + "peer says its answer is finished, which h9k status reports along with how many streams are still "
            + "held tail-only.[/]");
        return ExitCodes.Ok;
    }
}
