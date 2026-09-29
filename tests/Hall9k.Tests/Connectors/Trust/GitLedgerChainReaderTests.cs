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
/// <para>
/// This is deliberately the SMALL half of a split (host-gate-slowdown-2026-09-28): the trust rules
/// themselves — vouch/revoke ordering, succession rank, carried-record structural gating,
/// membership authorization, the fixed-point loop — are exercised far faster in
/// <see cref="GitLedgerChainReaderFastTests"/>, against a hand-built in-memory commit graph with no
/// real git process and no real SSH key. What stays here is only what that fake cannot prove: real
/// commit signature verification (<c>git verify-commit</c> against a real SSH signature, including
/// the forged-committer-email injection defense), a carried bundle's real embedded-commit byte
/// round trip (<c>git hash-object</c> injection and re-verification), a real ref rewind refusal
/// (<see cref="LedgerAppendOnlyRefFetcher"/>'s own local-verified-tip reconciliation), and genesis
/// selection against a real ref's own commit order. Every other scenario this class used to cover
/// is ported to <see cref="GitLedgerChainReaderFastTests"/> instead — see that class' own tests for
/// the identical assertions this class no longer makes.
/// </para>
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
        // (GitPlumbingLedgerCommitAccess.AllowedSignersPrincipal) closes this: the commit was never
        // actually signed by root's own key, so self-certification must fail.
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
    public async Task A_vouch_introduced_purely_via_a_merge_commits_second_parent_is_still_applied()
    {
        // The exact gap the independent pre-PR review reproduced live against the pre-fix
        // ChangedPathsAsync (cycle 2, conformance lens, medium): `git diff-tree` with no
        // `-m`/`-c`/`--cc`/`--diff-merges` flag names no paths at all for a merge commit, so once
        // CommitsOldestFirstAsync started replaying `--topo-order --first-parent` (this same task's
        // own cycle-1 fix), a merge commit whose first parent is the ref's own prior tip legitimately
        // entered the walk — but every path it introduced purely via its second parent vanished from
        // ComputeOwnerChainAsync's view: neither applied to the chain nor recorded as unverified.
        // This is a real-git-plumbing scenario (GitPlumbingLedgerCommitAccess.ChangedPathsAsync's own
        // `--diff-merges=first-parent` flag), not a trust rule, so it stays end-to-end rather than
        // moving to the fake-graph suite, which never models a multi-parent commit at all.
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
        // A real proof that CommitsOldestFirstAsync's own git flags (`--topo-order --first-parent
        // --reverse`) actually deliver commits in the order the genesis-is-the-first-commit rule
        // assumes — the fast suite exhaustively covers the rule itself given a declared order.
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
        string repositoryPath, GeneratedIdentity node, GeneratedIdentity signer, string? ownerFingerprint = null)
    {
        string refName = $"refs/hall9k/ledger/nodes/{node.NodeId}";
        string path = $"nodes/{node.NodeId}/node.yaml";
        // Mirrors ProjectJoinCommand.WriteNodeFileAsync's own real content: owner_fingerprint
        // defaults to this node's own fingerprint, the exact claim a plain "no --owner" join
        // records for the node establishing its own root — the shape every self-announcing root
        // test below relies on.
        string content = BuildYaml(
            ("node_id", node.NodeId.ToString()),
            ("public_key", node.PublicKeyLine),
            ("owner_fingerprint", ownerFingerprint ?? node.Fingerprint));

        await WriteAsync(repositoryPath, refName, path, content, signer);
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
