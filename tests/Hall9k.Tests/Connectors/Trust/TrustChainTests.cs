using FluentAssertions;
using Hall9k.Connectors.Trust;
using Xunit;

namespace Hall9k.Tests.Connectors.Trust;

/// <summary>
/// <see cref="TrustChain"/>'s own pure logic — no git, no repository, since every method here is a
/// fold over data a caller already built by hand. The node-id-bound overload of
/// <see cref="TrustChain.IsAllowedSigner(string, Guid)"/> is what
/// <see cref="Hall9k.Connectors.Messaging.GitLedgerMessageTransport"/> now checks a message sender
/// against (independent pre-PR review, cycle 1, conformance and adversarial lenses, medium): a
/// vouched node's key must answer only for the exact node id it was vouched under, never for some
/// other node the same owner happens to have vouched.
/// </summary>
public sealed class TrustChainTests
{
    private static readonly Guid VouchedNodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherNodeId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static TrustChain BuildChain()
    {
        TrustedNode node = new(VouchedNodeId.ToString(), "ssh-ed25519 AAAAnode node", "node-fingerprint", DateTimeOffset.UnixEpoch);
        TrustedOwner owner = new("root-fingerprint", "ssh-ed25519 AAAAroot root", [node]);
        ProjectMember member = new("root-fingerprint", MembershipRole.Owner, DateTimeOffset.UnixEpoch);
        return new TrustChain(new Dictionary<string, TrustedOwner> { ["root-fingerprint"] = owner }, [member]);
    }

    [Fact]
    public void IsAllowedSigner_WithNodeId_AcceptsTheNodeItWasVouchedUnder()
    {
        TrustChain chain = BuildChain();

        chain.IsAllowedSigner("node-fingerprint", VouchedNodeId).Should().BeTrue();
    }

    [Fact]
    public void IsAllowedSigner_WithNodeId_RejectsTheIdenticalKeyClaimedForADifferentNodeId()
    {
        // The shape of the attack the review reproduced: a member overwrites some other node's own
        // self-announced node.yaml with a key that IS genuinely vouched — just not for that node id.
        TrustChain chain = BuildChain();

        chain.IsAllowedSigner("node-fingerprint", OtherNodeId).Should().BeFalse(
            "the key was vouched for VouchedNodeId, never for a different node id claiming the same key");
    }

    [Fact]
    public void IsAllowedSigner_WithNodeId_AcceptsTheRootsOwnKeyOnlyForTheRootsOwnNodeId()
    {
        // idea 6be68ee2, trust-ledger finding 7: the root's own key answers only for the exact node
        // id GitLedgerChainReader actually attached as RootNodeId, never for an arbitrary node id
        // that merely declares the same key.
        Guid rootNodeId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        TrustedOwner owner = new(
            "root-fingerprint", "ssh-ed25519 AAAAroot root",
            [new TrustedNode(VouchedNodeId.ToString(), "ssh-ed25519 AAAAnode node", "node-fingerprint", DateTimeOffset.UnixEpoch)],
            RootNodeId: rootNodeId.ToString());
        TrustChain chain = new(
            new Dictionary<string, TrustedOwner> { ["root-fingerprint"] = owner },
            [new ProjectMember("root-fingerprint", MembershipRole.Owner, DateTimeOffset.UnixEpoch)]);

        chain.IsAllowedSigner("root-fingerprint", rootNodeId).Should().BeTrue();
    }

    [Fact]
    public void IsAllowedSigner_WithNodeId_RejectsAKeyNoOwnerChainContainsAtAll()
    {
        TrustChain chain = BuildChain();

        chain.IsAllowedSigner("stranger-fingerprint", VouchedNodeId).Should().BeFalse();
    }

    [Fact]
    public void ContainsForNode_IsTrueForTheRootsOwnKeyOnlyAtTheRootsOwnNodeId()
    {
        Guid rootNodeId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        TrustedOwner owner = new("root-fingerprint", "ssh-ed25519 AAAAroot root", [], RootNodeId: rootNodeId.ToString());

        owner.ContainsForNode("root-fingerprint", rootNodeId.ToString()).Should().BeTrue();
        owner.ContainsForNode("root-fingerprint", OtherNodeId.ToString()).Should().BeFalse(
            "OtherNodeId was never attached as this root's own node id");
    }

