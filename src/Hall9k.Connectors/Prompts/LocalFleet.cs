using Hall9k.Connectors.Trust;

namespace Hall9k.Connectors.Prompts;

/// <summary>
/// The local owner's own fleet as a prompt builder needs it to decide whose replicated text is
/// foreign (security review idea 6be68ee2, prompt-builders findings 1 to 6): the node ids the
/// ledger chain vouches for that owner (<see cref="TrustedOwner.FleetNodeIds"/>), and, when the
/// caller has the chain in hand, the chain itself so a foreign note can be labelled with the owner
/// it came from.
/// <para>
/// A builder takes this as a nullable parameter where null means the fleet is not known, and not
/// known reads as "nobody is local": replicated text is then fenced, never trusted. That fail-closed
/// reading is deliberate and it has a stated cost. After a teammate forwards a record in a
/// catch-up answer, that record's verified sender is the teammate, so it reads foreign even when
/// the fact originated on one of this owner's own nodes.
/// </para>
/// </summary>
public sealed class LocalFleet(IReadOnlySet<Guid> nodeIds, TrustChain? chain = null)
{
    /// <summary>The node ids vouched into the local owner's fleet, the only senders whose text is unfenced.</summary>
    public IReadOnlySet<Guid> NodeIds { get; } = nodeIds;

    /// <summary>The chain the fleet was read from, or null when the caller only had the ids. Read for labels alone, never for a trust decision.</summary>
    public TrustChain? Chain { get; } = chain;

    /// <summary>
    /// The fleet of <paramref name="ownerRootFingerprint"/> in <paramref name="chain"/>, or null
    /// when the chain does not name that owner (the answer for a fresh, still-unvouched project, and
    /// for an owner whose fingerprint could not be resolved): unknown, not empty, so it fences.
    /// </summary>
    public static LocalFleet? Of(TrustChain chain, string? ownerRootFingerprint) =>
        ownerRootFingerprint is not null && chain.OwnerChains.TryGetValue(ownerRootFingerprint, out TrustedOwner? owner)
            ? new LocalFleet(new HashSet<Guid>(owner.FleetNodeIds()), chain)
            : null;

    /// <summary>
    /// The fleet for a caller that reads it from the ledger on a miss: the repository's chain
    /// through <paramref name="chainReader"/>, then <see cref="Of"/>. Null for every way the answer
    /// can be missing (no repository path, no owner fingerprint, a chain that will not read), each
    /// of which is "not known" and therefore fences. Not gated on anything: the caller decides
    /// whether a rendered field carries a sender at all (<see cref="ReplicatedNote.CarriesSender"/>)
    /// before paying for the walk.
    /// </summary>
    public static async ValueTask<LocalFleet?> ReadAsync(
        ILedgerChainReader chainReader, string? repositoryPath, string? ownerRootFingerprint,
        CancellationToken cancellationToken)
    {
        if (ownerRootFingerprint is null || string.IsNullOrWhiteSpace(repositoryPath))
        {
            return null;
        }

        try
        {
            return Of(await chainReader.ComputeAsync(repositoryPath, cancellationToken), ownerRootFingerprint);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Whatever stopped the read, a prompt must still compose, and no fleet fences. Broader
            // than the InvalidOperationException a chain read reports for a fetch or git failure,
            // because a git binary that will not start or a missing directory surfaces as an IO
            // exception, and a prompt is composed far from anything able to handle one.
            return null;
        }
    }
}
