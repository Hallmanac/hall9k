using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Tasks.Queries;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Who is reading the throughput block: this node's owner, as <see cref="PhaseOwnerKey"/> keys an owner
/// in a per-owner split. Read-only, unlike the bootstrap other commands run, since a status screen must
/// not create the identity it is asking about.
/// </summary>
internal static class ThroughputViewer
{
    /// <summary>This node's owner key, or null when this machine has no registered node or its owner has no document yet.</summary>
    public static async Task<string?> ReadOwnerKeyAsync(IQuerySession session, CancellationToken cancellationToken)
    {
        string machineName = Environment.MachineName;
        NodeDetails? node = (await session.Query<NodeDetails>()
            .Where(candidate => candidate.MachineName == machineName)
            .Take(1)
            .ToListAsync(cancellationToken)).FirstOrDefault();
        if (node is null)
        {
            return null;
        }

        OwnerDetails? owner = await session.LoadAsync<OwnerDetails>(node.OwnerId, cancellationToken);
        return owner is null ? null : PhaseOwnerKey.Of(owner.RootFingerprint, owner.Id);
    }
}
