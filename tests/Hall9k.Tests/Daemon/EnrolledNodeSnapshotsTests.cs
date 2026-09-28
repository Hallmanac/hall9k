using FluentAssertions;
using Hall9k.Connectors.Trust;
using Hall9k.Daemon.AutoPrReview;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The in-process handoff from the message sweep's trust chain to auto-pr-review: what counts as
/// the owner's fleet for mint rank, what reads as "no fleet to defer to", and — since the
/// membership gate (security review idea 6be68ee2, finding 1) — the whole project team's declared
/// GitHub accounts and which members have none.
/// </summary>
public sealed class EnrolledNodeSnapshotsTests
{
    private const string Root = "owner-root";
    private const string OtherRoot = "other-root";
    private static readonly DateTimeOffset IssuedAt = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid RootNode = Guid.Parse("01a00d41-0000-7000-8000-000000000001");
    private static readonly Guid Vouched = Guid.Parse("01a04de3-0000-7000-8000-000000000002");
    private static readonly Guid Revoked = Guid.Parse("01a09999-0000-7000-8000-000000000003");
    private static readonly Guid Project = Guid.Parse("01a0aaaa-0000-7000-8000-000000000004");
    private static readonly Guid OtherRootNode = Guid.Parse("01a0bbbb-0000-7000-8000-000000000005");
    private static readonly Guid OtherVouched = Guid.Parse("01a0cccc-0000-7000-8000-000000000006");

    private static TrustChain ChainOf(
        TrustedOwner? owner, IReadOnlyList<ProjectMember>? members = null,
        IReadOnlyDictionary<string, NodeGitHubDeclaration>? declarations = null) => new(
        owner is null ? new Dictionary<string, TrustedOwner>() : new Dictionary<string, TrustedOwner> { [Root] = owner },
        members ?? [],
        NodeDeclarations: declarations);

    private static TrustedOwner Owner(string root, Guid rootNode, params TrustedNode[] nodes) => new(
        root, $"ssh-ed25519 AAAAFAKE{root} root", nodes, RootNodeId: rootNode.ToString(),
        RevokedNodeIds: new HashSet<string> { Revoked.ToString() });

    private static TrustedOwner Owner(params TrustedNode[] nodes) => Owner(Root, RootNode, nodes);

    private static TrustedNode Node(Guid id) => new(id.ToString(), $"ssh-ed25519 AAAAFAKE{id:N} test", $"fp-{id:N}", IssuedAt);

    private static NodeGitHubDeclaration Declaration(Guid nodeId, string fingerprint, long accountId, string login) =>
        new(nodeId.ToString(), fingerprint, new DeclaredGitHubAccount(accountId, login), IssuedAt);

    [Fact]
    public void The_fleet_is_the_roots_own_node_plus_every_vouched_node_and_never_a_revoked_one()
    {
        IReadOnlyCollection<Guid>? fleet = EnrolledNodeSnapshots.FleetOf(ChainOf(Owner(Node(Vouched))), Root);

        fleet.Should().BeEquivalentTo([RootNode, Vouched]);
    }

    [Fact]
    public void A_chain_that_does_not_name_this_owner_has_no_fleet()
    {
        EnrolledNodeSnapshots.FleetOf(ChainOf(null), Root).Should().BeNull();
    }

    [Fact]
    public void A_recorded_chain_is_readable_per_project_and_nothing_is_recorded_before_the_first_sweep()
    {
        EnrolledNodeSnapshots snapshots = new();

        snapshots.TryGet(Project).Should().BeNull();

        snapshots.Record(Project, ChainOf(Owner(Node(Vouched))), Root);

        snapshots.TryGet(Project)!.FleetNodeIds.Should().BeEquivalentTo([RootNode, Vouched]);
        snapshots.TryGet(Guid.NewGuid()).Should().BeNull("another project's chain says nothing about this one");
    }

    [Fact]
    public void A_later_chain_that_no_longer_names_the_owner_erases_the_snapshot_so_the_node_reads_as_leader()
    {
        EnrolledNodeSnapshots snapshots = new();
        snapshots.Record(Project, ChainOf(Owner(Node(Vouched))), Root);

        snapshots.Record(Project, ChainOf(null), Root);

        snapshots.TryGet(Project).Should().BeNull();
    }

