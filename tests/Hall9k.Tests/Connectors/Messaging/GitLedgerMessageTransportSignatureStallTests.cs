using System.Diagnostics;
using FluentAssertions;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Connectors.Ledger;
using Hall9k.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Connectors.Messaging;

/// <summary>
/// <see cref="GitLedgerMessageTransport.ReadSinceAsync"/>'s own rejected-signature stall (idea
/// 6be68ee2, trust finding 12): a commit that introduces a candidate seq but fails signature
/// verification is treated exactly like an unreachable gap, never skipped past. This is the one
/// behavior in that class that needs a real repository and a real signature to exercise honestly —
/// <see cref="InMemoryMessageTransport"/> never performs signature verification at all — so this
/// file uses <see cref="LedgerTestRepo"/>, the sanctioned bare-hub helper (Brian's 2026-09-13
/// testing rule, extended here the same way <c>GitLedgerTests</c> already is): a plain hub and one
/// node clone, no branch, no GitHub.
/// </summary>
[Collection("RealProcessSpawn")]
[Trait("Category", "RealProcessSpawn")]
public sealed class GitLedgerMessageTransportSignatureStallTests : IDisposable
{
    private readonly LedgerTestRepo _repo = new();
    private readonly GitLedger _ledger = new(NullLogger<GitLedger>.Instance);
    private readonly GitLedgerMessageTransport _transport;
    private readonly LedgerCommitter _committer = new("sender-node", "sender-node@hall9k.local");
    private readonly string _signingKeyPath;
    private readonly LedgerSigningKey _signingKey;
    private readonly string _publicKeyLine;
    private readonly string _repositoryPath;
    private readonly Guid _senderNodeId = DomainId.New();
    private readonly TrustChain _trustChain;

    public GitLedgerMessageTransportSignatureStallTests()
    {
        (_signingKeyPath, _publicKeyLine) = GenerateSshKeypair();
        _signingKey = new LedgerSigningKey(_signingKeyPath);
        _transport = new GitLedgerMessageTransport(_ledger, new FakeLedgerChainReader(TrustChain.Empty));

        string hub = _repo.CreateHub();
        _repositoryPath = _repo.CloneNode(hub);

        string nodeFileContent = $"node_id: \"{_senderNodeId}\"\npublic_key: \"{_publicKeyLine}\"\n";
        _ledger.WriteAsync(
            new LedgerWriteRequest(
                _repositoryPath, $"refs/hall9k/ledger/nodes/{_senderNodeId}", $"nodes/{_senderNodeId}/node.yaml",
                nodeFileContent, ExpectedBlobId: null, "seed node file", _committer, _signingKey),
            CancellationToken.None).GetAwaiter().GetResult();

        string senderFingerprint = NodeKeyStore.Fingerprint(_publicKeyLine);
        _trustChain = new TrustChain(
            new Dictionary<string, TrustedOwner>
            {
                ["forge-test-owner"] = new TrustedOwner(
                    "forge-test-owner", "ssh-ed25519 AAAAFAKE owner-root",
                    [new TrustedNode(_senderNodeId.ToString(), _publicKeyLine, senderFingerprint, DateTimeOffset.UtcNow)]),
            },
            [new ProjectMember("forge-test-owner", MembershipRole.Owner, DateTimeOffset.UtcNow)]);
    }

    public void Dispose()
    {
        _repo.Dispose();
        File.Delete(_signingKeyPath);
        File.Delete($"{_signingKeyPath}.pub");
    }

    [Fact]
    public async Task A_planted_forged_next_seq_stalls_the_read_then_releases_once_the_senders_own_flush_overwrites_it()
    {
        string refName = $"refs/hall9k/messages/{_senderNodeId}";

        await _transport.FlushAsync(
            _repositoryPath, _senderNodeId, [new TransportEnvelope(1, "genuine envelope 1")], _committer, _signingKey,
            CancellationToken.None);

        string tipAfterFlush = ResolveLocalRef(refName);
        string forgedCommit = PlantUnsignedCommit(
            tipAfterFlush, new Dictionary<string, string> { ["messages/2.json"] = "forged envelope 2" });
        Push(refName, forgedCommit);

        TransportReadResult stalled = await _transport.ReadSinceAsync(
            _repositoryPath, _senderNodeId, sinceSeq: 0, CancellationToken.None, _trustChain);

        stalled.SenderVouched.Should().BeTrue();
        stalled.Envelopes.Should().ContainSingle(envelope => envelope.Seq == 1 && envelope.Content == "genuine envelope 1");
        stalled.RejectedSeqs.Should().ContainSingle().Which.Should().Be(2);
        stalled.StalledAtSeq.Should().Be(2, "a rejected signature is treated exactly like an unreachable gap");
        stalled.HighestSeqInspected.Should().Be(1, "the cursor must never advance past the rejected seq");

        // The sender's own next, genuinely signed flush overwrites the forged path — its own
        // FetchRefAsync pulls the forged tip down first, then legitimately re-introduces
        // messages/2.json in a new, correctly signed commit on top of it.
        await _transport.FlushAsync(
            _repositoryPath, _senderNodeId, [new TransportEnvelope(2, "genuine envelope 2")], _committer, _signingKey,
            CancellationToken.None);

        TransportReadResult released = await _transport.ReadSinceAsync(
            _repositoryPath, _senderNodeId, sinceSeq: 0, CancellationToken.None, _trustChain);

        released.RejectedSeqs.Should().BeEmpty();
        released.StalledAtSeq.Should().BeNull();
        released.HighestSeqInspected.Should().Be(2);
        released.Envelopes.Select(envelope => (envelope.Seq, envelope.Content)).Should().BeEquivalentTo(
        [
            (1L, "genuine envelope 1"),
            (2L, "genuine envelope 2"),
        ]);
    }