    [Fact]
    public void ContainsForNode_IsFalseForTheRootsOwnKeyWhenRootNodeIdIsNull()
    {
        TrustedOwner owner = new("root-fingerprint", "ssh-ed25519 AAAAroot root", []);

        owner.ContainsForNode("root-fingerprint", OtherNodeId.ToString()).Should().BeFalse(
            "no node id has ever been proven to be this root's own device");
    }

    [Fact]
    public void FleetNodeIds_IncludesTheRootsOwnNodeAlongsideEveryVouchedNode()
    {
        Guid rootNodeId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        TrustedOwner owner = new(
            "root-fingerprint", "ssh-ed25519 AAAAroot root",
            [new TrustedNode(VouchedNodeId.ToString(), "ssh-ed25519 AAAAnode node", "node-fingerprint", DateTimeOffset.UnixEpoch)],
            RootNodeId: rootNodeId.ToString());

        owner.FleetNodeIds().Should().BeEquivalentTo([rootNodeId, VouchedNodeId]);
    }

    [Fact]
    public void FleetNodeIds_IsOnlyTheVouchedSetWhenTheLedgerNamesNoRootNodeId()
    {
        TrustedOwner owner = new(
            "root-fingerprint", "ssh-ed25519 AAAAroot root",
            [new TrustedNode(VouchedNodeId.ToString(), "ssh-ed25519 AAAAnode node", "node-fingerprint", DateTimeOffset.UnixEpoch)]);

        owner.FleetNodeIds().Should().BeEquivalentTo([VouchedNodeId]);
    }

    [Fact]
    public void FleetNodeIds_DedupesARootNodeThatWasAlsoSelfVouched()
    {
        // The pre-fix workaround PLAN.md's own entry for this branch names: an owner ran
        // h9k node vouch against its own root node id before the root counted as fleet on its own,
        // leaving that id in both RootNodeId and Nodes (independent pre-PR review, cycle 1,
        // adversarial lens, low).
        Guid rootNodeId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        TrustedOwner owner = new(
            "root-fingerprint", "ssh-ed25519 AAAAroot root",
            [new TrustedNode(rootNodeId.ToString(), "ssh-ed25519 AAAAroot root", "root-fingerprint", DateTimeOffset.UnixEpoch)],
            RootNodeId: rootNodeId.ToString());

        owner.FleetNodeIds().Should().Equal([rootNodeId], "the root's own node id must appear once, not twice");
    }

    [Fact]
    public void FleetNodeIds_ExcludesTheRootsOwnNodeOnceItIsRevoked()
    {
        // independent pre-PR review, cycle 1, conformance lens, medium: the root's own node has no
        // owners/<root>/nodes/<id>.yaml entry to remove on revocation, so RevokedNodeIds is the
        // only record a revocation of it ever leaves.
        Guid rootNodeId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        TrustedOwner owner = new(
            "root-fingerprint", "ssh-ed25519 AAAAroot root", [],
            RootNodeId: rootNodeId.ToString(),
            RevokedNodeIds: new HashSet<string> { rootNodeId.ToString() });

        owner.FleetNodeIds().Should().BeEmpty("h9k node revoke against the root's own node must actually drop it from the fleet");
    }

    private static NodeGitHubDeclaration Declaration(
        Guid nodeId, string keyFingerprint, long accountId, string login, int minutesAfterEpoch) =>
        new(nodeId.ToString(), keyFingerprint, new DeclaredGitHubAccount(accountId, login), DateTimeOffset.UnixEpoch.AddMinutes(minutesAfterEpoch));

    private static TrustChain WithDeclarations(params NodeGitHubDeclaration[] declarations) =>
        BuildChain() with { NodeDeclarations = declarations.ToDictionary(declaration => declaration.NodeId) };

    [Fact]
    public void DeclaredAccountsOf_ListsTheAccountAVouchedNodeDeclaresWithItsOwnKey()
    {
        TrustChain chain = WithDeclarations(Declaration(VouchedNodeId, "node-fingerprint", 42, "octocat", 1));

        chain.DeclaredAccountsOf("root-fingerprint").Should().Equal(new DeclaredGitHubAccount(42, "octocat"));
    }

    [Fact]
    public void DeclaredAccountsOf_IsEmptyWhenNoNodeDeclaresAnything()
    {
        BuildChain().DeclaredAccountsOf("root-fingerprint").Should().BeEmpty();
    }

