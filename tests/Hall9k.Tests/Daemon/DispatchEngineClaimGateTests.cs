using FluentAssertions;
using Hall9k.Daemon.Dispatch;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// <see cref="DispatchEngine.IsGrantedToThisOwner"/> is the pure decision behind the daemon's own
/// claim gate (idea 20723ef8, closing the residual gap independent pre-PR review, cycle 6
/// (adversarial lens, medium) left open on <c>ClaimRequestWatchLoop.cs:144</c>): whether a task's
/// self-declared <c>AssignedOwnerId</c> Guid — a vouched node's own claim, unverifiable against a
/// different real owner's id since Owner events never replicate — actually names this node's own
/// owner, once a cooperative grant's own verified fingerprint is in play.
/// </summary>
public sealed class DispatchEngineClaimGateTests
{
    private static readonly Guid VictimOwnerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AttackerOwnerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string VictimTrueFingerprint = "victim-true-fingerprint";
    private const string AttackerTrueFingerprint = "attacker-true-fingerprint";

    [Fact]
    public void A_forged_guid_naming_a_different_owner_never_grants_that_owners_own_node_a_claim()
    {
        // The adversarial scenario itself: a vouched node sends its own true (now-verified)
        // fingerprint alongside a real other owner's Guid it merely knows from this project's own
        // replicated task history. On the NAMED (victim) owner's own node, the Guid matches but the
        // recorded fingerprint is the attacker's, not this node's own — recorded by fingerprint,
        // never trusted as this node's own claim.
        bool granted = DispatchEngine.IsGrantedToThisOwner(
            assignedOwnerId: VictimOwnerId,
            assignedOwnerFingerprint: AttackerTrueFingerprint,
            thisOwnerId: VictimOwnerId,
            thisOwnerRootFingerprint: VictimTrueFingerprint);

        granted.Should().BeFalse(
            "the recorded fingerprint is the attacker's own, not this (the named owner's) node's own — a " +
            "forged Guid must not steal a claim on the receiving node");
    }

    [Fact]
    public void The_true_grantees_own_node_claims_despite_the_forged_guid()
    {
        // The other half of the same scenario: on the ATTACKER's own node — the true grantee, since
        // the recorded fingerprint is genuinely theirs — the claim succeeds even though the grant's
        // own self-declared Guid names somebody else entirely.
        bool granted = DispatchEngine.IsGrantedToThisOwner(
            assignedOwnerId: VictimOwnerId,
            assignedOwnerFingerprint: AttackerTrueFingerprint,
            thisOwnerId: AttackerOwnerId,
            thisOwnerRootFingerprint: AttackerTrueFingerprint);

        granted.Should().BeTrue(
            "the recorded fingerprint is this node's own owner's true fingerprint, so a forged Guid must not " +
            "block the true grantee's own claim");
    }

    [Fact]
    public void An_unknown_fingerprint_is_refused_the_same_as_an_unassigned_task_always_was()
    {
        bool granted = DispatchEngine.IsGrantedToThisOwner(
            assignedOwnerId: VictimOwnerId,
            assignedOwnerFingerprint: "nobody-owns-this-fingerprint",
            thisOwnerId: AttackerOwnerId,
            thisOwnerRootFingerprint: null);

        granted.Should().BeFalse("a node with no root fingerprint of its own to compare has nothing to verify against");
    }

    [Fact]
    public void An_event_written_before_this_field_existed_replays_through_the_plain_guid_comparison()
    {
        bool matchingGuid = DispatchEngine.IsGrantedToThisOwner(
            assignedOwnerId: AttackerOwnerId,
            assignedOwnerFingerprint: null,
            thisOwnerId: AttackerOwnerId,
            thisOwnerRootFingerprint: AttackerTrueFingerprint);

        bool differentGuid = DispatchEngine.IsGrantedToThisOwner(
            assignedOwnerId: VictimOwnerId,
            assignedOwnerFingerprint: null,
            thisOwnerId: AttackerOwnerId,
            thisOwnerRootFingerprint: AttackerTrueFingerprint);

        matchingGuid.Should().BeTrue("no fingerprint was ever recorded on this event, so the plain Guid comparison this gate always made still decides");
        differentGuid.Should().BeFalse("the Guid names a different owner, and there is no fingerprint here to override that");
    }
}
