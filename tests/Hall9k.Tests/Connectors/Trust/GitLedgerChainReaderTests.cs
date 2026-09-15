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
