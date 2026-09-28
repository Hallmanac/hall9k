using System.Diagnostics;
using System.Globalization;
using System.Text;
using FluentAssertions;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Connectors.Ledger;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Connectors.Trust;

/// <summary>
/// <see cref="GitLedgerChainReader"/> against real throwaway bare repositories
/// (<see cref="LedgerTestRepo"/>, reused directly rather than a second copy of the identical
/// pattern) — the second place, besides <see cref="Hall9k.Tests.Connectors.Ledger.GitLedgerTests"/>,
/// git is allowed in tests at all (Brian's 2026-09-13 testing rule). Every test stands up a "hub"
/// bare repo and one or more "node" clones of it, writes ledger files directly through a real
/// <see cref="GitLedger"/> signed with real generated ed25519 keys, then asks a fresh
/// <see cref="GitLedgerChainReader"/> — reading through its own independent clone wherever the
/// scenario calls for "a third node's own read" — what it currently trusts.
/// <para>
/// <c>[Collection("RealProcessSpawn")]</c> (README.md, "<c>[Collection("RealProcessSpawn")]</c>"):
/// the carried-record tests (task f53fecfd) each spin up a source hub plus a target hub and one or
/// more clones of each, several real <c>git</c> subprocess spawns per test, heavily enough to
/// contend with <see cref="Hall9k.Tests.Daemon.ProcessManagerParityTests"/>' own nested process
/// spawn/teardown the identical way that class's own doc names for its sibling classes in this
/// collection (independent pre-PR review, cycle 1, adversarial lens, low).
/// </para>
/// </summary>
[Collection("RealProcessSpawn")]
[Trait("Category", "RealProcessSpawn")]
public sealed class GitLedgerChainReaderTests : IDisposable
{
    private readonly LedgerTestRepo _repo = new();
    private readonly GitLedger _ledger = new(NullLogger<GitLedger>.Instance);
    private readonly GitLedgerChainReader _chainReader = new();
    private readonly List<string> _generatedKeyPaths = [];

    public void Dispose()
    {
        _repo.Dispose();
        foreach (string keyPath in _generatedKeyPaths)
        {
            try
            {
                File.Delete(keyPath);
                File.Delete($"{keyPath}.pub");
            }
            catch (IOException)
            {
                // Best-effort cleanup of a throwaway temp key.
            }
        }
    }

    [Fact]
    public async Task A_vouched_second_node_verifies_on_a_third_nodes_read()
    {
        string hub = _repo.CreateHub();
        (string nodeARepo, GeneratedIdentity nodeA) = await EstablishGenesisRootAsync(hub);
        GeneratedIdentity nodeB = GenerateIdentity();

        // Node B self-announces (unvouched) in its own clone.
        string nodeBRepo = _repo.CloneNode(hub);
        await WriteNodeFileAsync(nodeBRepo, nodeB, nodeB);

        // Node A, already the enrolled root, vouches node B.
        await VouchAsync(nodeARepo, nodeA.Fingerprint, nodeB, nodeA);

        // A third, independent clone — never the writer, never the vouchee — reads the chain fresh.
        string nodeCRepo = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(nodeCRepo, CancellationToken.None);

        chain.IsAllowedSigner(nodeB.Fingerprint).Should().BeTrue("node B was vouched by the enrolled root");
        chain.OwnerChains[nodeA.Fingerprint].Nodes.Should().Contain(node => node.NodeId == nodeB.NodeId.ToString());
    }

    [Fact]
    public async Task The_genesis_nodes_own_self_announced_node_file_becomes_the_roots_own_node_id()
    {
        // The exact shape idea 202383dc's own join flow produces for a genesis node: root.yaml and
        // this node's own nodes/<id>/node.yaml both carry the identical key, and there is never an
        // owners/<root>/nodes/<id>.yaml vouch file for it — a root never vouches itself.
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);
        await WriteNodeFileAsync(ownerRepo, owner, owner);

