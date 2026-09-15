using FluentAssertions;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Trust;
using Hall9k.Daemon;
using Hall9k.Daemon.Messaging;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Trust;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <see cref="MessageSweepEngine.SweepOnceAsync"/> itself, through the real seam
/// (<see cref="InMemoryMessageTransport"/> over a real Marten/Postgres session, per Brian's
/// 2026-09-13 testing rule), never only the pure helpers <c>Hall9k.Tests.Daemon.MessageSweepEngineTests</c>
/// already covers: independent pre-PR review, cycle 1, conformance lens found that the probe's own
/// skip decision (idea 202383dc, M1b) was proven only against a hand-built dictionary, never against
/// a sweep that has actually read a sender once. <see cref="CountingMessageTransport"/> counts every
/// <see cref="IMessageTransport.ReadSinceAsync"/> call so this test can assert the second sweep never
/// calls it again for a sender whose tip has not moved, the same guarantee <see
/// cref="MessageSweepEngine.SendersToRead"/>'s own doc promises.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection("Hall9kHome")]
public sealed class MessageSweepEngineTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string RepositoryPath = "/does/not/matter/on/a/fake/ledger";
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"hall9k-message-sweep-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    public MessageSweepEngineTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", _home);
        await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();
    }

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", _previousHome);
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_second_sweep_never_rereads_a_sender_whose_tip_has_not_moved()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        CountingMessageTransport transport = new(new InMemoryMessageTransport(ledger));
        MessageOutbox senderOutbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = (
            new LedgerCommitter("node-a", "node-a@hall9k.local"), new LedgerSigningKey("/dev/null/node-a"));

        // Node A queues and flushes one project-broadcast envelope directly against the shared
        // transport, standing in for a daemon sweep on node A's own machine — this test cares only
        // about node B's own sweep, reading it.
        await using (IDocumentSession sendSession = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                sendSession, nodeA, "owner-a-fingerprint", MessageAudience.Project, about: null, MessageKind.Note,
                "only once", Now, cts.Token);
            await senderOutbox.FlushAsync(sendSession, RepositoryPath, nodeA, committerA, signingKeyA, Now, cts.Token);
        }

        NodeContext nodeB = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, cts.Token);
        await using (IDocumentSession claimSession = _postgres.Store.LightweightSession())
        {
            OwnerAggregate owner =
                (await claimSession.Events.AggregateStreamAsync<OwnerAggregate>(nodeB.OwnerId, token: cts.Token))!;
            claimSession.Events.Append(
                nodeB.OwnerId, OwnerDecider.ClaimRoot(owner, "owner-b-root-fingerprint", verified: true, Now));

            Guid projectId = DomainId.New();
            claimSession.Events.StartStream<ProjectAggregate>(
                projectId,
                ProjectDecider.Register(
                    projectId, nodeB.OwnerId, DomainId.New(), "smoke", RepositoryPath, null, null, Now));
            await claimSession.SaveChangesAsync(cts.Token);
        }

        MessageSweepEngine engine = new(
            _postgres.Store, nodeB, new MessageOutbox(transport), new MessageInbox(transport), transport,
            new FakeLedgerChainReader(TrustChain.Empty), new MessageNodeIdentityResolver(new NodeKeyStore()),
            Options.Create(new DaemonOptions()), NullLogger<MessageSweepEngine>.Instance);

        await engine.SweepOnceAsync(cts.Token);
        transport.ProbeCount.Should().Be(1, "the first sweep always probes once");
        transport.ReadCount.Should().Be(1, "the sender's tip moved, so the first sweep reads it once");

        await using (IDocumentSession verifySession = _postgres.Store.LightweightSession())
        {
            int received = await verifySession.Query<MessageDetails>().CountAsync(cts.Token);
            received.Should().Be(1, "the first sweep actually stored node A's envelope, not merely probed it");
        }

        await engine.SweepOnceAsync(cts.Token);
        transport.ProbeCount.Should().Be(2, "the second sweep still probes every tick");
        transport.ReadCount.Should().Be(
            1, "node A's tip has not moved since the first sweep cached it, so the second sweep must skip the read");
    }

    /// <summary>
    /// The bounded fix for the human resolution ruled 2026-09-15: this sweep already computes the
    /// trust chain once per tick (commit bab68199) — this proves a dropped, unverifiable vouch that
    /// chain read observes is actually persisted (<see cref="MessageSweepEngine"/>'s own
    /// <c>PersistUnverifiedWritesAsync</c>) as the standing record <c>h9k status</c> reads instead
    /// of ever walking the ledger itself.
    /// </summary>
    [Fact]
    public async Task A_sweep_persists_a_dropped_unverifiable_vouch_its_trust_chain_observed()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        MessageOutbox senderOutbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = (
            new LedgerCommitter("node-a", "node-a@hall9k.local"), new LedgerSigningKey("/dev/null/node-a"));

        // Node A queues and flushes one project-broadcast envelope directly against the shared
        // transport — the same standing needed to move the tip node B's own probe sees, so this
        // sweep actually computes a trust chain at all (ProbeAndReadAsync only bothers when at
        // least one sender's tip moved).
        await using (IDocumentSession sendSession = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                sendSession, nodeA, "owner-a-fingerprint", MessageAudience.Project, about: null, MessageKind.Note,
                "only once", Now, cts.Token);
            await senderOutbox.FlushAsync(sendSession, RepositoryPath, nodeA, committerA, signingKeyA, Now, cts.Token);
        }

        NodeContext nodeB = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, cts.Token);
        Guid projectId = DomainId.New();
        await using (IDocumentSession claimSession = _postgres.Store.LightweightSession())
        {
            OwnerAggregate owner =
                (await claimSession.Events.AggregateStreamAsync<OwnerAggregate>(nodeB.OwnerId, token: cts.Token))!;
            claimSession.Events.Append(
                nodeB.OwnerId, OwnerDecider.ClaimRoot(owner, "owner-b-root-fingerprint", verified: true, Now));

            claimSession.Events.StartStream<ProjectAggregate>(
                projectId,
                ProjectDecider.Register(
                    projectId, nodeB.OwnerId, DomainId.New(), "smoke-unverified", RepositoryPath, null, null, Now));
            await claimSession.SaveChangesAsync(cts.Token);
        }

        // Stands in for a chain read that found node A's own vouch signed by nobody this project
        // currently trusts — the exact shape GitLedgerChainReader.ComputeAsync itself would return
        // for a stranger's self-consistent, but unverifiable, node file (Hall9k.Connectors.Trust
        // .UnverifiedLedgerWrite's own doc: "recorded here rather than silently dropped").
        UnverifiedLedgerWrite droppedVouch = new(
            "vouch", nodeA.ToString(), "owner-a-fingerprint",
            "commit deadbeef is not signed by root owner-a-fingerprint or any node currently enrolled in it");
        TrustChain chainWithDroppedVouch = new(new Dictionary<string, TrustedOwner>(), [], [droppedVouch]);

        MessageSweepEngine engine = new(
            _postgres.Store, nodeB, new MessageOutbox(transport), new MessageInbox(transport), transport,
            new FakeLedgerChainReader(chainWithDroppedVouch), new MessageNodeIdentityResolver(new NodeKeyStore()),
            Options.Create(new DaemonOptions()), NullLogger<MessageSweepEngine>.Instance);

        await engine.SweepOnceAsync(cts.Token);

        await using (IDocumentSession verifySession = _postgres.Store.LightweightSession())
        {
            IReadOnlyList<UnverifiedLedgerWriteDetails> observed =
                await verifySession.Query<UnverifiedLedgerWriteDetails>().ToListAsync(cts.Token);
            observed.Should().ContainSingle(write =>
                write.ProjectId == projectId
                && write.Kind == "vouch"
                && write.Identifier == nodeA.ToString()
                && write.RootFingerprint == "owner-a-fingerprint"
                && write.Reason == droppedVouch.Reason);
        }
    }

    private static async Task SeedNodeFileAsync(FakeLedger ledger, Guid nodeId, CancellationToken cancellationToken)
    {
        LedgerCommitter committer = new("seed", "seed@hall9k.local");
        LedgerSigningKey signingKey = new("/dev/null/seed");
        string content = $"node_id: \"{nodeId}\"\npublic_key: \"ssh-ed25519 AAAAFAKE{nodeId:N} test\"\n";
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, $"refs/hall9k/ledger/nodes/{nodeId}", $"nodes/{nodeId}/node.yaml", content,
                ExpectedBlobId: null, "seed node file", committer, signingKey),
            cancellationToken);
    }

    /// <summary>Counts <see cref="IMessageTransport.ProbeAsync"/> and
    /// <see cref="IMessageTransport.ReadSinceAsync"/> calls against a real
    /// <see cref="InMemoryMessageTransport"/>, so a test can prove a second sweep skipped a read
    /// rather than merely inferring it from projection state that a skipped read and a read that
    /// found nothing new both leave identical.</summary>
    private sealed class CountingMessageTransport(IMessageTransport inner) : IMessageTransport
    {
        public int ProbeCount { get; private set; }
        public int ReadCount { get; private set; }

        public Task SendAsync(
            string repositoryPath, Guid fromNodeId, long seq, string content, LedgerCommitter committer,
            LedgerSigningKey signingKey, CancellationToken cancellationToken) =>
            inner.SendAsync(repositoryPath, fromNodeId, seq, content, committer, signingKey, cancellationToken);

        public Task FlushAsync(
            string repositoryPath, Guid fromNodeId, IReadOnlyList<TransportEnvelope> envelopes,
            LedgerCommitter committer, LedgerSigningKey signingKey, CancellationToken cancellationToken) =>
            inner.FlushAsync(repositoryPath, fromNodeId, envelopes, committer, signingKey, cancellationToken);

        public Task<TransportReadResult> ReadSinceAsync(
            string repositoryPath, Guid senderNodeId, long sinceSeq, CancellationToken cancellationToken,
            TrustChain? trustChain = null)
        {
            ReadCount++;
            return inner.ReadSinceAsync(repositoryPath, senderNodeId, sinceSeq, cancellationToken, trustChain);
        }

        public Task<IReadOnlyList<MessageOutboxTip>> ProbeAsync(string repositoryPath, CancellationToken cancellationToken)
        {
            ProbeCount++;
            return inner.ProbeAsync(repositoryPath, cancellationToken);
        }

        public Task SquashAsync(
            string repositoryPath, Guid fromNodeId, IReadOnlyList<TransportEnvelope> survivors,
            LedgerCommitter committer, LedgerSigningKey signingKey, CancellationToken cancellationToken) =>
            inner.SquashAsync(repositoryPath, fromNodeId, survivors, committer, signingKey, cancellationToken);
    }
}