    [Fact]
    public async Task A_tampered_past_seq_stalls_the_read_then_releases_at_the_senders_next_retention_squash()
    {
        string refName = $"refs/hall9k/messages/{_senderNodeId}";

        await _transport.FlushAsync(
            _repositoryPath, _senderNodeId,
            [new TransportEnvelope(1, "genuine envelope 1"), new TransportEnvelope(2, "genuine envelope 2")],
            _committer, _signingKey, CancellationToken.None);

        string tipAfterFlush = ResolveLocalRef(refName);
        string forgedCommit = PlantUnsignedCommit(
            tipAfterFlush, new Dictionary<string, string> { ["messages/1.json"] = "tampered envelope 1" });
        Push(refName, forgedCommit);

        TransportReadResult stalled = await _transport.ReadSinceAsync(
            _repositoryPath, _senderNodeId, sinceSeq: 0, CancellationToken.None, _trustChain);

        stalled.Envelopes.Should().BeEmpty("seq 1 stalls the whole read, so seq 2 behind it is never reached either");
        stalled.RejectedSeqs.Should().ContainSingle().Which.Should().Be(1);
        stalled.StalledAtSeq.Should().Be(1);

        // The sender's own next retention squash rewrites the outbox to hold only what still
        // survives (seq 2), with a low-water mark that tells a reader the range below it was
        // deliberately pruned rather than forged — releasing the stall without ever having to
        // repair the tampered commit itself.
        await _transport.SquashAsync(
            _repositoryPath, _senderNodeId, [new TransportEnvelope(2, "genuine envelope 2")], lowWaterMark: 2,
            _committer, _signingKey, CancellationToken.None);

        TransportReadResult released = await _transport.ReadSinceAsync(
            _repositoryPath, _senderNodeId, sinceSeq: 0, CancellationToken.None, _trustChain);

        released.RejectedSeqs.Should().BeEmpty();
        released.StalledAtSeq.Should().BeNull();
        released.PrunedBelowSeq.Should().Be(1, "the squash's own low-water mark pruned everything below seq 2");
        released.Envelopes.Should().ContainSingle(envelope => envelope.Seq == 2 && envelope.Content == "genuine envelope 2");
    }

    private string ResolveLocalRef(string refName)
    {
        (int exitCode, string output, string error) =
            LedgerTestRepo.RunGit(_repositoryPath, "rev-parse", "--verify", "--quiet", $"{refName}^{{commit}}");
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"git rev-parse {refName} failed: {error}");
        }

        return output.Trim();
    }

    /// <summary>Builds an UNSIGNED commit directly against the node clone's own default index —
    /// safe here since nothing else in this single-threaded test touches it concurrently — seeded
    /// from <paramref name="parentTip"/>'s own tree and overwriting exactly the paths named in
    /// <paramref name="pathContents"/>, the same shape a forger with push access but no signing key
    /// could produce. Never pushed by this method: the caller decides when.</summary>
    private string PlantUnsignedCommit(string parentTip, IReadOnlyDictionary<string, string> pathContents)
    {
        RunGitOrThrow("read-tree", parentTip);

        foreach ((string path, string content) in pathContents)
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"h9k-forge-blob-{Guid.NewGuid():N}");
            File.WriteAllText(tempFile, content);
            try
            {
                string blobId = RunGitOrThrow("hash-object", "-w", tempFile).Trim();
                RunGitOrThrow("update-index", "--add", "--cacheinfo", $"100644,{blobId},{path}");
            }
            finally
            {
                File.Delete(tempFile);
            }
        }

        string treeId = RunGitOrThrow("write-tree").Trim();
        string commitId = RunGitOrThrow(
            "-c", "user.name=forger", "-c", "user.email=forger@hall9k.local",
            "commit-tree", treeId, "-p", parentTip, "-m", "forged, deliberately unsigned").Trim();
        return commitId;
    }

    private void Push(string refName, string commitId) => RunGitOrThrow("push", "origin", $"{commitId}:{refName}");

    private string RunGitOrThrow(params string[] arguments)
    {
        (int exitCode, string output, string error) = LedgerTestRepo.RunGit(_repositoryPath, arguments);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {output}{error}");
        }

        return output;
    }

    private static (string PrivateKeyPath, string PublicKey) GenerateSshKeypair()
    {
        string keyPath = Path.Combine(Path.GetTempPath(), $"h9k-message-transport-signing-key-{Guid.NewGuid():N}");
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
        process.StartInfo.ArgumentList.Add("message-transport-test");
        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ssh-keygen failed: {output}{error}");
        }

        return (keyPath, File.ReadAllText($"{keyPath}.pub").Trim());
    }
}
