using Hall9k.Domain.Features.Owner;
using Marten;

namespace Hall9k.Connectors.Trust;

/// <summary>
/// Closes the gap draft f245371d found, absorbed by task f53fecfd's own criterion 4:
/// <see cref="OwnerAggregate.RootFingerprintVerified"/> is set once, at <c>h9k project join</c> time,
/// and never revisited — so a node that claimed a root with <c>--owner</c> (unverified) and was later
/// vouched into it on the ledger by someone else, or whose vouch a carried record later establishes,
/// stays "claimed, unverified" in <c>h9k owner show</c> and <c>h9k status</c> forever, even though
/// <c>h9k project members</c> already reads the same live chain and shows it verified.
/// <para>
/// Reconciled from a chain read wherever one is already in hand — <c>h9k project join</c>, at the end
/// of every run, and the daemon's own message sweep, once per eligible project per tick — rather than
/// through a dedicated poll of its own: cheap (one in-memory check against a chain already computed
/// for another reason), idempotent (a no-op the moment <see cref="OwnerAggregate.RootFingerprintVerified"/>
/// is already true), and self-healing within one sweep interval of whichever chain read first sees
/// the enrollment.
/// </para>
/// </summary>
public static class OwnerRootVerificationReconciler
{
    /// <summary>
    /// Pure: whether this owner's root claim should now flip to verified, given one project's own
    /// chain read. Never true once already verified — nothing here ever un-verifies a root; that is
    /// a revocation's own concern, played out through the ordinary chain read every other caller
    /// already trusts.
    /// </summary>
    public static bool ShouldVerify(OwnerAggregate owner, string myKeyFingerprint, TrustChain chain) =>
        owner.RootFingerprint is { } root && !owner.RootFingerprintVerified && chain.IsEnrolledInOwner(myKeyFingerprint, root);

    /// <summary>
    /// Appends <see cref="OwnerDecider.VerifyRoot"/> when <see cref="ShouldVerify"/> holds — the
    /// caller still owns <c>SaveChangesAsync</c>. Returns whether it did, so a caller folding this
    /// into a larger outcome (<c>h9k project join</c>'s own report) knows without a second read.
    /// </summary>
    public static bool Reconcile(
        IDocumentSession session, OwnerAggregate owner, string myKeyFingerprint, TrustChain chain, DateTimeOffset now)
    {
        if (!ShouldVerify(owner, myKeyFingerprint, chain))
        {
            return false;
        }

        session.Events.Append(owner.Id, OwnerDecider.VerifyRoot(owner, now));
        return true;
    }
}
