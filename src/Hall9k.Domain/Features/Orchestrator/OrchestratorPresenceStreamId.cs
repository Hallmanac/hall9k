using System.Security.Cryptography;
using System.Text;

namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// The deterministic Marten stream id for one project's orchestrator presence on one node (idea
/// 89471598, piece 1) — the same reasoning
/// <see cref="Hall9k.Domain.Features.Trust.UnverifiedLedgerWriteStreamId"/> already applies:
/// never <see cref="Guid.NewGuid"/>, a stable address derived from a key that already exists, so
/// every launch, shutdown, and loss of a window for this project on this node folds into one
/// stream rather than opening a new one per window.
/// <para>
/// Keyed on the node as well as the project because presence is a node-local fact: the same
/// project registered on two machines has one orchestrator window per machine, and neither one's
/// liveness is answerable from the other (its process table is not readable from here).
/// </para>
/// </summary>
public static class OrchestratorPresenceStreamId
{
    public static Guid For(Guid nodeId, Guid projectId) =>
        Derive("hall9k-orchestrator-presence", nodeId.ToString("N"), projectId.ToString("N"));

    private static Guid Derive(params string[] parts)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts)));
        return new Guid(hash[..16]);
    }
}
