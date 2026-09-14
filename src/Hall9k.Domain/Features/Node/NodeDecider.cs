using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Node;

public static class NodeDecider
{
    public static NodeRegistered Register(Guid id, Guid ownerId, string machineName, string operatingSystem, DateTimeOffset registeredAt)
    {
        if (ownerId == Guid.Empty)
        {
            throw new DomainValidationException("A node requires an owner — nodes belong to humans (PLAN.md §6.2).");
        }

        if (machineName.IsBlank())
        {
            throw new DomainValidationException("A node requires its machine name.");
        }

        return new NodeRegistered(id, ownerId, machineName, operatingSystem, registeredAt);
    }

    public static NodeKeyRegistered RegisterKey(
        NodeAggregate node, string publicKey, string keyFingerprint, DateTimeOffset registeredAt)
    {
        if (node.PublicKey is not null)
        {
            throw new DomainValidationException(
                $"Node {node.Id} already has a signing key ({node.KeyFingerprint}) — a node's key is "
                + "generated once and never replaced.");
        }

        if (publicKey.IsBlank() || keyFingerprint.IsBlank())
        {
            throw new DomainValidationException("A node's key registration needs both the public key and its fingerprint.");
        }

        return new NodeKeyRegistered(node.Id, publicKey, keyFingerprint, registeredAt);
    }

    public static NodeOwnerClaimed ClaimOwner(NodeAggregate node, string ownerFingerprint, DateTimeOffset claimedAt)
    {
        if (ownerFingerprint.IsBlank())
        {
            throw new DomainValidationException("A node's owner claim needs the fingerprint it is claiming.");
        }

        return new NodeOwnerClaimed(node.Id, ownerFingerprint, claimedAt);
    }
}
