using Hall9k.Connectors.Prompts;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Marten;

namespace Hall9k.Daemon.AutoPrReview;

/// <summary>
/// The local owner's fleet for one project, for the prompt builders that fence replicated text
/// from outside it (security review idea 6be68ee2, prompt-builders findings 1 to 6). Reads the
/// snapshot the message sweep already keeps (<see cref="EnrolledNodeSnapshots"/>) and only on a miss
/// pays for a ledger read of its own (<see cref="ILedgerChainReader.ComputeAsync"/>), so a prompt
/// composed between sweeps never waits on git.
/// <para>
/// Null means not known, and a caller passes it straight to a builder, where it fences: no snapshot,
/// no node row, no owner fingerprint, or a chain that will not read are all "not known", never
/// "empty and therefore fine". Callers ask only when a field they are about to render carries a
/// replicated sender (<see cref="ReplicatedNote.CarriesSender"/>), so a solo project never gets here.
/// </para>
/// </summary>
public sealed class LocalFleetProvider(
    IDocumentStore store, ILedgerChainReader chainReader, EnrolledNodeSnapshots snapshots)
{
    public async ValueTask<LocalFleet?> GetAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (snapshots.TryGet(projectId) is { } snapshot)
        {
            return new LocalFleet(new HashSet<Guid>(snapshot.FleetNodeIds), snapshot.Chain);
        }

        await using IQuerySession query = store.QuerySession();
        string machineName = Environment.MachineName;
        NodeDetails? node = (await query.Query<NodeDetails>()
            .Where(candidate => candidate.MachineName == machineName)
            .Take(1)
            .ToListAsync(cancellationToken)).FirstOrDefault();
        ProjectDetails? project = await query.LoadAsync<ProjectDetails>(projectId, cancellationToken);
        string? fingerprint = node is null
            ? null
            : await OwnerRootFingerprintResolver.ResolveAsync(query, node.OwnerId, cancellationToken);

        return await LocalFleet.ReadAsync(chainReader, project?.RepositoryPath, fingerprint, cancellationToken);
    }
}
