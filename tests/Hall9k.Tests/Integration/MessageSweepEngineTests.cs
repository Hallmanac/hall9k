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
        Guid senderProjectId = DomainId.New();
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = (
            new LedgerCommitter("node-a", "node-a@hall9k.local"), new LedgerSigningKey("/dev/null/node-a"));

        // Node A queues and flushes one project-broadcast envelope directly against the shared
        // transport, standing in for a daemon sweep on node A's own machine — this test cares only
        // about node B's own sweep, reading it. senderProjectId is node A's OWN local project id,
        // used only to scope its own local queue/flush — never compared to node B's own local id
        // for the identical shared project (MessageStreamId's own doc).
        await using (IDocumentSession sendSession = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                sendSession, nodeA, senderProjectId, "owner-a-fingerprint", MessageAudience.Project, about: null,
                MessageKind.Note, "only once", Now, cts.Token);
            await senderOutbox.FlushAsync(
                sendSession, RepositoryPath, nodeA, senderProjectId, "shared-project-key", adoptUnassigned: false,
                committerA, signingKeyA, Now, cts.Token);
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
            new FakeLedgerChainReader(new TrustChain(new Dictionary<string, TrustedOwner>(), [], GenesisRootFingerprint: "shared-project-key")),
            new MessageNodeIdentityResolver(new NodeKeyStore()),
            Options.Create(new DaemonOptions()), NullLogger<MessageSweepEngine>.Instance);

        await engine.SweepOnceAsync(cts.Token);
        transport.ProbeCount.Should().Be(1, "the first sweep always probes once");
        transport.ReadCount.Should().Be(1, "the sender's tip moved, so the first sweep reads it once");

        await using (IDocumentSession verifySession = _postgres.Store.LightweightSession())
        {
            // Filtered to ReceivedAt, never a bare document count: node A's own local queue/flush
            // above and node B's own receive below now land on genuinely distinct local streams
            // (idea 202383dc, M2 — each keyed by its own install's own local project id), so this
            // one shared Postgres schema standing in for two separate nodes' own separate databases
            // (this class's own doc) legitimately holds two rows for the identical envelope, one
            // per side, where the pre-M2 scheme happened to collapse them into one by sharing an
            // identical (sender, seq) stream id.
            int received = await verifySession.Query<MessageDetails>().Where(message => message.ReceivedAt != null).CountAsync(cts.Token);
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
        Guid senderProjectId = DomainId.New();
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = (
            new LedgerCommitter("node-a", "node-a@hall9k.local"), new LedgerSigningKey("/dev/null/node-a"));

        // Node A queues and flushes one project-broadcast envelope directly against the shared
        // transport — the same standing needed to move the tip node B's own probe sees, so this
        // sweep actually computes a trust chain at all (ProbeAndReadAsync only bothers when at
        // least one sender's tip moved).
        await using (IDocumentSession sendSession = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                sendSession, nodeA, senderProjectId, "owner-a-fingerprint", MessageAudience.Project, about: null,
                MessageKind.Note, "only once", Now, cts.Token);
            await senderOutbox.FlushAsync(
                sendSession, RepositoryPath, nodeA, senderProjectId, "shared-project-key", adoptUnassigned: false,
                committerA, signingKeyA, Now, cts.Token);
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

    /// <summary>
    /// Two commits touching the identical (kind, identifier, root) — a revoked node correcting its
    /// own earlier bad commit over the same node id, say — collapse to the same
    /// <see cref="UnverifiedLedgerWriteStreamId"/>. Before the fix, <c>PersistUnverifiedWritesAsync</c>
    /// called <c>AggregateStreamAsync</c> then <c>StartStream</c> per element with no
    /// de-duplication, so two entries sharing a stream id in one tick called <c>StartStream</c>
    /// twice for the same id in the same session and <c>SaveChangesAsync</c> failed the whole
    /// batch — silently dropping every writer that tick was supposed to persist, this one included
    /// (independent pre-PR review, cycle 1, adversarial lens, medium).
    /// </summary>
    [Fact]
    public async Task A_sweep_collapses_two_unverified_writes_sharing_a_stream_id_in_one_tick()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));

        FakeLedger ledger = new();
        InMemoryMessageTransport transport = new(ledger);

        (NodeContext nodeB, Guid projectId) = await SeedOwnerAndProjectAsync(_postgres.Store, "smoke-duplicate-unverified", cts.Token);
        Guid nodeA = DomainId.New();

        UnverifiedLedgerWrite firstCommit = new(
            "vouch", nodeA.ToString(), "owner-a-fingerprint", "commit aaaaaaa is not signed by anyone currently trusted");
        UnverifiedLedgerWrite secondCommit = new(
            "vouch", nodeA.ToString(), "owner-a-fingerprint", "commit bbbbbbb is not signed by anyone currently trusted");
        TrustChain chainWithDuplicateWrites = new(new Dictionary<string, TrustedOwner>(), [], [firstCommit, secondCommit]);

        MessageSweepEngine engine = new(
            _postgres.Store, nodeB, new MessageOutbox(transport), new MessageInbox(transport), transport,
            new FakeLedgerChainReader(chainWithDuplicateWrites), new MessageNodeIdentityResolver(new NodeKeyStore()),
            Options.Create(new DaemonOptions()), NullLogger<MessageSweepEngine>.Instance);

        await engine.SweepOnceAsync(cts.Token);

        await using (IDocumentSession verifySession = _postgres.Store.LightweightSession())
        {
            IReadOnlyList<UnverifiedLedgerWriteDetails> observed =
                await verifySession.Query<UnverifiedLedgerWriteDetails>().ToListAsync(cts.Token);
            observed.Should().ContainSingle(write =>
                write.ProjectId == projectId && write.Kind == "vouch" && write.Identifier == nodeA.ToString());
            observed.Single().Reason.Should().Be(secondCommit.Reason, "the last-observed commit in this tick wins");
        }
    }

    /// <summary>
    /// The identical writer, still there, unchanged, across two ticks close enough together that
    /// neither sits past the refresh age — before the fix this appended a fresh, identical event on
    /// every single tick, forever, for as long as the offending ledger commit sat in the ref
    /// (independent pre-PR review, cycle 1, both lenses, medium).
    /// </summary>
    [Fact]
    public async Task A_second_sweep_with_an_unchanged_unverified_write_does_not_append_another_event()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));

        FakeLedger ledger = new();
        InMemoryMessageTransport transport = new(ledger);

        (NodeContext nodeB, Guid projectId) = await SeedOwnerAndProjectAsync(_postgres.Store, "smoke-repeat-unverified", cts.Token);
        Guid nodeA = DomainId.New();

        UnverifiedLedgerWrite standingWrite = new(
            "vouch", nodeA.ToString(), "owner-a-fingerprint", "commit ccccccc is not signed by anyone currently trusted");
        TrustChain chainWithStandingWrite = new(new Dictionary<string, TrustedOwner>(), [], [standingWrite]);

        MessageSweepEngine engine = new(
            _postgres.Store, nodeB, new MessageOutbox(transport), new MessageInbox(transport), transport,
            new FakeLedgerChainReader(chainWithStandingWrite), new MessageNodeIdentityResolver(new NodeKeyStore()),
            Options.Create(new DaemonOptions()), NullLogger<MessageSweepEngine>.Instance);

        await engine.SweepOnceAsync(cts.Token);
        await engine.SweepOnceAsync(cts.Token);

        Guid streamId = UnverifiedLedgerWriteStreamId.For(projectId, "vouch", nodeA.ToString(), "owner-a-fingerprint");
        await using (IDocumentSession verifySession = _postgres.Store.LightweightSession())
        {
            IReadOnlyList<JasperFx.Events.IEvent> events = await verifySession.Events.FetchStreamAsync(streamId, token: cts.Token);
            events.Should().ContainSingle("the second sweep saw the identical, unchanged writer and must not append again");
        }
    }

    /// <summary>
    /// The resilience fix for the fixed-adopter bug (independent pre-PR review, cycle 1, both
    /// lenses, medium): before the fix, a pending legacy message (idea 202383dc, M2's migration
    /// rule — still carrying <see cref="Guid.Empty"/> as its own ProjectId) was only ever adopted by
    /// <c>eligibleProjects[0]</c>, and only on a tick where that SPECIFIC project's own trust chain
    /// read succeeded — if it never did, the message stayed pending forever even with a second,
    /// perfectly healthy eligible project sitting right behind it. This node registers two eligible
    /// projects, the lower-id one wired to a chain reader that always throws, and proves the legacy
    /// message still flushes — through the SECOND project's own repository, never the first's.
    /// </summary>
    [Fact]
    public async Task A_pending_legacy_message_still_flushes_through_a_second_project_when_the_lowest_id_ones_chain_read_keeps_failing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        const string FailingRepositoryPath = "/does/not/matter/on/a/fake/ledger-failing";
        const string HealthyRepositoryPath = "/does/not/matter/on/a/fake/ledger-healthy";
        const string HealthyProjectKey = "healthy-project-key";

        FakeLedger ledger = new();
        InMemoryMessageTransport transport = new(ledger);

        NodeContext nodeB = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, cts.Token);

        // The healthy project's own repository needs node B's self-announced node file too — the
        // transport's own read-side vouch check (InMemoryMessageTransport.ReadSinceAsync), never
        // required for the flush this test actually cares about, only for this test's own
        // after-the-fact read verifying what landed on the wire.
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                HealthyRepositoryPath, $"refs/hall9k/ledger/nodes/{nodeB.NodeId}", $"nodes/{nodeB.NodeId}/node.yaml",
                $"node_id: \"{nodeB.NodeId}\"\npublic_key: \"ssh-ed25519 AAAAFAKE{nodeB.NodeId:N} test\"\n",
                ExpectedBlobId: null, "seed node file", new LedgerCommitter("seed", "seed@hall9k.local"),
                new LedgerSigningKey("/dev/null/seed")),
            cts.Token);

        Guid failingProjectId = DomainId.New();
        Guid healthyProjectId = DomainId.New();
        await using (IDocumentSession claimSession = _postgres.Store.LightweightSession())
        {
            OwnerAggregate owner =
                (await claimSession.Events.AggregateStreamAsync<OwnerAggregate>(nodeB.OwnerId, token: cts.Token))!;
            claimSession.Events.Append(
                nodeB.OwnerId, OwnerDecider.ClaimRoot(owner, "owner-b-root-fingerprint", verified: true, Now));

            // Registered in this order so failingProjectId (UUIDv7, minted first) sorts lower —
            // exactly the "lowest eligible project id" LegacyMessageAdoption and the old, fixed
            // eligibleProjects[0] rule both pick by.
            claimSession.Events.StartStream<ProjectAggregate>(
                failingProjectId,
                ProjectDecider.Register(
                    failingProjectId, nodeB.OwnerId, DomainId.New(), "failing", FailingRepositoryPath, null, null, Now));
            claimSession.Events.StartStream<ProjectAggregate>(
                healthyProjectId,
                ProjectDecider.Register(
                    healthyProjectId, nodeB.OwnerId, DomainId.New(), "healthy", HealthyRepositoryPath, null, null, Now));

            // The pending legacy message: node B's own outbox item queued before idea 202383dc's M2
            // shipped, still carrying Guid.Empty as its own ProjectId (MessageQueued's own doc).
            claimSession.Events.StartStream<MessageAggregate>(
                MessageStreamId.ForMessage(nodeB.NodeId, Guid.Empty, 1),
                new MessageQueued(
                    nodeB.NodeId, 1, "owner-b-root-fingerprint", MessageAudience.Project.Value, null, MessageKind.Note.Value,
                    "legacy pending on node B", Now, Guid.Empty));

            await claimSession.SaveChangesAsync(cts.Token);
        }

        MessageSweepEngine engine = new(
            _postgres.Store, nodeB, new MessageOutbox(transport), new MessageInbox(transport), transport,
            new AlwaysThrowingForOneRepositoryChainReader(FailingRepositoryPath, HealthyRepositoryPath, HealthyProjectKey),
            new MessageNodeIdentityResolver(new NodeKeyStore()), Options.Create(new DaemonOptions()),
            NullLogger<MessageSweepEngine>.Instance);

        await engine.SweepOnceAsync(cts.Token);

        await using (IDocumentSession verifySession = _postgres.Store.LightweightSession())
        {
            MessageDetails? legacy = await verifySession.LoadAsync<MessageDetails>(
                MessageStreamId.ForMessage(nodeB.NodeId, Guid.Empty, 1), cts.Token);
            legacy!.SentAt.Should().NotBeNull("the healthy project's own flush must adopt it even though the lowest-id project's chain read keeps failing");
            legacy.ProjectId.Should().Be(healthyProjectId, "the healthy project is the one that actually adopted it");

            // Independent pre-PR review, cycle 2, verify pass (medium, low): before this fix,
            // nothing durably recorded which project actually claimed the adoption, so
            // LegacyMessageAdoption.IsAdoptingProjectAsync kept recomputing the static "lowest
            // eligible project id" guess forever, permanently disagreeing with this sweep's own
            // real, dynamic choice for as long as the lowest-id project's chain read kept
            // failing. This is the persisted fact that closes that gap.
            LegacyMessageAdoptionDetails? adoption = await verifySession.LoadAsync<LegacyMessageAdoptionDetails>(
                MessageStreamId.ForLegacyAdoption(), cts.Token);
            adoption.Should().NotBeNull("the sweep's own successful adopting flush must permanently record who the real adopter is");
            adoption!.ProjectId.Should().Be(
                healthyProjectId,
                "the persisted decision must name the project that actually adopted, never the lowest-id project the static guess alone would have named");
        }

        TransportReadResult fromHealthy = await transport.ReadSinceAsync(HealthyRepositoryPath, nodeB.NodeId, sinceSeq: 0, cts.Token);
        fromHealthy.Envelopes.Should().ContainSingle(envelope => envelope.Content.Contains("legacy pending on node B"));
    }

    /// <summary>An <see cref="ILedgerChainReader"/> that throws for one specific repository path
    /// (standing in for a chain read that genuinely keeps failing, not merely an absent genesis) and
    /// returns a real, usable <see cref="TrustChain"/> for another.</summary>
    private sealed class AlwaysThrowingForOneRepositoryChainReader(
        string failingRepositoryPath, string healthyRepositoryPath, string healthyProjectKey) : ILedgerChainReader
    {
        public Task<TrustChain> ComputeAsync(string repositoryPath, CancellationToken cancellationToken) =>
            repositoryPath == failingRepositoryPath
                ? throw new InvalidOperationException("Simulated: this project's own trust chain read never succeeds.")
                : repositoryPath == healthyRepositoryPath
                    ? Task.FromResult(new TrustChain(new Dictionary<string, TrustedOwner>(), [], GenesisRootFingerprint: healthyProjectKey))
                    : Task.FromResult(TrustChain.Empty);
    }

    /// <summary>Shared setup <see cref="A_sweep_persists_a_dropped_unverifiable_vouch_its_trust_chain_observed"/>
    /// also inlines: a fresh node claims an owner root and registers one project, so a
    /// <see cref="MessageSweepEngine"/> built against it has somewhere to persist an unverified
    /// write.</summary>
    private static async Task<(NodeContext NodeB, Guid ProjectId)> SeedOwnerAndProjectAsync(
        IDocumentStore store, string projectName, CancellationToken cancellationToken)
    {
        NodeContext nodeB = await NodeBootstrapSeed.NewNodeAsync(store, cancellationToken);
        Guid projectId = DomainId.New();
        await using (IDocumentSession claimSession = store.LightweightSession())
        {
            OwnerAggregate owner =
                (await claimSession.Events.AggregateStreamAsync<OwnerAggregate>(nodeB.OwnerId, token: cancellationToken))!;
            claimSession.Events.Append(
                nodeB.OwnerId, OwnerDecider.ClaimRoot(owner, "owner-b-root-fingerprint", verified: true, Now));

            claimSession.Events.StartStream<ProjectAggregate>(
                projectId,
                ProjectDecider.Register(
                    projectId, nodeB.OwnerId, DomainId.New(), projectName, RepositoryPath, null, null, Now));
            await claimSession.SaveChangesAsync(cancellationToken);
        }

        return (nodeB, projectId);
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
