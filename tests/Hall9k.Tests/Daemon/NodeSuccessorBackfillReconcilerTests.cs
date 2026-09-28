using FluentAssertions;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Trust;
using Hall9k.Daemon.Messaging;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// <see cref="NodeSuccessorBackfillReconciler"/> against a <see cref="FakeLedger"/> and a hand-built
/// <see cref="TrustChain"/> — no Postgres needed, since (unlike its sibling
/// <see cref="NodeGitHubDeclarationOneShot"/>) this reconciler takes the trust chain
/// <see cref="MessageSweepEngine"/> already computed rather than reading one itself, so this stays a
/// DB-free unit test (Brian's 2026-09-13 testing rule).
/// </summary>
public sealed class NodeSuccessorBackfillReconcilerTests
{
    private const string RepositoryPath = "/does/not/matter/on/a/fake/ledger";
    private static readonly LedgerCommitter Committer = new("Test", "test@hall9k.local");
    private const string RootPublicKeyLine = "ssh-ed25519 AAAAAAAA root";
    private const string FleetPublicKeyLine = "ssh-ed25519 BBBBBBBB fleet";
    private static readonly string RootFingerprint = NodeKeyStore.Fingerprint(RootPublicKeyLine);
    private static readonly LedgerSigningKey RootSigningKey = new("/keys/root");

    [Fact]
    public async Task A_root_key_holder_backfills_a_missing_successor_record_for_an_already_vouched_node()
    {
        FakeLedger ledger = new();
        Guid fleetNodeId = DomainId.New();
        TrustChain trustChain = ChainWithOneFleetNode(fleetNodeId);

        NodeSuccessorBackfillReconciler reconciler = new(ledger, NullLogger<NodeSuccessorBackfillReconciler>.Instance);
        await reconciler.ReconcileAsync(Project(), RootIdentity(), trustChain, CancellationToken.None);

        LedgerWriteRequest write = ledger.Writes.Should().ContainSingle().Subject;
        write.RefName.Should().Be($"refs/hall9k/ledger/owners/{RootFingerprint}");
        write.Path.Should().Be($"owners/{RootFingerprint}/successors/{fleetNodeId}.yaml");
        write.Content.Should().Contain(FleetPublicKeyLine);
        write.SigningKey.Should().Be(RootSigningKey, "the backfill is signed by this root node's own key");
    }

    [Fact]
    public async Task A_node_whose_own_key_is_not_a_live_root_key_writes_nothing()
    {
        FakeLedger ledger = new();
        Guid fleetNodeId = DomainId.New();
        TrustChain trustChain = ChainWithOneFleetNode(fleetNodeId);

        // This node's own identity carries the FLEET node's key, never the root's own.
        MessageNodeIdentity fleetNodeIdentity = new(RootFingerprint, Committer, new LedgerSigningKey("/keys/fleet"), FleetPublicKeyLine);

        NodeSuccessorBackfillReconciler reconciler = new(ledger, NullLogger<NodeSuccessorBackfillReconciler>.Instance);
        await reconciler.ReconcileAsync(Project(), fleetNodeIdentity, trustChain, CancellationToken.None);

        ledger.Writes.Should().BeEmpty("only a live root key holder ever has anything here to backfill");
    }

    [Fact]
    public async Task A_node_already_counting_as_a_successor_is_not_rewritten()
    {
        // Gated on the trust chain's own SuccessorNodeIds, not a raw ledger read of the file's own
        // existence: a node whose only successor record was signed by someone other than a live
        // root key has a file that exists but never counts, and the old existence-only check left it
        // stuck that way forever (independent pre-PR review, cycle 1, both lenses, medium).
        FakeLedger ledger = new();
        Guid fleetNodeId = DomainId.New();
        TrustChain trustChain = ChainWithOneFleetNode(fleetNodeId, successorNodeIds: [fleetNodeId.ToString()]);

        NodeSuccessorBackfillReconciler reconciler = new(ledger, NullLogger<NodeSuccessorBackfillReconciler>.Instance);
        await reconciler.ReconcileAsync(Project(), RootIdentity(), trustChain, CancellationToken.None);

        ledger.Writes.Should().BeEmpty("a successor record that already counts on read is left alone");
    }

    [Fact]
    public async Task A_node_already_rotated_in_as_a_root_key_is_not_rewritten()
    {
        // A rotated-in node is no longer a listed successor candidate (rotation removes it from
        // SuccessorNodeIds the moment it lands) but still must not be backfilled again — it counts
        // through RootKeys instead.
        FakeLedger ledger = new();
        Guid fleetNodeId = DomainId.New();
        TrustedOwner owner = new(
            RootFingerprint, RootPublicKeyLine,
            [new TrustedNode(fleetNodeId.ToString(), FleetPublicKeyLine, "fleet-node-fingerprint", DateTimeOffset.UtcNow)],
            RootKeys: [new LiveRootKey(RootPublicKeyLine, RootFingerprint, null), new LiveRootKey(FleetPublicKeyLine, "fleet-node-fingerprint", fleetNodeId.ToString())]);
        TrustChain trustChain = new(new Dictionary<string, TrustedOwner> { [RootFingerprint] = owner }, []);

        NodeSuccessorBackfillReconciler reconciler = new(ledger, NullLogger<NodeSuccessorBackfillReconciler>.Instance);
        await reconciler.ReconcileAsync(Project(), RootIdentity(), trustChain, CancellationToken.None);

        ledger.Writes.Should().BeEmpty("a node already ranked as a live root key has nothing left to backfill");
    }

    [Fact]
    public async Task A_second_tick_with_a_chain_now_showing_the_record_counting_writes_nothing_further()
    {
        // Production always hands this reconciler a freshly computed chain each tick, so the second
        // call here uses one reflecting the first call's own write — the same self-healing shape
        // MessageSweepEngine's own per-tick call already provides.
        FakeLedger ledger = new();
        Guid fleetNodeId = DomainId.New();
        ProjectDetails project = Project();

        NodeSuccessorBackfillReconciler reconciler = new(ledger, NullLogger<NodeSuccessorBackfillReconciler>.Instance);
        await reconciler.ReconcileAsync(project, RootIdentity(), ChainWithOneFleetNode(fleetNodeId), CancellationToken.None);
        int writesAfterFirstTick = ledger.Writes.Count;

        TrustChain healedChain = ChainWithOneFleetNode(fleetNodeId, successorNodeIds: [fleetNodeId.ToString()]);
        await reconciler.ReconcileAsync(project, RootIdentity(), healedChain, CancellationToken.None);

        ledger.Writes.Should().HaveCount(writesAfterFirstTick, "the gap the first tick just filled no longer counts as missing");
    }

    private static TrustChain ChainWithOneFleetNode(Guid fleetNodeId, IReadOnlyList<string>? successorNodeIds = null)
    {
        TrustedOwner owner = new(
            RootFingerprint, RootPublicKeyLine,
            [new TrustedNode(fleetNodeId.ToString(), FleetPublicKeyLine, "fleet-node-fingerprint", DateTimeOffset.UtcNow)],
            SuccessorNodeIds: successorNodeIds);
        return new TrustChain(new Dictionary<string, TrustedOwner> { [RootFingerprint] = owner }, []);
    }

    private static MessageNodeIdentity RootIdentity() => new(RootFingerprint, Committer, RootSigningKey, RootPublicKeyLine);

    private static ProjectDetails Project() => new() { Id = DomainId.New(), RepositoryPath = RepositoryPath };
}