    [Fact]
    public void DeclaredAccountsOf_IgnoresADeclarationSignedByAKeyThatIsNotTheOneVouchedForThatNode()
    {
        // Someone overwrote the node's file under their own key: the declaration is self-signed, but
        // the key is not the one the chain vouched for this node id.
        TrustChain chain = WithDeclarations(Declaration(VouchedNodeId, "some-other-fingerprint", 42, "octocat", 1));

        chain.DeclaredAccountsOf("root-fingerprint").Should().BeEmpty();
    }

    [Fact]
    public void DeclaredAccountsOf_IgnoresANodeOutsideThisRootsFleet()
    {
        TrustChain chain = WithDeclarations(Declaration(OtherNodeId, "node-fingerprint", 42, "octocat", 1));

        chain.DeclaredAccountsOf("root-fingerprint").Should().BeEmpty();
    }

    [Fact]
    public void DeclaredAccountsOf_CollapsesARenamedAccountToItsNewestLoginAndKeepsTwoAccountsApart()
    {
        Guid rootNodeId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        TrustedNode node = new(VouchedNodeId.ToString(), "ssh-ed25519 AAAAnode node", "node-fingerprint", DateTimeOffset.UnixEpoch);
        TrustedNode second = new(OtherNodeId.ToString(), "ssh-ed25519 AAAAsecond second", "second-fingerprint", DateTimeOffset.UnixEpoch);
        TrustedOwner owner = new("root-fingerprint", "ssh-ed25519 AAAAroot root", [node, second], RootNodeId: rootNodeId.ToString());
        TrustChain chain = new(
            new Dictionary<string, TrustedOwner> { ["root-fingerprint"] = owner },
            [new ProjectMember("root-fingerprint", MembershipRole.Owner, DateTimeOffset.UnixEpoch)])
        {
            NodeDeclarations = new[]
            {
                Declaration(rootNodeId, "root-fingerprint", 42, "old-name", 1),
                Declaration(VouchedNodeId, "node-fingerprint", 42, "new-name", 5),
                Declaration(OtherNodeId, "second-fingerprint", 77, "work-account", 3),
            }.ToDictionary(declaration => declaration.NodeId),
        };

        chain.DeclaredAccountsOf("root-fingerprint").Should().BeEquivalentTo(
            [new DeclaredGitHubAccount(42, "new-name"), new DeclaredGitHubAccount(77, "work-account")]);
    }

    [Fact]
    public void DeclaredAccountsOf_BreaksATieOnTheDeclarationTimeByTheHigherNodeIdWhateverTheFleetOrder()
    {
        Guid rootNodeId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        TrustedNode node = new(VouchedNodeId.ToString(), "ssh-ed25519 AAAAnode node", "node-fingerprint", DateTimeOffset.UnixEpoch);
        TrustedOwner owner = new("root-fingerprint", "ssh-ed25519 AAAAroot root", [node], RootNodeId: rootNodeId.ToString());
        TrustChain chain = new(
            new Dictionary<string, TrustedOwner> { ["root-fingerprint"] = owner },
            [new ProjectMember("root-fingerprint", MembershipRole.Owner, DateTimeOffset.UnixEpoch)])
        {
            NodeDeclarations = new[]
            {
                Declaration(rootNodeId, "root-fingerprint", 42, "declared-by-lower-id", 1),
                Declaration(VouchedNodeId, "node-fingerprint", 42, "declared-by-higher-id", 1),
            }.ToDictionary(declaration => declaration.NodeId),
        };

        // The root node lists first in the fleet and has the lower id, so a tie resolved by fleet order would pick it.
        chain.DeclaredAccountsOf("root-fingerprint").Should().Equal(new DeclaredGitHubAccount(42, "declared-by-higher-id"));
    }

    [Fact]
    public void NewestDeclaredAccountOf_PicksTheSingleNewestDeclarationAcrossTwoDistinctAccounts()
    {
        // Unlike DeclaredAccountsOf, which keeps one entry per account, a member's own label
        // carries at most one login — the newest declaration wins even when it names a different
        // account than an older one this same fleet also declared (task b7d8222e).
        Guid rootNodeId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        TrustedNode node = new(VouchedNodeId.ToString(), "ssh-ed25519 AAAAnode node", "node-fingerprint", DateTimeOffset.UnixEpoch);
        TrustedOwner owner = new("root-fingerprint", "ssh-ed25519 AAAAroot root", [node], RootNodeId: rootNodeId.ToString());
        TrustChain chain = new(
            new Dictionary<string, TrustedOwner> { ["root-fingerprint"] = owner },
            [new ProjectMember("root-fingerprint", MembershipRole.Owner, DateTimeOffset.UnixEpoch)])
        {
            NodeDeclarations = new[]
            {
                Declaration(rootNodeId, "root-fingerprint", 42, "work-account", 1),
                Declaration(VouchedNodeId, "node-fingerprint", 77, "personal-account", 5),
            }.ToDictionary(declaration => declaration.NodeId),
        };

        chain.NewestDeclaredAccountOf("root-fingerprint").Should().Be(new DeclaredGitHubAccount(77, "personal-account"));
    }

