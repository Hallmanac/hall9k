using FluentAssertions;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Connectors.Trust;

/// <summary>
/// <see cref="OwnerRootVerificationReconciler.ShouldVerify"/> — the pure decision every caller
/// (<c>ProjectJoinCommand</c>'s own reconciliation step, <c>MessageSweepEngine</c>'s per-tick one)
/// shares, tested here without a document store or a document session: task f53fecfd, criterion 4,
/// the gap draft f245371d found.
/// </summary>
public sealed class OwnerRootVerificationReconcilerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private const string Root = "51b61f499d3efdd5e5fc5cbee235a677b115f0193830d48d8b8b36b8f2dd256";
    private const string MyFingerprint = "carrying-node-key-fingerprint";

    [Fact]
    public void An_owner_claimed_with_owner_and_now_enrolled_on_the_chain_should_verify()
    {
        OwnerAggregate owner = ClaimedUnverified();
        TrustChain chain = ChainEnrolling(MyFingerprint, Root);

        OwnerRootVerificationReconciler.ShouldVerify(owner, MyFingerprint, chain).Should().BeTrue();
    }

    [Fact]
    public void An_owner_already_verified_never_needs_reconciling_again()
    {
        OwnerAggregate owner = ClaimedVerified();
        TrustChain chain = ChainEnrolling(MyFingerprint, Root);

        OwnerRootVerificationReconciler.ShouldVerify(owner, MyFingerprint, chain).Should().BeFalse();
    }

    [Fact]
    public void An_owner_with_no_root_claimed_yet_has_nothing_to_verify()
    {
        OwnerAggregate owner = new();
        TrustChain chain = ChainEnrolling(MyFingerprint, Root);

        OwnerRootVerificationReconciler.ShouldVerify(owner, MyFingerprint, chain).Should().BeFalse();
    }

    [Fact]
    public void A_chain_that_does_not_enrol_this_node_under_the_claimed_root_never_verifies()
    {
        OwnerAggregate owner = ClaimedUnverified();
        TrustChain chain = ChainEnrolling("some-other-nodes-fingerprint", Root);

        OwnerRootVerificationReconciler.ShouldVerify(owner, MyFingerprint, chain).Should().BeFalse();
    }

    [Fact]
    public void A_chain_enrolling_this_node_under_a_different_root_never_verifies()
    {
        OwnerAggregate owner = ClaimedUnverified();
        TrustChain chain = ChainEnrolling(MyFingerprint, "a-different-root-fingerprint");

        OwnerRootVerificationReconciler.ShouldVerify(owner, MyFingerprint, chain).Should().BeFalse();
    }

    private static OwnerAggregate ClaimedUnverified()
    {
        OwnerAggregate owner = new();
        owner.Apply(OwnerDecider.Register(DomainId.New(), "Test Owner", "owner@test.local", Now));
        owner.Apply(OwnerDecider.ClaimRoot(owner, Root, verified: false, Now));
        return owner;
    }

    private static OwnerAggregate ClaimedVerified()
    {
        OwnerAggregate owner = new();
        owner.Apply(OwnerDecider.Register(DomainId.New(), "Test Owner", "owner@test.local", Now));
        owner.Apply(OwnerDecider.ClaimRoot(owner, Root, verified: true, Now));
        return owner;
    }

    private static TrustChain ChainEnrolling(string enrolledFingerprint, string root) =>
        new(
            new Dictionary<string, TrustedOwner>
            {
                [root] = new TrustedOwner(
                    root, "ssh-ed25519 AAAAroot root-owner",
                    [new TrustedNode("11111111-1111-1111-1111-111111111111", $"ssh-ed25519 AAAA{enrolledFingerprint}", enrolledFingerprint, Now)]),
            },
            []);
}
