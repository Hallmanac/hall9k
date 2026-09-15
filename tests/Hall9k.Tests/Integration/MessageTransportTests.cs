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
                readSession, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(1), cancellationToken: cts.Token);
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
                readSession, RepositoryPath, nodeB, nodeA, ownerA, Now.AddSeconds(3), cancellationToken: cts.Token);
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
                firstRead, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(1), cancellationToken: cts.Token);
            first.EnvelopesStored.Should().Be(1);
        }

        // Forces the transport to hand back the same seq again, exactly what a genuine re-fetch
        // (a manual re-sync, a lower bound forced after an outage) would look like.
        await using (IDocumentSession secondRead = _postgres.Store.LightweightSession())
        {
            MessageInboxSweepResult second = await inbox.ReadFromAsync(
                secondRead, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(2), sinceSeqOverride: 0, cancellationToken: cts.Token);

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
                overriddenRead, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(1), sinceSeqOverride: 100, cancellationToken: cts.Token);

            sweep.EnvelopesConsidered.Should().Be(0);
            sweep.EnvelopesStored.Should().Be(0);
        }

        // An ordinary read afterward, with no override, still finds the real envelope — proving the
        // cursor never silently jumped past it.
        await using IDocumentSession ordinaryRead = _postgres.Store.LightweightSession();
        MessageInboxSweepResult ordinarySweep = await inbox.ReadFromAsync(
            ordinaryRead, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(2), cancellationToken: cts.Token);

        ordinarySweep.EnvelopesStored.Should().Be(1, "the override must never have advanced the cursor past unread content");
    }

    [Fact]
    public async Task An_override_that_finds_real_content_still_never_drags_the_cursor_over_the_gap_it_skipped()
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
            await outbox.SendAsync(
                sendSession, RepositoryPath, nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null,
                MessageKind.Note, "seq two", committerA, signingKeyA, Now.AddSeconds(1), cts.Token);
            await outbox.SendAsync(
                sendSession, RepositoryPath, nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null,
                MessageKind.Note, "seq three", committerA, signingKeyA, Now.AddSeconds(2), cts.Token);
        }

        // An override past the persisted cursor (0) that skips straight to seq 2 and does find real
        // content past it (seq 3) must still never drag the cursor forward: seq 1 and 2 sat in the
        // gap this sweep never asked the transport for, and advancing the cursor to 3 would abandon
        // them forever.
        await using (IDocumentSession overriddenRead = _postgres.Store.LightweightSession())
        {
            MessageInboxSweepResult sweep = await inbox.ReadFromAsync(
                overriddenRead, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(3), sinceSeqOverride: 2, cancellationToken: cts.Token);

            sweep.EnvelopesConsidered.Should().Be(1, "only seq 3 sits past the override");
            sweep.EnvelopesStored.Should().Be(1);
        }

        // An ordinary read afterward, with no override, still finds every envelope in the gap the
        // override skipped over — proving the cursor never silently jumped to seq 3.
        await using IDocumentSession ordinaryRead = _postgres.Store.LightweightSession();
        MessageInboxSweepResult ordinarySweep = await inbox.ReadFromAsync(
            ordinaryRead, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(4), cancellationToken: cts.Token);

        ordinarySweep.EnvelopesConsidered.Should().Be(3, "seq 1 through 3 must all still be reachable");
        ordinarySweep.EnvelopesStored.Should().Be(2, "seq 1 and 2 were never stored by the skipping override; seq 3 is a re-fetch");
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
                firstRead, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(1), cancellationToken: cts.Token);

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
            secondRead, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(3), cancellationToken: cts.Token);

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
            readSession, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(1), cancellationToken: cts.Token);

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
            readSession, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(2), cancellationToken: cts.Token);

        sweep.EnvelopesConsidered.Should().Be(2, "the unsupported version never fails the reader");
        sweep.EnvelopesStored.Should().Be(1, "only the version-1 envelope is stored");

        MessageDetails? stored = await readSession.LoadAsync<MessageDetails>(
            MessageStreamId.ForMessage(nodeA, 2), cts.Token);
        stored!.Body.Should().Be("seq two");
    }

    [Fact]
    public async Task A_malformed_version_one_envelope_is_refused_but_does_not_fail_the_reader()
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

        // A version-1 envelope with an audience this reader cannot route at all — a decode failure,
        // never an unsupported version — must still never stop the sweep from reaching seq 2.
        string malformedEnvelope =
            $$"""{"version":1,"seq":1,"at":"2026-09-14T12:00:00Z","fromNode":"{{nodeA}}","fromOwner":"owner-a-fingerprint","to":"team:x","about":null,"kind":"note","body":"unroutable"}""";
        await transport.SendAsync(RepositoryPath, nodeA, 1, malformedEnvelope, committerA, signingKeyA, cts.Token);

        MessageEnvelopeV1 goodEnvelope = new(
            2, Now.AddSeconds(1), nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note, "seq two");
        await transport.SendAsync(
            RepositoryPath, nodeA, 2, MessageEnvelopeCodec.Encode(goodEnvelope), committerA, signingKeyA, cts.Token);

        await using IDocumentSession readSession = _postgres.Store.LightweightSession();
        MessageInboxSweepResult sweep = await inbox.ReadFromAsync(
            readSession, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(2), cancellationToken: cts.Token);

        sweep.EnvelopesConsidered.Should().Be(2, "the malformed envelope never fails the reader");
        sweep.EnvelopesStored.Should().Be(1, "only the routable envelope is stored");

        MessageDetails? stored = await readSession.LoadAsync<MessageDetails>(
            MessageStreamId.ForMessage(nodeA, 2), cts.Token);
        stored!.Body.Should().Be("seq two");
    }

    [Fact]
    public async Task An_envelope_claiming_a_mismatched_seq_or_sender_is_refused_but_does_not_fail_the_reader()
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

        // Stored at transport position 1, but the envelope's own content claims seq 2 — a
        // forged or corrupted position that must never be routed as if it genuinely were seq 1.
        MessageEnvelopeV1 claimsSeqTwo = new(
            2, Now, nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note, "claims seq two");
        await transport.SendAsync(
            RepositoryPath, nodeA, 1, MessageEnvelopeCodec.Encode(claimsSeqTwo), committerA, signingKeyA, cts.Token);

        MessageEnvelopeV1 goodEnvelope = new(
            2, Now.AddSeconds(1), nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note, "seq two for real");
        await transport.SendAsync(
            RepositoryPath, nodeA, 2, MessageEnvelopeCodec.Encode(goodEnvelope), committerA, signingKeyA, cts.Token);

        await using IDocumentSession readSession = _postgres.Store.LightweightSession();
        MessageInboxSweepResult sweep = await inbox.ReadFromAsync(
            readSession, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(2), cancellationToken: cts.Token);

        sweep.EnvelopesConsidered.Should().Be(2, "the mismatched envelope never fails the reader");
        sweep.EnvelopesStored.Should().Be(1, "only the envelope whose own seq matches its transport position is stored");

        MessageDetails? atPositionOne = await readSession.LoadAsync<MessageDetails>(
            MessageStreamId.ForMessage(nodeA, 1), cts.Token);
        atPositionOne.Should().BeNull("the mismatched envelope must never be recorded under the position it was found at");
    }

    [Fact]
    public async Task A_transport_rejected_candidate_still_advances_the_cursor_past_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        const string ownerB = "owner-b-fingerprint";

        // The shape GitLedgerMessageTransport reports when the only unread candidate failed
        // sender verification: nothing parses, but the transport still inspected seq 5.
        RejectingMessageTransport transport = new(HighestSeqInspected: 5, RejectedSeqs: [5]);
        MessageInbox inbox = new(transport);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        MessageInboxSweepResult sweep = await inbox.ReadFromAsync(
            session, RepositoryPath, nodeA, nodeB, ownerB, Now, cancellationToken: cts.Token);

        sweep.EnvelopesConsidered.Should().Be(1, "the rejected candidate was still inspected");
        sweep.EnvelopesStored.Should().Be(0);

        MessageInboxDetails? inboxDoc = await session.LoadAsync<MessageInboxDetails>(
            MessageStreamId.ForInbox(nodeA), cts.Token);
        inboxDoc!.HighestSeqReceived.Should().Be(
            5, "the cursor must move past a rejected candidate, or the identical rejection repeats every sweep");
    }

    [Fact]
    public async Task A_rejected_candidate_marks_the_sender_ignored_for_status_even_though_the_cursor_moved()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        const string ownerB = "owner-b-fingerprint";

        RejectingMessageTransport transport = new(HighestSeqInspected: 5, RejectedSeqs: [5]);
        MessageInbox inbox = new(transport);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        MessageInboxSweepResult sweep = await inbox.ReadFromAsync(
            session, RepositoryPath, nodeA, nodeB, ownerB, Now, cancellationToken: cts.Token);

        sweep.SenderIgnored.Should().BeTrue("an envelope failing sender verification must be visible to h9k status");

        MessageInboxDetails? inboxDoc = await session.LoadAsync<MessageInboxDetails>(
            MessageStreamId.ForInbox(nodeA), cts.Token);
        inboxDoc!.SenderIgnored.Should().BeTrue(
            "the cursor advancing past a rejected candidate must never be read as this sender being fine");
        inboxDoc.HighestSeqReceived.Should().Be(5, "the cursor still moves past the rejected candidate");
    }

    [Fact]
    public async Task A_clean_sweep_after_a_rejected_candidate_clears_the_ignored_mark()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        const string ownerB = "owner-b-fingerprint";
        Guid inboxStreamId = MessageStreamId.ForInbox(nodeA);

        RejectingMessageTransport rejecting = new(HighestSeqInspected: 5, RejectedSeqs: [5]);
        MessageInbox inboxOverRejecting = new(rejecting);

        await using (IDocumentSession firstSession = _postgres.Store.LightweightSession())
        {
            await inboxOverRejecting.ReadFromAsync(
                firstSession, RepositoryPath, nodeA, nodeB, ownerB, Now, cancellationToken: cts.Token);
        }

        // The next sweep finds the sender vouched and nothing new past the cursor — no rejection
        // this time — which must clear the mark the previous sweep raised.
        RejectingMessageTransport clean = new(HighestSeqInspected: 5, RejectedSeqs: []);
        MessageInbox inboxOverClean = new(clean);

        await using (IDocumentSession secondSession = _postgres.Store.LightweightSession())
        {
            MessageInboxSweepResult second = await inboxOverClean.ReadFromAsync(
                secondSession, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(1), cancellationToken: cts.Token);
            second.SenderIgnored.Should().BeFalse();
        }

        await using IDocumentSession assertSession = _postgres.Store.LightweightSession();
        MessageInboxDetails? inboxDoc = await assertSession.LoadAsync<MessageInboxDetails>(inboxStreamId, cts.Token);
        inboxDoc!.SenderIgnored.Should().BeFalse("a later clean sweep must clear an earlier verification-failure mark");
    }

    [Fact]
    public async Task A_seq_conflict_from_an_earlier_unrecorded_push_moves_to_the_next_seq_instead_of_misattributing_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        MessageOutbox outbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");

        // Simulates a push that landed at the ledger for seq 1 whose own local MessageSent record
        // was never saved (a crash, a cancellation, right after the push succeeded) — this node's
        // own store has no idea seq 1 was ever used.
        MessageEnvelopeV1 phantom = new(
            1, Now, nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note, "landed but never recorded locally");
        await transport.SendAsync(
            RepositoryPath, nodeA, 1, MessageEnvelopeCodec.Encode(phantom), committerA, signingKeyA, cts.Token);

        MessageEnvelopeV1 sent;
        await using (IDocumentSession sendSession = _postgres.Store.LightweightSession())
        {
            sent = await outbox.SendAsync(
                sendSession, RepositoryPath, nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null,
                MessageKind.Note, "a brand new message", committerA, signingKeyA, Now.AddSeconds(1), cts.Token);
        }

        sent.Seq.Should().Be(2, "seq 1 already holds different content this node's own store never saw");

        await using IDocumentSession assertSession = _postgres.Store.LightweightSession();
        MessageDetails? atTwo = await assertSession.LoadAsync<MessageDetails>(MessageStreamId.ForMessage(nodeA, 2), cts.Token);
        atTwo.Should().NotBeNull();
        atTwo!.SendFailed.Should().BeFalse();
        atTwo.SentAt.Should().NotBeNull();

        MessageDetails? atOne = await assertSession.LoadAsync<MessageDetails>(MessageStreamId.ForMessage(nodeA, 1), cts.Token);
        atOne.Should().BeNull("this node's own store must never record a failure against seq 1's real, different content");
    }

    [Fact]
    public async Task A_sender_vouched_again_clears_the_ignored_mark_even_with_nothing_new()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        Guid inboxStreamId = MessageStreamId.ForInbox(nodeA);

        // Deliberately no node file for nodeA yet.
        FakeLedger ledger = new();
        InMemoryMessageTransport transport = new(ledger);
        MessageInbox inbox = new(transport);

        await using (IDocumentSession firstSession = _postgres.Store.LightweightSession())
        {
            MessageInboxSweepResult first = await inbox.ReadFromAsync(
                firstSession, RepositoryPath, nodeA, nodeB, "owner-b-fingerprint", Now, cancellationToken: cts.Token);
            first.SenderIgnored.Should().BeTrue();
        }

        // The sender's node file shows up, but it has still sent nothing at all — no envelope
        // ever exists to advance the cursor to.
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);

        await using (IDocumentSession secondSession = _postgres.Store.LightweightSession())
        {
            MessageInboxSweepResult second = await inbox.ReadFromAsync(
                secondSession, RepositoryPath, nodeA, nodeB, "owner-b-fingerprint", Now.AddSeconds(1), cancellationToken: cts.Token);
            second.SenderIgnored.Should().BeFalse();
            second.EnvelopesConsidered.Should().Be(0);
        }

        await using IDocumentSession assertSession = _postgres.Store.LightweightSession();
        MessageInboxDetails? inboxDoc = await assertSession.LoadAsync<MessageInboxDetails>(inboxStreamId, cts.Token);
        inboxDoc!.SenderIgnored.Should().BeFalse("the sender's node file now vouches for it, even though nothing new arrived");
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
            session, RepositoryPath, nodeA, nodeB, "owner-b-fingerprint", Now, cancellationToken: cts.Token);

        sweep.SenderIgnored.Should().BeTrue();
        sweep.EnvelopesStored.Should().Be(0);

        MessageInboxDetails? inboxDoc = await session.LoadAsync<MessageInboxDetails>(
            MessageStreamId.ForInbox(nodeA), cts.Token);
        inboxDoc.Should().NotBeNull("h9k status (M1b) names an ignored sender from this");
        inboxDoc!.SenderIgnored.Should().BeTrue();
    }

    [Fact]
    public async Task A_sender_that_stays_unvouched_is_marked_ignored_only_once()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        Guid inboxStreamId = MessageStreamId.ForInbox(nodeA);

        // Deliberately no node file for nodeA on every sweep below — nothing about this sender
        // ever changes between them.
        FakeLedger ledger = new();
        InMemoryMessageTransport transport = new(ledger);
        MessageInbox inbox = new(transport);

        await using (IDocumentSession firstSession = _postgres.Store.LightweightSession())
        {
            await inbox.ReadFromAsync(
                firstSession, RepositoryPath, nodeA, nodeB, "owner-b-fingerprint", Now, cancellationToken: cts.Token);
        }

        await using (IDocumentSession secondSession = _postgres.Store.LightweightSession())
        {
            await inbox.ReadFromAsync(
                secondSession, RepositoryPath, nodeA, nodeB, "owner-b-fingerprint", Now.AddSeconds(1), cancellationToken: cts.Token);
        }

        await using IDocumentSession assertSession = _postgres.Store.LightweightSession();
        IReadOnlyList<JasperFx.Events.IEvent> events =
            await assertSession.Events.FetchStreamAsync(inboxStreamId, token: cts.Token);

        events.Should().HaveCount(1, "a sweep that finds the sender still unvouched must never repeat an already-recorded ignore");
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

    /// <summary>Stands in for what <see cref="GitLedgerMessageTransport"/> itself cannot be
    /// exercised through here (Brian's 2026-09-13 testing rule): a sender that is vouched for, but
    /// whose only candidate above the cursor the transport already rejected on its own terms (a bad
    /// signature, a missing introducing commit) — <see cref="MessageInbox"/> must still see that
    /// candidate as inspected, never as "nothing happened this sweep".</summary>
    private sealed record RejectingMessageTransport(long HighestSeqInspected, IReadOnlyList<long> RejectedSeqs) : IMessageTransport
    {
        public Task SendAsync(
            string repositoryPath, Guid fromNodeId, long seq, string content, LedgerCommitter committer,
            LedgerSigningKey signingKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This fake only stands in for a read.");

        public Task<TransportReadResult> ReadSinceAsync(
            string repositoryPath, Guid senderNodeId, long sinceSeq, CancellationToken cancellationToken) =>
            Task.FromResult(TransportReadResult.Ok([], HighestSeqInspected, RejectedSeqs));
    }
}
