using System.Globalization;
using System.Text;
using FluentAssertions;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Connectors.Trust;

/// <summary>
/// <see cref="GitLedgerChainReader"/>'s trust rules — vouch/revoke ordering, succession rank,
/// carried-record structural gating, membership authorization, the fixed-point loop — against a
/// hand-built <see cref="FakeLedgerGraph"/> rather than a real git repository and real signing keys.
/// This is the fast half of the split <c>GitLedgerChainReaderTests</c> used to run in one collection
/// (host-gate-slowdown-2026-09-28): every scenario ported here is identical in intent to its
/// same-named counterpart that used to live in <c>GitLedgerChainReaderTests</c>, minus the git
/// plumbing and real SSH signing <see cref="GitLedgerChainReaderTests"/>'s own small end-to-end set
/// still proves (commit signature verification, carried bundles with embedded commits, ref rewind
/// refusal, and genesis selection by ref order). Not in <c>[Collection("RealProcessSpawn")]</c> and
/// carries no <c>RealProcessSpawn</c> trait, so this class runs in the parallel test gate.
/// <para>
/// "Signed by" here is a plain equality check against the public key line a write's own caller
/// named as the signer (<see cref="FakeLedgerGraph"/>) — there is no real signature to forge or
/// verify, so a scenario whose entire point is that a forged or malformed real signature is
/// rejected stays in the kept end-to-end set instead of being ported here.
/// </para>
/// </summary>
public sealed class GitLedgerChainReaderFastTests
{
    private readonly FakeLedgerGraph graph = new();
    private readonly GitLedgerChainReader chainReader;

    public GitLedgerChainReaderFastTests()
    {
        chainReader = new GitLedgerChainReader(_ => graph);
    }

    private Task<TrustChain> ComputeAsync() => chainReader.ComputeAsync("fake", CancellationToken.None);

    // ---- fixed-point / discovery / self-consistency (task 202383dc) -------------------------

    [Fact]
    public async Task A_vouched_second_node_verifies_on_a_third_nodes_read()
    {
        Identity owner = EstablishGenesisRoot();
        Identity nodeB = GenerateIdentity();
        WriteNodeFile(nodeB, nodeB);
        Vouch(owner.Fingerprint, nodeB, owner);

        TrustChain chain = await ComputeAsync();

        chain.IsAllowedSigner(nodeB.Fingerprint).Should().BeTrue("node B was vouched by the enrolled root");
        chain.OwnerChains[owner.Fingerprint].Nodes.Should().Contain(node => node.NodeId == nodeB.NodeId.ToString());
    }

    [Fact]
    public async Task The_genesis_nodes_own_self_announced_node_file_becomes_the_roots_own_node_id()
    {
        Identity owner = EstablishGenesisRoot();
        WriteNodeFile(owner, owner);

        TrustChain chain = await ComputeAsync();

        chain.OwnerChains[owner.Fingerprint].RootNodeId.Should().Be(owner.NodeId.ToString());
        chain.OwnerChains[owner.Fingerprint].Nodes.Should().BeEmpty("the root never vouches itself");
        chain.OwnerChains[owner.Fingerprint].FleetNodeIds().Should().Contain(owner.NodeId);
    }

    [Fact]
    public async Task A_second_roots_own_self_announced_node_file_never_gets_attached_to_a_different_root()
    {
        Identity owner = EstablishGenesisRoot();
        WriteNodeFile(owner, owner);

        Identity other = GenerateIdentity();
        WriteRootFile(other);
        WriteNodeFile(other, other);

        TrustChain chain = await ComputeAsync();

        chain.OwnerChains[owner.Fingerprint].RootNodeId.Should().Be(owner.NodeId.ToString());
        chain.OwnerChains[other.Fingerprint].RootNodeId.Should().Be(
            other.NodeId.ToString(), "each root's own node id resolves against its own key, never the other root's");
    }

    [Fact]
    public async Task A_strangers_own_node_file_cannot_impersonate_a_real_roots_own_node_id()
    {
        Identity owner = EstablishGenesisRoot();

        Identity stranger = GenerateIdentity();
        Guid forgedNodeId = Guid.NewGuid();
        string refName = $"refs/hall9k/ledger/nodes/{forgedNodeId}";
        string path = $"nodes/{forgedNodeId}/node.yaml";
        string content = BuildYaml(("node_id", forgedNodeId.ToString()), ("public_key", owner.PublicKeyLine));
        graph.Write(refName, path, content, stranger.PublicKeyLine);

        TrustChain chain = await ComputeAsync();

        chain.OwnerChains[owner.Fingerprint].RootNodeId.Should().BeNull(
            "the forged node.yaml was never actually signed by the root's own key, only claims to carry it");
        chain.UnverifiedWrites.Should().Contain(
            write => write.Kind == "node" && write.Identifier == forgedNodeId.ToString() && write.RootFingerprint == owner.Fingerprint,
            "the forgery is named rather than silently vanishing with no diagnostic at all");
    }

    [Fact]
    public async Task A_forgery_sorting_after_the_already_resolved_genuine_root_node_is_still_named()
    {
        Identity owner = EstablishGenesisRoot();
        Identity ownerNode = owner with { NodeId = Guid.Parse("00000000-0000-0000-0000-000000000001") };
        WriteNodeFile(ownerNode, owner);

        Identity stranger = GenerateIdentity();
        Guid forgedNodeId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        string refName = $"refs/hall9k/ledger/nodes/{forgedNodeId}";
        string path = $"nodes/{forgedNodeId}/node.yaml";
        string content = BuildYaml(("node_id", forgedNodeId.ToString()), ("public_key", owner.PublicKeyLine));
        graph.Write(refName, path, content, stranger.PublicKeyLine);

        TrustChain chain = await ComputeAsync();

        chain.OwnerChains[owner.Fingerprint].RootNodeId.Should().Be(
            ownerNode.NodeId.ToString(), "the genuine self-announced node still resolves regardless of the forgery elsewhere");
        chain.UnverifiedWrites.Should().Contain(
            write => write.Kind == "node" && write.Identifier == forgedNodeId.ToString() && write.RootFingerprint == owner.Fingerprint,
            "the forgery must be named even though it sorts after the already-resolved genuine root node");
    }

    // ---- GitHub declarations and display names (task e6744304) ------------------------------

