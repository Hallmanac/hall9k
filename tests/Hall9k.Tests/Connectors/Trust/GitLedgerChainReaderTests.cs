using System.Diagnostics;
using System.Globalization;
using System.Text;
using FluentAssertions;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Trust;
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
/// </summary>
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
    public async Task A_revocation_voids_the_revoked_nodes_earlier_membership_writes()
    {
        // The walked model (team half, 2026-09-13; ruled by the window, 2026-09-13): a members-ref
        // write is authorized against the owner chain's own live state at read time, never a
        // snapshot pinned to that write's own claimed committer date. Accepted consequence: once a
        // node is revoked, a membership write it made earlier — even while it was still legitimately
        // enrolled — is no longer authorized on the next read.
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);

        GeneratedIdentity laptop = GenerateIdentity();
        string laptopRepo = _repo.CloneNode(hub);
        await WriteNodeFileAsync(laptopRepo, laptop, laptop);
        await VouchAsync(ownerRepo, owner.Fingerprint, laptop, owner);

        // The laptop, while still enrolled, adds bob as a member.
        GeneratedIdentity bob = GenerateIdentity();
        await WriteMemberFileAsync(ownerRepo, bob.Fingerprint, "member", laptop);

        string readerBeforeRevoke = _repo.CloneNode(hub);
        (await _chainReader.ComputeAsync(readerBeforeRevoke, CancellationToken.None))
            .RoleOf(bob.Fingerprint).Should().Be(MembershipRole.Member, "the laptop was enrolled when it signed this write");

        // The laptop is revoked afterward.
        await RevokeAsync(ownerRepo, owner.Fingerprint, laptop.NodeId, owner);

        string readerAfterRevoke = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerAfterRevoke, CancellationToken.None);

        chain.RoleOf(bob.Fingerprint).Should().BeNull(
            "the laptop's revocation voids every membership write it ever signed, including this "
            + "earlier one made while it was still enrolled");
        chain.IsAllowedSigner(laptop.Fingerprint).Should().BeFalse("the laptop is revoked as of this read");
    }

    [Fact]
    public async Task A_re_vouch_restores_a_revoked_nodes_earlier_membership_writes()
    {
        // The other half of the same rule: since a membership write is authorized against the
        // owner chain's own live state at read time, restoring the node to that live state (a
        // re-vouch) restores every membership write it ever signed, exactly the latest-of-vouch-or-
        // revocation rule the owner chain itself already applies to enrollment.
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);

        GeneratedIdentity laptop = GenerateIdentity();
        string laptopRepo = _repo.CloneNode(hub);
        await WriteNodeFileAsync(laptopRepo, laptop, laptop);
        await VouchAsync(ownerRepo, owner.Fingerprint, laptop, owner);

        GeneratedIdentity bob = GenerateIdentity();
        await WriteMemberFileAsync(ownerRepo, bob.Fingerprint, "member", laptop);

        await RevokeAsync(ownerRepo, owner.Fingerprint, laptop.NodeId, owner);

        string readerAfterRevoke = _repo.CloneNode(hub);
        (await _chainReader.ComputeAsync(readerAfterRevoke, CancellationToken.None))
            .RoleOf(bob.Fingerprint).Should().BeNull("the laptop is revoked");

        // The laptop is vouched again — a surviving node undoing a bad revocation.
        await VouchAsync(ownerRepo, owner.Fingerprint, laptop, owner);

        string readerAfterReVouch = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerAfterReVouch, CancellationToken.None);

        chain.RoleOf(bob.Fingerprint).Should().Be(
            MembershipRole.Member, "the re-vouch restores the laptop to the owner chain's live state, "
            + "which restores every membership write it ever signed, this one included");
        chain.IsAllowedSigner(laptop.Fingerprint).Should().BeTrue("the laptop is enrolled again as of this read");
    }

    [Fact]
    public async Task A_revoked_nodes_backdated_committer_date_cannot_authorize_a_membership_write()
    {
        // The adversarial lens's own injection (cycle 1, both high) against the pre-fix
        // owner.AsOf(commitTime): a revoked node crafts a members-ref commit and backdates
        // GIT_COMMITTER_DATE to before its own revocation, hoping to be judged against the chain as
        // it stood at that claimed date rather than as it stands now. The fix (ruled by the window,
        // 2026-09-13) removed committer-date pinning from the authorization rule entirely, so a
        // members-ref write is judged solely against the owner chain's own live state — a claimed
        // date, true or forged, is never consulted at all, and this test proves backdating buys the
        // revoked node nothing.
        string hub = _repo.CreateHub();
        (string ownerRepo, GeneratedIdentity owner) = await EstablishGenesisRootAsync(hub);

        GeneratedIdentity laptop = GenerateIdentity();
        string laptopRepo = _repo.CloneNode(hub);
        await WriteNodeFileAsync(laptopRepo, laptop, laptop);
        await VouchAsync(ownerRepo, owner.Fingerprint, laptop, owner);
        await RevokeAsync(ownerRepo, owner.Fingerprint, laptop.NodeId, owner);

        string membersRefName = "refs/hall9k/ledger/members";
        await RunGitCaptureAsync(ownerRepo, ["fetch", "origin", $"+{membersRefName}:{membersRefName}"]);
        string membersTip = await RunGitCaptureAsync(ownerRepo, ["rev-parse", "--verify", membersRefName]);

        // The laptop, still holding its own key after revocation, crafts a members-ref commit,
        // self-claiming owner role, and backdates it to well before its own revocation above.
        GeneratedIdentity attacker = GenerateIdentity();
        string attackerPath = $"members/{attacker.Fingerprint}.yaml";
        string attackerContent = BuildYaml(("root_fingerprint", attacker.Fingerprint), ("role", "owner"), ("issued_at", Now()));
        string maliciousTree = await BuildTreeWithFileAsync(ownerRepo, membersTip, attackerPath, attackerContent);
        string maliciousCommit = await CommitTreeAsync(
            ownerRepo, maliciousTree, [membersTip], laptop, "self-promote via backdated commit",
            committerDate: DateTimeOffset.UtcNow.AddMinutes(-30));
        await RunGitCaptureAsync(ownerRepo, ["push", "origin", $"{maliciousCommit}:{membersRefName}"]);

        string readerRepo = _repo.CloneNode(hub);
        TrustChain chain = await _chainReader.ComputeAsync(readerRepo, CancellationToken.None);

        chain.RoleOf(attacker.Fingerprint).Should().BeNull(
            "the laptop is revoked, and a write from its own key can no longer be authorized "
            + "by claiming an earlier committer date");
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

    // ---- test scaffolding -------------------------------------------------------------------

    private sealed record GeneratedIdentity(string PrivateKeyPath, string PublicKeyLine, string Fingerprint, Guid NodeId);

    private static readonly LedgerCommitter Committer = new("Chain Reader Test", "chain-reader-test@hall9k.local");

    private async Task<(string RepositoryPath, GeneratedIdentity Owner)> EstablishGenesisRootAsync(string hub)
    {
        GeneratedIdentity owner = GenerateIdentity();
        string repo = _repo.CloneNode(hub);
        await WriteRootFileAsync(repo, owner);
        await WriteMemberFileAsync(repo, owner.Fingerprint, "owner", owner);
        return (repo, owner);
    }

    private async Task WriteRootFileAsync(string repositoryPath, GeneratedIdentity root)
    {
        string refName = $"refs/hall9k/ledger/owners/{root.Fingerprint}";
        string path = $"owners/{root.Fingerprint}/root.yaml";
        string content = BuildYaml(("public_key", root.PublicKeyLine), ("created_at", Now()));
        await WriteAsync(repositoryPath, refName, path, content, root);
    }

    private async Task WriteNodeFileAsync(string repositoryPath, GeneratedIdentity node, GeneratedIdentity signer)
    {
        string refName = $"refs/hall9k/ledger/nodes/{node.NodeId}";
        string path = $"nodes/{node.NodeId}/node.yaml";
        string content = BuildYaml(("node_id", node.NodeId.ToString()), ("public_key", node.PublicKeyLine));
        await WriteAsync(repositoryPath, refName, path, content, signer);
    }

    private async Task WriteMemberFileAsync(string repositoryPath, string rootFingerprint, string role, GeneratedIdentity signer)
    {
        string refName = "refs/hall9k/ledger/members";
        string path = $"members/{rootFingerprint}.yaml";
        string content = BuildYaml(("root_fingerprint", rootFingerprint), ("role", role), ("issued_at", Now()));
        await WriteAsync(repositoryPath, refName, path, content, signer);
    }

    private async Task VouchAsync(string repositoryPath, string ownerRoot, GeneratedIdentity target, GeneratedIdentity signer)
    {
        string refName = $"refs/hall9k/ledger/owners/{ownerRoot}";
        string path = $"owners/{ownerRoot}/nodes/{target.NodeId}.yaml";
        string content = BuildYaml(
            ("node_id", target.NodeId.ToString()), ("public_key", target.PublicKeyLine), ("issued_at", Now()));
        await WriteAsync(repositoryPath, refName, path, content, signer);
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
        await WriteAsync(repositoryPath, refName, path, content, signer);
    }

    /// <summary>Always a fresh commit — read-then-write against whatever blob id is currently
    /// there, never write-if-absent, so a re-vouch after a revocation lands as a new commit even
    /// when its content is unchanged from before.</summary>
    private async Task WriteAsync(
        string repositoryPath, string refName, string path, string content, GeneratedIdentity signer)
    {
        LedgerFile current = await _ledger.ReadAsync(repositoryPath, refName, path, CancellationToken.None);
        LedgerWriteOutcome outcome = await _ledger.WriteAsync(
            new LedgerWriteRequest(
                repositoryPath, refName, path, content, current.BlobId, "test write", Committer,
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
