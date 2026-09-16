using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// Idea 202383dc, M2: a node registered to several projects sends and reads each project's own
/// messages through that project's own repository. <see cref="MessageTransportTests"/> already
/// covers the single-project shape in full (one repository, one local project, one sender, one
/// receiver); this file is where a second, genuinely distinct project actually interacts with the
/// first — two repositories, two local project ids — through <see cref="InMemoryMessageTransport"/>
/// and <see cref="FakeLedger"/>, never a real repository (Brian's 2026-09-13 testing rule).
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class MessageProjectScopingTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string RepositoryX = "/repo-x";
    private const string RepositoryY = "/repo-y";
    private const string ProjectKeyX = "genesis-fingerprint-x";
    private const string ProjectKeyY = "genesis-fingerprint-y";
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;

    public MessageProjectScopingTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_sender_in_two_projects_lands_each_message_only_in_its_own_repository()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        Guid projectX = DomainId.New();
        Guid projectY = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, RepositoryX, cts.Token);
        await SeedNodeFileAsync(ledger, nodeA, RepositoryY, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        MessageOutbox outbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        await MessageOutbox.QueueAsync(
            session, nodeA, projectX, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note,
            "for project X", Now, cts.Token);
        await MessageOutbox.QueueAsync(
            session, nodeA, projectY, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note,
            "for project Y", Now.AddSeconds(1), cts.Token);

        await outbox.FlushAsync(
            session, RepositoryX, nodeA, projectX, ProjectKeyX, adoptUnassigned: false, committerA, signingKeyA,
            Now, cts.Token);
        await outbox.FlushAsync(
            session, RepositoryY, nodeA, projectY, ProjectKeyY, adoptUnassigned: false, committerA, signingKeyA,
            Now.AddSeconds(1), cts.Token);

        TransportReadResult fromX = await transport.ReadSinceAsync(RepositoryX, nodeA, sinceSeq: 0, cts.Token);
        TransportReadResult fromY = await transport.ReadSinceAsync(RepositoryY, nodeA, sinceSeq: 0, cts.Token);

        fromX.Envelopes.Should().ContainSingle(envelope => envelope.Content.Contains("for project X"));
        fromX.Envelopes.Should().NotContain(envelope => envelope.Content.Contains("for project Y"));
        fromY.Envelopes.Should().ContainSingle(envelope => envelope.Content.Contains("for project Y"));
        fromY.Envelopes.Should().NotContain(envelope => envelope.Content.Contains("for project X"));

        // Each project's own outbox ref keeps its own contiguous sequence starting at 1 — a global
        // counter shared across every project would instead have landed "for project Y" at seq 2.
        fromX.Envelopes.Single().Seq.Should().Be(1);
        fromY.Envelopes.Single().Seq.Should().Be(1);
    }

    [Fact]
    public async Task A_receiver_in_both_projects_stores_each_under_a_distinct_local_stream()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        Guid projectX = DomainId.New();
        Guid projectY = DomainId.New();
        const string ownerB = "owner-b-fingerprint";

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, RepositoryX, cts.Token);
        await SeedNodeFileAsync(ledger, nodeA, RepositoryY, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        MessageOutbox outbox = new(transport);
        MessageInbox inbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");

        await using IDocumentSession sendSession = _postgres.Store.LightweightSession();
        await MessageOutbox.QueueAsync(
            sendSession, nodeA, projectX, "owner-a-fingerprint", MessageAudience.Project, null, MessageKind.Note,
            "broadcast to X", Now, cts.Token);
        await MessageOutbox.QueueAsync(
            sendSession, nodeA, projectY, "owner-a-fingerprint", MessageAudience.Project, null, MessageKind.Note,
            "broadcast to Y", Now.AddSeconds(1), cts.Token);
        await outbox.FlushAsync(
            sendSession, RepositoryX, nodeA, projectX, ProjectKeyX, adoptUnassigned: false, committerA, signingKeyA,
            Now, cts.Token);
        await outbox.FlushAsync(
            sendSession, RepositoryY, nodeA, projectY, ProjectKeyY, adoptUnassigned: false, committerA, signingKeyA,
            Now.AddSeconds(1), cts.Token);

        // Node B's own two local project ids for these same two shared repositories — distinct
        // Guids from node A's own (projectX, projectY) above, exactly as two separate installs
        // registering the same repository each mint their own.
        Guid localProjectXAtB = DomainId.New();
        Guid localProjectYAtB = DomainId.New();

        await using IDocumentSession readSession = _postgres.Store.LightweightSession();
        MessageInboxSweepResult fromX = await inbox.ReadFromAsync(
            readSession, RepositoryX, nodeA, localProjectXAtB, nodeB, ownerB, Now.AddSeconds(2), cancellationToken: cts.Token);
        MessageInboxSweepResult fromY = await inbox.ReadFromAsync(
            readSession, RepositoryY, nodeA, localProjectYAtB, nodeB, ownerB, Now.AddSeconds(3), cancellationToken: cts.Token);

        fromX.EnvelopesStored.Should().Be(1);
        fromY.EnvelopesStored.Should().Be(1);

        MessageDetails? storedUnderX = await readSession.LoadAsync<MessageDetails>(
            MessageStreamId.ForMessage(nodeA, localProjectXAtB, 1), cts.Token);
        MessageDetails? storedUnderY = await readSession.LoadAsync<MessageDetails>(
            MessageStreamId.ForMessage(nodeA, localProjectYAtB, 1), cts.Token);

        storedUnderX.Should().NotBeNull();
        storedUnderY.Should().NotBeNull();
        storedUnderX!.Id.Should().NotBe(storedUnderY!.Id, "each project's own copy lands on its own distinct local stream");
        storedUnderX.Body.Should().Be("broadcast to X");
        storedUnderY.Body.Should().Be("broadcast to Y");
        storedUnderX.ProjectId.Should().Be(localProjectXAtB);
        storedUnderY.ProjectId.Should().Be(localProjectYAtB);
    }

    [Fact]
    public async Task A_receiver_in_one_project_never_sees_a_message_sent_only_to_the_other()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        Guid projectX = DomainId.New();
        const string ownerB = "owner-b-fingerprint";

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, RepositoryX, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        MessageOutbox outbox = new(transport);
        MessageInbox inbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");

        // Node A sends only into project X's own repository — project Y's repository never
        // receives anything from this sender at all.
        await using IDocumentSession sendSession = _postgres.Store.LightweightSession();
        await MessageOutbox.QueueAsync(
            sendSession, nodeA, projectX, "owner-a-fingerprint", MessageAudience.Project, null, MessageKind.Note,
            "only for X", Now, cts.Token);
        await outbox.FlushAsync(
            sendSession, RepositoryX, nodeA, projectX, ProjectKeyX, adoptUnassigned: false, committerA, signingKeyA,
            Now, cts.Token);

        Guid localProjectYAtB = DomainId.New();
        await using IDocumentSession readSession = _postgres.Store.LightweightSession();
        MessageInboxSweepResult fromY = await inbox.ReadFromAsync(
            readSession, RepositoryY, nodeA, localProjectYAtB, nodeB, ownerB, Now.AddSeconds(1), cancellationToken: cts.Token);

        // Node A has never even announced a node file under project Y's own repository here
        // (SeedNodeFileAsync only wrote it against the shared FakeLedger's own default repository
        // key), so this read is refused as not vouched — the honest outcome for a sender this
        // project has no reason to trust at all, and either way nothing from project X ever leaks
        // into project Y's own view of this sender.
        fromY.EnvelopesStored.Should().Be(0, "project Y's own repository never received anything from this sender");
    }

    [Fact]
    public async Task A_flush_failure_in_one_project_leaves_the_other_projects_pending_untouched()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        Guid projectX = DomainId.New();
        Guid projectY = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, RepositoryX, cts.Token);
        await SeedNodeFileAsync(ledger, nodeA, RepositoryY, cts.Token);
        SelectivelyFailingTransport transport = new(new InMemoryMessageTransport(ledger), failingRepositoryPath: RepositoryX);
        MessageOutbox outbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        await MessageOutbox.QueueAsync(
            session, nodeA, projectX, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note,
            "doomed", Now, cts.Token);
        await MessageOutbox.QueueAsync(
            session, nodeA, projectY, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note,
            "healthy", Now.AddSeconds(1), cts.Token);

        Func<Task> flushX = () => outbox.FlushAsync(
            session, RepositoryX, nodeA, projectX, ProjectKeyX, adoptUnassigned: false, committerA, signingKeyA,
            Now, cts.Token);
        await flushX.Should().ThrowAsync<LedgerPushRejectedException>();

        MessageFlushResult flushY = await outbox.FlushAsync(
            session, RepositoryY, nodeA, projectY, ProjectKeyY, adoptUnassigned: false, committerA, signingKeyA,
            Now.AddSeconds(1), cts.Token);
        flushY.EnvelopesFlushed.Should().Be(1, "project Y's own flush never touches project X's own failure");

        MessageDetails? doomed = await session.LoadAsync<MessageDetails>(
            MessageStreamId.ForMessage(nodeA, projectX, 1), cts.Token);
        MessageDetails? healthy = await session.LoadAsync<MessageDetails>(
            MessageStreamId.ForMessage(nodeA, projectY, 1), cts.Token);

        doomed!.SendFailed.Should().BeTrue();
        doomed.SentAt.Should().BeNull("project X's own push failed, so this message stays pending for the next sweep");
        healthy!.SentAt.Should().NotBeNull("project Y's own flush succeeded independently of project X's failure");
    }

    [Fact]
    public async Task A_read_in_one_project_leaves_the_other_projects_cursor_for_the_same_sender_unmoved()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        Guid projectX = DomainId.New();
        Guid projectY = DomainId.New();
        const string ownerB = "owner-b-fingerprint";

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, RepositoryX, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        MessageOutbox outbox = new(transport);
        MessageInbox inbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");

        await using IDocumentSession sendSession = _postgres.Store.LightweightSession();
        await MessageOutbox.QueueAsync(
            sendSession, nodeA, projectX, "owner-a-fingerprint", MessageAudience.Project, null, MessageKind.Note,
            "for X", Now, cts.Token);
        await MessageOutbox.QueueAsync(
            sendSession, nodeA, projectY, "owner-a-fingerprint", MessageAudience.Project, null, MessageKind.Note,
            "for Y", Now.AddSeconds(1), cts.Token);
        await outbox.FlushAsync(
            sendSession, RepositoryX, nodeA, projectX, ProjectKeyX, adoptUnassigned: false, committerA, signingKeyA,
            Now, cts.Token);
        await outbox.FlushAsync(
            sendSession, RepositoryY, nodeA, projectY, ProjectKeyY, adoptUnassigned: false, committerA, signingKeyA,
            Now.AddSeconds(1), cts.Token);

        Guid localProjectXAtB = DomainId.New();
        Guid localProjectYAtB = DomainId.New();

        // Only project X's own copy is ever read.
        await using IDocumentSession readSession = _postgres.Store.LightweightSession();
        await inbox.ReadFromAsync(
            readSession, RepositoryX, nodeA, localProjectXAtB, nodeB, ownerB, Now.AddSeconds(2), cancellationToken: cts.Token);

        MessageInboxDetails? cursorX = await readSession.LoadAsync<MessageInboxDetails>(
            MessageStreamId.ForInbox(nodeA, localProjectXAtB), cts.Token);
        MessageInboxDetails? cursorY = await readSession.LoadAsync<MessageInboxDetails>(
            MessageStreamId.ForInbox(nodeA, localProjectYAtB), cts.Token);

        cursorX.Should().NotBeNull();
        cursorX!.HighestSeqReceived.Should().Be(1, "project X's own read genuinely advanced its own cursor for this sender");
        cursorY.Should().BeNull(
            "project Y's own cursor for the identical sender was never touched by project X's own read");
    }

    [Fact]
    public async Task Two_installs_with_different_local_project_ids_stamp_the_identical_wire_project_key()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        const string sharedRepository = "/shared-repo";
        const string ledgerDerivedKey = "shared-genesis-fingerprint";

        // Two different installs' own local project ids for what is really the identical shared
        // project — a fresh, uncorrelated Guid each, the way two nodes independently running
        // "h9k project add" against the same repository actually behave.
        Guid localProjectIdAtInstallOne = DomainId.New();
        Guid localProjectIdAtInstallTwo = DomainId.New();
        localProjectIdAtInstallOne.Should().NotBe(localProjectIdAtInstallTwo);

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, sharedRepository, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        MessageOutbox outbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        await MessageOutbox.QueueAsync(
            session, nodeA, localProjectIdAtInstallOne, "owner-a-fingerprint", MessageAudience.Node(nodeB), null,
            MessageKind.Note, "from install one", Now, cts.Token);
        await outbox.FlushAsync(
            session, sharedRepository, nodeA, localProjectIdAtInstallOne, ledgerDerivedKey, adoptUnassigned: false,
            committerA, signingKeyA, Now, cts.Token);

        // A different node acting as "install two" for the identical repository, using its own,
        // different local project id — but the identical ledger-derived key, since both installs
        // compute it from the same shared repository's own ledger (TrustChain.GenesisRootFingerprint).
        Guid nodeC = DomainId.New();
        await SeedNodeFileAsync(ledger, nodeC, sharedRepository, cts.Token);
        (LedgerCommitter committerC, LedgerSigningKey signingKeyC) = Signing("node-c");
        await MessageOutbox.QueueAsync(
            session, nodeC, localProjectIdAtInstallTwo, "owner-c-fingerprint", MessageAudience.Node(nodeB), null,
            MessageKind.Note, "from install two", Now.AddSeconds(1), cts.Token);
        await outbox.FlushAsync(
            session, sharedRepository, nodeC, localProjectIdAtInstallTwo, ledgerDerivedKey, adoptUnassigned: false,
            committerC, signingKeyC, Now.AddSeconds(1), cts.Token);

        TransportReadResult fromA = await transport.ReadSinceAsync(sharedRepository, nodeA, sinceSeq: 0, cts.Token);
        TransportReadResult fromC = await transport.ReadSinceAsync(sharedRepository, nodeC, sinceSeq: 0, cts.Token);

        MessageEnvelopeCodec.DecodeResult decodedA = MessageEnvelopeCodec.Decode(fromA.Envelopes.Single().Content);
        MessageEnvelopeCodec.DecodeResult decodedC = MessageEnvelopeCodec.Decode(fromC.Envelopes.Single().Content);

        decodedA.Envelope!.ProjectKey.Should().Be(ledgerDerivedKey);
        decodedC.Envelope!.ProjectKey.Should().Be(ledgerDerivedKey);
        decodedA.Envelope.ProjectKey.Should().Be(
            decodedC.Envelope.ProjectKey,
            "the wire project key is identical on every node sharing the repository, whatever each install's own local project id happens to be");
    }

    [Fact]
    public async Task MessageSendCommand_WithNoEligibleProjectsRegistered_RefusesRatherThanQueueingUnscoped()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await NodeBootstrapSeed.SeedGitHubConnectionAsync(_postgres.Store, cts.Token);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        await NodeBootstrap.EnsureAsync(session, cts.Token);
        await session.SaveChangesAsync(cts.Token);

        MessageSendCommand.Settings settings = new() { Text = "hello", To = "project" };
        Func<Task> send = () => MessageSendCommand.RunAsync(session, settings, cts.Token);

        await send.Should().ThrowAsync<DomainValidationException>(
            "h9k message send must refuse rather than silently queue a message with no project at all to belong to");
    }

    [Fact]
    public async Task MessageSendCommand_WithSeveralEligibleProjectsAndNoExplicitProject_RefusesAndListsThem()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await NodeBootstrapSeed.SeedGitHubConnectionAsync(_postgres.Store, cts.Token);

        Guid ownerId;
        await using (IDocumentSession bootstrapSession = _postgres.Store.LightweightSession())
        {
            BootstrapContext context = await NodeBootstrap.EnsureAsync(bootstrapSession, cts.Token);
            await bootstrapSession.SaveChangesAsync(cts.Token);
            ownerId = context.OwnerId;
        }

        await using (IDocumentSession registerSession = _postgres.Store.LightweightSession())
        {
            OwnerAggregate owner = await registerSession.Events.AggregateStreamAsync<OwnerAggregate>(ownerId, token: cts.Token)
                ?? throw new InvalidOperationException("Expected the owner stream bootstrap just started to exist.");
            registerSession.Events.Append(ownerId, OwnerDecider.ClaimRoot(owner, "owner-a-fingerprint", verified: true, Now));

            Guid firstProjectId = DomainId.New();
            Guid secondProjectId = DomainId.New();
            registerSession.Events.StartStream<ProjectAggregate>(
                firstProjectId,
                ProjectDecider.Register(firstProjectId, ownerId, DomainId.New(), "alpha", RepositoryX, null, null, Now));
            registerSession.Events.StartStream<ProjectAggregate>(
                secondProjectId,
                ProjectDecider.Register(secondProjectId, ownerId, DomainId.New(), "beta", RepositoryY, null, null, Now));
            await registerSession.SaveChangesAsync(cts.Token);
        }

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        MessageSendCommand.Settings settings = new() { Text = "hello", To = "project" };
        Func<Task> send = () => MessageSendCommand.RunAsync(session, settings, cts.Token);

        var thrown = await send.Should().ThrowAsync<DomainConflictException>(
            "with two eligible projects and no --project, this node cannot guess which one this message belongs to");
        thrown.Which.Message.Should().Contain("alpha");
        thrown.Which.Message.Should().Contain("beta");
    }

    [Fact]
    public async Task MessageSendCommand_WithSeveralEligibleProjectsAndAnExplicitProjectFragment_QueuesUnderTheNamedOne()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await NodeBootstrapSeed.SeedGitHubConnectionAsync(_postgres.Store, cts.Token);

        Guid ownerId;
        await using (IDocumentSession bootstrapSession = _postgres.Store.LightweightSession())
        {
            BootstrapContext context = await NodeBootstrap.EnsureAsync(bootstrapSession, cts.Token);
            await bootstrapSession.SaveChangesAsync(cts.Token);
            ownerId = context.OwnerId;
        }

        Guid betaProjectId = DomainId.New();
        await using (IDocumentSession registerSession = _postgres.Store.LightweightSession())
        {
            OwnerAggregate owner = await registerSession.Events.AggregateStreamAsync<OwnerAggregate>(ownerId, token: cts.Token)
                ?? throw new InvalidOperationException("Expected the owner stream bootstrap just started to exist.");
            registerSession.Events.Append(ownerId, OwnerDecider.ClaimRoot(owner, "owner-a-fingerprint", verified: true, Now));

            Guid alphaProjectId = DomainId.New();
            registerSession.Events.StartStream<ProjectAggregate>(
                alphaProjectId,
                ProjectDecider.Register(alphaProjectId, ownerId, DomainId.New(), "alpha", RepositoryX, null, null, Now));
            registerSession.Events.StartStream<ProjectAggregate>(
                betaProjectId,
                ProjectDecider.Register(betaProjectId, ownerId, DomainId.New(), "beta", RepositoryY, null, null, Now));
            await registerSession.SaveChangesAsync(cts.Token);
        }

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        MessageSendCommand.Settings settings = new() { Text = "for beta", To = "project", Project = "bet" };
        int exitCode = await MessageSendCommand.RunAsync(session, settings, cts.Token);

        exitCode.Should().Be(ExitCodes.Ok);
        MessageDetails queued = (await session.Query<MessageDetails>().Where(message => message.Body == "for beta")
            .ToListAsync(cts.Token)).Single();
        queued.ProjectId.Should().Be(betaProjectId, "the 'bet' fragment unambiguously names the beta project");
    }

    [Fact]
    public async Task A_new_send_never_reuses_a_seq_a_still_pending_legacy_message_already_holds()
    {
        // Simulates a node that had a message queued before idea 202383dc's M2 shipped (still
        // carrying Guid.Empty as its own ProjectId, per MessageQueued's own doc) and never sent —
        // origin unreachable, say. Before the fix, NextSeqAsync ignored it entirely, so the very
        // next send under M2 also got seq 1, and the next flush that adopts both would silently
        // overwrite one with the other on the wire (independent pre-PR review, cycle 1, conformance
        // lens, medium).
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        Guid projectA = await RegisterEligibleProjectAsync("solo", RepositoryX, cts.Token);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream<MessageAggregate>(
            MessageStreamId.ForMessage(nodeA, Guid.Empty, 1),
            new MessageQueued(
                nodeA, 1, "owner-a-fingerprint", MessageAudience.Node(nodeB).Value, null, MessageKind.Note.Value,
                "legacy pending", Now, Guid.Empty));
        await session.SaveChangesAsync(cts.Token);

        MessageEnvelopeV1 queued = await MessageOutbox.QueueAsync(
            session, nodeA, projectA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note,
            "post-upgrade", Now.AddSeconds(1), cts.Token);

        queued.Seq.Should().Be(2, "seq 1 is still held by the pending legacy message this project will adopt on its next flush");
    }

    [Fact]
    public async Task A_new_send_never_reuses_a_seq_an_already_sent_legacy_message_still_holds_on_the_wire()
    {
        // The already-sent counterpart to the pending case above: legacy seqs 1-3 were sent under
        // the old, single-project system (SentAt already set, ProjectId still Guid.Empty forever —
        // an already-sent legacy message is never backfilled with a real project id) and are still
        // physically present on the adopting project's own ref within MessageRetention. Before the
        // fix, a post-upgrade send restarted at seq 1 and a later flush silently overwrote that
        // still-live wire content (independent pre-PR review, cycle 1, adversarial lens, medium).
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        Guid projectA = await RegisterEligibleProjectAsync("solo", RepositoryX, cts.Token);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        foreach (long seq in new[] { 1L, 2L, 3L })
        {
            session.Events.StartStream<MessageAggregate>(
                MessageStreamId.ForMessage(nodeA, Guid.Empty, seq),
                new MessageQueued(
                    nodeA, seq, "owner-a-fingerprint", MessageAudience.Node(nodeB).Value, null, MessageKind.Note.Value,
                    $"legacy sent {seq}", Now, Guid.Empty));
        }

        await session.SaveChangesAsync(cts.Token);
        foreach (long seq in new[] { 1L, 2L, 3L })
        {
            session.Events.Append(MessageStreamId.ForMessage(nodeA, Guid.Empty, seq), new MessageSent(nodeA, seq, Now, Guid.Empty));
        }

        await session.SaveChangesAsync(cts.Token);

        MessageEnvelopeV1 queued = await MessageOutbox.QueueAsync(
            session, nodeA, projectA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note,
            "post-upgrade", Now.AddSeconds(1), cts.Token);

        queued.Seq.Should().Be(4, "seqs 1-3 are already physically written to this project's own ref by the legacy sender");
    }

    [Fact]
    public async Task Only_the_lowest_id_eligible_project_gets_the_legacy_seq_offset_a_second_project_still_starts_at_one()
    {
        // A pending legacy message only ever collides with whichever project actually adopts it —
        // LegacyMessageAdoption's own rule, the lowest-id eligible project. Every OTHER eligible
        // project's own first-ever message must still start contiguous at 1, or a brand-new reader
        // of that project's own ref stalls forever looking for a seq 1 that will never come
        // (MessageOutbox.NextSeqAsync's own doc).
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        Guid lowestIdProject = await RegisterEligibleProjectAsync("lowest", RepositoryX, cts.Token);
        Guid higherIdProject = await RegisterEligibleProjectAsync("higher", RepositoryY, cts.Token);
        lowestIdProject.CompareTo(higherIdProject).Should().BeLessThan(
            0, "UUIDv7 ids mint in creation order, so registering \"lowest\" first must keep it the lower of the two");

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream<MessageAggregate>(
            MessageStreamId.ForMessage(nodeA, Guid.Empty, 1),
            new MessageQueued(
                nodeA, 1, "owner-a-fingerprint", MessageAudience.Node(nodeB).Value, null, MessageKind.Note.Value,
                "legacy pending", Now, Guid.Empty));
        await session.SaveChangesAsync(cts.Token);

        MessageEnvelopeV1 queuedForHigher = await MessageOutbox.QueueAsync(
            session, nodeA, higherIdProject, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note,
            "post-upgrade for the other project", Now.AddSeconds(1), cts.Token);

        queuedForHigher.Seq.Should().Be(
            1, "this project never adopts the legacy batch, so its own first-ever message must still start contiguous at 1");
    }

    [Fact]
    public async Task SquashAsync_stamps_the_ledger_derived_project_key_onto_every_surviving_envelope()
    {
        // ToEnvelope (SquashAsync's own rebuild of each survivor from MessageDetails) carries no
        // project key of its own — before the fix, every squashed ref silently dropped the key
        // FlushAsync had originally stamped (independent pre-PR review, cycle 1, adversarial lens,
        // medium).
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, RepositoryX, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        MessageOutbox outbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");

        Guid projectId = DomainId.New();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        await MessageOutbox.QueueAsync(
            session, nodeA, projectId, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note,
            "surviving", Now, cts.Token);
        await outbox.FlushAsync(
            session, RepositoryX, nodeA, projectId, ProjectKeyX, adoptUnassigned: false, committerA, signingKeyA, Now,
            cts.Token);

        DateTimeOffset squashNow = Now.AddHours(1);
        await outbox.SquashAsync(
            session, RepositoryX, nodeA, projectId, ProjectKeyX, TimeSpan.FromHours(48), committerA, signingKeyA, squashNow,
            cts.Token);

        TransportReadResult afterSquash = await transport.ReadSinceAsync(RepositoryX, nodeA, sinceSeq: 0, cts.Token);
        MessageEnvelopeCodec.DecodeResult decoded = MessageEnvelopeCodec.Decode(afterSquash.Envelopes.Single().Content);
        decoded.Envelope!.ProjectKey.Should().Be(ProjectKeyX, "a squash must stamp the identical project key a flush would have");
    }

    [Fact]
    public async Task A_first_per_project_read_by_the_adopting_project_inherits_the_pre_upgrade_cursor_instead_of_restarting_at_zero()
    {
        // Simulates a receiver that had already read and handled a sender's first three envelopes
        // before idea 202383dc's M2 shipped, recorded on the old, unscoped inbox stream. Before the
        // fix, the adopting project's own brand-new per-project cursor started at 0 regardless, so
        // its first read re-fetched and re-stored every envelope already handled under the old
        // stream ids (independent pre-PR review, cycle 1, conformance lens, medium).
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        const string ownerB = "owner-b-fingerprint";

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, RepositoryX, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        MessageOutbox outbox = new(transport);
        MessageInbox inbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");

        Guid senderProjectId = DomainId.New();
        await using IDocumentSession sendSession = _postgres.Store.LightweightSession();
        foreach (int i in Enumerable.Range(0, 3))
        {
            await MessageOutbox.QueueAsync(
                sendSession, nodeA, senderProjectId, "owner-a-fingerprint", MessageAudience.Project, null, MessageKind.Note,
                $"pre-upgrade {i}", Now.AddSeconds(i), cts.Token);
        }

        await outbox.FlushAsync(
            sendSession, RepositoryX, nodeA, senderProjectId, ProjectKeyX, adoptUnassigned: false, committerA, signingKeyA,
            Now, cts.Token);

        // The receiver's own pre-M2 cursor for this sender, already advanced past the three
        // envelopes above under the old, unscoped stream id.
        sendSession.Events.StartStream<MessageInboxAggregate>(
            MessageStreamId.ForInboxBeforeProjectScoping(nodeA), new InboxCursorAdvanced(nodeA, Guid.Empty, 3, Now));
        await sendSession.SaveChangesAsync(cts.Token);

        await MessageOutbox.QueueAsync(
            sendSession, nodeA, senderProjectId, "owner-a-fingerprint", MessageAudience.Project, null, MessageKind.Note,
            "post-upgrade", Now.AddSeconds(10), cts.Token);
        await outbox.FlushAsync(
            sendSession, RepositoryX, nodeA, senderProjectId, ProjectKeyX, adoptUnassigned: false, committerA, signingKeyA,
            Now.AddSeconds(10), cts.Token);

        Guid localProjectAtB = await RegisterEligibleProjectAsync("adopting", RepositoryX, cts.Token);
        await using IDocumentSession readSession = _postgres.Store.LightweightSession();
        MessageInboxSweepResult read = await inbox.ReadFromAsync(
            readSession, RepositoryX, nodeA, localProjectAtB, nodeB, ownerB, Now.AddSeconds(11), cancellationToken: cts.Token);

        read.EnvelopesStored.Should().Be(1, "the pre-upgrade cursor already covers the first three envelopes");

        MessageInboxDetails? newCursor = await readSession.LoadAsync<MessageInboxDetails>(
            MessageStreamId.ForInbox(nodeA, localProjectAtB), cts.Token);
        newCursor!.HighestSeqReceived.Should().Be(4, "the new per-project cursor picks up exactly where the pre-upgrade one left off");
    }

    /// <summary>Registers a fresh, eligible (not archived, with a repository) project under a
    /// throwaway owner and connection id — enough for <c>LegacyMessageAdoption</c>'s own eligibility
    /// query, with no need for the owner-bootstrap machinery <see cref="MessageSendCommand"/>'s own
    /// tests exercise separately.</summary>
    private async Task<Guid> RegisterEligibleProjectAsync(string name, string repositoryPath, CancellationToken cancellationToken)
    {
        Guid projectId = DomainId.New();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream<ProjectAggregate>(
            projectId,
            ProjectDecider.Register(projectId, DomainId.New(), DomainId.New(), name, repositoryPath, null, null, Now));
        await session.SaveChangesAsync(cancellationToken);
        return projectId;
    }

    /// <summary>Seeds the sender's own self-announced node file into ONE specific repository — a
    /// node file written into project X's own repository is invisible to a read against project
    /// Y's, the identical isolation a real, separate remote would give two genuinely different
    /// repositories, so every test that reads a sender's outbox from more than one repository
    /// seeds it into each one it actually reads from.</summary>
    private static async Task SeedNodeFileAsync(
        FakeLedger ledger, Guid nodeId, string repositoryPath, CancellationToken cancellationToken)
    {
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("seed");
        string content = $"node_id: \"{nodeId}\"\npublic_key: \"ssh-ed25519 AAAAFAKE{nodeId:N} test\"\n";
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                repositoryPath, $"refs/hall9k/ledger/nodes/{nodeId}", $"nodes/{nodeId}/node.yaml", content,
                ExpectedBlobId: null, "seed node file", committer, signingKey),
            cancellationToken);
    }

    private static (LedgerCommitter Committer, LedgerSigningKey SigningKey) Signing(string name) =>
        (new LedgerCommitter(name, $"{name}@hall9k.local"), new LedgerSigningKey($"/dev/null/{name}"));

    /// <summary>Fails <see cref="IMessageTransport.FlushAsync"/> for exactly one repository path,
    /// the same shape a real outage against one project's own remote would produce, while every
    /// other repository's own flush call passes straight through to the real
    /// <see cref="InMemoryMessageTransport"/> underneath.</summary>
    private sealed class SelectivelyFailingTransport(IMessageTransport inner, string failingRepositoryPath) : IMessageTransport
    {
        public Task SendAsync(
            string repositoryPath, Guid fromNodeId, long seq, string content, LedgerCommitter committer,
            LedgerSigningKey signingKey, CancellationToken cancellationToken) =>
            inner.SendAsync(repositoryPath, fromNodeId, seq, content, committer, signingKey, cancellationToken);

        public Task FlushAsync(
            string repositoryPath, Guid fromNodeId, IReadOnlyList<TransportEnvelope> envelopes, LedgerCommitter committer,
            LedgerSigningKey signingKey, CancellationToken cancellationToken) =>
            repositoryPath == failingRepositoryPath
                ? throw new LedgerPushRejectedException(refName: $"refs/hall9k/messages/{fromNodeId}", attempts: 5, gitError: "rejected")
                : inner.FlushAsync(repositoryPath, fromNodeId, envelopes, committer, signingKey, cancellationToken);

        public Task<TransportReadResult> ReadSinceAsync(
            string repositoryPath, Guid senderNodeId, long sinceSeq, CancellationToken cancellationToken,
            TrustChain? trustChain = null) =>
            inner.ReadSinceAsync(repositoryPath, senderNodeId, sinceSeq, cancellationToken, trustChain);

        public Task<IReadOnlyList<MessageOutboxTip>> ProbeAsync(string repositoryPath, CancellationToken cancellationToken) =>
            inner.ProbeAsync(repositoryPath, cancellationToken);

        public Task SquashAsync(
            string repositoryPath, Guid fromNodeId, IReadOnlyList<TransportEnvelope> survivors, LedgerCommitter committer,
            LedgerSigningKey signingKey, CancellationToken cancellationToken) =>
            inner.SquashAsync(repositoryPath, fromNodeId, survivors, committer, signingKey, cancellationToken);
    }
}
