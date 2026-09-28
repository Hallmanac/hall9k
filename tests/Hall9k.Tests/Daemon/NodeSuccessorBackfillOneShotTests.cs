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
/// <see cref="NodeSuccessorBackfillOneShot"/> against a <see cref="FakeLedger"/> and a hand-built
/// <see cref="TrustChain"/> — no Postgres needed, since (unlike its sibling
/// <see cref="NodeGitHubDeclarationOneShot"/>) this one-shot takes the trust chain
/// <see cref="MessageSweepEngine"/> already computed rather than reading one itself, so this stays a
/// DB-free unit test (Brian's 2026-09-13 testing rule).
/// </summary>
public sealed class NodeSuccessorBackfillOneShotTests
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

        NodeSuccessorBackfillOneShot oneShot = new(ledger, NullLogger<NodeSuccessorBackfillOneShot>.Instance);
        await oneShot.RunOnceAsync(Project(), RootIdentity(), trustChain, CancellationToken.None);

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

        NodeSuccessorBackfillOneShot oneShot = new(ledger, NullLogger<NodeSuccessorBackfillOneShot>.Instance);
        await oneShot.RunOnceAsync(Project(), fleetNodeIdentity, trustChain, CancellationToken.None);

        ledger.Writes.Should().BeEmpty("only a live root key holder ever has anything here to backfill");
    }

    [Fact]
    public async Task An_already_existing_successor_record_is_not_rewritten()
    {
        FakeLedger ledger = new();
        Guid fleetNodeId = DomainId.New();
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, $"refs/hall9k/ledger/owners/{RootFingerprint}", $"owners/{RootFingerprint}/successors/{fleetNodeId}.yaml",
                "node_id: \"already-here\"\n", null, "seed", Committer, RootSigningKey),
            CancellationToken.None);
        int writesBefore = ledger.Writes.Count;

        TrustChain trustChain = ChainWithOneFleetNode(fleetNodeId);
        NodeSuccessorBackfillOneShot oneShot = new(ledger, NullLogger<NodeSuccessorBackfillOneShot>.Instance);
        await oneShot.RunOnceAsync(Project(), RootIdentity(), trustChain, CancellationToken.None);

        ledger.Writes.Should().HaveCount(writesBefore, "a successor record already there is left alone");
    }

    [Fact]
    public async Task A_second_call_for_the_same_project_does_nothing_even_when_a_gap_still_exists()
    {
        FakeLedger ledger = new();
        Guid fleetNodeId = DomainId.New();
        TrustChain trustChain = ChainWithOneFleetNode(fleetNodeId);
        ProjectDetails project = Project();

        NodeSuccessorBackfillOneShot oneShot = new(ledger, NullLogger<NodeSuccessorBackfillOneShot>.Instance);
        await oneShot.RunOnceAsync(project, RootIdentity(), trustChain, CancellationToken.None);
        int writesAfterFirstCall = ledger.Writes.Count;

        await oneShot.RunOnceAsync(project, RootIdentity(), trustChain, CancellationToken.None);

        ledger.Writes.Should().HaveCount(writesAfterFirstCall, "once per process and project, the identical hook NodeGitHubDeclarationOneShot already follows");
    }

    private static TrustChain ChainWithOneFleetNode(Guid fleetNodeId)
    {
        TrustedOwner owner = new(
            RootFingerprint, RootPublicKeyLine,
            [new TrustedNode(fleetNodeId.ToString(), FleetPublicKeyLine, "fleet-node-fingerprint", DateTimeOffset.UtcNow)]);
        return new TrustChain(new Dictionary<string, TrustedOwner> { [RootFingerprint] = owner }, []);
    }

    private static MessageNodeIdentity RootIdentity() => new(RootFingerprint, Committer, RootSigningKey, RootPublicKeyLine);

    private static ProjectDetails Project() => new() { Id = DomainId.New(), RepositoryPath = RepositoryPath };
}
