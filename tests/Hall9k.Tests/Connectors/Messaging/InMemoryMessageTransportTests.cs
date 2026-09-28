using FluentAssertions;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Connectors.Messaging;

/// <summary>
/// <see cref="InMemoryMessageTransport.ReadSinceAsync"/>'s own chain-level gate
/// (<c>InMemoryMessageTransport.cs:179</c>), driven with a <see cref="FakeLedgerChainReader"/> and a
/// <see cref="FakeLedger"/> alone — no git repository, no Docker (Brian's 2026-09-13 testing rule).
/// Idea 6be68ee2, trust-ledger finding 7: a node file that repeats the root's own public key under a
/// DIFFERENT node id must never vouch that sender, since <see cref="TrustedOwner.ContainsForNode"/>
/// binds the root's own key to <see cref="TrustedOwner.RootNodeId"/> specifically.
/// </summary>
public sealed class InMemoryMessageTransportTests
{
    private const string RepositoryPath = "/does/not/matter/on/a/fake/ledger";
    private const string RootPublicKeyLine = "ssh-ed25519 AAAAFAKEROOT root";

    // The real fingerprint InMemoryMessageTransport.ReadSinceAsync itself computes off the node
    // file's own public_key line — a hand-picked literal would never match it, and the chain-level
    // gate under test would then refuse every sender for the wrong reason (no fingerprint in the
    // chain at all) rather than the one this test actually means to prove.
    private static readonly string RootFingerprintValue = NodeKeyStore.Fingerprint(RootPublicKeyLine);

    private static async Task SeedNodeFileAsync(FakeLedger ledger, Guid nodeId, string publicKeyLine, CancellationToken cancellationToken)
    {
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, $"refs/hall9k/ledger/nodes/{nodeId}", $"nodes/{nodeId}/node.yaml",
                $"node_id: \"{nodeId}\"\npublic_key: \"{publicKeyLine}\"\n",
                ExpectedBlobId: null, "seed node file", new LedgerCommitter("seed", "seed@hall9k.local"),
                new LedgerSigningKey("/dev/null/seed")),
            cancellationToken);
    }

    [Fact]
    public async Task ReadSinceAsync_RefusesASenderWhoseNodeFileRepeatsTheRootsOwnKeyUnderAForgedNodeId()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        Guid rootNodeId = DomainId.New();
        Guid forgedNodeId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, rootNodeId, RootPublicKeyLine, cts.Token);
        // The forgery: a second node file, under a node id the root never claimed, declaring the
        // exact same public key line as the root's own — so it fingerprints identically.
        await SeedNodeFileAsync(ledger, forgedNodeId, RootPublicKeyLine, cts.Token);

        TrustedOwner owner = new(RootFingerprintValue, RootPublicKeyLine, [], RootNodeId: rootNodeId.ToString());
        TrustChain trustChain = new(
            new Dictionary<string, TrustedOwner> { [RootFingerprintValue] = owner },
            [new ProjectMember(RootFingerprintValue, MembershipRole.Owner, DateTimeOffset.UnixEpoch)]);
        FakeLedgerChainReader chainReader = new(trustChain);

        InMemoryMessageTransport transport = new(ledger, chainReader);

        TransportReadResult result = await transport.ReadSinceAsync(RepositoryPath, forgedNodeId, sinceSeq: 0, cts.Token, trustChain);

        result.SenderVouched.Should().BeFalse(
            "the root's own key answers only for the root's own node id, never for a second node that merely declares the same key");
    }

    [Fact]
    public async Task ReadSinceAsync_VouchesTheRootsOwnNodeIdForTheIdenticalKey()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        Guid rootNodeId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, rootNodeId, RootPublicKeyLine, cts.Token);

        TrustedOwner owner = new(RootFingerprintValue, RootPublicKeyLine, [], RootNodeId: rootNodeId.ToString());
        TrustChain trustChain = new(
            new Dictionary<string, TrustedOwner> { [RootFingerprintValue] = owner },
            [new ProjectMember(RootFingerprintValue, MembershipRole.Owner, DateTimeOffset.UnixEpoch)]);
        FakeLedgerChainReader chainReader = new(trustChain);

        InMemoryMessageTransport transport = new(ledger, chainReader);

        TransportReadResult result = await transport.ReadSinceAsync(RepositoryPath, rootNodeId, sinceSeq: 0, cts.Token, trustChain);

        result.SenderVouched.Should().BeTrue("this is the root's own established device, the one node id ContainsForNode grants its key");
    }
}