    [Fact]
    public void NewestDeclaredAccountOf_BreaksATieOnDeclarationTimeByTheHigherNodeId()
    {
        Guid rootNodeId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        TrustedNode node = new(VouchedNodeId.ToString(), "ssh-ed25519 AAAAnode node", "node-fingerprint", DateTimeOffset.UnixEpoch);
        TrustedOwner owner = new("root-fingerprint", "ssh-ed25519 AAAAroot root", [node], RootNodeId: rootNodeId.ToString());
        TrustChain chain = new(
            new Dictionary<string, TrustedOwner> { ["root-fingerprint"] = owner },
            [new ProjectMember("root-fingerprint", MembershipRole.Owner, DateTimeOffset.UnixEpoch)])
        {
            NodeDeclarations = new[]
            {
                Declaration(rootNodeId, "root-fingerprint", 42, "declared-by-lower-id", 1),
                Declaration(VouchedNodeId, "node-fingerprint", 77, "declared-by-higher-id", 1),
            }.ToDictionary(declaration => declaration.NodeId),
        };

        chain.NewestDeclaredAccountOf("root-fingerprint").Should().Be(new DeclaredGitHubAccount(77, "declared-by-higher-id"));
    }

    [Fact]
    public void NewestDeclaredAccountOf_IsNullWhenNobodyInTheFleetDeclaresOne()
    {
        BuildChain().NewestDeclaredAccountOf("root-fingerprint").Should().BeNull();
    }

    [Fact]
    public void ResolveRootActingNodeId_ResolvesK0sOwnRootNodeIdWhenNoRotationHasEverLanded()
    {
        Guid rootNodeId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        TrustedOwner owner = new("root-fingerprint", "ssh-ed25519 AAAAroot root", [], RootNodeId: rootNodeId.ToString());

        owner.ResolveRootActingNodeId().Should().Be(rootNodeId);
    }

    [Fact]
    public void ResolveRootActingNodeId_IsNullWhenK0sOwnNodeCannotBeResolvedAndNoRotationExists()
    {
        TrustedOwner owner = new("root-fingerprint", "ssh-ed25519 AAAAroot root", []);

        owner.ResolveRootActingNodeId().Should().BeNull(
            "an older ledger with no RootNodeId resolved and no rotation ever landed has nobody this can name");
    }

    [Fact]
    public void ResolveRootActingNodeId_FallsThroughToARotationsOwnNodeWhenK0sOwnCannotBeResolved()
    {
        Guid rotatedInNodeId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        TrustedOwner owner = new(
            "root-fingerprint", "ssh-ed25519 AAAAroot root", [],
            RootKeys:
            [
                new LiveRootKey("ssh-ed25519 AAAAroot root", "root-fingerprint", IntroducedByNodeId: null),
                new LiveRootKey("ssh-ed25519 AAAAk1 k1", "k1-fingerprint", rotatedInNodeId.ToString()),
            ]);

        owner.ResolveRootActingNodeId().Should().Be(
            rotatedInNodeId, "K0's own node cannot be named on this ledger, so the next-highest-ranked live key's own node is used instead");
    }

    [Fact]
    public void ResolveRootActingNodeId_PrefersK0sOwnNodeOverARotationWhenBothResolve()
    {
        Guid rootNodeId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        Guid rotatedInNodeId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        TrustedOwner owner = new(
            "root-fingerprint", "ssh-ed25519 AAAAroot root", [], RootNodeId: rootNodeId.ToString(),
            RootKeys:
            [
                new LiveRootKey("ssh-ed25519 AAAAroot root", "root-fingerprint", IntroducedByNodeId: null),
                new LiveRootKey("ssh-ed25519 AAAAk1 k1", "k1-fingerprint", rotatedInNodeId.ToString()),
            ]);

        owner.ResolveRootActingNodeId().Should().Be(rootNodeId, "K0 is always the highest-ranked live root key");
    }
}