        string readerRepo = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.OwnerChains[owner.Fingerprint].RootNodeId.Should().Be(owner.NodeId.ToString());
        chain.OwnerChains[owner.Fingerprint].Nodes.Should().BeEmpty("the root never vouches itself");
        chain.OwnerChains[owner.Fingerprint].FleetNodeIds().Should().Contain(owner.NodeId);
    }

    [Fact]
    public async Task A_second_roots_own_self_announced_node_file_never_gets_attached_to_a_different_root()
    {
        // A second, unrelated root that also establishes itself and self-announces its own node —
        // the discovery walk scans every node ref in the project, so this proves it matches each
        // one against its own root's key rather than the first (or only) chain it finds.
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);
        await WriteNodeFileAsync(ownerRepo, owner, owner);

        GeneratedIdentity other = GenerateIdentity();
        string otherRepo = _repo.CloneNode(hub);
        await WriteRootFileAsync(otherRepo, other);
        await WriteNodeFileAsync(otherRepo, other, other);

        string readerRepo = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.OwnerChains[owner.Fingerprint].RootNodeId.Should().Be(owner.NodeId.ToString());
        chain.OwnerChains[other.Fingerprint].RootNodeId.Should().Be(
            other.NodeId.ToString(), "each root's own node id resolves against its own key, never the other root's");
    }

    [Fact]
    public async Task A_strangers_own_node_file_cannot_impersonate_a_real_roots_own_node_id()
    {
        // A node.yaml whose declared public_key field is copy-pasted from the real root's own key,
        // but committed/signed with a different (the stranger's own) key: self-consistency fails
        // because the commit was never actually signed by the key it claims to be.
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);

        GeneratedIdentity stranger = GenerateIdentity();
        string strangerRepo = _repo.CloneNode(hub);
        Guid forgedNodeId = Guid.NewGuid();
        string refName = $"refs/hall9k/ledger/nodes/{forgedNodeId}";
        string path = $"nodes/{forgedNodeId}/node.yaml";
        string content = BuildYaml(("node_id", forgedNodeId.ToString()), ("public_key", owner.PublicKeyLine));
        await WriteAsync(strangerRepo, refName, path, content, stranger);

        string readerRepo = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.OwnerChains[owner.Fingerprint].RootNodeId.Should().BeNull(
            "the forged node.yaml was never actually signed by the root's own key, only claims to carry it");
        chain.UnverifiedWrites.Should().Contain(
            write => write.Kind == "node" && write.Identifier == forgedNodeId.ToString() && write.RootFingerprint == owner.Fingerprint,
            "the forgery is named rather than silently vanishing with no diagnostic at all "
            + "(independent pre-PR review, cycle 1, adversarial lens, medium)");
    }

    [Fact]
    public async Task A_forgery_sorting_after_the_already_resolved_genuine_root_node_is_still_named()
    {
        // independent pre-PR review, cycle 3, both lenses, medium: AttachRootNodeIdsAsync used to
        // stop scanning node refs the moment every owner chain already had a RootNodeId, so a
        // forgery claiming an already-resolved root's own key went unrecorded whenever ls-remote's
        // refname order happened to place it after the genuine self-announced node ref. Node ids
        // are pinned (rather than left to GenerateIdentity's own random Guid) so the genuine ref
        // reliably sorts before the forged one, reproducing the exact ordering the bug depended on.
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);
        GeneratedIdentity ownerNode = owner with { NodeId = Guid.Parse("00000000-0000-0000-0000-000000000001") };
        await WriteNodeFileAsync(ownerRepo, ownerNode, owner);

        GeneratedIdentity stranger = GenerateIdentity();
        string strangerRepo = _repo.CloneNode(hub);
        Guid forgedNodeId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        string refName = $"refs/hall9k/ledger/nodes/{forgedNodeId}";
        string path = $"nodes/{forgedNodeId}/node.yaml";
        string content = BuildYaml(("node_id", forgedNodeId.ToString()), ("public_key", owner.PublicKeyLine));
        await WriteAsync(strangerRepo, refName, path, content, stranger);

        string readerRepo = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.OwnerChains[owner.Fingerprint].RootNodeId.Should().Be(
            ownerNode.NodeId.ToString(), "the genuine self-announced node still resolves regardless of the forgery elsewhere");
        chain.UnverifiedWrites.Should().Contain(
            write => write.Kind == "node" && write.Identifier == forgedNodeId.ToString() && write.RootFingerprint == owner.Fingerprint,
            "the forgery must be named even though it sorts after the already-resolved genuine root node");
    }

    [Fact]
    public async Task A_root_nodes_self_signed_github_declaration_is_read_for_the_root()
    {
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);
        await WriteNodeFileAsync(ownerRepo, owner, owner, github: new DeclaredGitHubAccount(42, "octocat"));

        TrustChain chain = await _chainReader.ComputeAsync(_repo.CloneNode(hub), CancellationToken.None);

        chain.NodeDeclarations[owner.NodeId.ToString()].Account.Should().Be(new DeclaredGitHubAccount(42, "octocat"));
        chain.DeclaredAccountsOf(owner.Fingerprint).Should().Equal(new DeclaredGitHubAccount(42, "octocat"));
        chain.UnverifiedWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task A_vouched_non_root_nodes_self_signed_github_declaration_is_read_for_its_owner()
    {
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);
        await WriteNodeFileAsync(ownerRepo, owner, owner);
        GeneratedIdentity nodeB = GenerateIdentity();
        string nodeBRepo = _repo.CloneNode(hub);
        await WriteNodeFileAsync(nodeBRepo, nodeB, nodeB, owner.Fingerprint, new DeclaredGitHubAccount(7, "second-machine"));
        await VouchAsync(ownerRepo, owner.Fingerprint, nodeB, owner);

        TrustChain chain = await _chainReader.ComputeAsync(_repo.CloneNode(hub), CancellationToken.None);

        chain.NodeDeclarations.Should().ContainKey(nodeB.NodeId.ToString());
        chain.DeclaredAccountsOf(owner.Fingerprint).Should().Equal(new DeclaredGitHubAccount(7, "second-machine"));
    }

    [Fact]
    public async Task A_node_file_written_before_the_declaration_existed_reads_as_no_declaration_and_is_not_reported()
    {
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);
        await WriteNodeFileAsync(ownerRepo, owner, owner);

        TrustChain chain = await _chainReader.ComputeAsync(_repo.CloneNode(hub), CancellationToken.None);

        chain.NodeDeclarations.Should().BeEmpty();
        chain.DeclaredAccountsOf(owner.Fingerprint).Should().BeEmpty();
        chain.UnverifiedWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task Two_nodes_of_one_owner_declaring_different_accounts_are_two_entries_and_one_renamed_account_is_one()
    {
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);
        await WriteNodeFileAsync(ownerRepo, owner, owner);
        // Pinned well before node C's write below: the newest-login rule reads the committer time,
        // which has one-second resolution, so leaving both to the wall clock could tie them.
        await RewriteNodeFileWithDeclarationAsync(
            ownerRepo, owner, new DeclaredGitHubAccount(42, "personal"), new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        GeneratedIdentity nodeB = GenerateIdentity();
        GeneratedIdentity nodeC = GenerateIdentity();
        string nodeBRepo = _repo.CloneNode(hub);
        await WriteNodeFileAsync(nodeBRepo, nodeB, nodeB, owner.Fingerprint, new DeclaredGitHubAccount(99, "work"));
        string nodeCRepo = _repo.CloneNode(hub);
        // Same account id as the root node, under the name it was renamed to since.
        await WriteNodeFileAsync(nodeCRepo, nodeC, nodeC, owner.Fingerprint, new DeclaredGitHubAccount(42, "personal-renamed"));
        await VouchAsync(ownerRepo, owner.Fingerprint, nodeB, owner);
        await VouchAsync(ownerRepo, owner.Fingerprint, nodeC, owner);

        TrustChain chain = await _chainReader.ComputeAsync(_repo.CloneNode(hub), CancellationToken.None);

        chain.DeclaredAccountsOf(owner.Fingerprint).Should().BeEquivalentTo(
            [new DeclaredGitHubAccount(42, "personal-renamed"), new DeclaredGitHubAccount(99, "work")]);
        chain.UnverifiedWrites.Should().BeEmpty("two accounts on two nodes is never an error");
    }

    [Fact]
    public async Task A_non_root_node_file_rewritten_by_a_key_other_than_its_own_yields_no_declaration_and_is_named()
    {
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);
        GeneratedIdentity nodeB = GenerateIdentity();
        string nodeBRepo = _repo.CloneNode(hub);
        await WriteNodeFileAsync(nodeBRepo, nodeB, nodeB, owner.Fingerprint);
        await VouchAsync(ownerRepo, owner.Fingerprint, nodeB, owner);

        // Anyone with push rewrites node B's file, keeping B's public key line, but signs it as themselves.
        GeneratedIdentity stranger = GenerateIdentity();
        string strangerRepo = _repo.CloneNode(hub);
        await WriteNodeFileAsync(strangerRepo, nodeB, stranger, owner.Fingerprint, new DeclaredGitHubAccount(666, "forged"));

        TrustChain chain = await _chainReader.ComputeAsync(_repo.CloneNode(hub), CancellationToken.None);

        chain.NodeDeclarations.Should().NotContainKey(nodeB.NodeId.ToString());
        chain.DeclaredAccountsOf(owner.Fingerprint).Should().BeEmpty();
        chain.UnverifiedWrites.Should().ContainSingle(
            write => write.Kind == "node" && write.Identifier == nodeB.NodeId.ToString()
                && write.Reason.Contains("GitHub account", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_root_node_file_rewritten_by_a_key_other_than_its_own_yields_no_declaration_and_is_named_once()
    {
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);
        await WriteNodeFileAsync(ownerRepo, owner, owner);

        GeneratedIdentity stranger = GenerateIdentity();
        string strangerRepo = _repo.CloneNode(hub);
        await WriteNodeFileAsync(strangerRepo, owner, stranger, github: new DeclaredGitHubAccount(666, "forged"));

        TrustChain chain = await _chainReader.ComputeAsync(_repo.CloneNode(hub), CancellationToken.None);

        chain.NodeDeclarations.Should().BeEmpty();
        chain.UnverifiedWrites.Should().ContainSingle(write => write.Kind == "node" && write.Identifier == owner.NodeId.ToString());
    }

    [Fact]
    public async Task A_root_nodes_self_signed_display_name_is_read_for_the_root()
    {
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);
        await WriteNodeFileAsync(ownerRepo, owner, owner, displayName: "Ada Lovelace");

        TrustChain chain = await _chainReader.ComputeAsync(_repo.CloneNode(hub), CancellationToken.None);

        chain.NodeDisplayNames[owner.NodeId.ToString()].Name.Value.Should().Be("Ada Lovelace");
        chain.DisplayNameOf(owner.Fingerprint).Value.Should().Be("Ada Lovelace");
        chain.UnverifiedWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task A_vouched_non_root_nodes_self_signed_display_name_is_read_for_its_owner()
    {
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);
        await WriteNodeFileAsync(ownerRepo, owner, owner);
        GeneratedIdentity nodeB = GenerateIdentity();
        string nodeBRepo = _repo.CloneNode(hub);
        await WriteNodeFileAsync(nodeBRepo, nodeB, nodeB, owner.Fingerprint, displayName: "Second Machine");
        await VouchAsync(ownerRepo, owner.Fingerprint, nodeB, owner);

        TrustChain chain = await _chainReader.ComputeAsync(_repo.CloneNode(hub), CancellationToken.None);

        chain.NodeDisplayNames.Should().ContainKey(nodeB.NodeId.ToString());
        chain.DisplayNameOf(owner.Fingerprint).Value.Should().Be("Second Machine");
    }

    [Fact]
    public async Task A_node_file_written_before_the_display_name_existed_reads_as_no_name()
    {
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);
        await WriteNodeFileAsync(ownerRepo, owner, owner);

        TrustChain chain = await _chainReader.ComputeAsync(_repo.CloneNode(hub), CancellationToken.None);

        chain.NodeDisplayNames.Should().BeEmpty();
        chain.DisplayNameOf(owner.Fingerprint).Should().Be(DisplayName.None);
    }

    /// <summary>
    /// The identical self-signature gate a GitHub declaration carries, applied to a display name
    /// (task e6744304). Unlike a forged GitHub declaration, though, this never reaches
    /// <see cref="TrustChain.UnverifiedWrites"/>: a label only has no trust consequence to warn
    /// about.
    /// </summary>
    [Fact]
    public async Task A_node_file_rewritten_by_a_key_other_than_its_own_yields_no_display_name_and_is_not_reported()
    {
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);
        GeneratedIdentity nodeB = GenerateIdentity();
        string nodeBRepo = _repo.CloneNode(hub);
        await WriteNodeFileAsync(nodeBRepo, nodeB, nodeB, owner.Fingerprint);
        await VouchAsync(ownerRepo, owner.Fingerprint, nodeB, owner);

        // Anyone with push rewrites node B's file, keeping B's public key line, but signs it as themselves.
        GeneratedIdentity stranger = GenerateIdentity();
        string strangerRepo = _repo.CloneNode(hub);
        await WriteNodeFileAsync(strangerRepo, nodeB, stranger, owner.Fingerprint, displayName: "Forged Name");

        TrustChain chain = await _chainReader.ComputeAsync(_repo.CloneNode(hub), CancellationToken.None);

        chain.NodeDisplayNames.Should().NotContainKey(nodeB.NodeId.ToString());
        chain.DisplayNameOf(owner.Fingerprint).Should().Be(DisplayName.None);
        chain.UnverifiedWrites.Should().BeEmpty("a display name is a label only, with no trust consequence to name");
    }

    [Fact]
    public async Task Two_nodes_of_one_owner_declaring_different_display_names_the_newest_wins()
    {
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);
        await WriteNodeFileAsync(ownerRepo, owner, owner);
        // Pinned well before node B's write below: the newest-wins rule reads the committer time,
        // which has one-second resolution, so leaving both to the wall clock could tie them.
        await RewriteNodeFileWithDisplayNameAsync(
            ownerRepo, owner, "Older Name", new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        GeneratedIdentity nodeB = GenerateIdentity();
        string nodeBRepo = _repo.CloneNode(hub);
        await WriteNodeFileAsync(nodeBRepo, nodeB, nodeB, owner.Fingerprint, displayName: "Newer Name");
        await VouchAsync(ownerRepo, owner.Fingerprint, nodeB, owner);

        TrustChain chain = await _chainReader.ComputeAsync(_repo.CloneNode(hub), CancellationToken.None);

        chain.DisplayNameOf(owner.Fingerprint).Value.Should().Be("Newer Name");
    }

    /// <summary>
    /// The GitHub declaration and the display name share one node file (task e6744304), so a commit
    /// that refreshes only the GitHub declaration still counts as "touching" the display name's own
    /// path. The newest-wins tie-break must key off when the name itself last changed, not simply
    /// the file's own newest commit, or an unrelated GitHub-only refresh on one machine could make
    /// its own (unchanged) name outrank a different name set more recently on another machine
    /// (independent pre-PR review, cycle 1, adversarial lens, medium).
    /// </summary>
    [Fact]
    public async Task A_github_only_rewrite_does_not_make_an_unrelated_display_name_look_newer()
    {
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);
        await WriteNodeFileAsync(ownerRepo, owner, owner);
        await RewriteNodeFileWithDisplayNameAsync(
            ownerRepo, owner, "Ada", new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        GeneratedIdentity nodeB = GenerateIdentity();
        string nodeBRepo = _repo.CloneNode(hub);
        await WriteNodeFileAsync(nodeBRepo, nodeB, nodeB, owner.Fingerprint, displayName: "Bob");
        await VouchAsync(ownerRepo, owner.Fingerprint, nodeB, owner);

        // The owner's own daemon later refreshes only its GitHub declaration — a real commit to the
        // identical node.yaml, well after node B declared "Bob" — while "Ada" itself never changes.
        await RewriteNodeFileWithDeclarationAsync(
            ownerRepo, owner, new DeclaredGitHubAccount(42, "octocat"),
            new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), displayName: "Ada");

        TrustChain chain = await _chainReader.ComputeAsync(_repo.CloneNode(hub), CancellationToken.None);

        chain.DisplayNameOf(owner.Fingerprint).Value.Should().Be("Bob");
    }

    [Fact]
    public async Task A_second_clone_reads_the_declaration_another_clone_wrote_after_it_fetches()
    {
        string hub = _repo.CreateHub();
        (string cloneA, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);
        await WriteNodeFileAsync(cloneA, owner, owner);
        string cloneB = _repo.CloneNode(hub);
        (await _chainReader.ComputeAsync(cloneB, CancellationToken.None)).NodeDeclarations.Should().BeEmpty();

        // Clone A declares its account and pushes; clone B has not seen it yet.
        await WriteNodeFileAsync(cloneA, owner, owner, github: new DeclaredGitHubAccount(42, "octocat"));

        TrustChain chain = await _chainReader.ComputeAsync(cloneB, CancellationToken.None);

        chain.DeclaredAccountsOf(owner.Fingerprint).Should().Equal(new DeclaredGitHubAccount(42, "octocat"));
    }

    [Fact]
    public async Task A_revoked_root_node_is_dropped_from_its_own_fleet()
    {
        // independent pre-PR review, cycle 1, conformance lens, medium: the root's own node has no
        // owners/<root>/nodes/<id>.yaml vouch entry for h9k node revoke to remove, so without this
        // fix a revoked root node stayed in FleetNodeIds() forever even though h9k node revoke
        // reports it removed.
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);
        await WriteNodeFileAsync(ownerRepo, owner, owner);

        string readerBeforeRevoke = _repo.CloneNode(hub);
        (await _chainReader.ComputeAsync(readerBeforeRevoke, CancellationToken.None))
            .OwnerChains[owner.Fingerprint].FleetNodeIds().Should().Contain(owner.NodeId, "the root's own node resolves before any revocation");

        await RevokeAsync(ownerRepo, owner.Fingerprint, owner.NodeId, owner);

        string readerAfterRevoke = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerAfterRevoke, CancellationToken.None);

        chain.OwnerChains[owner.Fingerprint].RootNodeId.Should().Be(
            owner.NodeId.ToString(), "the ledger still names it — only FleetNodeIds excludes a revoked entry");
        chain.OwnerChains[owner.Fingerprint].FleetNodeIds().Should().NotContain(
            owner.NodeId, "h9k node revoke against the root's own node must actually drop it from the fleet");
    }

    [Fact]
    public async Task A_node_that_retired_its_own_self_created_root_no_longer_attaches_to_it()
    {
        // independent pre-PR review, cycle 1, adversarial lens, medium: a node that once
        // self-created a root (no --owner) and later re-ran h9k project join --owner <real> against
        // its real owner keeps the same key — so it still fingerprints back to the root it
        // originally established — while RetireSelfRootEverywhereAsync retires that self-created
        // root by adding a retired.yaml beside root.yaml, never by removing root.yaml itself, so the
        // retired root still self-certifies. The node's own node.yaml is rewritten by that rerun to
        // carry the real owner's fingerprint as owner_fingerprint. Without checking that field, this
        // node kept re-attaching as the retired root's own RootNodeId forever.
        string hub = _repo.CreateHub();
        (string retiredRepo, GeneratedIdentity retiredRoot) = await EstablishGenesisRootAsync(hub);
        await WriteNodeFileAsync(retiredRepo, retiredRoot, retiredRoot);

        (string realRepo, GeneratedIdentity realOwner) = await EstablishGenesisRootAsync(hub);

        // The node re-runs h9k project join --owner <realOwner>: same key, same node id, but its own
        // node.yaml now claims the real owner instead of itself.
        await WriteNodeFileAsync(retiredRepo, retiredRoot, retiredRoot, ownerFingerprint: realOwner.Fingerprint);

        string readerRepo = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

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
        string hub = _repo.CreateHub();
        (string nodeARepo, GeneratedIdentity nodeA) = await EstablishGenesisRootAsync(hub);

        // A stranger establishes their own, internally self-consistent root and node file — never
        // added to this project's members ref by anyone.
        GeneratedIdentity stranger = GenerateIdentity();
        string strangerRepo = _repo.CloneNode(hub);
        await WriteRootFileAsync(strangerRepo, stranger);
        await WriteNodeFileAsync(strangerRepo, stranger, stranger);

        string readerRepo = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.IsAllowedSigner(stranger.Fingerprint).Should().BeFalse("the stranger was never made a project member");
        chain.Members.Should().NotContain(member => member.RootFingerprint == stranger.Fingerprint);
        chain.IsAllowedSigner(nodeA.Fingerprint).Should().BeTrue("the genesis root is unaffected by the stranger's own unrelated ref");
    }

    [Fact]
    public async Task A_member_role_node_cannot_write_a_membership()
    {
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);

        // A second root, added by the genesis owner as a plain member (role: member) — simulating
        // what T2's invite sweep will eventually do; this task writes it directly to set up the
        // scenario.
        GeneratedIdentity member = GenerateIdentity();
        await WriteRootFileAsync(ownerRepo, member);
        await WriteMemberFileAsync(ownerRepo, member.Fingerprint, "member", owner);

        // The member-role root now tries to vouch a third root into project membership, signed
        // with its own key rather than the owner's.
        GeneratedIdentity outsider = GenerateIdentity();
        await WriteMemberFileAsync(ownerRepo, outsider.Fingerprint, "owner", member);

        string readerRepo = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.RoleOf(member.Fingerprint).Should().Be(MembershipRole.Member);
        chain.Members.Should().NotContain(
            m => m.RootFingerprint == outsider.Fingerprint, "a member-role root cannot write a membership");
    }

    [Fact]
    public async Task Revocation_then_re_vouch_restores()
    {
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);
        GeneratedIdentity node = GenerateIdentity();

        string nodeRepo = _repo.CloneNode(hub);
        await WriteNodeFileAsync(nodeRepo, node, node);
        await VouchAsync(ownerRepo, owner.Fingerprint, node, owner);

        string readerRepo1 = _repo.CloneNode(hub);
        (await _chainReader.ComputeAsync(readerRepo1, CancellationToken.None))
            .IsAllowedSigner(node.Fingerprint).Should().BeTrue("just vouched");

        await RevokeAsync(ownerRepo, owner.Fingerprint, node.NodeId, owner);

        string readerRepo2 = _repo.CloneNode(hub);
        (await _chainReader.ComputeAsync(readerRepo2, CancellationToken.None))
            .IsAllowedSigner(node.Fingerprint).Should().BeFalse("revoked, and revocation is later in ref order than the vouch");

        await VouchAsync(ownerRepo, owner.Fingerprint, node, owner);

        string readerRepo3 = _repo.CloneNode(hub);
        (await _chainReader.ComputeAsync(readerRepo3, CancellationToken.None))
            .IsAllowedSigner(node.Fingerprint).Should().BeTrue("a surviving node undoes a bad revocation by vouching again");
    }

    [Fact]
    public async Task A_forged_committer_email_cannot_smuggle_an_unauthorized_key_into_self_certification()
    {
        // The exact injection the independent pre-PR review reproduced (cycle 1, conformance and
        // adversarial lenses, both high) against the pre-fix IsSignedByAsync, which wrote the
        // commit's own (attacker-controlled) committer email as the allowed-signers principal: a
        // committer email crafted as "x <attacker key>" smuggles the attacker's own key into the
        // allowed-signers line's key-type/key-data fields, pushing the real candidate key (root's
        // own, here) into a trailing, ignored comment — so git verify-commit ends up verifying the
        // signature against the attacker's key it was actually signed with, not the root's, and the
        // check wrongly reports the commit as self-certified. A fixed allowed-signers principal
        // (GitLedgerChainReader.AllowedSignersPrincipal) closes this: the commit was never actually
        // signed by root's own key, so self-certification must fail.
        string hub = _repo.CreateHub();
        GeneratedIdentity root = GenerateIdentity();
        GeneratedIdentity attacker = GenerateIdentity();
        string repo = _repo.CloneNode(hub);

        string refName = $"refs/hall9k/ledger/owners/{root.Fingerprint}";
        string path = $"owners/{root.Fingerprint}/root.yaml";
        string content = BuildYaml(("public_key", root.PublicKeyLine), ("created_at", Now()));
        LedgerCommitter forgedCommitter = new("Attacker", $"x {attacker.PublicKeyLine}");

        LedgerFile current = await _ledger.ReadAsync(repo, refName, path, CancellationToken.None);
        LedgerWriteOutcome outcome = await _ledger.WriteAsync(
            new LedgerWriteRequest(
                repo, refName, path, content, current.BlobId, "forged root", forgedCommitter,
                new LedgerSigningKey(attacker.PrivateKeyPath)),
            CancellationToken.None);
        outcome.Verdict.Should().Be(LedgerWriteVerdict.Written, "the write itself is unauthenticated at A1's own layer");

        string readerRepo = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.OwnerChains.Should().NotContainKey(
            root.Fingerprint, "the commit was actually signed by the attacker's key, never root's own");
        chain.IsAllowedSigner(root.Fingerprint).Should().BeFalse();
    }

    [Fact]
    public async Task A_roots_own_file_overwritten_with_a_mismatched_key_is_named_as_an_unverified_write()
    {
        // independent pre-PR review, cycle 1, conformance lens, medium: a self-certification
        // failure used to drop the whole chain silently, with no UnverifiedLedgerWrite naming the
        // offending commit — this proves the fix names it instead.
        string hub = _repo.CreateHub();
        GeneratedIdentity root = GenerateIdentity();
        GeneratedIdentity attacker = GenerateIdentity();
        string repo = _repo.CloneNode(hub);
        await WriteRootFileAsync(repo, root);

        // A second commit, on the same ref, replaces root.yaml's own declared key with the
        // attacker's — self-certification must now fail (the currently-served key no longer
        // fingerprints back to root's own namespace).
        string refName = $"refs/hall9k/ledger/owners/{root.Fingerprint}";
        string path = $"owners/{root.Fingerprint}/root.yaml";
        string overwriteContent = BuildYaml(("public_key", attacker.PublicKeyLine), ("created_at", Now()));
        await WriteAsync(repo, refName, path, overwriteContent, attacker);

        string readerRepo = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.OwnerChains.Should().NotContainKey(root.Fingerprint, "the currently-served content no longer self-certifies");
        chain.UnverifiedWrites.Should().Contain(
            write => write.Kind == "root" && write.RootFingerprint == root.Fingerprint,
            "the offending commit is named rather than the chain silently vanishing with no diagnostic");
    }

    [Fact]
    public async Task A_forged_vouch_signed_by_a_stranger_is_named_as_an_unverified_write()
    {
        // independent pre-PR review, cycle 3, conformance and adversarial lenses, both high:
        // ComputeOwnerChainAsync built this exact diagnostic but returned an empty list instead of
        // it on its success path, so a forged vouch was refused correctly but never named anywhere
        // a caller — h9k status, h9k project members — could see it.
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);
        GeneratedIdentity attacker = GenerateIdentity();

        // The attacker pushes a node file into the owner's own namespace, signed with its own key
        // — never the owner's, never any node the owner has enrolled.
        await VouchAsync(ownerRepo, owner.Fingerprint, attacker, attacker);

        string readerRepo = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.OwnerChains[owner.Fingerprint].Nodes.Should().NotContain(
            node => node.NodeId == attacker.NodeId.ToString(), "the vouch was never signed by the root or any enrolled node");
        chain.UnverifiedWrites.Should().Contain(
            write => write.Kind == "vouch" && write.Identifier == attacker.NodeId.ToString() && write.RootFingerprint == owner.Fingerprint,
            "the forged vouch is named rather than silently discarded");
    }

    [Fact]
    public async Task A_members_write_signed_by_a_vouched_node_is_refused_regardless_of_its_own_enrollment_state()
    {
        // idea 6be68ee2, trust-ledger finding 2: a members-ref write now requires the owner's own
        // live root key, never merely a currently enrolled node — replacing the old rule (T1, ruled
        // by the window 2026-09-13) under which a vouched node's own write rode the owner chain's
        // live state and was only voided once that node was later revoked. Under the new rule the
        // laptop's write is refused from the very first read, whether the laptop is enrolled,
        // revoked, or re-vouched afterward — none of that ever mattered to authorization again.
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);

        GeneratedIdentity laptop = GenerateIdentity();
        string laptopRepo = _repo.CloneNode(hub);
        await WriteNodeFileAsync(laptopRepo, laptop, laptop);
        await VouchAsync(ownerRepo, owner.Fingerprint, laptop, owner);

        GeneratedIdentity bob = GenerateIdentity();
        await WriteMemberFileAsync(ownerRepo, bob.Fingerprint, "member", laptop);

        string readerWhileEnrolled = _repo.CloneNode(hub);
        TrustChain whileEnrolled = await _chainReader.ComputeAsync(readerWhileEnrolled, CancellationToken.None);
        whileEnrolled.RoleOf(bob.Fingerprint).Should().BeNull(
            "the laptop is fully enrolled but never held the owner's own root key, so its members write "
            + "is never authorized");
        whileEnrolled.UnverifiedWrites.Should().Contain(
            write => write.Kind == "membership" && write.Identifier == bob.Fingerprint,
            "the refused write is named rather than silently dropped");

        // Revoking and re-vouching the laptop changes nothing: its members write was never
        // authorized to begin with, so there is nothing for either to void or restore.
        await RevokeAsync(ownerRepo, owner.Fingerprint, laptop.NodeId, owner);
        await VouchAsync(ownerRepo, owner.Fingerprint, laptop, owner);

        string readerAfterChurn = _repo.CloneNode(hub);
        (await _chainReader.ComputeAsync(readerAfterChurn, CancellationToken.None))
            .RoleOf(bob.Fingerprint).Should().BeNull("the laptop's write was never authorized, so re-enrolling it changes nothing");
    }

    [Fact]
    public async Task A_refused_membership_write_is_dropped_once_a_later_root_signed_commit_rewrites_the_identical_path()
    {
        // idea 6be68ee2 criterion 4 (independent pre-PR review, cycle 1, conformance lens, medium):
        // a refused write must not stand forever in h9k status once a later, authorized commit
        // rewrites the exact same path — the shape h9k project member reaffirm exists to produce.
        // Without this, PersistUnverifiedWritesAsync only resolves a standing record once the chain
        // stops naming it at all, never once a fresher write to the identical path supersedes it.
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);

        GeneratedIdentity laptop = GenerateIdentity();
        await WriteNodeFileAsync(ownerRepo, laptop, laptop);
        await VouchAsync(ownerRepo, owner.Fingerprint, laptop, owner);

        GeneratedIdentity bob = GenerateIdentity();
        await WriteMemberFileAsync(ownerRepo, bob.Fingerprint, "member", laptop);

        string readerWhileRefused = _repo.CloneNode(hub);
        (await _chainReader.ComputeAsync(readerWhileRefused, CancellationToken.None)).UnverifiedWrites.Should().Contain(
            write => write.Kind == "membership" && write.Identifier == bob.Fingerprint,
            "the laptop's own write is refused and named before any root-signed rewrite lands");

        // Root reaffirms the identical path with a fresh, content-changing, root-signed commit —
        // the shape `h9k project member reaffirm` produces.
        await WriteMemberFileAsync(ownerRepo, bob.Fingerprint, "member", owner);

        string readerAfterReaffirm = _repo.CloneNode(hub);
        TrustChain afterReaffirm = await _chainReader.ComputeAsync(readerAfterReaffirm, CancellationToken.None);

        afterReaffirm.RoleOf(bob.Fingerprint).Should().Be(MembershipRole.Member, "the root-signed rewrite is now what the chain trusts");
        afterReaffirm.UnverifiedWrites.Should().NotContain(
            write => write.Kind == "membership" && write.Identifier == bob.Fingerprint,
            "the laptop's earlier refusal is superseded by the later root-signed rewrite of the identical path, not left standing forever");
    }

    [Fact]
    public async Task A_revocation_a_members_write_and_a_role_change_are_authorized_only_by_a_live_root_key()
    {
        // idea 6be68ee2, trust-ledger finding 2: the core rule this task adds. A node revocation
        // (owners/<root>/revoked/<node>.yaml), a members-ref write (an add), and a role change (an
        // existing member's own role field rewritten) are each authorized only by one of the
        // owner's own live root keys — never merely a node vouched into that owner's own fleet, so a
        // compromised fleet node can never revoke its peers or rewrite membership. Peer vouching
        // itself is unaffected: a plain enrolled node still vouches another node exactly as before.
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);

        GeneratedIdentity laptop = GenerateIdentity();
        string laptopRepo = _repo.CloneNode(hub);
        await WriteNodeFileAsync(laptopRepo, laptop, laptop);
        await VouchAsync(ownerRepo, owner.Fingerprint, laptop, owner);

        GeneratedIdentity target = GenerateIdentity();
        string targetRepo = _repo.CloneNode(hub);
        await WriteNodeFileAsync(targetRepo, target, target);
        await VouchAsync(ownerRepo, owner.Fingerprint, target, owner);

        GeneratedIdentity bob = GenerateIdentity();
        await WriteMemberFileAsync(ownerRepo, bob.Fingerprint, "member", owner);

        // Peer vouching keeps working: the laptop, a plain enrolled node, can still vouch a peer.
        GeneratedIdentity peer = GenerateIdentity();
        string peerRepo = _repo.CloneNode(hub);
        await WriteNodeFileAsync(peerRepo, peer, peer);
        await VouchAsync(ownerRepo, owner.Fingerprint, peer, laptop);

        // The laptop — merely enrolled, never a root key — signs a revocation, a members write
        // (add), and a role change.
        await RevokeAsync(ownerRepo, owner.Fingerprint, target.NodeId, laptop);
        GeneratedIdentity outsider = GenerateIdentity();
        await WriteMemberFileAsync(ownerRepo, outsider.Fingerprint, "member", laptop);
        await WriteMemberFileAsync(ownerRepo, bob.Fingerprint, "owner", laptop);

        string readerAfterVouchedAttempts = _repo.CloneNode(hub);
        TrustChain afterVouchedAttempts = await _chainReader.ComputeAsync(readerAfterVouchedAttempts, CancellationToken.None);

        afterVouchedAttempts.IsAllowedSigner(target.Fingerprint).Should().BeTrue(
            "the laptop's own revocation is not signed by a live root key and is ignored");
        afterVouchedAttempts.Members.Should().NotContain(
            m => m.RootFingerprint == outsider.Fingerprint, "the laptop's own members write is not signed by a live root key and is ignored");
        afterVouchedAttempts.RoleOf(bob.Fingerprint).Should().Be(
            MembershipRole.Member, "the laptop's own role change is not signed by a live root key and is ignored");
        afterVouchedAttempts.IsAllowedSigner(peer.Fingerprint).Should().BeTrue(
            "peer vouching by a plain enrolled node keeps working — only revocation and membership writes narrowed");

        // The root itself signs the identical three writes — every one applies.
        await RevokeAsync(ownerRepo, owner.Fingerprint, target.NodeId, owner);
        await WriteMemberFileAsync(ownerRepo, outsider.Fingerprint, "member", owner);
        await WriteMemberFileAsync(ownerRepo, bob.Fingerprint, "owner", owner);

        string readerAfterRootSigned = _repo.CloneNode(hub);
        TrustChain afterRootSigned = await _chainReader.ComputeAsync(readerAfterRootSigned, CancellationToken.None);

        afterRootSigned.IsAllowedSigner(target.Fingerprint).Should().BeFalse("the root's own revocation is applied");
        afterRootSigned.RoleOf(outsider.Fingerprint).Should().Be(MembershipRole.Member, "the root's own members write is applied");
        afterRootSigned.RoleOf(bob.Fingerprint).Should().Be(MembershipRole.Owner, "the root's own role change is applied");
    }

    [Fact]
    public async Task A_backdated_committer_date_cannot_authorize_a_vouched_nodes_membership_write()
    {
        // The adversarial lens's own injection (cycle 1, both high) against the pre-fix
        // owner.AsOf(commitTime): a node crafts a members-ref commit and backdates
        // GIT_COMMITTER_DATE, hoping to be judged against the chain as it stood at that claimed
        // date rather than as it stands now. The fix (ruled by the window, 2026-09-13) removed
        // committer-date pinning from the authorization rule entirely, so a members-ref write is
        // judged solely against the owner chain's own live state — a claimed date, true or forged,
        // is never consulted at all. Under idea 6be68ee2's own narrower rule this write was never
        // going to be authorized regardless of timing (the laptop never holds a live root key,
        // revoked or not), so this test backdates to a moment BEFORE the laptop was even vouched —
        // the one claim that could still matter if committer dates were ever consulted again — and
        // proves it still buys nothing.
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);

        GeneratedIdentity laptop = GenerateIdentity();
        string laptopRepo = _repo.CloneNode(hub);
        await WriteNodeFileAsync(laptopRepo, laptop, laptop);
        DateTimeOffset beforeVouch = DateTimeOffset.UtcNow.AddMinutes(-30);
        await VouchAsync(ownerRepo, owner.Fingerprint, laptop, owner);

        string membersRefName = "refs/hall9k/ledger/members";
        await RunGitCaptureAsync(ownerRepo, ["fetch", "origin", $"+{membersRefName}:{membersRefName}"]);
        string membersTip = await RunGitCaptureAsync(ownerRepo, ["rev-parse", "--verify", membersRefName]);

        // The laptop, fully enrolled but never a root key, crafts a members-ref commit,
        // self-claiming owner role, and backdates it to before it was even vouched.
        GeneratedIdentity attacker = GenerateIdentity();
        string attackerPath = $"members/{attacker.Fingerprint}.yaml";
        string attackerContent = BuildYaml(("root_fingerprint", attacker.Fingerprint), ("role", "owner"), ("issued_at", Now()));
        string maliciousTree = await BuildTreeWithFileAsync(ownerRepo, membersTip, attackerPath, attackerContent);
        string maliciousCommit = await CommitTreeAsync(
            ownerRepo, maliciousTree, [membersTip], laptop, "self-promote via backdated commit", committerDate: beforeVouch);
        await RunGitCaptureAsync(ownerRepo, ["push", "origin", $"{maliciousCommit}:{membersRefName}"]);

        string readerRepo = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.RoleOf(attacker.Fingerprint).Should().BeNull(
            "the laptop never holds a live root key, so its own write can never be authorized "
            + "regardless of the committer date it claims");
        chain.UnverifiedWrites.Should().Contain(write => write.Identifier == attacker.Fingerprint);
    }

    [Fact]
    public async Task A_vouch_introduced_purely_via_a_merge_commits_second_parent_is_still_applied()
    {
        // The exact gap the independent pre-PR review reproduced live against the pre-fix
        // ChangedPathsAsync (cycle 2, conformance lens, medium): `git diff-tree` with no
        // `-m`/`-c`/`--cc`/`--diff-merges` flag names no paths at all for a merge commit, so once
        // CommitsOldestFirstAsync started replaying `--topo-order --first-parent` (this same task's
        // own cycle-1 fix), a merge commit whose first parent is the ref's own prior tip legitimately
        // entered the walk — but every path it introduced purely via its second parent vanished from
        // ComputeOwnerChainAsync's view: neither applied to the chain nor recorded as unverified.
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);
        GeneratedIdentity sideNode = GenerateIdentity();

        string refName = $"refs/hall9k/ledger/owners/{owner.Fingerprint}";
        string tip = await RunGitCaptureAsync(ownerRepo, ["rev-parse", "--verify", refName]);

        string vouchPath = $"owners/{owner.Fingerprint}/nodes/{sideNode.NodeId}.yaml";
        string vouchContent = BuildYaml(
            ("node_id", sideNode.NodeId.ToString()), ("public_key", sideNode.PublicKeyLine), ("issued_at", Now()));

        // A side commit, never itself the ref's own tip, introduces the vouch — signed by the owner,
        // exactly as VouchAsync would sign it.
        string sideTree = await BuildTreeWithFileAsync(ownerRepo, tip, vouchPath, vouchContent);
        string sideCommit = await CommitTreeAsync(ownerRepo, sideTree, [tip], owner, "side vouch");

        // Merged straight onto the ref's own current tip: the merge's first parent is the ref's own
        // prior mainline tip, so it legitimately enters CommitsOldestFirstAsync's
        // --topo-order --first-parent replay — but the vouch itself was introduced purely via the
        // second parent.
        string mergeCommit = await CommitTreeAsync(ownerRepo, sideTree, [tip, sideCommit], owner, "merge side vouch");
        await RunGitCaptureAsync(ownerRepo, ["push", "origin", $"{mergeCommit}:{refName}"]);

        string readerRepo = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.IsAllowedSigner(sideNode.Fingerprint).Should().BeTrue(
            "the vouch was legitimately signed by the enrolled root, even though it entered the ref "
            + "purely via a merge commit's second parent");
        chain.OwnerChains[owner.Fingerprint].Nodes.Should().Contain(node => node.NodeId == sideNode.NodeId.ToString());
    }

    [Fact]
    public async Task Genesis_picks_the_first_file_by_ref_order()
    {
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);

        // A second, wholly unrelated root self-claims owner AFTER genesis is already spent — this
        // is "a later self-claimed owner without a vouch", ignored per idea 202383dc's own model.
        GeneratedIdentity laterSelfClaim = GenerateIdentity();
        string laterRepo = _repo.CloneNode(hub);
        await WriteRootFileAsync(laterRepo, laterSelfClaim);
        await WriteMemberFileAsync(laterRepo, laterSelfClaim.Fingerprint, "owner", laterSelfClaim);

        string readerRepo = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.Members.Should().ContainSingle(m => m.RootFingerprint == owner.Fingerprint && m.Role == MembershipRole.Owner);
        chain.Members.Should().NotContain(m => m.RootFingerprint == laterSelfClaim.Fingerprint);
    }

    [Fact]
    public async Task Genesis_exposes_the_project_key_recorded_in_its_own_commit()
    {
        string hub = _repo.CreateHub();
        const string projectKey = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub, projectKey);

        string readerRepo = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.ProjectKey.Should().Be(projectKey);
        // The two are deliberately unrelated (Brian's ruling 2026-09-17): GenesisRootFingerprint
        // names the owner who wrote genesis, never the project itself.
        chain.GenesisRootFingerprint.Should().Be(owner.Fingerprint);
        chain.ProjectKey.Should().NotBe(chain.GenesisRootFingerprint);
    }

    [Fact]
    public async Task A_ledger_with_no_project_key_yet_exposes_a_null_one()
    {
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);

        string readerRepo = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.GenesisRootFingerprint.Should().Be(owner.Fingerprint);
        chain.ProjectKey.Should().BeNull("this genesis predates the project key and h9k project assign-key never ran");
    }

    [Fact]
    public async Task A_later_authorized_rewrite_of_the_genesis_file_backfills_the_project_key()
    {
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);

        string readerRepoBeforeBackfill = _repo.CloneNode(hub);
        TrustChain beforeBackfill = await _chainReader.ComputeAsync(readerRepoBeforeBackfill, CancellationToken.None);
        beforeBackfill.ProjectKey.Should().BeNull();

        // h9k project assign-key's own write: the genesis owner rewrites its own already-existing
        // members/<fingerprint>.yaml, signed, adding the key rather than replacing the file.
        const string backfilledKey = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
        await WriteMemberFileAsync(ownerRepo, owner.Fingerprint, "owner", owner, backfilledKey);

        string readerRepoAfterBackfill = _repo.CloneNode(hub);
        TrustChain afterBackfill = await _chainReader.ComputeAsync(readerRepoAfterBackfill, CancellationToken.None);
        afterBackfill.ProjectKey.Should().Be(backfilledKey);
        afterBackfill.GenesisRootFingerprint.Should().Be(owner.Fingerprint, "genesis identity itself never moves");
    }

    [Fact]
    public async Task An_unauthorized_rewrite_of_the_genesis_file_by_a_stranger_cannot_set_the_project_key()
    {
        // independent pre-PR review, cycle 1, conformance and adversarial lenses, both high: a
        // blind tip read of members/<genesis-fingerprint>.yaml would have let anyone who can push
        // refs/hall9k/ledger/members set this project's own key, since that read trusts the ref's
        // current content unconditionally. The fix tracks projectKey live during the authorized
        // replay instead (ComputeMembersAsync's own doc) — this proves a rewrite signed by neither
        // the genesis root's own key nor any node it has enrolled is refused the identical way any
        // other unauthorized membership write already is, and never reaches the project key at all.
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);
        GeneratedIdentity attacker = GenerateIdentity();

        // A stranger — never vouched into owner's chain, never a member of any role — pushes a
        // second commit onto the genesis fingerprint's own file, adding a project_key, signed with
        // its own key rather than owner's or any node owner has enrolled.
        const string forgedKey = "01ARZ3NDEKTSV4RRFFQ69G5FZZ";
        await WriteMemberFileAsync(ownerRepo, owner.Fingerprint, "owner", attacker, forgedKey);

        string readerRepo = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.ProjectKey.Should().BeNull("the rewrite was never signed by the genesis root's own chain, so it is refused");
        chain.UnverifiedWrites.Should().Contain(
            write => write.Kind == "membership" && write.RootFingerprint == owner.Fingerprint,
            "the forged rewrite is named rather than silently discarded");
    }

    // ---- carried-record verification (task f53fecfd) --------------------------------------

    [Fact]
    public async Task A_valid_carried_bundle_establishes_the_root_and_enrols_the_carrying_node()
    {
        (string sourceRepo, GeneratedIdentity root, GeneratedIdentity carrier,
            var rootSigned, var vouchSigned) = await EstablishSourceVouchAsync();

        string targetHub = _repo.CreateHub();
        string targetRepo = _repo.CloneNode(targetHub);
        await WriteNodeFileAsync(targetRepo, carrier, carrier, root.Fingerprint);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/root.yaml", rootSigned.Content, carrier);
        string carried = BuildCarriedRecordYaml(
            carrier.NodeId, carrier.PublicKeyLine, Guid.NewGuid(), sourceRepo,
            rootSigned.Content, rootSigned.Sha, rootSigned.RawBytes, vouchSigned.Content, vouchSigned.Sha, vouchSigned.RawBytes);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/carried/{carrier.NodeId}.yaml", carried, carrier);

        string readerRepo = _repo.CloneNode(targetHub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.OwnerChains.Should().ContainKey(root.Fingerprint, "the carried bundle establishes the root even though nobody here holds its own private key");
        chain.OwnerChains[root.Fingerprint].Nodes.Should().Contain(node => node.NodeId == carrier.NodeId.ToString() && node.Fingerprint == carrier.Fingerprint);
        chain.IsEnrolledInOwner(carrier.Fingerprint, root.Fingerprint).Should().BeTrue();
        chain.UnverifiedWrites.Should().BeEmpty("a bundle that verifies on every check is never recorded as unverifiable");
    }

    [Fact]
    public async Task A_carried_bundle_still_verifies_after_the_carrying_nodes_own_key_later_rotates()
    {
        (string sourceRepo, GeneratedIdentity root, GeneratedIdentity carrier,
            var rootSigned, var vouchSigned) = await EstablishSourceVouchAsync();

        string targetHub = _repo.CreateHub();
        string targetRepo = _repo.CloneNode(targetHub);
        // The original carry: node.yaml self-announces under the carried key, and the root and
        // carried bundle land signed by that same key — an ordinary, successful carry.
        await WriteNodeFileAsync(targetRepo, carrier, carrier, root.Fingerprint);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/root.yaml", rootSigned.Content, carrier);
        string carried = BuildCarriedRecordYaml(
            carrier.NodeId, carrier.PublicKeyLine, Guid.NewGuid(), sourceRepo,
            rootSigned.Content, rootSigned.Sha, rootSigned.RawBytes, vouchSigned.Content, vouchSigned.Sha, vouchSigned.RawBytes);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/carried/{carrier.NodeId}.yaml", carried, carrier);

        // Later, unrelated to the carry itself: this exact node loses its private key (a reinstall,
        // a move to a new disk), NodeKeyStore mints a fresh one, and the next h9k project join
        // rewrites node.yaml at the identical node id, self-signed by the new key — no relation at
        // all to the key the root actually vouched.
        GeneratedIdentity rotated = GenerateIdentity();
        GeneratedIdentity rotatedAtSameNode = new(rotated.PrivateKeyPath, rotated.PublicKeyLine, rotated.Fingerprint, carrier.NodeId);
        await WriteNodeFileAsync(targetRepo, rotatedAtSameNode, rotatedAtSameNode, root.Fingerprint);

        string readerRepo = _repo.CloneNode(targetHub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.OwnerChains.Should().ContainKey(root.Fingerprint,
            "check 4 accepts any commit in node.yaml's own history that self-signs the carried key, not only the current tip");
        chain.IsEnrolledInOwner(carrier.Fingerprint, root.Fingerprint).Should().BeTrue();
        chain.UnverifiedWrites.Should().BeEmpty("a later, unrelated key rotation on the same node must never retroactively cancel a carry that was genuine when it happened");
    }

    [Fact]
    public async Task A_carried_bundle_whose_embedded_root_yaml_does_not_self_certify_is_rejected_and_recorded()
    {
        (string sourceRepo, GeneratedIdentity root, GeneratedIdentity carrier,
            var rootSigned, var vouchSigned) = await EstablishSourceVouchAsync();

        // Check 1's own failure: the embedded root.yaml declares a key that does not fingerprint
        // back to `root` at all — a different identity's own public key, syntactically well-formed.
        GeneratedIdentity impostor = GenerateIdentity();
        string tamperedRootYaml = BuildYaml(("public_key", impostor.PublicKeyLine), ("created_at", Now()));

        string targetHub = _repo.CreateHub();
        string targetRepo = _repo.CloneNode(targetHub);
        await WriteNodeFileAsync(targetRepo, carrier, carrier, root.Fingerprint);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/root.yaml", rootSigned.Content, carrier);
        string carried = BuildCarriedRecordYaml(
            carrier.NodeId, carrier.PublicKeyLine, Guid.NewGuid(), sourceRepo,
            tamperedRootYaml, rootSigned.Sha, rootSigned.RawBytes, vouchSigned.Content, vouchSigned.Sha, vouchSigned.RawBytes);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/carried/{carrier.NodeId}.yaml", carried, carrier);

        string readerRepo = _repo.CloneNode(targetHub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.OwnerChains.Should().NotContainKey(root.Fingerprint, "root.yaml's own commit is not signed by the root, and the only bundle present fails to self-certify");
        chain.UnverifiedWrites.Should().Contain(write => write.Kind == "carried" && write.Identifier == carrier.NodeId.ToString());
    }

    [Fact]
    public async Task A_carried_bundle_whose_embedded_root_commit_is_not_signed_by_the_root_is_rejected_and_recorded()
    {
        (string sourceRepo, GeneratedIdentity root, GeneratedIdentity carrier,
            var rootSigned, var vouchSigned) = await EstablishSourceVouchAsync();

        // Check 2's own failure: the embedded root commit's own raw bytes are genuine (a real, validly
        // signed commit) but signed by someone other than the root — the vouch commit's own bytes,
        // which is signed by `root` too, would actually pass check 2 by coincidence, so this uses a
        // commit from a THIRD identity that never touched this root's own chain at all.
        GeneratedIdentity stranger = GenerateIdentity();
        string strangerRepo = _repo.CloneNode(_repo.CreateHub());
        await WriteRootFileAsync(strangerRepo, stranger);
        var strangerRootSigned = await ReadSignedCommitAsync(
            strangerRepo, $"refs/hall9k/ledger/owners/{stranger.Fingerprint}", $"owners/{stranger.Fingerprint}/root.yaml");

        string targetHub = _repo.CreateHub();
        string targetRepo = _repo.CloneNode(targetHub);
        await WriteNodeFileAsync(targetRepo, carrier, carrier, root.Fingerprint);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/root.yaml", rootSigned.Content, carrier);
        string carried = BuildCarriedRecordYaml(
            carrier.NodeId, carrier.PublicKeyLine, Guid.NewGuid(), sourceRepo,
            rootSigned.Content, strangerRootSigned.Sha, strangerRootSigned.RawBytes, vouchSigned.Content, vouchSigned.Sha, vouchSigned.RawBytes);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/carried/{carrier.NodeId}.yaml", carried, carrier);

        string readerRepo = _repo.CloneNode(targetHub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.OwnerChains.Should().NotContainKey(root.Fingerprint);
        chain.UnverifiedWrites.Should().Contain(write => write.Kind == "carried" && write.Identifier == carrier.NodeId.ToString());
    }

    [Fact]
    public async Task A_carried_bundle_whose_embedded_vouch_commit_is_not_signed_by_the_root_is_rejected_and_recorded()
    {
        (string sourceRepo, GeneratedIdentity root, GeneratedIdentity carrier,
            var rootSigned, var vouchSigned) = await EstablishSourceVouchAsync();

        // Check 3's own failure: the embedded vouch commit is a real, validly signed commit, but
        // signed by the carrier itself rather than the root — self-vouching is never authoritative.
        var selfSignedVouch = await ReadSignedCommitAsync(
            sourceRepo, $"refs/hall9k/ledger/nodes/{carrier.NodeId}", $"nodes/{carrier.NodeId}/node.yaml");

        string targetHub = _repo.CreateHub();
        string targetRepo = _repo.CloneNode(targetHub);
        await WriteNodeFileAsync(targetRepo, carrier, carrier, root.Fingerprint);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/root.yaml", rootSigned.Content, carrier);
        string carried = BuildCarriedRecordYaml(
            carrier.NodeId, carrier.PublicKeyLine, Guid.NewGuid(), sourceRepo,
            rootSigned.Content, rootSigned.Sha, rootSigned.RawBytes, vouchSigned.Content, selfSignedVouch.Sha, selfSignedVouch.RawBytes);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/carried/{carrier.NodeId}.yaml", carried, carrier);

        string readerRepo = _repo.CloneNode(targetHub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.OwnerChains.Should().NotContainKey(root.Fingerprint);
        chain.UnverifiedWrites.Should().Contain(write => write.Kind == "carried" && write.Identifier == carrier.NodeId.ToString());
    }

    [Fact]
    public async Task A_bundle_presented_by_a_node_with_a_different_key_establishes_nothing()
    {
        // A bundle copied verbatim from a readable source ledger (every field genuinely valid, every
        // signature genuine), but presented on the target by a node that does NOT hold the carried
        // node's own private key: its own self-announced node.yaml on the target names a different
        // key entirely, so check 4 fails even though checks 1 through 3 all pass.
        (string sourceRepo, GeneratedIdentity root, GeneratedIdentity carrier,
            var rootSigned, var vouchSigned) = await EstablishSourceVouchAsync();
        GeneratedIdentity presenter = GenerateIdentity();

        string targetHub = _repo.CreateHub();
        string targetRepo = _repo.CloneNode(targetHub);
        // The presenter self-announces under its OWN key at the carried node's own id — it never
        // held `carrier`'s own private key, so it cannot produce a node.yaml declaring that key.
        await WriteNodeFileAsync(targetRepo, new GeneratedIdentity(presenter.PrivateKeyPath, presenter.PublicKeyLine, presenter.Fingerprint, carrier.NodeId), presenter, root.Fingerprint);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/root.yaml", rootSigned.Content, presenter);
        string carried = BuildCarriedRecordYaml(
            carrier.NodeId, carrier.PublicKeyLine, Guid.NewGuid(), sourceRepo,
            rootSigned.Content, rootSigned.Sha, rootSigned.RawBytes, vouchSigned.Content, vouchSigned.Sha, vouchSigned.RawBytes);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/carried/{carrier.NodeId}.yaml", carried, presenter);

        string readerRepo = _repo.CloneNode(targetHub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.OwnerChains.Should().NotContainKey(root.Fingerprint, "the presenter never held the carried node's own private key");
        chain.IsEnrolledInOwner(carrier.Fingerprint, root.Fingerprint).Should().BeFalse();
        chain.UnverifiedWrites.Should().Contain(write => write.Kind == "carried" && write.Identifier == carrier.NodeId.ToString());
    }

    [Fact]
    public async Task An_on_ledger_revocation_overrides_a_carried_vouch()
    {
        (string sourceRepo, GeneratedIdentity root, GeneratedIdentity carrier,
            var rootSigned, var vouchSigned) = await EstablishSourceVouchAsync();

        string targetHub = _repo.CreateHub();
        string targetRepo = _repo.CloneNode(targetHub);
        await WriteNodeFileAsync(targetRepo, carrier, carrier, root.Fingerprint);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/root.yaml", rootSigned.Content, carrier);
        string carried = BuildCarriedRecordYaml(
            carrier.NodeId, carrier.PublicKeyLine, Guid.NewGuid(), sourceRepo,
            rootSigned.Content, rootSigned.Sha, rootSigned.RawBytes, vouchSigned.Content, vouchSigned.Sha, vouchSigned.RawBytes);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/carried/{carrier.NodeId}.yaml", carried, carrier);

        // The ordinary revoke shape, signed by the root's own real key (idea 6be68ee2, trust-ledger
        // finding 2: a revocation now requires a live root key, never merely a node this chain
        // treats as enrolled). Nobody on this carried-only target ledger ever pushes anything else
        // signed by root, but this test still has access to root's own private key locally (it is
        // just a generated keypair), which is exactly what actually holding the root key means —
        // wherever that holder chooses to sign from.
        await RevokeAsync(targetRepo, root.Fingerprint, carrier.NodeId, root);

        string readerRepo = _repo.CloneNode(targetHub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.OwnerChains.Should().ContainKey(root.Fingerprint, "the root itself is still established by the carried bundle");
        chain.OwnerChains[root.Fingerprint].Nodes.Should().NotContain(node => node.NodeId == carrier.NodeId.ToString());
        chain.IsEnrolledInOwner(carrier.Fingerprint, root.Fingerprint).Should().BeFalse("the revocation lands after the carried vouch in ref order, so it wins");
    }

    [Fact]
    public async Task A_carried_nodes_own_self_signed_revocation_is_refused_since_it_never_holds_the_root_key()
    {
        // idea 6be68ee2, trust-ledger finding 2: even on a carried ledger, where nobody locally
        // holds the root's own private key at all, a revocation still requires a live root key —
        // the carried node's own established key, however legitimately enrolled, is not enough, so
        // a compromised carried node cannot revoke its own peers (or, exercised here, even itself)
        // by simply signing with the key its own carry already established. An accepted,
        // documented consequence of this task's own narrower rule: a purely carried fleet has no
        // local way to revoke a node until its real root, or a validated succession heir, reaches
        // this exact ledger directly.
        (string sourceRepo, GeneratedIdentity root, GeneratedIdentity carrier,
            var rootSigned, var vouchSigned) = await EstablishSourceVouchAsync();

        string targetHub = _repo.CreateHub();
        string targetRepo = _repo.CloneNode(targetHub);
        await WriteNodeFileAsync(targetRepo, carrier, carrier, root.Fingerprint);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/root.yaml", rootSigned.Content, carrier);
        string carried = BuildCarriedRecordYaml(
            carrier.NodeId, carrier.PublicKeyLine, Guid.NewGuid(), sourceRepo,
            rootSigned.Content, rootSigned.Sha, rootSigned.RawBytes, vouchSigned.Content, vouchSigned.Sha, vouchSigned.RawBytes);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/carried/{carrier.NodeId}.yaml", carried, carrier);

        await RevokeAsync(targetRepo, root.Fingerprint, carrier.NodeId, carrier);

        string readerRepo = _repo.CloneNode(targetHub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

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
        // The re-authorization gate above only ever kicks in once a node id's own carried path has
        // SUCCEEDED once — never merely been touched. A first, garbage commit at that exact path
        // (a stranger's own attempt, or this same node's own earlier malformed retry) must not
        // permanently lock the real, valid carry out for lack of anyone yet enrolled to
        // "re-authorize" it (independent review finding, self-review round two): nothing was ever
        // actually established, so there is nothing for a second attempt to need authorization from.
        (string sourceRepo, GeneratedIdentity root, GeneratedIdentity carrier,
            var rootSigned, var vouchSigned) = await EstablishSourceVouchAsync();

        string targetHub = _repo.CreateHub();
        string targetRepo = _repo.CloneNode(targetHub);
        await WriteNodeFileAsync(targetRepo, carrier, carrier, root.Fingerprint);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/root.yaml", rootSigned.Content, carrier);
        string carriedPath = $"owners/{root.Fingerprint}/carried/{carrier.NodeId}.yaml";

        // A garbage first commit at this exact node id's own carried path — missing every
        // required field, so VerifyCarriedRecordAsync fails it outright.
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", carriedPath, "node_id: \"garbage\"\n", carrier);

        // The genuine, fully valid bundle, a later commit at the identical path.
        string carried = BuildCarriedRecordYaml(
            carrier.NodeId, carrier.PublicKeyLine, Guid.NewGuid(), sourceRepo,
            rootSigned.Content, rootSigned.Sha, rootSigned.RawBytes, vouchSigned.Content, vouchSigned.Sha, vouchSigned.RawBytes);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", carriedPath, carried, carrier);

        string readerRepo = _repo.CloneNode(targetHub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.IsEnrolledInOwner(carrier.Fingerprint, root.Fingerprint).Should().BeTrue(
            "the valid second commit still self-authorizes on its own embedded evidence — nothing was ever actually established by the first, failed one");
    }

    [Fact]
    public async Task A_revoked_carried_node_cannot_resurrect_itself_by_re_pushing_its_own_carried_record()
    {
        // The revoked node still holds its own private key and its embedded evidence never
        // changes, so the only thing standing between it and simply reinstating itself is that a
        // SECOND touch of its own carried path needs authorization from the live chain, not merely
        // a bundle that verifies offline on its own terms again (independent pre-PR review,
        // adversarial lens, high).
        (string sourceRepo, GeneratedIdentity root, GeneratedIdentity carrier,
            var rootSigned, var vouchSigned) = await EstablishSourceVouchAsync();

        string targetHub = _repo.CreateHub();
        string targetRepo = _repo.CloneNode(targetHub);
        await WriteNodeFileAsync(targetRepo, carrier, carrier, root.Fingerprint);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/root.yaml", rootSigned.Content, carrier);
        string carried = BuildCarriedRecordYaml(
            carrier.NodeId, carrier.PublicKeyLine, Guid.NewGuid(), sourceRepo,
            rootSigned.Content, rootSigned.Sha, rootSigned.RawBytes, vouchSigned.Content, vouchSigned.Sha, vouchSigned.RawBytes);
        string carriedPath = $"owners/{root.Fingerprint}/carried/{carrier.NodeId}.yaml";
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", carriedPath, carried, carrier);
        // Root-signed (idea 6be68ee2, trust-ledger finding 2: a revocation now requires a live root
        // key) — the test has access to root's own private key locally, standing in for root
        // reaching this carried-only ledger directly to revoke the carrier.
        await RevokeAsync(targetRepo, root.Fingerprint, carrier.NodeId, root);

        // The revoked node re-pushes its own bundle again, signed with its own (still held)
        // private key — nobody else's. A distinct commit (a different source_project_id this
        // time, so the tree genuinely changes and this re-touch is not a content-identical no-op
        // git would never even report as a changed path) rather than the byte-identical original.
        string recarried = BuildCarriedRecordYaml(
            carrier.NodeId, carrier.PublicKeyLine, Guid.NewGuid(), sourceRepo,
            rootSigned.Content, rootSigned.Sha, rootSigned.RawBytes, vouchSigned.Content, vouchSigned.Sha, vouchSigned.RawBytes);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", carriedPath, recarried, carrier);

        string readerRepo = _repo.CloneNode(targetHub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

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
        // A genuinely root-signed commit exists and is reused as "the vouch" — root.yaml's own
        // establishing commit, already proven signed by root via check 2 — but its own message
        // never names this node, so it must never satisfy check 3 on its own merits alone
        // (independent pre-PR review, adversarial lens, critical: without binding the vouch
        // commit's own signed bytes to a specific node, any commit root ever signed for any
        // reason vouches every node an attacker cares to name).
        (string sourceRepo, GeneratedIdentity root, GeneratedIdentity carrier,
            var rootSigned, _) = await EstablishSourceVouchAsync();

        string targetHub = _repo.CreateHub();
        string targetRepo = _repo.CloneNode(targetHub);
        await WriteNodeFileAsync(targetRepo, carrier, carrier, root.Fingerprint);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/root.yaml", rootSigned.Content, carrier);
        string carried = BuildCarriedRecordYaml(
            carrier.NodeId, carrier.PublicKeyLine, Guid.NewGuid(), sourceRepo,
            rootSigned.Content, rootSigned.Sha, rootSigned.RawBytes,
            rootSigned.Content, rootSigned.Sha, rootSigned.RawBytes /* the root commit reused as "the vouch" */);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/carried/{carrier.NodeId}.yaml", carried, carrier);

        string readerRepo = _repo.CloneNode(targetHub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.OwnerChains.Should().NotContainKey(root.Fingerprint, "a commit that never names this node can never stand in as its vouch");
        chain.IsEnrolledInOwner(carrier.Fingerprint, root.Fingerprint).Should().BeFalse();
        chain.UnverifiedWrites.Should().Contain(write => write.Kind == "carried" && write.Identifier == carrier.NodeId.ToString());
    }

    [Fact]
    public async Task A_genesis_members_commit_signed_by_a_carried_node_is_accepted()
    {
        (string sourceRepo, GeneratedIdentity root, GeneratedIdentity carrier,
            var rootSigned, var vouchSigned) = await EstablishSourceVouchAsync();

        string targetHub = _repo.CreateHub();
        string targetRepo = _repo.CloneNode(targetHub);
        await WriteNodeFileAsync(targetRepo, carrier, carrier, root.Fingerprint);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/root.yaml", rootSigned.Content, carrier);
        string carried = BuildCarriedRecordYaml(
            carrier.NodeId, carrier.PublicKeyLine, Guid.NewGuid(), sourceRepo,
            rootSigned.Content, rootSigned.Sha, rootSigned.RawBytes, vouchSigned.Content, vouchSigned.Sha, vouchSigned.RawBytes);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/carried/{carrier.NodeId}.yaml", carried, carrier);

        // Genesis members commit for this target project, signed by the CARRIED node — never the
        // root's own key, which nobody here holds — the exact shape h9k project join's own carry
        // path produces (EnsureGenesisMemberFileAsync, signed with the carrying node's own key).
        const string mintedProjectKey = "01ARZ3NDEKTSV4RRFFQ69G5FBB";
        await WriteMemberFileAsync(targetRepo, root.Fingerprint, "owner", carrier, mintedProjectKey);

        string readerRepo = _repo.CloneNode(targetHub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.Members.Should().Contain(member => member.RootFingerprint == root.Fingerprint && member.Role == MembershipRole.Owner);
        chain.ProjectKey.Should().Be(mintedProjectKey);
        chain.UnverifiedWrites.Should().NotContain(write => write.Kind == "membership");
    }

    /// <summary>
    /// Independent pre-PR review, conformance lens, medium: on a carried ledger, the genesis
    /// members commit can only ever be signed by the carrying node itself (nobody locally holds the
    /// root's own private key). If genesis were authorized the identical way every later membership
    /// write is — against the owner chain's own CURRENT enrollment — then the routine
    /// <c>h9k node revoke</c> the root-holding node runs once it later joins this same project would
    /// permanently wipe this project's own genesis member and project key, since the one-time
    /// bootstrap exception is already spent and nothing can ever re-earn it. Genesis must stay
    /// authorized once it is, exactly like a self-established root's own <c>root.yaml</c> stays
    /// established even after every node it ever vouched is revoked.
    /// </summary>
    [Fact]
    public async Task A_genesis_members_commit_signed_by_a_carried_node_stays_authorized_after_that_node_is_revoked()
    {
        (string sourceRepo, GeneratedIdentity root, GeneratedIdentity carrier,
            var rootSigned, var vouchSigned) = await EstablishSourceVouchAsync();

        string targetHub = _repo.CreateHub();
        string targetRepo = _repo.CloneNode(targetHub);
        await WriteNodeFileAsync(targetRepo, carrier, carrier, root.Fingerprint);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/root.yaml", rootSigned.Content, carrier);
        string carried = BuildCarriedRecordYaml(
            carrier.NodeId, carrier.PublicKeyLine, Guid.NewGuid(), sourceRepo,
            rootSigned.Content, rootSigned.Sha, rootSigned.RawBytes, vouchSigned.Content, vouchSigned.Sha, vouchSigned.RawBytes);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/carried/{carrier.NodeId}.yaml", carried, carrier);

        const string mintedProjectKey = "01ARZ3NDEKTSV4RRFFQ69G5FDD";
        await WriteMemberFileAsync(targetRepo, root.Fingerprint, "owner", carrier, mintedProjectKey);

        // The root-holding node later joins this same project (root.yaml already carried in) and
        // revokes the carrying node — signed with the root's own real key, which only that node
        // ever holds, the identical authority h9k node revoke's own fan-out relies on.
        await RevokeAsync(targetRepo, root.Fingerprint, carrier.NodeId, root);

        string readerRepo = _repo.CloneNode(targetHub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

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
        (string sourceRepo, GeneratedIdentity root, GeneratedIdentity carrier,
            var rootSigned, var vouchSigned) = await EstablishSourceVouchAsync();
        GeneratedIdentity stranger = GenerateIdentity();

        string targetHub = _repo.CreateHub();
        string targetRepo = _repo.CloneNode(targetHub);
        await WriteNodeFileAsync(targetRepo, carrier, carrier, root.Fingerprint);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/root.yaml", rootSigned.Content, carrier);
        string carried = BuildCarriedRecordYaml(
            carrier.NodeId, carrier.PublicKeyLine, Guid.NewGuid(), sourceRepo,
            rootSigned.Content, rootSigned.Sha, rootSigned.RawBytes, vouchSigned.Content, vouchSigned.Sha, vouchSigned.RawBytes);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/carried/{carrier.NodeId}.yaml", carried, carrier);

        // A genesis members commit for the identical root, but signed by a stranger this chain
        // never enrolled at all — neither the root's own key nor the carried node's.
        await WriteMemberFileAsync(targetRepo, root.Fingerprint, "owner", stranger, "01ARZ3NDEKTSV4RRFFQ69G5FCC");

        string readerRepo = _repo.CloneNode(targetHub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.Members.Should().BeEmpty("genesis is spent by this refused commit either way, but it never grants membership");
        chain.ProjectKey.Should().BeNull();
        chain.UnverifiedWrites.Should().Contain(write => write.Kind == "membership" && write.RootFingerprint == root.Fingerprint);
    }

    [Fact]
    public async Task A_carried_bundle_with_a_substituted_node_public_key_establishes_nothing()
    {
        // The exact attack check 3's fingerprint binding closes (independent pre-PR review, cycle
        // 1, both lenses, high): an attacker who can merely READ a source ledger copies a genuine
        // bundle's root and vouch commits verbatim, but swaps node_public_key for a key of their
        // own — the node id stays the victim's, so a check that only asked "signed by root and
        // names this node id" would still pass, and the attacker's own self-signed node.yaml on
        // the target satisfies check 4 too. The genuine vouch commit's own message names the
        // victim's own fingerprint, never the attacker's substituted one, so check 3 now refuses it.
        (string sourceRepo, GeneratedIdentity root, GeneratedIdentity carrier,
            var rootSigned, var vouchSigned) = await EstablishSourceVouchAsync();
        GeneratedIdentity attacker = GenerateIdentity();

        string targetHub = _repo.CreateHub();
        string targetRepo = _repo.CloneNode(targetHub);
        // The attacker self-announces under the VICTIM's own node id, declaring their own key —
        // free to do, since node ids are unauthenticated GUIDs with no first-writer protection.
        await WriteNodeFileAsync(
            targetRepo, new GeneratedIdentity(attacker.PrivateKeyPath, attacker.PublicKeyLine, attacker.Fingerprint, carrier.NodeId),
            attacker, root.Fingerprint);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/root.yaml", rootSigned.Content, attacker);
        string carried = BuildCarriedRecordYaml(
            carrier.NodeId, attacker.PublicKeyLine, Guid.NewGuid(), sourceRepo,
            rootSigned.Content, rootSigned.Sha, rootSigned.RawBytes, vouchSigned.Content, vouchSigned.Sha, vouchSigned.RawBytes);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/carried/{carrier.NodeId}.yaml", carried, attacker);

        string readerRepo = _repo.CloneNode(targetHub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.OwnerChains.Should().NotContainKey(root.Fingerprint, "the genuine vouch commit's own message names the victim's key, never the attacker's substituted one");
        chain.IsEnrolledInOwner(attacker.Fingerprint, root.Fingerprint).Should().BeFalse();
        chain.UnverifiedWrites.Should().Contain(write => write.Kind == "carried" && write.Identifier == carrier.NodeId.ToString());
    }

    [Fact]
    public async Task A_carried_bundle_using_the_roots_own_revocation_commit_as_its_vouch_establishes_nothing()
    {
        // The related gap the conformance lens named: a root-signed "Revoke node {id}" commit also
        // names the node id in its own message, so a check that only asked "signed by root and
        // names this node id" would have accepted it as though it were a vouch. Binding check 3 to
        // the exact "Vouch node {id} key {fingerprint}" marker closes it too, not just the
        // substituted-key attack above.
        (string sourceRepo, GeneratedIdentity root, GeneratedIdentity carrier, var rootSigned, _) = await EstablishSourceVouchAsync();
        await RevokeAsync(sourceRepo, root.Fingerprint, carrier.NodeId, root);
        (string Content, string Sha, string RawBytes) revokeSigned = await ReadSignedCommitAsync(
            sourceRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/revoked/{carrier.NodeId}.yaml");

        string targetHub = _repo.CreateHub();
        string targetRepo = _repo.CloneNode(targetHub);
        await WriteNodeFileAsync(targetRepo, carrier, carrier, root.Fingerprint);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/root.yaml", rootSigned.Content, carrier);
        string carried = BuildCarriedRecordYaml(
            carrier.NodeId, carrier.PublicKeyLine, Guid.NewGuid(), sourceRepo,
            rootSigned.Content, rootSigned.Sha, rootSigned.RawBytes, revokeSigned.Content, revokeSigned.Sha, revokeSigned.RawBytes);
        await WriteAsync(targetRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/carried/{carrier.NodeId}.yaml", carried, carrier);

        string readerRepo = _repo.CloneNode(targetHub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.OwnerChains.Should().NotContainKey(root.Fingerprint, "a revocation commit was never a vouch, even though it also names the node id");
        chain.UnverifiedWrites.Should().Contain(write => write.Kind == "carried" && write.Identifier == carrier.NodeId.ToString());
    }

    [Fact]
    public async Task A_normally_vouched_then_revoked_node_cannot_resurrect_itself_via_a_first_time_carried_record()
    {
        // The resurrection gate (carriedPathsEstablished) only ever fired for a node id previously
        // established BY a carry. A node vouched the ORDINARY way and then revoked had never
        // touched the carried path before, so — before this fix — its first-ever carried record
        // self-authorized on its own unchanged embedded evidence and came back, erasing the
        // revocation the identical way the gate already prevented for a node carried before
        // (independent pre-PR review, adversarial lens, high).
        string hub = _repo.CreateHub();
        (string repositoryPath, GeneratedIdentity root) = await EstablishGenesisRootAsync(hub);
        GeneratedIdentity carrier = GenerateIdentity();
        await WriteNodeFileAsync(repositoryPath, carrier, carrier);

        // Vouched the ORDINARY way, signed by the root's own real key — never carried before.
        await VouchAsync(repositoryPath, root.Fingerprint, carrier, root);
        await RevokeAsync(repositoryPath, root.Fingerprint, carrier.NodeId, root);

        (string Content, string Sha, string RawBytes) rootSigned = await ReadSignedCommitAsync(
            repositoryPath, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/root.yaml");
        (string Content, string Sha, string RawBytes) vouchSigned = await ReadSignedCommitAsync(
            repositoryPath, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/nodes/{carrier.NodeId}.yaml");

        // The revoked node's first-ever carried record, presenting its own still-valid, unchanged
        // source evidence — signed with its own (still held) private key, nobody else's.
        string carried = BuildCarriedRecordYaml(
            carrier.NodeId, carrier.PublicKeyLine, Guid.NewGuid(), repositoryPath,
            rootSigned.Content, rootSigned.Sha, rootSigned.RawBytes, vouchSigned.Content, vouchSigned.Sha, vouchSigned.RawBytes);
        await WriteAsync(repositoryPath, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/carried/{carrier.NodeId}.yaml", carried, carrier);

        string readerRepo = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.IsEnrolledInOwner(carrier.Fingerprint, root.Fingerprint).Should().BeFalse(
            "a node revoked the ordinary way cannot resurrect itself by presenting its own carried evidence for the first time");
        chain.UnverifiedWrites.Should().Contain(write => write.Kind == "carried" && write.Identifier == carrier.NodeId.ToString());
    }

    // ---- succession (idea 6be68ee2: an owner's root authority is a ranked set of root keys) -----

    [Fact]
    public async Task A_root_signed_successor_attaches_and_a_node_signed_one_does_not()
    {
        string hub = _repo.CreateHub();
        (string repositoryPath, GeneratedIdentity root) = await EstablishGenesisRootAsync(hub);

        GeneratedIdentity rootSignedHeir = GenerateIdentity();
        await WriteNodeFileAsync(repositoryPath, rootSignedHeir, rootSignedHeir);
        await VouchAsync(repositoryPath, root.Fingerprint, rootSignedHeir, root);
        await WriteSuccessorAsync(repositoryPath, root.Fingerprint, rootSignedHeir, root);

        GeneratedIdentity nodeSignedHeir = GenerateIdentity();
        await WriteNodeFileAsync(repositoryPath, nodeSignedHeir, nodeSignedHeir);
        await VouchAsync(repositoryPath, root.Fingerprint, nodeSignedHeir, root);
        await WriteSuccessorAsync(repositoryPath, root.Fingerprint, nodeSignedHeir, nodeSignedHeir);

        string readerRepo = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.OwnerChains[root.Fingerprint].SuccessorNodeIds.Should().Contain(rootSignedHeir.NodeId.ToString());
        chain.OwnerChains[root.Fingerprint].SuccessorNodeIds.Should().NotContain(nodeSignedHeir.NodeId.ToString());
        chain.UnverifiedWrites.Should().Contain(write => write.Kind == "successor" && write.Identifier == nodeSignedHeir.NodeId.ToString());
    }

    [Fact]
    public async Task A_rotation_by_a_listed_successor_is_accepted_and_a_k0_members_write_still_verifies_afterwards()
    {
        string hub = _repo.CreateHub();
        (string repositoryPath, GeneratedIdentity root) = await EstablishGenesisRootAsync(hub);
        GeneratedIdentity heir = GenerateIdentity();
        await WriteNodeFileAsync(repositoryPath, heir, heir);
        await VouchAsync(repositoryPath, root.Fingerprint, heir, root);
        await WriteSuccessorAsync(repositoryPath, root.Fingerprint, heir, root);
        await WriteRotationAsync(repositoryPath, root.Fingerprint, 1, heir, root.PublicKeyLine);

        // K0 signs a fresh, ordinary (non-genesis) membership write AFTER the rotation.
        GeneratedIdentity newMember = GenerateIdentity();
        await WriteMemberFileAsync(repositoryPath, newMember.Fingerprint, "member", root);

        string readerRepo = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.OwnerChains[root.Fingerprint].RootKeys.Should().Contain(
            key => key.Fingerprint == heir.Fingerprint && key.IntroducedByNodeId == heir.NodeId.ToString());
        chain.RoleOf(newMember.Fingerprint).Should().Be(
            MembershipRole.Member, "K0 still verifies every members write it signs after a later rotation");
    }

    [Fact]
    public async Task A_rotation_by_an_unlisted_key_is_ignored()
    {
        string hub = _repo.CreateHub();
        (string repositoryPath, GeneratedIdentity root) = await EstablishGenesisRootAsync(hub);
        GeneratedIdentity node = GenerateIdentity();
        await WriteNodeFileAsync(repositoryPath, node, node);
        await VouchAsync(repositoryPath, root.Fingerprint, node, root);
        // Never listed as a successor.
        await WriteRotationAsync(repositoryPath, root.Fingerprint, 1, node, root.PublicKeyLine);

        string readerRepo = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.OwnerChains[root.Fingerprint].RootKeys.Should().ContainSingle(key => key.Fingerprint == root.Fingerprint);
        chain.UnverifiedWrites.Should().Contain(write => write.Kind == "rotation" && write.Identifier == node.NodeId.ToString());
    }

    [Fact]
    public async Task A_stale_rotation_superseding_a_non_current_key_is_ignored()
    {
        string hub = _repo.CreateHub();
        (string repositoryPath, GeneratedIdentity root) = await EstablishGenesisRootAsync(hub);

        GeneratedIdentity firstHeir = GenerateIdentity();
        await WriteNodeFileAsync(repositoryPath, firstHeir, firstHeir);
        await VouchAsync(repositoryPath, root.Fingerprint, firstHeir, root);
        await WriteSuccessorAsync(repositoryPath, root.Fingerprint, firstHeir, root);
        await WriteRotationAsync(repositoryPath, root.Fingerprint, 1, firstHeir, root.PublicKeyLine);

        // Names root's own key (K0) as what it supersedes, but the first rotation already moved the
        // chain's own top forward to K1 — stale by the time this one is reached.
        GeneratedIdentity secondHeir = GenerateIdentity();
        await WriteNodeFileAsync(repositoryPath, secondHeir, secondHeir);
        await VouchAsync(repositoryPath, root.Fingerprint, secondHeir, root);
        await WriteSuccessorAsync(repositoryPath, root.Fingerprint, secondHeir, root);
        await WriteRotationAsync(repositoryPath, root.Fingerprint, 2, secondHeir, root.PublicKeyLine);

        string readerRepo = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        IReadOnlyList<LiveRootKey> rootKeys = chain.OwnerChains[root.Fingerprint].RootKeys;
        rootKeys.Should().HaveCount(2, "only the first rotation to land actually moved the chain's own top");
        rootKeys.Should().Contain(key => key.IntroducedByNodeId == firstHeir.NodeId.ToString());
        rootKeys.Should().NotContain(key => key.IntroducedByNodeId == secondHeir.NodeId.ToString());
        chain.UnverifiedWrites.Should().Contain(write => write.Kind == "rotation" && write.Identifier == secondHeir.NodeId.ToString());
    }

    [Fact]
    public async Task A_revoked_node_cannot_promote()
    {
        string hub = _repo.CreateHub();
        (string repositoryPath, GeneratedIdentity root) = await EstablishGenesisRootAsync(hub);
        GeneratedIdentity heir = GenerateIdentity();
        await WriteNodeFileAsync(repositoryPath, heir, heir);
        await VouchAsync(repositoryPath, root.Fingerprint, heir, root);
        await WriteSuccessorAsync(repositoryPath, root.Fingerprint, heir, root);
        await RevokeAsync(repositoryPath, root.Fingerprint, heir.NodeId, root);
        // The revoked node still holds its own private key and can still sign a rotation commit —
        // it just no longer counts, since it is not currently vouched into the fleet.
        await WriteRotationAsync(repositoryPath, root.Fingerprint, 1, heir, root.PublicKeyLine);

        string readerRepo = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.OwnerChains[root.Fingerprint].RootKeys.Should().ContainSingle(key => key.Fingerprint == root.Fingerprint);
        chain.UnverifiedWrites.Should().Contain(write => write.Kind == "rotation" && write.Identifier == heir.NodeId.ToString());
    }

    [Fact]
    public async Task A_revoked_successor_record_truncates_a_still_vouched_rotated_keys_rank()
    {
        // The rank rule in isolation (independent pre-PR review, cycle 1, conformance lens, low —
        // the test this replaces conflated an ordinary fleet revoke with the rank-only
        // revoked-successor record, so it never actually proved the rank rule by itself; a rotated
        // key's own successor and rotation records were already misreported as unverifiable writers
        // through the identical seam the fix at GitLedgerChainReader.cs:705 addresses, which is what
        // let the old assertions pass regardless of whether this rank rule worked at all).
        string hub = _repo.CreateHub();
        (string repositoryPath, GeneratedIdentity root) = await EstablishGenesisRootAsync(hub);
        GeneratedIdentity heir = GenerateIdentity();
        await WriteNodeFileAsync(repositoryPath, heir, heir);
        await VouchAsync(repositoryPath, root.Fingerprint, heir, root);
        await WriteSuccessorAsync(repositoryPath, root.Fingerprint, heir, root);
        await WriteRotationAsync(repositoryPath, root.Fingerprint, 1, heir, root.PublicKeyLine);

        // K1 (the promoted heir) signs a membership write while it still holds that rank — a
        // members-ref write is authorized only by a live root key (idea 6be68ee2, trust-ledger
        // finding 2), and K1 is one, right now.
        GeneratedIdentity newMember = GenerateIdentity();
        await WriteMemberFileAsync(repositoryPath, newMember.Fingerprint, "member", heir);

        string beforeReaderRepo = _repo.CloneNode(hub);
        TrustChain beforeRevocation = await _chainReader.ComputeAsync(beforeReaderRepo, CancellationToken.None);
        beforeRevocation.OwnerChains[root.Fingerprint].RootKeys.Should().Contain(
            key => key.IntroducedByNodeId == heir.NodeId.ToString(), "the rotation already landed");
        beforeRevocation.RoleOf(newMember.Fingerprint).Should().Be(
            MembershipRole.Member, "K1 was a live root key when it signed this write, and still is at this read");

        // K0 revokes only the heir's own RANK — never its ordinary fleet membership: no
        // `h9k node revoke` ever runs here, so the heir stays vouched in Nodes throughout.
        await WriteRevokedSuccessorAsync(repositoryPath, root.Fingerprint, heir.NodeId, root);

        string afterReaderRepo = _repo.CloneNode(hub);
        TrustChain afterRevocation = await _chainReader.ComputeAsync(afterReaderRepo, CancellationToken.None);

        TrustedOwner owner = afterRevocation.OwnerChains[root.Fingerprint];
        owner.RootKeys.Should().ContainSingle(
            key => key.Fingerprint == root.Fingerprint, "the revoked-successor record, signed by K0, strips the heir's own rank");
        owner.Nodes.Should().Contain(
            node => node.NodeId == heir.NodeId.ToString(), "the heir's ordinary fleet membership was never touched — only its rank was revoked");
        afterRevocation.RoleOf(newMember.Fingerprint).Should().BeNull(
            "membership is authorized against the chain's own live state at read time (2026-09-13), never a "
            + "snapshot of when the write landed — now that the heir's own rank is stripped, its earlier "
            + "membership write is no longer signed by any live root key (idea 6be68ee2, trust-ledger "
            + "finding 2: staying vouched in Nodes is no longer enough for a members-ref write)");
    }

    [Fact]
    public async Task A_lower_ranked_keys_revoked_successor_is_refused()
    {
        string hub = _repo.CreateHub();
        (string repositoryPath, GeneratedIdentity root) = await EstablishGenesisRootAsync(hub);

        GeneratedIdentity firstHeir = GenerateIdentity();
        await WriteNodeFileAsync(repositoryPath, firstHeir, firstHeir);
        await VouchAsync(repositoryPath, root.Fingerprint, firstHeir, root);
        await WriteSuccessorAsync(repositoryPath, root.Fingerprint, firstHeir, root);
        await WriteRotationAsync(repositoryPath, root.Fingerprint, 1, firstHeir, root.PublicKeyLine);

        GeneratedIdentity secondHeir = GenerateIdentity();
        await WriteNodeFileAsync(repositoryPath, secondHeir, secondHeir);
        await VouchAsync(repositoryPath, root.Fingerprint, secondHeir, root);
        await WriteSuccessorAsync(repositoryPath, root.Fingerprint, secondHeir, firstHeir);
        await WriteRotationAsync(repositoryPath, root.Fingerprint, 2, secondHeir, firstHeir.PublicKeyLine);

        // The chain is now [K0, K1 (firstHeir), K2 (secondHeir)] — an earlier key always outranks a
        // later one. K2 attempts to revoke K1's own rank, signed by K2's own key: that key ranks
        // BELOW K1, not above it, so this must be refused rather than silently accepted.
        await WriteRevokedSuccessorAsync(repositoryPath, root.Fingerprint, firstHeir.NodeId, secondHeir);

        string readerRepo = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

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
        // idea 6be68ee2, trust-ledger finding 2 (independent pre-PR review, cycle 1, both lenses,
        // high): succession used to read a PRELIMINARY fleet state authorized the old, broader
        // "root or any currently enrolled node" rule, so a merely vouched node's own revocation of
        // an already-rotated-in successor could still strip that successor from the preliminary
        // fleet, which made succession refuse the successor's own rotation as "not currently
        // vouched" and silently collapsed the live root-key set back to [K0] — even though the
        // authoritative pass would itself have refused the very revocation responsible. This is the
        // exact attack: a compromised, merely vouched fleet node (mallory) must never be able to
        // strip a promoted successor's own live root key this way.
        string hub = _repo.CreateHub();
        (string repositoryPath, GeneratedIdentity root) = await EstablishGenesisRootAsync(hub);

        GeneratedIdentity heir = GenerateIdentity();
        await WriteNodeFileAsync(repositoryPath, heir, heir);
        await VouchAsync(repositoryPath, root.Fingerprint, heir, root);
        await WriteSuccessorAsync(repositoryPath, root.Fingerprint, heir, root);
        await WriteRotationAsync(repositoryPath, root.Fingerprint, 1, heir, root.PublicKeyLine);

        // Mallory is merely vouched into the fleet — never a root key — and then signs a revocation
        // of the already-promoted heir's own ordinary fleet membership.
        GeneratedIdentity mallory = GenerateIdentity();
        await WriteNodeFileAsync(repositoryPath, mallory, mallory);
        await VouchAsync(repositoryPath, root.Fingerprint, mallory, root);
        await RevokeAsync(repositoryPath, root.Fingerprint, heir.NodeId, mallory);

        string readerRepo = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

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
        // Independent pre-PR review, cycle 2, adversarial lens, high: unlike the mallory scenario
        // above, this is the heir revoking its OWN now-obsolete ordinary node record after its own
        // rotation has already landed — a routine, fully-authorized cleanup step (the heir is now
        // itself a live root key, so its own signature does authorize a revocation). The fixed-point
        // loop in ComputeOwnerChainAsync used to oscillate forever between two answers for this
        // exact case: with the heir still counted as a live root key, its self-revoke succeeds and
        // removes it from Nodes, which then made ComputeSuccessionAsync's own "is this node
        // currently vouched" check fail and retract the rotation, collapsing the root-key set back
        // to [K0] — which then made the very same self-revoke unauthorized again, putting the heir
        // back in Nodes and re-validating the rotation, forever.
        string hub = _repo.CreateHub();
        (string repositoryPath, GeneratedIdentity root) = await EstablishGenesisRootAsync(hub);

        GeneratedIdentity heir = GenerateIdentity();
        await WriteNodeFileAsync(repositoryPath, heir, heir);
        await VouchAsync(repositoryPath, root.Fingerprint, heir, root);
        await WriteSuccessorAsync(repositoryPath, root.Fingerprint, heir, root);
        await WriteRotationAsync(repositoryPath, root.Fingerprint, 1, heir, root.PublicKeyLine);
        await RevokeAsync(repositoryPath, root.Fingerprint, heir.NodeId, heir);

        string readerRepo = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

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
        // Independent pre-PR review, cycle 4, adversarial lens, medium: unlike the two scenarios
        // above, this self-revoke lands BEFORE the successor and rotation records that would
        // eventually promote the very same node — a shape only a hand-crafted history a compromised
        // node pushes directly can produce, never an ordinary command flow. With rootKeys=[K0], the
        // self-revoke is unauthorized (heir is not yet a root key), so it is refused and the
        // successor/rotation validate, making heir a root key. But with rootKeys=[K0, heir], the
        // self-revoke IS now authorized, at a commit index earlier than the successor record — the
        // order-aware carve-out (5a593790a) does not protect it (revokedIndex is not > the
        // successor's own commit index) — so the successor and rotation are refused, collapsing
        // rootKeys back to [K0], which then makes the self-revoke unauthorized again: the two
        // candidate sets alternate forever rather than converging. The reader must still terminate
        // (never hang) and answer deterministically — never a result that depends on incidental
        // history length — rather than whichever of the two states the iteration cap happened to
        // land on.
        string hub = _repo.CreateHub();
        (string repositoryPath, GeneratedIdentity root) = await EstablishGenesisRootAsync(hub);

        GeneratedIdentity heir = GenerateIdentity();
        await WriteNodeFileAsync(repositoryPath, heir, heir);
        await VouchAsync(repositoryPath, root.Fingerprint, heir, root);
        await RevokeAsync(repositoryPath, root.Fingerprint, heir.NodeId, heir);
        await WriteSuccessorAsync(repositoryPath, root.Fingerprint, heir, root);
        await WriteRotationAsync(repositoryPath, root.Fingerprint, 1, heir, root.PublicKeyLine);

        string readerRepo = _repo.CloneNode(hub);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, cts.Token);

        TrustedOwner owner = chain.OwnerChains[root.Fingerprint];
        owner.RootKeys.Should().ContainSingle(key => key.Fingerprint == root.Fingerprint,
            "the self-contradictory history never resolves the heir's own promotion, so the smaller, "
            + "more conservative candidate root-key set the cycle ever produced wins rather than "
            + "granting root trust to a disputed key");

        // Deterministic regardless of how many times the reader is asked: the cycle-detection
        // resolution never depends on anything but the two candidate states themselves.
        TrustChain secondRead = await _chainReader.ComputeAsync(readerRepo, cts.Token);
        secondRead.OwnerChains[root.Fingerprint].RootKeys.Should().BeEquivalentTo(owner.RootKeys);
    }

    [Fact]
    public async Task A_one_owner_fleet_with_no_rotation_reads_exactly_as_before()
    {
        string hub = _repo.CreateHub();
        (string repositoryPath, GeneratedIdentity root) = await EstablishGenesisRootAsync(hub);
        await WriteNodeFileAsync(repositoryPath, root, root);

        string readerRepo = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        TrustedOwner owner = chain.OwnerChains[root.Fingerprint];
        owner.RootKeys.Should().ContainSingle();
        owner.RootKeys[0].PublicKeyLine.Should().Be(root.PublicKeyLine);
        owner.RootKeys[0].Fingerprint.Should().Be(root.Fingerprint);
        owner.RootKeys[0].IntroducedByNodeId.Should().BeNull();
        owner.SuccessorNodeIds.Should().BeEmpty();
        chain.IsAllowedSigner(root.Fingerprint).Should().BeTrue();
    }

    /// <summary>
    /// idea 6be68ee2, trust finding 8: a rewind of the owners ref back to before a revocation must
    /// not resurrect the revoked node, even though origin itself now shows the pre-revocation state.
    /// The chain reader's own append-only integrity check refuses the rewound fetch and keeps
    /// replaying from the verified (post-revocation) tip instead.
    /// </summary>
    [Fact]
    public async Task A_rewind_of_the_owners_ref_after_a_revocation_does_not_reinstate_the_node()
    {
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);
        GeneratedIdentity nodeB = GenerateIdentity();
        string nodeBRepo = _repo.CloneNode(hub);
        await WriteNodeFileAsync(nodeBRepo, nodeB, nodeB);
        await VouchAsync(ownerRepo, owner.Fingerprint, nodeB, owner);

        string ownersRefName = $"refs/hall9k/ledger/owners/{owner.Fingerprint}";
        string readerRepo = _repo.CloneNode(hub);

        TrustChain vouched = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);
        vouched.OwnerChains[owner.Fingerprint].Nodes.Should().Contain(node => node.NodeId == nodeB.NodeId.ToString());

        (int vouchExit, string vouchTipOutput, string vouchError) = LedgerTestRepo.RevParseQuiet(ownerRepo, ownersRefName);
        vouchExit.Should().Be(0, vouchError);
        string vouchTip = vouchTipOutput.Trim();

        await RevokeAsync(ownerRepo, owner.Fingerprint, nodeB.NodeId, owner);

        TrustChain revoked = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);
        revoked.OwnerChains[owner.Fingerprint].Nodes.Should().NotContain(node => node.NodeId == nodeB.NodeId.ToString());

        // The rewind: origin is forced straight back to the vouch commit, discarding the
        // revocation this reader already verified past.
        (int rewindExit, _, string rewindError) = LedgerTestRepo.RunGit(hub, "update-ref", ownersRefName, vouchTip);
        rewindExit.Should().Be(0, rewindError);

        TrustChain afterRewind = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        afterRewind.OwnerChains[owner.Fingerprint].Nodes.Should().NotContain(
            node => node.NodeId == nodeB.NodeId.ToString(), "the revocation must not vanish just because origin rewound behind it");
        afterRewind.UnverifiedWrites.Should().Contain(
            write => write.Kind == "ref" && write.Identifier == ownersRefName && write.RootFingerprint == owner.Fingerprint,
            "the refusal is named so it reaches h9k status rather than silently healing with nothing recorded, "
            + "under its own \"ref\" kind rather than \"root\" so it never collides with a self-certification "
            + "or signature failure sharing the identical (kind, identifier, root) stream key");

        // The revoker's own repository, not just a third reader: RevokeAsync's own WriteAsync call
        // above is the only thing that ever touched ownerRepo's verified tip for this rewind, with
        // no ComputeAsync of its own read in between to move it. Before GitLedger.WriteAsync itself
        // advanced the verified ref on a successful push, this exact node — the one that just wrote
        // the revocation — was the one node blind to a rewind of that same write (independent pre-PR
        // review, cycle 2, both lenses, high).
        TrustChain ownerAfterRewind = await _chainReader.ComputeAsync(ownerRepo, CancellationToken.None);

        ownerAfterRewind.OwnerChains[owner.Fingerprint].Nodes.Should().NotContain(
            node => node.NodeId == nodeB.NodeId.ToString(),
            "the node that wrote the revocation must not reinstate it just because origin rewound behind it");
    }

    /// <summary>
    /// idea 6be68ee2, trust finding 8: an owners ref origin has since deleted is discovered by the
    /// union of ls-remote and this node's own locally verified suffixes, refused as a
    /// missing-remote-ref rather than silently dropped, and its local content is kept and read.
    /// </summary>
    [Fact]
    public async Task A_deleted_owners_prefix_ref_is_reported_as_missing_and_kept()
    {
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);
        string readerRepo = _repo.CloneNode(hub);

        TrustChain established = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);
        established.OwnerChains.Should().ContainKey(owner.Fingerprint);

        LedgerTestRepo.RunGit(hub, "update-ref", "-d", $"refs/hall9k/ledger/owners/{owner.Fingerprint}");

        TrustChain afterDeletion = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        afterDeletion.OwnerChains.Should().ContainKey(
            owner.Fingerprint, "the local copy is kept and read rather than the root silently vanishing");
        afterDeletion.UnverifiedWrites.Should().Contain(
            write => write.Kind == "ref"
                && write.Identifier == $"refs/hall9k/ledger/owners/{owner.Fingerprint}"
                && write.RootFingerprint == owner.Fingerprint,
            "a ref origin has since deleted is reported rather than silently dropped from discovery, under its "
            + "own \"ref\" kind rather than \"root\" so it never collides with a self-certification or "
            + "signature failure sharing the identical (kind, identifier, root) stream key");
    }

    /// <summary>Shared setup every carried-record test above needs: a genesis root on its own
    /// source project ledger, and a node genuinely vouched into it there — the source half of the
    /// evidence a carrying join reads and embeds. Each test builds its own separate TARGET ledger
    /// (a second, unrelated hub) and writes the carried bundle there itself, since that is exactly
    /// what varies from test to test.</summary>
    private async Task<(string SourceRepo, GeneratedIdentity Root, GeneratedIdentity Carrier,
        (string Content, string Sha, string RawBytes) RootSigned, (string Content, string Sha, string RawBytes) VouchSigned)>
        EstablishSourceVouchAsync()
    {
        string sourceHub = _repo.CreateHub();
        (string sourceRepo, GeneratedIdentity root) = await EstablishGenesisRootAsync(sourceHub);
        GeneratedIdentity carrier = GenerateIdentity();
        await WriteNodeFileAsync(sourceRepo, carrier, carrier);
        await VouchAsync(sourceRepo, root.Fingerprint, carrier, root);

        (string Content, string Sha, string RawBytes) rootSigned = await ReadSignedCommitAsync(
            sourceRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/root.yaml");
        (string Content, string Sha, string RawBytes) vouchSigned = await ReadSignedCommitAsync(
            sourceRepo, $"refs/hall9k/ledger/owners/{root.Fingerprint}", $"owners/{root.Fingerprint}/nodes/{carrier.NodeId}.yaml");

        return (sourceRepo, root, carrier, rootSigned, vouchSigned);
    }

    // ---- test scaffolding -------------------------------------------------------------------

    private sealed record GeneratedIdentity(string PrivateKeyPath, string PublicKeyLine, string Fingerprint, Guid NodeId);

    private static readonly LedgerCommitter Committer = new("Chain Reader Test", "chain-reader-test@hall9k.local");

    private async Task<(string RepositoryPath, GeneratedIdentity Owner)> EstablishGenesisRootAsync(string hub, string? projectKey = null)
    {
        GeneratedIdentity owner = GenerateIdentity();
        string repo = _repo.CloneNode(hub);
        await WriteRootFileAsync(repo, owner);
        await WriteMemberFileAsync(repo, owner.Fingerprint, "owner", owner, projectKey);
        return (repo, owner);
    }

    private async Task WriteRootFileAsync(string repositoryPath, GeneratedIdentity root)
    {
        string refName = $"refs/hall9k/ledger/owners/{root.Fingerprint}";
        string path = $"owners/{root.Fingerprint}/root.yaml";
        string content = BuildYaml(("public_key", root.PublicKeyLine), ("created_at", Now()));
        await WriteAsync(repositoryPath, refName, path, content, root);
    }

    private async Task WriteNodeFileAsync(
        string repositoryPath, GeneratedIdentity node, GeneratedIdentity signer, string? ownerFingerprint = null,
        DeclaredGitHubAccount? github = null, string? displayName = null)
    {
        string refName = $"refs/hall9k/ledger/nodes/{node.NodeId}";
        string path = $"nodes/{node.NodeId}/node.yaml";
        // Mirrors ProjectJoinCommand.WriteNodeFileAsync's own real content: owner_fingerprint
        // defaults to this node's own fingerprint, the exact claim a plain "no --owner" join
        // records for the node establishing its own root — the shape every self-announcing root
        // test below relies on.
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

        await WriteAsync(repositoryPath, refName, path, BuildYaml([.. fields]), signer);
    }

    /// <summary>
    /// Adds a GitHub declaration to a self-owned node file already on the hub, as a self-signed commit
    /// whose committer date is <paramref name="committedAt"/>, which <see cref="ILedger"/> writes cannot set.
    /// </summary>
    private async Task RewriteNodeFileWithDeclarationAsync(
        string repositoryPath, GeneratedIdentity node, DeclaredGitHubAccount github, DateTimeOffset committedAt,
        string? displayName = null)
    {
        string refName = $"refs/hall9k/ledger/nodes/{node.NodeId}";
        string path = $"nodes/{node.NodeId}/node.yaml";
        await RunGitCaptureAsync(repositoryPath, ["fetch", "origin", $"+{refName}:{refName}"]);
        string tip = await RunGitCaptureAsync(repositoryPath, ["rev-parse", "--verify", refName]);
        List<(string Key, string Value)> fields =
        [
            ("node_id", node.NodeId.ToString()),
            ("public_key", node.PublicKeyLine),
            ("owner_fingerprint", node.Fingerprint),
            ("github_login", github.Login),
            ("github_account_id", github.AccountId.ToString(CultureInfo.InvariantCulture)),
        ];
        if (displayName is not null)
        {
            fields.Add(("display_name", displayName));
        }

        string content = BuildYaml([.. fields]);
        string tree = await BuildTreeWithFileAsync(repositoryPath, tip, path, content);
        string commit = await CommitTreeAsync(repositoryPath, tree, [tip], node, "declare GitHub account", committerDate: committedAt);
        await RunGitCaptureAsync(repositoryPath, ["push", "origin", $"{commit}:{refName}"]);
    }

    /// <summary>The display-name sibling of <see cref="RewriteNodeFileWithDeclarationAsync"/>: adds
    /// a display name to a self-owned node file already on the hub, as a self-signed commit whose
    /// committer date is <paramref name="committedAt"/>.</summary>
    private async Task RewriteNodeFileWithDisplayNameAsync(
        string repositoryPath, GeneratedIdentity node, string displayName, DateTimeOffset committedAt)
    {
        string refName = $"refs/hall9k/ledger/nodes/{node.NodeId}";
        string path = $"nodes/{node.NodeId}/node.yaml";
        await RunGitCaptureAsync(repositoryPath, ["fetch", "origin", $"+{refName}:{refName}"]);
        string tip = await RunGitCaptureAsync(repositoryPath, ["rev-parse", "--verify", refName]);
        string content = BuildYaml(
            ("node_id", node.NodeId.ToString()),
            ("public_key", node.PublicKeyLine),
            ("owner_fingerprint", node.Fingerprint),
            ("display_name", displayName));
        string tree = await BuildTreeWithFileAsync(repositoryPath, tip, path, content);
        string commit = await CommitTreeAsync(repositoryPath, tree, [tip], node, "declare display name", committerDate: committedAt);
        await RunGitCaptureAsync(repositoryPath, ["push", "origin", $"{commit}:{refName}"]);
    }

    private async Task WriteMemberFileAsync(
        string repositoryPath, string rootFingerprint, string role, GeneratedIdentity signer, string? projectKey = null)
    {
        string refName = "refs/hall9k/ledger/members";
        string path = $"members/{rootFingerprint}.yaml";
        string content = projectKey is null
            ? BuildYaml(("root_fingerprint", rootFingerprint), ("role", role), ("issued_at", Now()))
            : BuildYaml(
                ("root_fingerprint", rootFingerprint), ("role", role), ("issued_at", Now()), ("project_key", projectKey));
        await WriteAsync(repositoryPath, refName, path, content, signer);
    }

    private async Task VouchAsync(string repositoryPath, string ownerRoot, GeneratedIdentity target, GeneratedIdentity signer)
    {
        string refName = $"refs/hall9k/ledger/owners/{ownerRoot}";
        string path = $"owners/{ownerRoot}/nodes/{target.NodeId}.yaml";
        string content = BuildYaml(
            ("node_id", target.NodeId.ToString()), ("public_key", target.PublicKeyLine), ("issued_at", Now()));
        // Mirrors NodeVouchCommand's own real commit message exactly ("Vouch node {id} key
        // {fingerprint}") — a carried bundle's own check 3 binds its embedded vouch commit to a
        // specific node id AND the fingerprint of the exact key it names by requiring both appear
        // in the commit's own signed message text, so a vouch this scaffolding writes for a
        // carried-record test has to carry it the same way production does.
        await WriteAsync(repositoryPath, refName, path, content, signer, $"Vouch node {target.NodeId} key {target.Fingerprint}");
    }

    private async Task DeleteMemberFileAsync(string repositoryPath, string rootFingerprint, GeneratedIdentity signer)
    {
        string refName = "refs/hall9k/ledger/members";
        string path = $"members/{rootFingerprint}.yaml";
        LedgerFile current = await _ledger.ReadAsync(repositoryPath, refName, path, CancellationToken.None);
        LedgerWriteOutcome outcome = await _ledger.DeleteAsync(
            new LedgerDeleteRequest(
                repositoryPath, refName, path, current.BlobId, "test delete", Committer,
                new LedgerSigningKey(signer.PrivateKeyPath)),
            CancellationToken.None);
        outcome.Verdict.Should().Be(LedgerWriteVerdict.Written, $"test setup deletion of {path} must land");
    }

    private async Task RevokeAsync(string repositoryPath, string ownerRoot, Guid targetNodeId, GeneratedIdentity signer)
    {
        string refName = $"refs/hall9k/ledger/owners/{ownerRoot}";
        string path = $"owners/{ownerRoot}/revoked/{targetNodeId}.yaml";
        string content = BuildYaml(("node_id", targetNodeId.ToString()), ("revoked_at", Now()));
        // Mirrors NodeRevokeCommand's own real commit message exactly ("Revoke node {id}") — so a
        // carried-bundle test that reuses this exact commit as "the vouch" exercises the identical
        // shape a genuine root-signed revocation commit actually has in production, rather than a
        // message that never named the node id at all.
        await WriteAsync(repositoryPath, refName, path, content, signer, $"Revoke node {targetNodeId}");
    }

    /// <summary>Mirrors <c>SuccessionLedgerWriter.WriteSuccessorAsync</c>'s own real content and
    /// path exactly: the node id and the key already vouched for it, signed by whichever key the
    /// caller passes — a root key for the "attaches" cases, a node's own key for the "does not"
    /// one.</summary>
    private async Task WriteSuccessorAsync(string repositoryPath, string ownerRoot, GeneratedIdentity target, GeneratedIdentity signer)
    {
        string refName = $"refs/hall9k/ledger/owners/{ownerRoot}";
        string path = $"owners/{ownerRoot}/successors/{target.NodeId}.yaml";
        string content = BuildYaml(
            ("node_id", target.NodeId.ToString()), ("public_key", target.PublicKeyLine), ("issued_at", Now()));
        await WriteAsync(repositoryPath, refName, path, content, signer, $"Successor {target.NodeId}");
    }

    /// <summary>Mirrors this task's own <c>owners/&lt;root&gt;/rotations/&lt;n&gt;.yaml</c> shape:
    /// the promoting node's own id and key, and the exact key it supersedes — signed by the
    /// promoting node's own key, never a root key, since a rotation is what a listed successor signs
    /// for itself.</summary>
    private async Task WriteRotationAsync(
        string repositoryPath, string ownerRoot, int sequence, GeneratedIdentity promoter, string supersedesPublicKeyLine)
    {
        string refName = $"refs/hall9k/ledger/owners/{ownerRoot}";
        string path = $"owners/{ownerRoot}/rotations/{sequence}.yaml";
        string content = BuildYaml(
            ("node_id", promoter.NodeId.ToString()),
            ("public_key", promoter.PublicKeyLine),
            ("supersedes_public_key", supersedesPublicKeyLine),
            ("issued_at", Now()));
        await WriteAsync(repositoryPath, refName, path, content, promoter, $"Rotate to node {promoter.NodeId}");
    }

    /// <summary>Mirrors <c>SuccessionLedgerWriter.WriteRevokedSuccessorAsync</c>'s own real content
    /// and path exactly.</summary>
    private async Task WriteRevokedSuccessorAsync(string repositoryPath, string ownerRoot, Guid targetNodeId, GeneratedIdentity signer)
    {
        string refName = $"refs/hall9k/ledger/owners/{ownerRoot}";
        string path = $"owners/{ownerRoot}/revoked-successors/{targetNodeId}.yaml";
        string content = BuildYaml(("node_id", targetNodeId.ToString()), ("revoked_at", Now()));
        await WriteAsync(repositoryPath, refName, path, content, signer, $"Revoke successor {targetNodeId}");
    }

    /// <summary>
    /// Reads a signed commit off a SOURCE ledger the same way <c>GitLedgerCommitReader</c> would —
    /// the whole point of the carried-record path being offline: this reads a repository that is
    /// merely local to this test (never a network remote), mirroring what a real carrying node's own
    /// local clone of a source project would answer.
    /// </summary>
    private static async Task<(string Content, string Sha, string RawBytes)> ReadSignedCommitAsync(
        string repositoryPath, string refName, string path)
    {
        string tip = await RunGitCaptureAsync(repositoryPath, ["rev-parse", "--verify", "--quiet", $"{refName}^{{commit}}"]);
        string content = await RunGitCaptureAsync(repositoryPath, ["show", $"{tip}:{path}"]);
        string log = await RunGitCaptureAsync(repositoryPath, ["log", "--format=%H", "--first-parent", tip, "--", path]);
        string sha = log.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        // Never RunGitCaptureAsync's own trimmed output for this one: an SSH signature covers the
        // commit object's exact bytes, so trimming so much as a trailing newline off the raw commit
        // here would make GitLedgerChainReader's own offline re-verification (which re-signs nothing,
        // only reinjects these exact bytes and checks the signature already on them) see different
        // bytes than the ones the source root's own key actually signed, and fail every legitimate
        // bundle this scaffolding builds.
        string raw = await RunGitCaptureRawAsync(repositoryPath, ["cat-file", "commit", sha]);
        return (content, sha, raw);
    }

    /// <summary>The untrimmed twin of <see cref="RunGitCaptureAsync"/> — for the one caller
    /// (<see cref="ReadSignedCommitAsync"/>) that needs the exact bytes a signature was computed
    /// over, not a hash or a tree id where a trailing newline never matters.</summary>
    private static async Task<string> RunGitCaptureRawAsync(string repositoryPath, IReadOnlyList<string> arguments)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(repositoryPath);
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed in {repositoryPath}: {error}");
        }

        return output;
    }

    /// <summary>Mirrors <c>ProjectJoinCommand.BuildCarriedRecordYaml</c> exactly — this test file's
    /// own writer for the bundle <see cref="GitLedgerChainReader"/>'s carried-record verification
    /// reads.</summary>
    private static string BuildCarriedRecordYaml(
        Guid carriedNodeId, string carriedNodePublicKeyLine, Guid sourceProjectId, string sourceOriginUrl,
        string rootContent, string rootSha, string rootRawBytes, string vouchContent, string vouchSha, string vouchRawBytes) =>
        BuildYaml(
            ("node_id", carriedNodeId.ToString()),
            ("node_public_key", carriedNodePublicKeyLine),
            ("source_project_id", sourceProjectId.ToString()),
            ("source_project_key", "01ARZ3NDEKTSV4RRFFQ69G5FAV"),
            ("source_origin_url", sourceOriginUrl),
            ("root_yaml_base64", EncodeBase64(rootContent)),
            ("root_commit_sha", rootSha),
            ("root_commit_base64", EncodeBase64(rootRawBytes)),
            ("vouch_yaml_base64", EncodeBase64(vouchContent)),
            ("vouch_commit_sha", vouchSha),
            ("vouch_commit_base64", EncodeBase64(vouchRawBytes)),
            ("carried_at", Now()));

    private static string EncodeBase64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    /// <summary>Always a fresh commit — read-then-write against whatever blob id is currently
    /// there, never write-if-absent, so a re-vouch after a revocation lands as a new commit even
    /// when its content is unchanged from before.</summary>
    private async Task WriteAsync(
        string repositoryPath, string refName, string path, string content, GeneratedIdentity signer, string message = "test write")
    {
        LedgerFile current = await _ledger.ReadAsync(repositoryPath, refName, path, CancellationToken.None);
        LedgerWriteOutcome outcome = await _ledger.WriteAsync(
            new LedgerWriteRequest(
                repositoryPath, refName, path, content, current.BlobId, message, Committer,
                new LedgerSigningKey(signer.PrivateKeyPath)),
            CancellationToken.None);
        outcome.Verdict.Should().Be(LedgerWriteVerdict.Written, $"test setup write to {path} must land");
    }

    /// <summary>
    /// Builds a new tree over <paramref name="parentTip"/>'s own — adding one file — without ever
    /// moving any ref: the merge-commit test needs a commit that exists purely as a side branch, not
    /// a fresh ref tip, which <c>GitLedger.WriteAsync</c>'s own push-and-commit flow cannot produce
    /// on its own. Mirrors <c>GitLedger.BuildTreeAsync</c>'s own private-index technique exactly.
    /// </summary>
    private static async Task<string> BuildTreeWithFileAsync(string repositoryPath, string parentTip, string path, string content)
    {
        string tempIndex = Path.Combine(Path.GetTempPath(), $"h9k-chain-reader-test-index-{Guid.NewGuid():N}");
        try
        {
            await RunGitCaptureAsync(repositoryPath, ["read-tree", parentTip], indexFile: tempIndex);
            string blobId = await RunGitCaptureAsync(repositoryPath, ["hash-object", "-w", "--stdin"], indexFile: tempIndex, standardInput: content);
            await RunGitCaptureAsync(
                repositoryPath, ["update-index", "--add", "--cacheinfo", $"100644,{blobId},{path}"], indexFile: tempIndex);
            return await RunGitCaptureAsync(repositoryPath, ["write-tree"], indexFile: tempIndex);
        }
        finally
        {
            try
            {
                File.Delete(tempIndex);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp index; nothing downstream reads it again.
            }
        }
    }

    /// <summary>A signed commit over an explicit parent list — the merge-commit test's own way to
    /// build a two-parent commit, which no <see cref="ILedger"/> write ever produces.
    /// <paramref name="committerDate"/>, when given, is what the backdating test needs: a committer
    /// date the writer chooses freely, exactly as an attacker crafting a raw commit with
    /// <c>GIT_COMMITTER_DATE</c> set would.</summary>
    private static async Task<string> CommitTreeAsync(
        string repositoryPath, string treeId, IReadOnlyList<string> parents, GeneratedIdentity signer, string message,
        DateTimeOffset? committerDate = null)
    {
        List<string> arguments =
        [
            "-c", $"user.name={Committer.Name}",
            "-c", $"user.email={Committer.Email}",
            "-c", "gpg.format=ssh",
            "-c", $"user.signingkey={signer.PrivateKeyPath}",
        ];

        arguments.Add("commit-tree");
        arguments.Add(treeId);
        foreach (string parent in parents)
        {
            arguments.Add("-p");
            arguments.Add(parent);
        }

        arguments.Add("-S");
        arguments.Add("-m");
        arguments.Add(message);

        return await RunGitCaptureAsync(repositoryPath, arguments, committerDate: committerDate);
    }

    private static async Task<string> RunGitCaptureAsync(
        string repositoryPath, IReadOnlyList<string> arguments, string? indexFile = null, string? standardInput = null,
        DateTimeOffset? committerDate = null)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
        };
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(repositoryPath);
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (indexFile is not null)
        {
            process.StartInfo.Environment["GIT_INDEX_FILE"] = indexFile;
        }

        if (committerDate is { } date)
        {
            string formatted = date.ToString("o", CultureInfo.InvariantCulture);
            process.StartInfo.Environment["GIT_COMMITTER_DATE"] = formatted;
            process.StartInfo.Environment["GIT_AUTHOR_DATE"] = formatted;
        }

        process.Start();
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }

        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed in {repositoryPath}: {error}");
        }

        return output.Trim();
    }

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

    private GeneratedIdentity GenerateIdentity()
    {
        string keyPath = Path.Combine(Path.GetTempPath(), $"h9k-chain-reader-key-{Guid.NewGuid():N}");
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "ssh-keygen",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.StartInfo.ArgumentList.Add("-t");
        process.StartInfo.ArgumentList.Add("ed25519");
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add(keyPath);
        process.StartInfo.ArgumentList.Add("-N");
        process.StartInfo.ArgumentList.Add(string.Empty);
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add("chain-reader-test");
        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ssh-keygen failed: {output}{error}");
        }

        _generatedKeyPaths.Add(keyPath);
        string publicKeyLine = File.ReadAllText($"{keyPath}.pub").Trim();
        return new GeneratedIdentity(keyPath, publicKeyLine, NodeKeyStore.Fingerprint(publicKeyLine), Guid.NewGuid());
    }
}