    [Fact]
    public void A_members_declared_account_is_recorded_by_its_numeric_id()
    {
        EnrolledNodeSnapshots snapshots = new();
        ProjectMember member = new(Root, MembershipRole.Owner, IssuedAt);
        Dictionary<string, NodeGitHubDeclaration> declarations = new()
        {
            [RootNode.ToString()] = Declaration(RootNode, Root, 111, "brian"),
        };

        snapshots.Record(Project, ChainOf(Owner(), [member], declarations), Root);

        snapshots.TryGet(Project)!.MemberAccountIds.Should().BeEquivalentTo([111L]);
        snapshots.TryGet(Project)!.MembersWithoutDeclaredAccount.Should().BeEmpty();
    }

    [Fact]
    public void Two_accounts_declared_across_one_members_own_nodes_both_count()
    {
        EnrolledNodeSnapshots snapshots = new();
        ProjectMember member = new(Root, MembershipRole.Owner, IssuedAt);
        Dictionary<string, NodeGitHubDeclaration> declarations = new()
        {
            [RootNode.ToString()] = Declaration(RootNode, Root, 111, "brian"),
            [Vouched.ToString()] = Declaration(Vouched, $"fp-{Vouched:N}", 222, "brian-work"),
        };

        snapshots.Record(Project, ChainOf(Owner(Node(Vouched)), [member], declarations), Root);

        snapshots.TryGet(Project)!.MemberAccountIds.Should().BeEquivalentTo([111L, 222L]);
    }

    [Fact]
    public void A_member_with_no_declared_account_is_named_rather_than_silently_dropped()
    {
        EnrolledNodeSnapshots snapshots = new();
        ProjectMember member = new(Root, MembershipRole.Owner, IssuedAt);

        snapshots.Record(Project, ChainOf(Owner(), [member]), Root);

        snapshots.TryGet(Project)!.MemberAccountIds.Should().BeEmpty();
        snapshots.TryGet(Project)!.MembersWithoutDeclaredAccount.Should().ContainSingle();
    }

    [Fact]
    public void A_declaration_signed_by_a_key_other_than_the_nodes_own_is_ignored()
    {
        EnrolledNodeSnapshots snapshots = new();
        ProjectMember member = new(Root, MembershipRole.Owner, IssuedAt);
        // Declared under the vouched node's id but signed with a fingerprint that is not that
        // node's own vouched key — TrustedOwner.ContainsForNode refuses it, exactly as
        // GitLedgerChainReader already does when it builds NodeDeclarations in the first place.
        Dictionary<string, NodeGitHubDeclaration> declarations = new()
        {
            [Vouched.ToString()] = Declaration(Vouched, "not-this-nodes-key", 333, "impostor"),
        };

        snapshots.Record(Project, ChainOf(Owner(Node(Vouched)), [member], declarations), Root);

        snapshots.TryGet(Project)!.MemberAccountIds.Should().BeEmpty();
        snapshots.TryGet(Project)!.MembersWithoutDeclaredAccount.Should().ContainSingle();
    }

    [Fact]
    public void Member_account_ids_span_every_current_member_not_only_this_owners_own_fleet()
    {
        EnrolledNodeSnapshots snapshots = new();
        ProjectMember thisOwner = new(Root, MembershipRole.Owner, IssuedAt);
        ProjectMember otherOwner = new(OtherRoot, MembershipRole.Member, IssuedAt);
        Dictionary<string, TrustedOwner> owners = new()
        {
            [Root] = Owner(),
            [OtherRoot] = Owner(OtherRoot, OtherRootNode, Node(OtherVouched)),
        };
        Dictionary<string, NodeGitHubDeclaration> declarations = new()
        {
            [OtherRootNode.ToString()] = Declaration(OtherRootNode, OtherRoot, 444, "teammate"),
        };
        TrustChain chain = new(owners, [thisOwner, otherOwner], NodeDeclarations: declarations);

        snapshots.Record(Project, chain, Root);

        snapshots.TryGet(Project)!.MemberAccountIds.Should().BeEquivalentTo([444L]);
    }
}