    [Fact]
    public async Task A_root_nodes_self_signed_github_declaration_is_read_for_the_root()
    {
        Identity owner = EstablishGenesisRoot();
        WriteNodeFile(owner, owner, github: new DeclaredGitHubAccount(42, "octocat"));

        TrustChain chain = await ComputeAsync();

        chain.NodeDeclarations[owner.NodeId.ToString()].Account.Should().Be(new DeclaredGitHubAccount(42, "octocat"));
        chain.DeclaredAccountsOf(owner.Fingerprint).Should().Equal(new DeclaredGitHubAccount(42, "octocat"));
        chain.UnverifiedWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task A_vouched_non_root_nodes_self_signed_github_declaration_is_read_for_its_owner()
    {
        Identity owner = EstablishGenesisRoot();
        WriteNodeFile(owner, owner);
        Identity nodeB = GenerateIdentity();
        WriteNodeFile(nodeB, nodeB, owner.Fingerprint, new DeclaredGitHubAccount(7, "second-machine"));
        Vouch(owner.Fingerprint, nodeB, owner);

        TrustChain chain = await ComputeAsync();

        chain.NodeDeclarations.Should().ContainKey(nodeB.NodeId.ToString());
        chain.DeclaredAccountsOf(owner.Fingerprint).Should().Equal(new DeclaredGitHubAccount(7, "second-machine"));
    }

    [Fact]
    public async Task A_node_file_written_before_the_declaration_existed_reads_as_no_declaration_and_is_not_reported()
    {
        Identity owner = EstablishGenesisRoot();
        WriteNodeFile(owner, owner);

        TrustChain chain = await ComputeAsync();

        chain.NodeDeclarations.Should().BeEmpty();
        chain.DeclaredAccountsOf(owner.Fingerprint).Should().BeEmpty();
        chain.UnverifiedWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task Two_nodes_of_one_owner_declaring_different_accounts_are_two_entries_and_one_renamed_account_is_one()
    {
        Identity owner = EstablishGenesisRoot();
        WriteNodeFile(owner, owner);
        WriteNodeFile(
            owner, owner, github: new DeclaredGitHubAccount(42, "personal"),
            committerTime: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Identity nodeB = GenerateIdentity();
        Identity nodeC = GenerateIdentity();
        WriteNodeFile(nodeB, nodeB, owner.Fingerprint, new DeclaredGitHubAccount(99, "work"));
        // Same account id as the root node, under the name it was renamed to since.
        WriteNodeFile(nodeC, nodeC, owner.Fingerprint, new DeclaredGitHubAccount(42, "personal-renamed"));
        Vouch(owner.Fingerprint, nodeB, owner);
        Vouch(owner.Fingerprint, nodeC, owner);

        TrustChain chain = await ComputeAsync();

        chain.DeclaredAccountsOf(owner.Fingerprint).Should().BeEquivalentTo(
            [new DeclaredGitHubAccount(42, "personal-renamed"), new DeclaredGitHubAccount(99, "work")]);
        chain.UnverifiedWrites.Should().BeEmpty("two accounts on two nodes is never an error");
    }

    [Fact]
    public async Task A_non_root_node_file_rewritten_by_a_key_other_than_its_own_yields_no_declaration_and_is_named()
    {
        Identity owner = EstablishGenesisRoot();
        Identity nodeB = GenerateIdentity();
        WriteNodeFile(nodeB, nodeB, owner.Fingerprint);
        Vouch(owner.Fingerprint, nodeB, owner);

        // Anyone with push rewrites node B's file, keeping B's public key line, but signs it as themselves.
        Identity stranger = GenerateIdentity();
        WriteNodeFile(nodeB, stranger, owner.Fingerprint, new DeclaredGitHubAccount(666, "forged"));

        TrustChain chain = await ComputeAsync();

        chain.NodeDeclarations.Should().NotContainKey(nodeB.NodeId.ToString());
        chain.DeclaredAccountsOf(owner.Fingerprint).Should().BeEmpty();
        chain.UnverifiedWrites.Should().ContainSingle(
            write => write.Kind == "node" && write.Identifier == nodeB.NodeId.ToString()
                && write.Reason.Contains("GitHub account", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_root_node_file_rewritten_by_a_key_other_than_its_own_yields_no_declaration_and_is_named_once()
    {
        Identity owner = EstablishGenesisRoot();
        WriteNodeFile(owner, owner);

        Identity stranger = GenerateIdentity();
        WriteNodeFile(owner, stranger, github: new DeclaredGitHubAccount(666, "forged"));

        TrustChain chain = await ComputeAsync();

        chain.NodeDeclarations.Should().BeEmpty();
        chain.UnverifiedWrites.Should().ContainSingle(write => write.Kind == "node" && write.Identifier == owner.NodeId.ToString());
    }

    [Fact]
    public async Task A_root_nodes_self_signed_display_name_is_read_for_the_root()
    {
        Identity owner = EstablishGenesisRoot();
        WriteNodeFile(owner, owner, displayName: "Ada Lovelace");

        TrustChain chain = await ComputeAsync();

        chain.NodeDisplayNames[owner.NodeId.ToString()].Name.Value.Should().Be("Ada Lovelace");
        chain.DisplayNameOf(owner.Fingerprint).Value.Should().Be("Ada Lovelace");
        chain.UnverifiedWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task A_vouched_non_root_nodes_self_signed_display_name_is_read_for_its_owner()
    {
        Identity owner = EstablishGenesisRoot();
        WriteNodeFile(owner, owner);
        Identity nodeB = GenerateIdentity();
        WriteNodeFile(nodeB, nodeB, owner.Fingerprint, displayName: "Second Machine");
        Vouch(owner.Fingerprint, nodeB, owner);

        TrustChain chain = await ComputeAsync();

        chain.NodeDisplayNames.Should().ContainKey(nodeB.NodeId.ToString());
        chain.DisplayNameOf(owner.Fingerprint).Value.Should().Be("Second Machine");
    }

    [Fact]
    public async Task A_node_file_written_before_the_display_name_existed_reads_as_no_name()
    {
        Identity owner = EstablishGenesisRoot();
        WriteNodeFile(owner, owner);

        TrustChain chain = await ComputeAsync();

        chain.NodeDisplayNames.Should().BeEmpty();
        chain.DisplayNameOf(owner.Fingerprint).Should().Be(DisplayName.None);
    }

    [Fact]
    public async Task A_node_file_rewritten_by_a_key_other_than_its_own_yields_no_display_name_and_is_not_reported()
    {
        Identity owner = EstablishGenesisRoot();
        Identity nodeB = GenerateIdentity();
        WriteNodeFile(nodeB, nodeB, owner.Fingerprint);
        Vouch(owner.Fingerprint, nodeB, owner);

        Identity stranger = GenerateIdentity();
        WriteNodeFile(nodeB, stranger, owner.Fingerprint, displayName: "Forged Name");

        TrustChain chain = await ComputeAsync();

        chain.NodeDisplayNames.Should().NotContainKey(nodeB.NodeId.ToString());
        chain.DisplayNameOf(owner.Fingerprint).Should().Be(DisplayName.None);
        chain.UnverifiedWrites.Should().BeEmpty("a display name is a label only, with no trust consequence to name");
    }

    [Fact]
    public async Task Two_nodes_of_one_owner_declaring_different_display_names_the_newest_wins()
    {
        Identity owner = EstablishGenesisRoot();
        WriteNodeFile(owner, owner);
        WriteNodeFile(owner, owner, displayName: "Older Name", committerTime: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Identity nodeB = GenerateIdentity();
        WriteNodeFile(nodeB, nodeB, owner.Fingerprint, displayName: "Newer Name");
        Vouch(owner.Fingerprint, nodeB, owner);

        TrustChain chain = await ComputeAsync();

        chain.DisplayNameOf(owner.Fingerprint).Value.Should().Be("Newer Name");
    }

    [Fact]
    public async Task A_github_only_rewrite_does_not_make_an_unrelated_display_name_look_newer()
    {
        Identity owner = EstablishGenesisRoot();
        WriteNodeFile(owner, owner);
        WriteNodeFile(owner, owner, displayName: "Ada", committerTime: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Identity nodeB = GenerateIdentity();
        WriteNodeFile(nodeB, nodeB, owner.Fingerprint, displayName: "Bob");
        Vouch(owner.Fingerprint, nodeB, owner);

        // The owner's own daemon later refreshes only its GitHub declaration — well after node B
        // declared "Bob" — while "Ada" itself never changes.
        WriteNodeFile(
            owner, owner, github: new DeclaredGitHubAccount(42, "octocat"), displayName: "Ada",
            committerTime: new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));

        TrustChain chain = await ComputeAsync();

        chain.DisplayNameOf(owner.Fingerprint).Value.Should().Be("Bob");
    }

    [Fact]
    public async Task A_second_clone_reads_the_declaration_another_clone_wrote_after_it_fetches()
    {
        Identity owner = EstablishGenesisRoot();
        WriteNodeFile(owner, owner);
        (await ComputeAsync()).NodeDeclarations.Should().BeEmpty();

        WriteNodeFile(owner, owner, github: new DeclaredGitHubAccount(42, "octocat"));

        TrustChain chain = await ComputeAsync();

        chain.DeclaredAccountsOf(owner.Fingerprint).Should().Equal(new DeclaredGitHubAccount(42, "octocat"));
    }

    // ---- revocation, retirement, strangers, membership writers ------------------------------

    [Fact]
    public async Task A_revoked_root_node_is_dropped_from_its_own_fleet()
    {
        Identity owner = EstablishGenesisRoot();
        WriteNodeFile(owner, owner);

        (await ComputeAsync()).OwnerChains[owner.Fingerprint].FleetNodeIds().Should().Contain(
            owner.NodeId, "the root's own node resolves before any revocation");

        Revoke(owner.Fingerprint, owner.NodeId, owner);

        TrustChain chain = await ComputeAsync();

        chain.OwnerChains[owner.Fingerprint].RootNodeId.Should().Be(
            owner.NodeId.ToString(), "the ledger still names it — only FleetNodeIds excludes a revoked entry");
        chain.OwnerChains[owner.Fingerprint].FleetNodeIds().Should().NotContain(
            owner.NodeId, "h9k node revoke against the root's own node must actually drop it from the fleet");
    }

    [Fact]
    public async Task A_node_that_retired_its_own_self_created_root_no_longer_attaches_to_it()
    {
        Identity retiredRoot = EstablishGenesisRoot();
        WriteNodeFile(retiredRoot, retiredRoot);

        Identity realOwner = EstablishGenesisRoot();

        // The node re-runs h9k project join --owner <realOwner>: same key, same node id, but its
        // own node.yaml now claims the real owner instead of itself.
        WriteNodeFile(retiredRoot, retiredRoot, ownerFingerprint: realOwner.Fingerprint);

        TrustChain chain = await ComputeAsync();

        chain.OwnerChains[retiredRoot.Fingerprint].RootNodeId.Should().BeNull(
            "the node's own current claim no longer names the retired root, whatever key it was signed with");
        chain.OwnerChains[retiredRoot.Fingerprint].FleetNodeIds().Should().NotContain(retiredRoot.NodeId);
        chain.UnverifiedWrites.Should().NotContain(
            write => write.Kind == "node" && write.Identifier == retiredRoot.NodeId.ToString(),
            "a node moving on from its own retired root is a legitimate, correctly signed write, not a forgery");
    }

    [Fact]
    public async Task A_strangers_root_and_node_file_are_ignored_everywhere()
    {
        Identity nodeA = EstablishGenesisRoot();

        Identity stranger = GenerateIdentity();
        WriteRootFile(stranger);
        WriteNodeFile(stranger, stranger);

        TrustChain chain = await ComputeAsync();

        chain.IsAllowedSigner(stranger.Fingerprint).Should().BeFalse("the stranger was never made a project member");
        chain.Members.Should().NotContain(member => member.RootFingerprint == stranger.Fingerprint);
        chain.IsAllowedSigner(nodeA.Fingerprint).Should().BeTrue("the genesis root is unaffected by the stranger's own unrelated ref");
    }

    [Fact]
    public async Task A_member_role_node_cannot_write_a_membership()
    {
        Identity owner = EstablishGenesisRoot();

        Identity member = GenerateIdentity();
        WriteRootFile(member);
        WriteMemberFile(member.Fingerprint, "member", owner);

        Identity outsider = GenerateIdentity();
        WriteMemberFile(outsider.Fingerprint, "owner", member);

        TrustChain chain = await ComputeAsync();

        chain.RoleOf(member.Fingerprint).Should().Be(MembershipRole.Member);
        chain.Members.Should().NotContain(
            m => m.RootFingerprint == outsider.Fingerprint, "a member-role root cannot write a membership");
    }

    [Fact]
    public async Task Revocation_then_re_vouch_restores()
    {
        Identity owner = EstablishGenesisRoot();
        Identity node = GenerateIdentity();

        WriteNodeFile(node, node);
        Vouch(owner.Fingerprint, node, owner);

        (await ComputeAsync()).IsAllowedSigner(node.Fingerprint).Should().BeTrue("just vouched");

        Revoke(owner.Fingerprint, node.NodeId, owner);

        (await ComputeAsync()).IsAllowedSigner(node.Fingerprint).Should().BeFalse("revoked, and revocation is later in ref order than the vouch");

        Vouch(owner.Fingerprint, node, owner);

        (await ComputeAsync()).IsAllowedSigner(node.Fingerprint).Should().BeTrue("a surviving node undoes a bad revocation by vouching again");
    }

    // ---- membership authorization narrowed to a live root key (idea 6be68ee2 finding 2) -----

    [Fact]
    public async Task A_roots_own_file_overwritten_with_a_mismatched_key_is_named_as_an_unverified_write()
    {
        Identity root = GenerateIdentity();
        Identity attacker = GenerateIdentity();
        WriteRootFile(root);

        // A second commit, on the same ref, replaces root.yaml's own declared key with the
        // attacker's — self-certification must now fail.
        string refName = $"refs/hall9k/ledger/owners/{root.Fingerprint}";
        string path = $"owners/{root.Fingerprint}/root.yaml";
        string overwriteContent = BuildYaml(("public_key", attacker.PublicKeyLine), ("created_at", Now()));
        graph.Write(refName, path, overwriteContent, attacker.PublicKeyLine);

        TrustChain chain = await ComputeAsync();

        chain.OwnerChains.Should().NotContainKey(root.Fingerprint, "the currently-served content no longer self-certifies");
        chain.UnverifiedWrites.Should().Contain(
            write => write.Kind == "root" && write.RootFingerprint == root.Fingerprint,
            "the offending commit is named rather than the chain silently vanishing with no diagnostic");
    }

    [Fact]
    public async Task A_forged_vouch_signed_by_a_stranger_is_named_as_an_unverified_write()
    {
        Identity owner = EstablishGenesisRoot();
        Identity attacker = GenerateIdentity();

        // The attacker pushes a node file into the owner's own namespace, signed with its own key
        // — never the owner's, never any node the owner has enrolled.
        Vouch(owner.Fingerprint, attacker, attacker);

        TrustChain chain = await ComputeAsync();

        chain.OwnerChains[owner.Fingerprint].Nodes.Should().NotContain(
            node => node.NodeId == attacker.NodeId.ToString(), "the vouch was never signed by the root or any enrolled node");
        chain.UnverifiedWrites.Should().Contain(
            write => write.Kind == "vouch" && write.Identifier == attacker.NodeId.ToString() && write.RootFingerprint == owner.Fingerprint,
            "the forged vouch is named rather than silently discarded");
    }

    [Fact]
    public async Task A_members_write_signed_by_a_vouched_node_is_refused_regardless_of_its_own_enrollment_state()
    {
        Identity owner = EstablishGenesisRoot();

        Identity laptop = GenerateIdentity();
        WriteNodeFile(laptop, laptop);
        Vouch(owner.Fingerprint, laptop, owner);

        Identity bob = GenerateIdentity();
        WriteMemberFile(bob.Fingerprint, "member", laptop);

        TrustChain whileEnrolled = await ComputeAsync();
        whileEnrolled.RoleOf(bob.Fingerprint).Should().BeNull(
            "the laptop is fully enrolled but never held the owner's own root key, so its members write "
            + "is never authorized");
        whileEnrolled.UnverifiedWrites.Should().Contain(
            write => write.Kind == "membership" && write.Identifier == bob.Fingerprint,
            "the refused write is named rather than silently dropped");

        // Revoking and re-vouching the laptop changes nothing: its members write was never
        // authorized to begin with, so there is nothing for either to void or restore.
        Revoke(owner.Fingerprint, laptop.NodeId, owner);
        Vouch(owner.Fingerprint, laptop, owner);

        (await ComputeAsync()).RoleOf(bob.Fingerprint).Should().BeNull("the laptop's write was never authorized, so re-enrolling it changes nothing");
    }

    [Fact]
    public async Task A_refused_membership_write_is_dropped_once_a_later_root_signed_commit_rewrites_the_identical_path()
    {
        Identity owner = EstablishGenesisRoot();

        Identity laptop = GenerateIdentity();
        WriteNodeFile(laptop, laptop);
        Vouch(owner.Fingerprint, laptop, owner);

        Identity bob = GenerateIdentity();
        WriteMemberFile(bob.Fingerprint, "member", laptop);

        (await ComputeAsync()).UnverifiedWrites.Should().Contain(
            write => write.Kind == "membership" && write.Identifier == bob.Fingerprint,
            "the laptop's own write is refused and named before any root-signed rewrite lands");

        // Root reaffirms the identical path with a fresh, content-changing, root-signed commit.
        WriteMemberFile(bob.Fingerprint, "member", owner);

        TrustChain afterReaffirm = await ComputeAsync();

        afterReaffirm.RoleOf(bob.Fingerprint).Should().Be(MembershipRole.Member, "the root-signed rewrite is now what the chain trusts");
        afterReaffirm.UnverifiedWrites.Should().NotContain(
            write => write.Kind == "membership" && write.Identifier == bob.Fingerprint,
            "the laptop's earlier refusal is superseded by the later root-signed rewrite of the identical path, not left standing forever");
    }

    [Fact]
    public async Task A_revocation_a_members_write_and_a_role_change_are_authorized_only_by_a_live_root_key()
    {
        Identity owner = EstablishGenesisRoot();

        Identity laptop = GenerateIdentity();
        WriteNodeFile(laptop, laptop);
        Vouch(owner.Fingerprint, laptop, owner);

        Identity target = GenerateIdentity();
        WriteNodeFile(target, target);
        Vouch(owner.Fingerprint, target, owner);

        Identity bob = GenerateIdentity();
        WriteMemberFile(bob.Fingerprint, "member", owner);

        // Peer vouching keeps working: the laptop, a plain enrolled node, can still vouch a peer.
        Identity peer = GenerateIdentity();
        WriteNodeFile(peer, peer);
        Vouch(owner.Fingerprint, peer, laptop);

        // The laptop — merely enrolled, never a root key — signs a revocation, a members write
        // (add), and a role change.
        Revoke(owner.Fingerprint, target.NodeId, laptop);
        Identity outsider = GenerateIdentity();
        WriteMemberFile(outsider.Fingerprint, "member", laptop);
        WriteMemberFile(bob.Fingerprint, "owner", laptop);

        TrustChain afterVouchedAttempts = await ComputeAsync();

        afterVouchedAttempts.IsAllowedSigner(target.Fingerprint).Should().BeTrue(
            "the laptop's own revocation is not signed by a live root key and is ignored");
        afterVouchedAttempts.Members.Should().NotContain(
            m => m.RootFingerprint == outsider.Fingerprint, "the laptop's own members write is not signed by a live root key and is ignored");
        afterVouchedAttempts.RoleOf(bob.Fingerprint).Should().Be(
            MembershipRole.Member, "the laptop's own role change is not signed by a live root key and is ignored");
        afterVouchedAttempts.IsAllowedSigner(peer.Fingerprint).Should().BeTrue(
            "peer vouching by a plain enrolled node keeps working — only revocation and membership writes narrowed");

        // The root itself signs the identical three writes — every one applies.
        Revoke(owner.Fingerprint, target.NodeId, owner);
        WriteMemberFile(outsider.Fingerprint, "member", owner);
        WriteMemberFile(bob.Fingerprint, "owner", owner);

        TrustChain afterRootSigned = await ComputeAsync();

        afterRootSigned.IsAllowedSigner(target.Fingerprint).Should().BeFalse("the root's own revocation is applied");
        afterRootSigned.RoleOf(outsider.Fingerprint).Should().Be(MembershipRole.Member, "the root's own members write is applied");
        afterRootSigned.RoleOf(bob.Fingerprint).Should().Be(MembershipRole.Owner, "the root's own role change is applied");
    }

    [Fact]
    public async Task A_backdated_committer_date_cannot_authorize_a_vouched_nodes_membership_write()
    {
        // The fix removed committer-date pinning from the authorization rule entirely, so a
        // members-ref write is judged solely against the owner chain's own live state — a claimed
        // date, true or forged, is never consulted at all. The fake graph has no notion of a
        // caller-chosen committer date distinct from signer identity, so this proves the same
        // property the real backdating test proves: timing never matters, only who signed it.
        Identity owner = EstablishGenesisRoot();

        Identity laptop = GenerateIdentity();
        WriteNodeFile(laptop, laptop);
        Vouch(owner.Fingerprint, laptop, owner);

        Identity attacker = GenerateIdentity();
        WriteMemberFile(attacker.Fingerprint, "owner", laptop, committerTime: DateTimeOffset.UtcNow.AddMinutes(-30));

        TrustChain chain = await ComputeAsync();

        chain.RoleOf(attacker.Fingerprint).Should().BeNull(
            "the laptop never holds a live root key, so its own write can never be authorized "
            + "regardless of the committer date it claims");
        chain.UnverifiedWrites.Should().Contain(write => write.Identifier == attacker.Fingerprint);
    }

    // ---- genesis and the project key ---------------------------------------------------------

    [Fact]
    public async Task Genesis_exposes_the_project_key_recorded_in_its_own_commit()
    {
        const string projectKey = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
        Identity owner = EstablishGenesisRoot(projectKey);

        TrustChain chain = await ComputeAsync();

        chain.ProjectKey.Should().Be(projectKey);
        chain.GenesisRootFingerprint.Should().Be(owner.Fingerprint);
        chain.ProjectKey.Should().NotBe(chain.GenesisRootFingerprint);
    }

    [Fact]
    public async Task A_ledger_with_no_project_key_yet_exposes_a_null_one()
    {
        Identity owner = EstablishGenesisRoot();

        TrustChain chain = await ComputeAsync();

        chain.GenesisRootFingerprint.Should().Be(owner.Fingerprint);
        chain.ProjectKey.Should().BeNull("this genesis predates the project key and h9k project assign-key never ran");
    }

    [Fact]
    public async Task A_later_authorized_rewrite_of_the_genesis_file_backfills_the_project_key()
    {
        Identity owner = EstablishGenesisRoot();

        (await ComputeAsync()).ProjectKey.Should().BeNull();

        const string backfilledKey = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
        WriteMemberFile(owner.Fingerprint, "owner", owner, backfilledKey);

        TrustChain afterBackfill = await ComputeAsync();
        afterBackfill.ProjectKey.Should().Be(backfilledKey);
        afterBackfill.GenesisRootFingerprint.Should().Be(owner.Fingerprint, "genesis identity itself never moves");
    }

    [Fact]
    public async Task An_unauthorized_rewrite_of_the_genesis_file_by_a_stranger_cannot_set_the_project_key()
    {
        Identity owner = EstablishGenesisRoot();
        Identity attacker = GenerateIdentity();

        const string forgedKey = "01ARZ3NDEKTSV4RRFFQ69G5FZZ";
        WriteMemberFile(owner.Fingerprint, "owner", attacker, forgedKey);

        TrustChain chain = await ComputeAsync();

        chain.ProjectKey.Should().BeNull("the rewrite was never signed by the genesis root's own chain, so it is refused");
        chain.UnverifiedWrites.Should().Contain(
            write => write.Kind == "membership" && write.RootFingerprint == owner.Fingerprint,
            "the forged rewrite is named rather than silently discarded");
    }

    // ---- carried-record verification (task f53fecfd) -----------------------------------------
    //
    // Checks 1-3 (fingerprint self-certification, and the root/vouch commit signature and message
    // binding) operate purely on the carried YAML's own decoded content — never on this reader's
    // own commit graph — so a fast test builds that embedded evidence directly, with no separate
    // "source ledger" needed at all. Check 4 (the carried key must match some commit in this
    // ledger's own node.yaml history) does read this reader's own graph, exactly as every other
    // scenario here does.

    private (Identity Root, Identity Carrier, string RootYaml, string RootRaw, string VouchRaw) EstablishSourceVouch()
    {
        Identity root = GenerateIdentity();
        Identity carrier = GenerateIdentity();
        string rootYaml = BuildYaml(("public_key", root.PublicKeyLine), ("created_at", Now()));
        string rootRaw = FakeLedgerGraph.EncodeRawCommit(root.PublicKeyLine, "establish root");
        string vouchRaw = FakeLedgerGraph.EncodeRawCommit(root.PublicKeyLine, $"Vouch node {carrier.NodeId} key {carrier.Fingerprint}");
        return (root, carrier, rootYaml, rootRaw, vouchRaw);
    }

    private void WriteCarriedTarget(Identity root, Identity carrier, string rootYaml, string carried, Identity signer)
    {
        WriteNodeFile(carrier, carrier, root.Fingerprint);
        graph.Write(
            $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/root.yaml", rootYaml, signer.PublicKeyLine);
        graph.Write(
            $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/carried/{carrier.NodeId}.yaml",
            carried, signer.PublicKeyLine);
    }

    [Fact]
    public async Task A_valid_carried_bundle_establishes_the_root_and_enrols_the_carrying_node()
    {
        (Identity root, Identity carrier, string rootYaml, string rootRaw, string vouchRaw) = EstablishSourceVouch();
        string carried = BuildCarriedRecordYaml(carrier.NodeId, carrier.PublicKeyLine, rootYaml, rootRaw, vouchRaw);
        WriteCarriedTarget(root, carrier, rootYaml, carried, carrier);

        TrustChain chain = await ComputeAsync();

        chain.OwnerChains.Should().ContainKey(root.Fingerprint, "the carried bundle establishes the root even though nobody here holds its own private key");
        chain.OwnerChains[root.Fingerprint].Nodes.Should().Contain(node => node.NodeId == carrier.NodeId.ToString() && node.Fingerprint == carrier.Fingerprint);
        chain.IsEnrolledInOwner(carrier.Fingerprint, root.Fingerprint).Should().BeTrue();
        chain.UnverifiedWrites.Should().BeEmpty("a bundle that verifies on every check is never recorded as unverifiable");
    }

    [Fact]
    public async Task A_carried_bundle_still_verifies_after_the_carrying_nodes_own_key_later_rotates()
    {
        (Identity root, Identity carrier, string rootYaml, string rootRaw, string vouchRaw) = EstablishSourceVouch();
        string carried = BuildCarriedRecordYaml(carrier.NodeId, carrier.PublicKeyLine, rootYaml, rootRaw, vouchRaw);
        WriteCarriedTarget(root, carrier, rootYaml, carried, carrier);

        // Later, unrelated to the carry itself: this exact node loses its private key, mints a
        // fresh one, and the next h9k project join rewrites node.yaml at the identical node id,
        // self-signed by the new key — no relation at all to the key the root actually vouched.
        Identity rotated = GenerateIdentity();
        Identity rotatedAtSameNode = new(rotated.PublicKeyLine, rotated.Fingerprint, carrier.NodeId);
        WriteNodeFile(rotatedAtSameNode, rotatedAtSameNode, root.Fingerprint);

        TrustChain chain = await ComputeAsync();

        chain.OwnerChains.Should().ContainKey(root.Fingerprint,
            "check 4 accepts any commit in node.yaml's own history that self-signs the carried key, not only the current tip");
        chain.IsEnrolledInOwner(carrier.Fingerprint, root.Fingerprint).Should().BeTrue();
        chain.UnverifiedWrites.Should().BeEmpty("a later, unrelated key rotation on the same node must never retroactively cancel a carry that was genuine when it happened");
    }

    [Fact]
    public async Task A_carried_bundle_whose_embedded_root_yaml_does_not_self_certify_is_rejected_and_recorded()
    {
        (Identity root, Identity carrier, string rootYaml, string rootRaw, string vouchRaw) = EstablishSourceVouch();

        // Check 1's own failure: the embedded root.yaml declares a key that does not fingerprint
        // back to `root` at all.
        Identity impostor = GenerateIdentity();
        string tamperedRootYaml = BuildYaml(("public_key", impostor.PublicKeyLine), ("created_at", Now()));
        string carried = BuildCarriedRecordYaml(carrier.NodeId, carrier.PublicKeyLine, tamperedRootYaml, rootRaw, vouchRaw);
        WriteCarriedTarget(root, carrier, rootYaml, carried, carrier);

        TrustChain chain = await ComputeAsync();

        chain.OwnerChains.Should().NotContainKey(root.Fingerprint, "root.yaml's own commit is not signed by the root, and the only bundle present fails to self-certify");
        chain.UnverifiedWrites.Should().Contain(write => write.Kind == "carried" && write.Identifier == carrier.NodeId.ToString());
    }

    [Fact]
    public async Task A_carried_bundle_whose_embedded_root_commit_is_not_signed_by_the_root_is_rejected_and_recorded()
    {
        (Identity root, Identity carrier, string rootYaml, _, string vouchRaw) = EstablishSourceVouch();

        // Check 2's own failure: the embedded root commit's own raw bytes are signed by someone
        // other than the root.
        Identity stranger = GenerateIdentity();
        string strangerRaw = FakeLedgerGraph.EncodeRawCommit(stranger.PublicKeyLine, "establish root");
        string carried = BuildCarriedRecordYaml(carrier.NodeId, carrier.PublicKeyLine, rootYaml, strangerRaw, vouchRaw);
        WriteCarriedTarget(root, carrier, rootYaml, carried, carrier);

        TrustChain chain = await ComputeAsync();

        chain.OwnerChains.Should().NotContainKey(root.Fingerprint);
        chain.UnverifiedWrites.Should().Contain(write => write.Kind == "carried" && write.Identifier == carrier.NodeId.ToString());
    }

    [Fact]
    public async Task A_carried_bundle_whose_embedded_vouch_commit_is_not_signed_by_the_root_is_rejected_and_recorded()
    {
        (Identity root, Identity carrier, string rootYaml, string rootRaw, _) = EstablishSourceVouch();

        // Check 3's own failure: the embedded vouch commit is signed by the carrier itself rather
        // than the root — self-vouching is never authoritative.
        string selfSignedVouch = FakeLedgerGraph.EncodeRawCommit(
            carrier.PublicKeyLine, $"Vouch node {carrier.NodeId} key {carrier.Fingerprint}");
        string carried = BuildCarriedRecordYaml(carrier.NodeId, carrier.PublicKeyLine, rootYaml, rootRaw, selfSignedVouch);
        WriteCarriedTarget(root, carrier, rootYaml, carried, carrier);

        TrustChain chain = await ComputeAsync();

        chain.OwnerChains.Should().NotContainKey(root.Fingerprint);
        chain.UnverifiedWrites.Should().Contain(write => write.Kind == "carried" && write.Identifier == carrier.NodeId.ToString());
    }

    [Fact]
    public async Task A_bundle_presented_by_a_node_with_a_different_key_establishes_nothing()
    {
        (Identity root, Identity carrier, string rootYaml, string rootRaw, string vouchRaw) = EstablishSourceVouch();
        Identity presenter = GenerateIdentity();

        string carried = BuildCarriedRecordYaml(carrier.NodeId, carrier.PublicKeyLine, rootYaml, rootRaw, vouchRaw);
        // The presenter self-announces under the VICTIM's own node id, declaring their OWN key —
        // it never held `carrier`'s own private key, so it cannot produce a node.yaml declaring
        // that key.
        WriteNodeFile(new Identity(presenter.PublicKeyLine, presenter.Fingerprint, carrier.NodeId), presenter, root.Fingerprint);
        graph.Write(
            $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/root.yaml", rootYaml, presenter.PublicKeyLine);
        graph.Write(
            $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/carried/{carrier.NodeId}.yaml",
            carried, presenter.PublicKeyLine);

        TrustChain chain = await ComputeAsync();

        chain.OwnerChains.Should().NotContainKey(root.Fingerprint, "the presenter never held the carried node's own private key");
        chain.IsEnrolledInOwner(carrier.Fingerprint, root.Fingerprint).Should().BeFalse();
        chain.UnverifiedWrites.Should().Contain(write => write.Kind == "carried" && write.Identifier == carrier.NodeId.ToString());
    }

    [Fact]
    public async Task An_on_ledger_revocation_overrides_a_carried_vouch()
    {
        (Identity root, Identity carrier, string rootYaml, string rootRaw, string vouchRaw) = EstablishSourceVouch();
        string carried = BuildCarriedRecordYaml(carrier.NodeId, carrier.PublicKeyLine, rootYaml, rootRaw, vouchRaw);
        WriteCarriedTarget(root, carrier, rootYaml, carried, carrier);

        // Nobody on this carried-only target ledger ever pushed anything else signed by root, but
        // this test still has access to root's own identity locally, exactly what actually holding
        // the root key means — wherever that holder chooses to sign from.
        Revoke(root.Fingerprint, carrier.NodeId, root);

        TrustChain chain = await ComputeAsync();

        chain.OwnerChains.Should().ContainKey(root.Fingerprint, "the root itself is still established by the carried bundle");
        chain.OwnerChains[root.Fingerprint].Nodes.Should().NotContain(node => node.NodeId == carrier.NodeId.ToString());
        chain.IsEnrolledInOwner(carrier.Fingerprint, root.Fingerprint).Should().BeFalse("the revocation lands after the carried vouch in ref order, so it wins");
    }

    [Fact]
    public async Task A_carried_nodes_own_self_signed_revocation_is_refused_since_it_never_holds_the_root_key()
    {
        (Identity root, Identity carrier, string rootYaml, string rootRaw, string vouchRaw) = EstablishSourceVouch();
        string carried = BuildCarriedRecordYaml(carrier.NodeId, carrier.PublicKeyLine, rootYaml, rootRaw, vouchRaw);
        WriteCarriedTarget(root, carrier, rootYaml, carried, carrier);

        Revoke(root.Fingerprint, carrier.NodeId, carrier);

        TrustChain chain = await ComputeAsync();

        chain.OwnerChains.Should().ContainKey(root.Fingerprint, "the root itself is still established by the carried bundle");
        chain.IsEnrolledInOwner(carrier.Fingerprint, root.Fingerprint).Should().BeTrue(
            "the carried node's own self-signed revocation is not signed by a live root key and is ignored");
        chain.UnverifiedWrites.Should().Contain(
            write => write.Kind == "revocation" && write.Identifier == carrier.NodeId.ToString(),
            "the refused revocation is named rather than silently dropped");
    }

    [Fact]
    public async Task An_earlier_failed_carry_attempt_never_blocks_a_later_genuinely_valid_one_for_the_same_node()
    {
        (Identity root, Identity carrier, string rootYaml, string rootRaw, string vouchRaw) = EstablishSourceVouch();
        WriteNodeFile(carrier, carrier, root.Fingerprint);
        graph.Write(
            $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/root.yaml", rootYaml, carrier.PublicKeyLine);
        string carriedPath = $"owners/{root.Fingerprint}/carried/{carrier.NodeId}.yaml";

        // A garbage first commit at this exact node id's own carried path — missing every
        // required field, so verification fails it outright.
        graph.Write($"refs/hall9k/ledger/owners/{root.Fingerprint}", carriedPath, "node_id: \"garbage\"\n", carrier.PublicKeyLine);

        // The genuine, fully valid bundle, a later commit at the identical path.
        string carried = BuildCarriedRecordYaml(carrier.NodeId, carrier.PublicKeyLine, rootYaml, rootRaw, vouchRaw);
        graph.Write($"refs/hall9k/ledger/owners/{root.Fingerprint}", carriedPath, carried, carrier.PublicKeyLine);

        TrustChain chain = await ComputeAsync();

        chain.IsEnrolledInOwner(carrier.Fingerprint, root.Fingerprint).Should().BeTrue(
            "the valid second commit still self-authorizes on its own embedded evidence — nothing was ever actually established by the first, failed one");
    }

    [Fact]
    public async Task A_revoked_carried_node_cannot_resurrect_itself_by_re_pushing_its_own_carried_record()
    {
        (Identity root, Identity carrier, string rootYaml, string rootRaw, string vouchRaw) = EstablishSourceVouch();
        WriteNodeFile(carrier, carrier, root.Fingerprint);
        graph.Write(
            $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/root.yaml", rootYaml, carrier.PublicKeyLine);
        string carried = BuildCarriedRecordYaml(carrier.NodeId, carrier.PublicKeyLine, rootYaml, rootRaw, vouchRaw);
        string carriedPath = $"owners/{root.Fingerprint}/carried/{carrier.NodeId}.yaml";
        graph.Write($"refs/hall9k/ledger/owners/{root.Fingerprint}", carriedPath, carried, carrier.PublicKeyLine);
        Revoke(root.Fingerprint, carrier.NodeId, root);

        // The revoked node re-pushes its own bundle again, signed with its own (still held)
        // private key — nobody else's. A distinct commit (a different source project id this
        // time) rather than the byte-identical original.
        string recarried = BuildCarriedRecordYaml(carrier.NodeId, carrier.PublicKeyLine, rootYaml, rootRaw, vouchRaw, sourceProjectId: Guid.NewGuid());
        graph.Write($"refs/hall9k/ledger/owners/{root.Fingerprint}", carriedPath, recarried, carrier.PublicKeyLine);

        TrustChain chain = await ComputeAsync();

        chain.OwnerChains.Should().ContainKey(root.Fingerprint, "the root itself was already established by the first, legitimate carry");
        chain.IsEnrolledInOwner(carrier.Fingerprint, root.Fingerprint).Should().BeFalse(
            "a revoked carried node cannot re-authorize its own return by presenting its own evidence again");
        chain.UnverifiedWrites.Should().Contain(
            write => write.Kind == "carried" && write.Identifier == carrier.NodeId.ToString(),
            "the unauthorized re-touch is named rather than silently accepted");
    }

    [Fact]
    public async Task A_carried_bundle_reusing_an_unrelated_root_signed_commit_as_its_vouch_establishes_nothing()
    {
        (Identity root, Identity carrier, string rootYaml, string rootRaw, _) = EstablishSourceVouch();

        // root.yaml's own establishing commit, reused as "the vouch" — its own message never
        // names this node, so it must never satisfy check 3 on its own merits alone.
        string carried = BuildCarriedRecordYaml(carrier.NodeId, carrier.PublicKeyLine, rootYaml, rootRaw, rootRaw);
        WriteCarriedTarget(root, carrier, rootYaml, carried, carrier);

        TrustChain chain = await ComputeAsync();

        chain.OwnerChains.Should().NotContainKey(root.Fingerprint, "a commit that never names this node can never stand in as its vouch");
        chain.IsEnrolledInOwner(carrier.Fingerprint, root.Fingerprint).Should().BeFalse();
        chain.UnverifiedWrites.Should().Contain(write => write.Kind == "carried" && write.Identifier == carrier.NodeId.ToString());
    }

    [Fact]
    public async Task A_genesis_members_commit_signed_by_a_carried_node_is_accepted()
    {
        (Identity root, Identity carrier, string rootYaml, string rootRaw, string vouchRaw) = EstablishSourceVouch();
        string carried = BuildCarriedRecordYaml(carrier.NodeId, carrier.PublicKeyLine, rootYaml, rootRaw, vouchRaw);
        WriteCarriedTarget(root, carrier, rootYaml, carried, carrier);

        // Genesis members commit for this target project, signed by the CARRIED node — never the
        // root's own key, which nobody here holds.
        const string mintedProjectKey = "01ARZ3NDEKTSV4RRFFQ69G5FBB";
        WriteMemberFile(root.Fingerprint, "owner", carrier, mintedProjectKey);

        TrustChain chain = await ComputeAsync();

        chain.Members.Should().Contain(member => member.RootFingerprint == root.Fingerprint && member.Role == MembershipRole.Owner);
        chain.ProjectKey.Should().Be(mintedProjectKey);
        chain.UnverifiedWrites.Should().NotContain(write => write.Kind == "membership");
    }

    [Fact]
    public async Task A_genesis_members_commit_signed_by_a_carried_node_stays_authorized_after_that_node_is_revoked()
    {
        (Identity root, Identity carrier, string rootYaml, string rootRaw, string vouchRaw) = EstablishSourceVouch();
        string carried = BuildCarriedRecordYaml(carrier.NodeId, carrier.PublicKeyLine, rootYaml, rootRaw, vouchRaw);
        WriteCarriedTarget(root, carrier, rootYaml, carried, carrier);

        const string mintedProjectKey = "01ARZ3NDEKTSV4RRFFQ69G5FDD";
        WriteMemberFile(root.Fingerprint, "owner", carrier, mintedProjectKey);

        // The root-holding node later joins this same project and revokes the carrying node —
        // signed with the root's own real key.
        Revoke(root.Fingerprint, carrier.NodeId, root);

        TrustChain chain = await ComputeAsync();

        chain.Members.Should().Contain(
            member => member.RootFingerprint == root.Fingerprint && member.Role == MembershipRole.Owner,
            "genesis is a one-time, immutable fact — revoking its only ever signer must never undo it");
        chain.ProjectKey.Should().Be(mintedProjectKey, "the project key was minted at genesis and never depends on the carrying node staying enrolled");
        chain.UnverifiedWrites.Should().NotContain(write => write.Kind == "membership");
        chain.IsEnrolledInOwner(carrier.Fingerprint, root.Fingerprint).Should().BeFalse("the carrying node is revoked as of this read");
    }

    [Fact]
    public async Task A_genesis_members_commit_signed_by_an_unenrolled_node_is_refused()
    {
        (Identity root, Identity carrier, string rootYaml, string rootRaw, string vouchRaw) = EstablishSourceVouch();
        Identity stranger = GenerateIdentity();
        string carried = BuildCarriedRecordYaml(carrier.NodeId, carrier.PublicKeyLine, rootYaml, rootRaw, vouchRaw);
        WriteCarriedTarget(root, carrier, rootYaml, carried, carrier);

        // A genesis members commit for the identical root, but signed by a stranger this chain
        // never enrolled at all.
        WriteMemberFile(root.Fingerprint, "owner", stranger, "01ARZ3NDEKTSV4RRFFQ69G5FCC");

        TrustChain chain = await ComputeAsync();

        chain.Members.Should().BeEmpty("genesis is spent by this refused commit either way, but it never grants membership");
        chain.ProjectKey.Should().BeNull();
        chain.UnverifiedWrites.Should().Contain(write => write.Kind == "membership" && write.RootFingerprint == root.Fingerprint);
    }

    [Fact]
    public async Task A_carried_bundle_with_a_substituted_node_public_key_establishes_nothing()
    {
        (Identity root, Identity carrier, string rootYaml, string rootRaw, string vouchRaw) = EstablishSourceVouch();
        Identity attacker = GenerateIdentity();

        // The attacker self-announces under the VICTIM's own node id, declaring their own key.
        WriteNodeFile(new Identity(attacker.PublicKeyLine, attacker.Fingerprint, carrier.NodeId), attacker, root.Fingerprint);
        graph.Write(
            $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/root.yaml", rootYaml, attacker.PublicKeyLine);
        // node_public_key is substituted to the attacker's key, but the genuine vouch commit's own
        // message still names the victim's own fingerprint.
        string carried = BuildCarriedRecordYaml(carrier.NodeId, attacker.PublicKeyLine, rootYaml, rootRaw, vouchRaw);
        graph.Write(
            $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/carried/{carrier.NodeId}.yaml",
            carried, attacker.PublicKeyLine);

        TrustChain chain = await ComputeAsync();

        chain.OwnerChains.Should().NotContainKey(root.Fingerprint, "the genuine vouch commit's own message names the victim's key, never the attacker's substituted one");
        chain.IsEnrolledInOwner(attacker.Fingerprint, root.Fingerprint).Should().BeFalse();
        chain.UnverifiedWrites.Should().Contain(write => write.Kind == "carried" && write.Identifier == carrier.NodeId.ToString());
    }

    [Fact]
    public async Task A_carried_bundle_using_the_roots_own_revocation_commit_as_its_vouch_establishes_nothing()
    {
        (Identity root, Identity carrier, string rootYaml, string rootRaw, _) = EstablishSourceVouch();

        // A root-signed "Revoke node {id}" commit also names the node id in its own message, so a
        // check that only asked "signed by root and names this node id" would have accepted it.
        string revokeRaw = FakeLedgerGraph.EncodeRawCommit(root.PublicKeyLine, $"Revoke node {carrier.NodeId}");
        string carried = BuildCarriedRecordYaml(carrier.NodeId, carrier.PublicKeyLine, rootYaml, rootRaw, revokeRaw);
        WriteCarriedTarget(root, carrier, rootYaml, carried, carrier);

        TrustChain chain = await ComputeAsync();

        chain.OwnerChains.Should().NotContainKey(root.Fingerprint, "a revocation commit was never a vouch, even though it also names the node id");
        chain.UnverifiedWrites.Should().Contain(write => write.Kind == "carried" && write.Identifier == carrier.NodeId.ToString());
    }

    [Fact]
    public async Task A_normally_vouched_then_revoked_node_cannot_resurrect_itself_via_a_first_time_carried_record()
    {
        Identity root = EstablishGenesisRoot();
        Identity carrier = GenerateIdentity();
        WriteNodeFile(carrier, carrier);

        // Vouched the ORDINARY way, signed by the root's own real key — never carried before.
        Vouch(root.Fingerprint, carrier, root);
        Revoke(root.Fingerprint, carrier.NodeId, root);

        string rootYaml = BuildYaml(("public_key", root.PublicKeyLine), ("created_at", Now()));
        string rootRaw = FakeLedgerGraph.EncodeRawCommit(root.PublicKeyLine, "establish root");
        string vouchRaw = FakeLedgerGraph.EncodeRawCommit(root.PublicKeyLine, $"Vouch node {carrier.NodeId} key {carrier.Fingerprint}");

        // The revoked node's first-ever carried record, presenting its own still-valid, unchanged
        // source evidence — signed with its own (still held) private key, nobody else's.
        string carried = BuildCarriedRecordYaml(carrier.NodeId, carrier.PublicKeyLine, rootYaml, rootRaw, vouchRaw);
        graph.Write(
            $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/carried/{carrier.NodeId}.yaml",
            carried, carrier.PublicKeyLine);

        TrustChain chain = await ComputeAsync();

        chain.IsEnrolledInOwner(carrier.Fingerprint, root.Fingerprint).Should().BeFalse(
            "a node revoked the ordinary way cannot resurrect itself by presenting its own carried evidence for the first time");
        chain.UnverifiedWrites.Should().Contain(write => write.Kind == "carried" && write.Identifier == carrier.NodeId.ToString());
    }

    // ---- succession (idea 6be68ee2: an owner's root authority is a ranked set of root keys) -----

    [Fact]
    public async Task A_root_signed_successor_attaches_and_a_node_signed_one_does_not()
    {
        Identity root = EstablishGenesisRoot();

        Identity rootSignedHeir = GenerateIdentity();
        WriteNodeFile(rootSignedHeir, rootSignedHeir);
        Vouch(root.Fingerprint, rootSignedHeir, root);
        WriteSuccessor(root.Fingerprint, rootSignedHeir, root);

        Identity nodeSignedHeir = GenerateIdentity();
        WriteNodeFile(nodeSignedHeir, nodeSignedHeir);
        Vouch(root.Fingerprint, nodeSignedHeir, root);
        WriteSuccessor(root.Fingerprint, nodeSignedHeir, nodeSignedHeir);

        TrustChain chain = await ComputeAsync();

        chain.OwnerChains[root.Fingerprint].SuccessorNodeIds.Should().Contain(rootSignedHeir.NodeId.ToString());
        chain.OwnerChains[root.Fingerprint].SuccessorNodeIds.Should().NotContain(nodeSignedHeir.NodeId.ToString());
        chain.UnverifiedWrites.Should().Contain(write => write.Kind == "successor" && write.Identifier == nodeSignedHeir.NodeId.ToString());
    }

    [Fact]
    public async Task A_rotation_by_a_listed_successor_is_accepted_and_a_k0_members_write_still_verifies_afterwards()
    {
        Identity root = EstablishGenesisRoot();
        Identity heir = GenerateIdentity();
        WriteNodeFile(heir, heir);
        Vouch(root.Fingerprint, heir, root);
        WriteSuccessor(root.Fingerprint, heir, root);
        WriteRotation(root.Fingerprint, 1, heir, root.PublicKeyLine);

        // K0 signs a fresh, ordinary (non-genesis) membership write AFTER the rotation.
        Identity newMember = GenerateIdentity();
        WriteMemberFile(newMember.Fingerprint, "member", root);

        TrustChain chain = await ComputeAsync();

        chain.OwnerChains[root.Fingerprint].RootKeys.Should().Contain(
            key => key.Fingerprint == heir.Fingerprint && key.IntroducedByNodeId == heir.NodeId.ToString());
        chain.RoleOf(newMember.Fingerprint).Should().Be(
            MembershipRole.Member, "K0 still verifies every members write it signs after a later rotation");
    }

    [Fact]
    public async Task A_rotation_by_an_unlisted_key_is_ignored()
    {
        Identity root = EstablishGenesisRoot();
        Identity node = GenerateIdentity();
        WriteNodeFile(node, node);
        Vouch(root.Fingerprint, node, root);
        // Never listed as a successor.
        WriteRotation(root.Fingerprint, 1, node, root.PublicKeyLine);

        TrustChain chain = await ComputeAsync();

        chain.OwnerChains[root.Fingerprint].RootKeys.Should().ContainSingle(key => key.Fingerprint == root.Fingerprint);
        chain.UnverifiedWrites.Should().Contain(write => write.Kind == "rotation" && write.Identifier == node.NodeId.ToString());
    }

    [Fact]
    public async Task A_stale_rotation_superseding_a_non_current_key_is_ignored()
    {
        Identity root = EstablishGenesisRoot();

        Identity firstHeir = GenerateIdentity();
        WriteNodeFile(firstHeir, firstHeir);
        Vouch(root.Fingerprint, firstHeir, root);
        WriteSuccessor(root.Fingerprint, firstHeir, root);
        WriteRotation(root.Fingerprint, 1, firstHeir, root.PublicKeyLine);

        // Names root's own key (K0) as what it supersedes, but the first rotation already moved
        // the chain's own top forward to K1 — stale by the time this one is reached.
        Identity secondHeir = GenerateIdentity();
        WriteNodeFile(secondHeir, secondHeir);
        Vouch(root.Fingerprint, secondHeir, root);
        WriteSuccessor(root.Fingerprint, secondHeir, root);
        WriteRotation(root.Fingerprint, 2, secondHeir, root.PublicKeyLine);

        TrustChain chain = await ComputeAsync();

        IReadOnlyList<LiveRootKey> rootKeys = chain.OwnerChains[root.Fingerprint].RootKeys;
        rootKeys.Should().HaveCount(2, "only the first rotation to land actually moved the chain's own top");
        rootKeys.Should().Contain(key => key.IntroducedByNodeId == firstHeir.NodeId.ToString());
        rootKeys.Should().NotContain(key => key.IntroducedByNodeId == secondHeir.NodeId.ToString());
        chain.UnverifiedWrites.Should().Contain(write => write.Kind == "rotation" && write.Identifier == secondHeir.NodeId.ToString());
    }

    [Fact]
    public async Task A_revoked_node_cannot_promote()
    {
        Identity root = EstablishGenesisRoot();
        Identity heir = GenerateIdentity();
        WriteNodeFile(heir, heir);
        Vouch(root.Fingerprint, heir, root);
        WriteSuccessor(root.Fingerprint, heir, root);
        Revoke(root.Fingerprint, heir.NodeId, root);
        // The revoked node still holds its own private key and can still sign a rotation commit —
        // it just no longer counts, since it is not currently vouched into the fleet.
        WriteRotation(root.Fingerprint, 1, heir, root.PublicKeyLine);

        TrustChain chain = await ComputeAsync();

        chain.OwnerChains[root.Fingerprint].RootKeys.Should().ContainSingle(key => key.Fingerprint == root.Fingerprint);
        chain.UnverifiedWrites.Should().Contain(write => write.Kind == "rotation" && write.Identifier == heir.NodeId.ToString());
    }

    [Fact]
    public async Task A_revoked_successor_record_truncates_a_still_vouched_rotated_keys_rank()
    {
        Identity root = EstablishGenesisRoot();
        Identity heir = GenerateIdentity();
        WriteNodeFile(heir, heir);
        Vouch(root.Fingerprint, heir, root);
        WriteSuccessor(root.Fingerprint, heir, root);
        WriteRotation(root.Fingerprint, 1, heir, root.PublicKeyLine);

        // K1 (the promoted heir) signs a membership write while it still holds that rank.
        Identity newMember = GenerateIdentity();
        WriteMemberFile(newMember.Fingerprint, "member", heir);

        TrustChain beforeRevocation = await ComputeAsync();
        beforeRevocation.OwnerChains[root.Fingerprint].RootKeys.Should().Contain(
            key => key.IntroducedByNodeId == heir.NodeId.ToString(), "the rotation already landed");
        beforeRevocation.RoleOf(newMember.Fingerprint).Should().Be(
            MembershipRole.Member, "K1 was a live root key when it signed this write, and still is at this read");

        // K0 revokes only the heir's own RANK — never its ordinary fleet membership: no
        // `h9k node revoke` ever runs here, so the heir stays vouched in Nodes throughout.
        WriteRevokedSuccessor(root.Fingerprint, heir.NodeId, root);

        TrustChain afterRevocation = await ComputeAsync();

        TrustedOwner owner = afterRevocation.OwnerChains[root.Fingerprint];
        owner.RootKeys.Should().ContainSingle(
            key => key.Fingerprint == root.Fingerprint, "the revoked-successor record, signed by K0, strips the heir's own rank");
        owner.Nodes.Should().Contain(
            node => node.NodeId == heir.NodeId.ToString(), "the heir's ordinary fleet membership was never touched — only its rank was revoked");
        afterRevocation.RoleOf(newMember.Fingerprint).Should().BeNull(
            "membership is authorized against the chain's own live state at read time, never a snapshot of "
            + "when the write landed — now that the heir's own rank is stripped, its earlier membership write "
            + "is no longer signed by any live root key");
    }

    [Fact]
    public async Task A_lower_ranked_keys_revoked_successor_is_refused()
    {
        Identity root = EstablishGenesisRoot();

        Identity firstHeir = GenerateIdentity();
        WriteNodeFile(firstHeir, firstHeir);
        Vouch(root.Fingerprint, firstHeir, root);
        WriteSuccessor(root.Fingerprint, firstHeir, root);
        WriteRotation(root.Fingerprint, 1, firstHeir, root.PublicKeyLine);

        Identity secondHeir = GenerateIdentity();
        WriteNodeFile(secondHeir, secondHeir);
        Vouch(root.Fingerprint, secondHeir, root);
        WriteSuccessor(root.Fingerprint, secondHeir, firstHeir);
        WriteRotation(root.Fingerprint, 2, secondHeir, firstHeir.PublicKeyLine);

        // The chain is now [K0, K1 (firstHeir), K2 (secondHeir)] — an earlier key always outranks
        // a later one. K2 attempts to revoke K1's own rank, signed by K2's own key: that key ranks
        // BELOW K1, not above it, so this must be refused rather than silently accepted.
        WriteRevokedSuccessor(root.Fingerprint, firstHeir.NodeId, secondHeir);

        TrustChain chain = await ComputeAsync();

        IReadOnlyList<LiveRootKey> rootKeys = chain.OwnerChains[root.Fingerprint].RootKeys;
        rootKeys.Should().HaveCount(3, "a revocation from a key ranked below its target never touches the chain");
        rootKeys.Should().Contain(key => key.IntroducedByNodeId == firstHeir.NodeId.ToString());
        rootKeys.Should().Contain(key => key.IntroducedByNodeId == secondHeir.NodeId.ToString());
        chain.UnverifiedWrites.Should().Contain(
            write => write.Kind == "revoked-successor" && write.Identifier == firstHeir.NodeId.ToString(),
            "the offending write is named rather than silently ignored");
    }

    [Fact]
    public async Task A_merely_vouched_nodes_revocation_of_an_already_rotated_in_successor_never_strips_its_rank()
    {
        Identity root = EstablishGenesisRoot();

        Identity heir = GenerateIdentity();
        WriteNodeFile(heir, heir);
        Vouch(root.Fingerprint, heir, root);
        WriteSuccessor(root.Fingerprint, heir, root);
        WriteRotation(root.Fingerprint, 1, heir, root.PublicKeyLine);

        // Mallory is merely vouched into the fleet — never a root key — and then signs a
        // revocation of the already-promoted heir's own ordinary fleet membership.
        Identity mallory = GenerateIdentity();
        WriteNodeFile(mallory, mallory);
        Vouch(root.Fingerprint, mallory, root);
        Revoke(root.Fingerprint, heir.NodeId, mallory);

        TrustChain chain = await ComputeAsync();

        TrustedOwner owner = chain.OwnerChains[root.Fingerprint];
        owner.RootKeys.Should().Contain(
            key => key.Fingerprint == heir.Fingerprint && key.IntroducedByNodeId == heir.NodeId.ToString(),
            "mallory's own revocation is not signed by a live root key, so the heir's own promoted rank must survive it");
        owner.Nodes.Should().Contain(
            node => node.NodeId == heir.NodeId.ToString(), "mallory's own revocation never actually authorized, so the heir stays enrolled too");
        chain.UnverifiedWrites.Should().Contain(
            write => write.Kind == "revocation" && write.Identifier == heir.NodeId.ToString(),
            "mallory's own attempt is named rather than silently taking effect");
    }

    [Fact]
    public async Task A_rotated_in_successors_own_self_revoke_of_its_obsolete_node_record_never_strips_its_own_rank()
    {
        Identity root = EstablishGenesisRoot();

        Identity heir = GenerateIdentity();
        WriteNodeFile(heir, heir);
        Vouch(root.Fingerprint, heir, root);
        WriteSuccessor(root.Fingerprint, heir, root);
        WriteRotation(root.Fingerprint, 1, heir, root.PublicKeyLine);
        Revoke(root.Fingerprint, heir.NodeId, heir);

        TrustChain chain = await ComputeAsync();

        TrustedOwner owner = chain.OwnerChains[root.Fingerprint];
        owner.RootKeys.Should().Contain(
            key => key.Fingerprint == heir.Fingerprint && key.IntroducedByNodeId == heir.NodeId.ToString(),
            "the heir's own self-revoke is fully authorized (it is itself a live root key) and targets only its "
            + "obsolete ordinary node record, never its own promoted rank");
        owner.Nodes.Should().NotContain(
            node => node.NodeId == heir.NodeId.ToString(), "the heir's own authorized self-revoke does take effect on its ordinary node record");
        owner.RevokedNodeIds.Should().Contain(heir.NodeId.ToString());
    }

    [Fact]
    public async Task A_self_revoke_landing_before_its_own_successor_record_never_oscillates_the_fixed_point_loop()
    {
        Identity root = EstablishGenesisRoot();

        Identity heir = GenerateIdentity();
        WriteNodeFile(heir, heir);
        Vouch(root.Fingerprint, heir, root);
        Revoke(root.Fingerprint, heir.NodeId, heir);
        WriteSuccessor(root.Fingerprint, heir, root);
        WriteRotation(root.Fingerprint, 1, heir, root.PublicKeyLine);

        TrustChain chain = await ComputeAsync();

        TrustedOwner owner = chain.OwnerChains[root.Fingerprint];
        owner.RootKeys.Should().ContainSingle(key => key.Fingerprint == root.Fingerprint,
            "the self-contradictory history never resolves the heir's own promotion, so the smaller, "
            + "more conservative candidate root-key set the cycle ever produced wins rather than "
            + "granting root trust to a disputed key");

        // Deterministic regardless of how many times the reader is asked.
        TrustChain secondRead = await ComputeAsync();
        secondRead.OwnerChains[root.Fingerprint].RootKeys.Should().BeEquivalentTo(owner.RootKeys);
    }

    [Fact]
    public async Task A_one_owner_fleet_with_no_rotation_reads_exactly_as_before()
    {
        Identity root = EstablishGenesisRoot();
        WriteNodeFile(root, root);

        TrustChain chain = await ComputeAsync();

        TrustedOwner owner = chain.OwnerChains[root.Fingerprint];
        owner.RootKeys.Should().ContainSingle();
        owner.RootKeys[0].PublicKeyLine.Should().Be(root.PublicKeyLine);
        owner.RootKeys[0].Fingerprint.Should().Be(root.Fingerprint);
        owner.RootKeys[0].IntroducedByNodeId.Should().BeNull();
        owner.SuccessorNodeIds.Should().BeEmpty();
        chain.IsAllowedSigner(root.Fingerprint).Should().BeTrue();
    }

    // ---- test scaffolding ----------------------------------------------------------------------

    private sealed record Identity(string PublicKeyLine, string Fingerprint, Guid NodeId);

    private static Identity GenerateIdentity()
    {
        string publicKeyLine = $"ssh-ed25519 {Convert.ToBase64String(Guid.NewGuid().ToByteArray())} fake";
        return new Identity(publicKeyLine, NodeKeyStore.Fingerprint(publicKeyLine), Guid.NewGuid());
    }

    private Identity EstablishGenesisRoot(string? projectKey = null)
    {
        Identity owner = GenerateIdentity();
        WriteRootFile(owner);
        WriteMemberFile(owner.Fingerprint, "owner", owner, projectKey);
        return owner;
    }

    private void WriteRootFile(Identity root)
    {
        string refName = $"refs/hall9k/ledger/owners/{root.Fingerprint}";
        string path = $"owners/{root.Fingerprint}/root.yaml";
        string content = BuildYaml(("public_key", root.PublicKeyLine), ("created_at", Now()));
        graph.Write(refName, path, content, root.PublicKeyLine);
    }

    private void WriteNodeFile(
        Identity node, Identity signer, string? ownerFingerprint = null, DeclaredGitHubAccount? github = null,
        string? displayName = null, DateTimeOffset? committerTime = null)
    {
        string refName = $"refs/hall9k/ledger/nodes/{node.NodeId}";
        string path = $"nodes/{node.NodeId}/node.yaml";
        List<(string Key, string Value)> fields =
        [
            ("node_id", node.NodeId.ToString()),
            ("public_key", node.PublicKeyLine),
            ("owner_fingerprint", ownerFingerprint ?? node.Fingerprint),
        ];
        if (github is not null)
        {
            fields.Add(("github_login", github.Login));
            fields.Add(("github_account_id", github.AccountId.ToString(CultureInfo.InvariantCulture)));
        }

        if (displayName is not null)
        {
            fields.Add(("display_name", displayName));
        }

        graph.Write(refName, path, BuildYaml([.. fields]), signer.PublicKeyLine, committerTime: committerTime);
    }

    private void WriteMemberFile(
        string rootFingerprint, string role, Identity signer, string? projectKey = null, DateTimeOffset? committerTime = null)
    {
        string refName = "refs/hall9k/ledger/members";
        string path = $"members/{rootFingerprint}.yaml";
        string content = projectKey is null
            ? BuildYaml(("root_fingerprint", rootFingerprint), ("role", role), ("issued_at", Now()))
            : BuildYaml(("root_fingerprint", rootFingerprint), ("role", role), ("issued_at", Now()), ("project_key", projectKey));
        graph.Write(refName, path, content, signer.PublicKeyLine, committerTime: committerTime);
    }

    private void Vouch(string ownerRoot, Identity target, Identity signer)
    {
        string refName = $"refs/hall9k/ledger/owners/{ownerRoot}";
        string path = $"owners/{ownerRoot}/nodes/{target.NodeId}.yaml";
        string content = BuildYaml(("node_id", target.NodeId.ToString()), ("public_key", target.PublicKeyLine), ("issued_at", Now()));
        graph.Write(refName, path, content, signer.PublicKeyLine, $"Vouch node {target.NodeId} key {target.Fingerprint}");
    }

    private void Revoke(string ownerRoot, Guid targetNodeId, Identity signer)
    {
        string refName = $"refs/hall9k/ledger/owners/{ownerRoot}";
        string path = $"owners/{ownerRoot}/revoked/{targetNodeId}.yaml";
        string content = BuildYaml(("node_id", targetNodeId.ToString()), ("revoked_at", Now()));
        graph.Write(refName, path, content, signer.PublicKeyLine, $"Revoke node {targetNodeId}");
    }

    private void WriteSuccessor(string ownerRoot, Identity target, Identity signer)
    {
        string refName = $"refs/hall9k/ledger/owners/{ownerRoot}";
        string path = $"owners/{ownerRoot}/successors/{target.NodeId}.yaml";
        string content = BuildYaml(("node_id", target.NodeId.ToString()), ("public_key", target.PublicKeyLine), ("issued_at", Now()));
        graph.Write(refName, path, content, signer.PublicKeyLine, $"Successor {target.NodeId}");
    }

    private void WriteRotation(string ownerRoot, int sequence, Identity promoter, string supersedesPublicKeyLine)
    {
        string refName = $"refs/hall9k/ledger/owners/{ownerRoot}";
        string path = $"owners/{ownerRoot}/rotations/{sequence}.yaml";
        string content = BuildYaml(
            ("node_id", promoter.NodeId.ToString()),
            ("public_key", promoter.PublicKeyLine),
            ("supersedes_public_key", supersedesPublicKeyLine),
            ("issued_at", Now()));
        graph.Write(refName, path, content, promoter.PublicKeyLine, $"Rotate to node {promoter.NodeId}");
    }

    private void WriteRevokedSuccessor(string ownerRoot, Guid targetNodeId, Identity signer)
    {
        string refName = $"refs/hall9k/ledger/owners/{ownerRoot}";
        string path = $"owners/{ownerRoot}/revoked-successors/{targetNodeId}.yaml";
        string content = BuildYaml(("node_id", targetNodeId.ToString()), ("revoked_at", Now()));
        graph.Write(refName, path, content, signer.PublicKeyLine, $"Revoke successor {targetNodeId}");
    }

    private static string BuildCarriedRecordYaml(
        Guid carriedNodeId, string carriedNodePublicKeyLine, string rootYaml, string rootRaw, string vouchRaw, Guid? sourceProjectId = null) =>
        BuildYaml(
            ("node_id", carriedNodeId.ToString()),
            ("node_public_key", carriedNodePublicKeyLine),
            ("source_project_id", (sourceProjectId ?? Guid.NewGuid()).ToString()),
            ("source_project_key", "01ARZ3NDEKTSV4RRFFQ69G5FAV"),
            ("source_origin_url", "fake-source"),
            ("root_yaml_base64", EncodeBase64(rootYaml)),
            ("root_commit_sha", "fake-root-sha"),
            ("root_commit_base64", EncodeBase64(rootRaw)),
            ("vouch_yaml_base64", EncodeBase64("fake-vouch-yaml")),
            ("vouch_commit_sha", "fake-vouch-sha"),
            ("vouch_commit_base64", EncodeBase64(vouchRaw)),
            ("carried_at", Now()));

    private static string EncodeBase64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string Now() => DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private static string BuildYaml(params (string Key, string Value)[] fields)
    {
        StringBuilder builder = new();
        foreach ((string key, string value) in fields)
        {
            builder.Append(key).Append(": \"").Append(value.Replace("\\", "\\\\").Replace("\"", "\\\"")).AppendLine("\"");
        }

        return builder.ToString();
    }
}
