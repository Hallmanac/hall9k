using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Infrastructure.Extensions;
using Marten;

namespace Hall9k.Daemon.Messaging;

/// <summary>This node's own identity for a message it is about to queue or flush: the owner root
/// fingerprint an envelope's own <c>from-owner</c> field carries, and the committer/signing key an
/// outbox write needs — the same triple <c>ProjectJoinCommand</c> builds inline for a ledger write,
/// resolved here instead so the message sweep never duplicates that construction.</summary>
public sealed record MessageNodeIdentity(string OwnerRootFingerprint, LedgerCommitter Committer, LedgerSigningKey SigningKey);

/// <summary>
/// Resolves <see cref="MessageNodeIdentity"/> for this node (idea 202383dc, M1b). Null when this
/// node's owner has not claimed a root fingerprint yet (<c>h9k project join</c> never ran) — there
/// is nothing to stamp an envelope's <c>from-owner</c> with, so the sweep simply has nothing to
/// send or receive as yet, the same "nothing to do" outcome an unregistered project gives it.
/// </summary>
public sealed class MessageNodeIdentityResolver(NodeKeyStore keyStore)
{
    public async Task<MessageNodeIdentity?> ResolveAsync(
        IQuerySession session, Guid nodeId, Guid ownerId, CancellationToken cancellationToken)
    {
        string? rootFingerprint = await OwnerRootFingerprintResolver.ResolveAsync(session, ownerId, cancellationToken);
        if (rootFingerprint is null)
        {
            return null;
        }

        OwnerDetails? owner = await session.LoadAsync<OwnerDetails>(ownerId, cancellationToken);
        NodeSigningKey key = await keyStore.EnsureAsync(nodeId, cancellationToken);
        string? ownerName = owner?.Name;
        string? ownerEmail = owner?.Email;
        LedgerCommitter committer = new(
            ownerName.IsNotBlank() ? ownerName : Environment.UserName,
            ownerEmail.IsNotBlank() ? ownerEmail : $"{nodeId}@hall9k.local");
        return new MessageNodeIdentity(rootFingerprint, committer, new LedgerSigningKey(key.PrivateKeyPath));
    }
}
