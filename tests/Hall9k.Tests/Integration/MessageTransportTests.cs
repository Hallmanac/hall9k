using FluentAssertions;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The message store and transport seam (idea 202383dc, M1a) against a real Marten/Postgres
/// session for both "nodes" in every scenario, but never a real git repository: every test here
/// drives <see cref="InMemoryMessageTransport"/>, per Brian's 2026-09-13 testing rule that a real
/// repository is reserved for <c>GitLedgerTests</c> and the chain reader's own tests. One shared
/// Postgres schema stands in for two separate nodes' own separate local databases — nothing here is
/// keyed by "which node's database this is", only by sender node id and seq, so a message this test
/// sends as node A and receives as node B never collides with the reverse direction or with a third
/// node's own traffic.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class MessageTransportTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string RepositoryPath = "repo-under-test";
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;

    public MessageTransportTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Two_nodes_exchange_notes_in_both_directions()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        const string ownerA = "owner-a-fingerprint";
        const string ownerB = "owner-b-fingerprint";

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        await SeedNodeFileAsync(ledger, nodeB, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        MessageOutbox outbox = new(transport);
        MessageInbox inbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");
        (LedgerCommitter committerB, LedgerSigningKey signingKeyB) = Signing("node-b");

        await using (IDocumentSession sendSession = _postgres.Store.LightweightSession())
        {
            await outbox.SendAsync(
                sendSession, RepositoryPath, nodeA, ownerA, MessageAudience.Node(nodeB), about: null,
                MessageKind.Note, "hello from A", committerA, signingKeyA, Now, cts.Token);
        }

        MessageInboxSweepResult sweepAtB;
        await using (IDocumentSession readSession = _postgres.Store.LightweightSession())
        {
            sweepAtB = await inbox.ReadFromAsync(
                readSession, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(1), cts.Token);
        }

        sweepAtB.SenderIgnored.Should().BeFalse();
        sweepAtB.EnvelopesStored.Should().Be(1);

        await using (IDocumentSession assertSession = _postgres.Store.LightweightSession())
        {
            MessageDetails? received = await assertSession.LoadAsync<MessageDetails>(
                MessageStreamId.ForMessage(nodeA, 1), cts.Token);
            received.Should().NotBeNull();
            received!.Body.Should().Be("hello from A");
            received.ReceivedAt.Should().NotBeNull();
        }

        await using (IDocumentSession sendSession = _postgres.Store.LightweightSession())
        {
            await outbox.SendAsync(
                sendSession, RepositoryPath, nodeB, ownerB, MessageAudience.Node(nodeA), about: null,
                MessageKind.Note, "hello back from B", committerB, signingKeyB, Now.AddSeconds(2), cts.Token);
        }

        MessageInboxSweepResult sweepAtA;
        await using (IDocumentSession readSession = _postgres.Store.LightweightSession())
        {
            sweepAtA = await inbox.ReadFromAsync(
                readSession, RepositoryPath, nodeB, nodeA, ownerA, Now.AddSeconds(3), cts.Token);
        }

        sweepAtA.EnvelopesStored.Should().Be(1);

        await using IDocumentSession finalAssert = _postgres.Store.LightweightSession();
        MessageDetails? reply = await finalAssert.LoadAsync<MessageDetails>(
            MessageStreamId.ForMessage(nodeB, 1), cts.Token);
        reply.Should().NotBeNull();
        reply!.Body.Should().Be("hello back from B");
    }

    [Fact]
    public async Task A_refetch_never_double_records()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        const string ownerB = "owner-b-fingerprint";

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        MessageOutbox outbox = new(transport);
        MessageInbox inbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");

        await using (IDocumentSession sendSession = _postgres.Store.LightweightSession())
        {
            await outbox.SendAsync(
                sendSession, RepositoryPath, nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null,
                MessageKind.Note, "only once", committerA, signingKeyA, Now, cts.Token);
        }

        await using (IDocumentSession firstRead = _postgres.Store.LightweightSession())
        {
            MessageInboxSweepResult first = await inbox.ReadFromAsync(
                firstRead, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(1), cts.Token);
            first.EnvelopesStored.Should().Be(1);
        }

        // Forces the transport to hand back the same seq again, exactly what a genuine re-fetch
        // (a manual re-sync, a lower bound forced after an outage) would look like.
        await using (IDocumentSession secondRead = _postgres.Store.LightweightSession())
        {
            MessageInboxSweepResult second = await inbox.ReadFromAsync(
                secondRead, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(2), cts.Token, sinceSeqOverride: 0);

            second.EnvelopesConsidered.Should().Be(1, "the transport handed the same envelope back again");
            second.EnvelopesStored.Should().Be(0, "the message aggregate's own duplicate check ignored it");
        }

        await using IDocumentSession assertSession = _postgres.Store.LightweightSession();
        MessageDetails? message = await assertSession.LoadAsync<MessageDetails>(
            MessageStreamId.ForMessage(nodeA, 1), cts.Token);
        message!.Body.Should().Be("only once");
    }

    [Fact]
    public async Task An_override_past_the_real_content_never_drags_the_cursor_forward_over_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        const string ownerB = "owner-b-fingerprint";

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        MessageOutbox outbox = new(transport);
        MessageInbox inbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");

        await using (IDocumentSession sendSession = _postgres.Store.LightweightSession())
        {
            await outbox.SendAsync(
                sendSession, RepositoryPath, nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null,
                MessageKind.Note, "seq one", committerA, signingKeyA, Now, cts.Token);
        }

        // An override far past anything the sender has actually sent — the transport correctly
        // finds nothing past it — must never advance the cursor to that override value: the real
        // envelope at seq 1 has not been read yet, and a cursor sitting past it would skip it forever.
        await using (IDocumentSession overriddenRead = _postgres.Store.LightweightSession())
        {
            MessageInboxSweepResult sweep = await inbox.ReadFromAsync(
                overriddenRead, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(1), cts.Token, sinceSeqOverride: 100);

            sweep.EnvelopesConsidered.Should().Be(0);
            sweep.EnvelopesStored.Should().Be(0);
        }

        // An ordinary read afterward, with no override, still finds the real envelope — proving the
        // cursor never silently jumped past it.
        await using IDocumentSession ordinaryRead = _postgres.Store.LightweightSession();
        MessageInboxSweepResult ordinarySweep = await inbox.ReadFromAsync(
            ordinaryRead, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(2), cts.Token);

        ordinarySweep.EnvelopesStored.Should().Be(1, "the override must never have advanced the cursor past unread content");
    }

    [Fact]
    public async Task An_envelope_for_another_node_advances_the_cursor_and_is_not_stored()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        Guid nodeC = DomainId.New();
        const string ownerB = "owner-b-fingerprint";

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        MessageOutbox outbox = new(transport);
        MessageInbox inbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");

        await using (IDocumentSession sendSession = _postgres.Store.LightweightSession())
        {
            await outbox.SendAsync(
                sendSession, RepositoryPath, nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeC), null,
                MessageKind.Note, "for C, not B", committerA, signingKeyA, Now, cts.Token);
        }

        await using (IDocumentSession firstRead = _postgres.Store.LightweightSession())
        {
            MessageInboxSweepResult sweep = await inbox.ReadFromAsync(
                firstRead, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(1), cts.Token);

            sweep.EnvelopesConsidered.Should().Be(1);
            sweep.EnvelopesStored.Should().Be(0, "this envelope is addressed to node C, not this reader");
        }

        // A second, real send addressed to B: if the cursor had not advanced past the first
        // envelope, this read would see both and the count below would be 2.
        await using (IDocumentSession sendSession = _postgres.Store.LightweightSession())
        {
            await outbox.SendAsync(
                sendSession, RepositoryPath, nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null,
                MessageKind.Note, "for B this time", committerA, signingKeyA, Now.AddSeconds(2), cts.Token);
        }

        await using IDocumentSession secondRead = _postgres.Store.LightweightSession();
        MessageInboxSweepResult secondSweep = await inbox.ReadFromAsync(
            secondRead, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(3), cts.Token);

        secondSweep.EnvelopesConsidered.Should().Be(1, "the cursor already advanced past the envelope addressed to C");
        secondSweep.EnvelopesStored.Should().Be(1);
    }

    [Fact]
    public async Task An_unrecognized_kind_is_stored_rather_than_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        const string ownerB = "owner-b-fingerprint";

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        MessageOutbox outbox = new(transport);
        MessageInbox inbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");

        await using (IDocumentSession sendSession = _postgres.Store.LightweightSession())
        {
            await outbox.SendAsync(
                sendSession, RepositoryPath, nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null,
                MessageKind.Parse("bookmark-announcement"), "a kind this version never learned",
                committerA, signingKeyA, Now, cts.Token);
        }

        await using IDocumentSession readSession = _postgres.Store.LightweightSession();
        MessageInboxSweepResult sweep = await inbox.ReadFromAsync(
            readSession, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(1), cts.Token);

        sweep.EnvelopesStored.Should().Be(1, "an unknown kind is stored, never refused");

        MessageDetails? message = await readSession.LoadAsync<MessageDetails>(
            MessageStreamId.ForMessage(nodeA, 1), cts.Token);
        message!.Kind.Should().Be("bookmark-announcement");
    }

    [Fact]
    public async Task An_unsupported_version_is_refused_but_does_not_fail_the_reader()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        const string ownerB = "owner-b-fingerprint";

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        MessageInbox inbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");

        string futureVersionEnvelope =
            $$"""{"version":2,"seq":1,"at":"2026-09-14T12:00:00Z","fromNode":"{{nodeA}}","fromOwner":"owner-a-fingerprint","to":"node:{{nodeB}}","about":null,"kind":"note","body":"from the future"}""";
        await transport.SendAsync(RepositoryPath, nodeA, 1, futureVersionEnvelope, committerA, signingKeyA, cts.Token);

        MessageEnvelopeV1 goodEnvelope = new(
            2, Now.AddSeconds(1), nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note, "seq two");
        await transport.SendAsync(
            RepositoryPath, nodeA, 2, MessageEnvelopeCodec.Encode(goodEnvelope), committerA, signingKeyA, cts.Token);

        await using IDocumentSession readSession = _postgres.Store.LightweightSession();
        MessageInboxSweepResult sweep = await inbox.ReadFromAsync(
            readSession, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(2), cts.Token);

        sweep.EnvelopesConsidered.Should().Be(2, "the unsupported version never fails the reader");
        sweep.EnvelopesStored.Should().Be(1, "only the version-1 envelope is stored");

        MessageDetails? stored = await readSession.LoadAsync<MessageDetails>(
            MessageStreamId.ForMessage(nodeA, 2), cts.Token);
        stored!.Body.Should().Be("seq two");
    }

    [Fact]
    public async Task A_sender_the_node_file_does_not_vouch_for_is_ignored()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();

        // Deliberately no node file for nodeA — this is exactly what "not vouched" means.
        FakeLedger ledger = new();
        InMemoryMessageTransport transport = new(ledger);
        MessageInbox inbox = new(transport);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        MessageInboxSweepResult sweep = await inbox.ReadFromAsync(
            session, RepositoryPath, nodeA, nodeB, "owner-b-fingerprint", Now, cts.Token);

        sweep.SenderIgnored.Should().BeTrue();
        sweep.EnvelopesStored.Should().Be(0);

        MessageInboxDetails? inboxDoc = await session.LoadAsync<MessageInboxDetails>(
            MessageStreamId.ForInbox(nodeA), cts.Token);
        inboxDoc.Should().NotBeNull("h9k status (M1b) names an ignored sender from this");
        inboxDoc!.SenderIgnored.Should().BeTrue();
    }

    private static async Task SeedNodeFileAsync(FakeLedger ledger, Guid nodeId, CancellationToken cancellationToken)
    {
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("seed");
        string content = $"node_id: \"{nodeId}\"\npublic_key: \"ssh-ed25519 AAAAFAKE{nodeId:N} test\"\n";
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, $"refs/hall9k/ledger/nodes/{nodeId}", $"nodes/{nodeId}/node.yaml", content,
                ExpectedBlobId: null, "seed node file", committer, signingKey),
            cancellationToken);
    }

    private static (LedgerCommitter Committer, LedgerSigningKey SigningKey) Signing(string name) =>
        (new LedgerCommitter(name, $"{name}@hall9k.local"), new LedgerSigningKey($"/dev/null/{name}"));
}
