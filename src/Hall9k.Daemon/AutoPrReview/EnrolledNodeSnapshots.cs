using System.Collections.Concurrent;
using Hall9k.Connectors.Trust;

namespace Hall9k.Daemon.AutoPrReview;

/// <summary>
/// One project's fleet as the message sweep last computed it: this node's own owner's fleet, for
/// <see cref="AutoPrReviewObservation.DecideMintHold"/> to rank against, and the whole project
/// team's declared GitHub accounts, for <see cref="AutoPrReviewObservation.DecideMembershipGate"/>
/// to match a pull request's or a comment's author against (security review idea 6be68ee2, finding
/// 1). <see cref="MemberAccountIds"/> is the union of <c>chain.DeclaredAccountsOf(member.RootFingerprint)</c>
/// over every current <c>chain.Members</c>, not only this owner's own fleet — the gate has to name
/// every member's declared accounts, whichever owner root they joined the project under.
/// <see cref="MembersWithoutDeclaredAccount"/> names, for a park card's sake, every current member
/// none of whose nodes has declared one at all — a fleet on a version before v0.10.54, or one not
/// restarted since — so that member's own parked pull requests can say what is actually missing
/// rather than a bare "not a member".
/// </summary>
public sealed record EnrolledFleetSnapshot(
    IReadOnlyCollection<Guid> FleetNodeIds,
    IReadOnlyCollection<long> MemberAccountIds,
    IReadOnlyCollection<string> MembersWithoutDeclaredAccount);

/// <summary>
/// The trust chain the message sweep last computed for each project, held in process so
/// <see cref="AutoPrReviewEngine"/> can read it without a git fetch of its own. The message sweep
/// already computes the trust chain for every eligible project every 15 to 45 seconds and never
/// persists it; it writes here, the engine reads here, and nothing else does.
/// <para>
/// No entry means "this node has no fleet to defer to, and no roster to check against either": the
/// sweep has not run yet, or the chain could not be read at all — both genuinely transient,
/// first-sweep-after-restart conditions. <see cref="AutoPrReviewObservation.DecideMintHold"/> reads
/// that as leader, which is what a single-node install has always done, and
/// <see cref="AutoPrReviewObservation.DecideMembershipGate"/> reads the identical absence as
/// genuinely <c>Unknown</c> instead — the one place these two readers of the same snapshot
/// deliberately disagree, because a mint-leadership question with no fleet to defer to has always
/// safely defaulted to "mint", while a membership question with no roster to check against has no
/// safe default at all: skipping the candidate and asking again next sweep is the only answer that
/// is never a guess. A failed chain read leaves the previous snapshot standing rather than erasing
/// it, since the most recently computed chain is still the best evidence of who the fleet is.
/// </para>
/// <para>
/// A chain that read successfully but does not name this node's owner (a join refused because the
/// account cannot push, a project already owned and still waiting on an invite) is a different
/// case, and NOT one of the two above (independent pre-PR review, cycle 1, both lenses): the
/// chain's own <see cref="TrustChain.Members"/> and declarations are still fully readable even
/// though <see cref="FleetOf"/> cannot rank this owner's own fleet, so <see cref="Record"/> keeps
/// the member roster from every such successful read — only <see cref="EnrolledFleetSnapshot.FleetNodeIds"/>
/// goes empty, which <see cref="AutoPrReviewObservation.DecideMintHold"/> already reads the same
/// way it reads a fleet naming only this node: mint now, never defer. Erasing the whole entry here
/// used to make the membership gate read <c>Unknown</c> on every sweep forever for a project stuck
/// in this state — never a retry, since there is nothing left for a later sweep to improve on.
/// </para>
/// </summary>
public sealed class EnrolledNodeSnapshots
{
    private readonly ConcurrentDictionary<Guid, EnrolledFleetSnapshot> _byProject = [];

    /// <summary>
    /// The owner's fleet in <paramref name="chain"/>: <see cref="TrustedOwner.FleetNodeIds"/>, which
    /// counts the root's own node (it has no vouch entry of its own) and leaves out every revoked
    /// one. Null when the chain does not contain this owner at all.
    /// </summary>
    public static IReadOnlyCollection<Guid>? FleetOf(TrustChain chain, string ownerRootFingerprint) =>
        chain.OwnerChains.TryGetValue(ownerRootFingerprint, out TrustedOwner? owner)
            ? [.. owner.FleetNodeIds()]
            : null;

    /// <summary>
    /// Records the chain the message sweep just computed for <paramref name="projectId"/> — always,
    /// whether or not <paramref name="chain"/> happens to name <paramref name="ownerRootFingerprint"/>
    /// (independent pre-PR review, cycle 1, both lenses): the member roster below is a fact about
    /// the chain's own <see cref="TrustChain.Members"/>, not about this owner's own fleet, so a
    /// successful read is recorded regardless. Only the fleet-ranking half is owner-specific, and it
    /// alone goes empty when <see cref="FleetOf"/> cannot name this owner.
    /// </summary>
    public void Record(Guid projectId, TrustChain chain, string ownerRootFingerprint)
    {
        IReadOnlyCollection<Guid> fleet = FleetOf(chain, ownerRootFingerprint) ?? [];

        HashSet<long> memberAccountIds = [];
        List<string> membersWithoutDeclaredAccount = [];
        foreach (ProjectMember member in chain.Members)
        {
            IReadOnlyList<DeclaredGitHubAccount> declared = chain.DeclaredAccountsOf(member.RootFingerprint);
            if (declared.Count == 0)
            {
                membersWithoutDeclaredAccount.Add(
                    chain.DisplayNameOf(member.RootFingerprint) is { HasValue: true } name
                        ? name.Value
                        : $"root {member.RootFingerprint[..Math.Min(8, member.RootFingerprint.Length)]}");
                continue;
            }

            foreach (DeclaredGitHubAccount account in declared)
            {
                memberAccountIds.Add(account.AccountId);
            }
        }

        _byProject[projectId] = new EnrolledFleetSnapshot(fleet, memberAccountIds, membersWithoutDeclaredAccount);
    }

    /// <summary>The last recorded snapshot for the project, or null when there is none.</summary>
    public EnrolledFleetSnapshot? TryGet(Guid projectId) =>
        _byProject.TryGetValue(projectId, out EnrolledFleetSnapshot? snapshot) ? snapshot : null;
}
