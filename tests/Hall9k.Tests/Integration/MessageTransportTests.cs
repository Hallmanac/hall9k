using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The message store and transport seam (idea 202383dc, M1a; queue-then-flush split M1b) against a
/// real Marten/Postgres session for both "nodes" in every scenario, but never a real git
/// repository: every test here drives <see cref="InMemoryMessageTransport"/>, per Brian's
/// 2026-09-13 testing rule that a real repository is reserved for <c>GitLedgerTests</c> and the
/// chain reader's own tests. One shared Postgres schema stands in for two separate nodes' own
/// separate local databases — nothing here is keyed by "which node's database this is", only by
/// sender node id and seq, so a message this test sends as node A and receives as node B never
/// collides with the reverse direction or with a third node's own traffic. Every send here queues
/// then immediately flushes, standing in for what a daemon sweep does on its own cadence.
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
            await MessageOutbox.QueueAsync(
                sendSession, nodeA, ownerA, MessageAudience.Node(nodeB), about: null, MessageKind.Note,
                "hello from A", Now, cts.Token);
            await outbox.FlushAsync(sendSession, RepositoryPath, nodeA, committerA, signingKeyA, Now, cts.Token);
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
            await MessageOutbox.QueueAsync(
                sendSession, nodeB, ownerB, MessageAudience.Node(nodeA), about: null, MessageKind.Note,
                "hello back from B", Now.AddSeconds(2), cts.Token);
            await outbox.FlushAsync(sendSession, RepositoryPath, nodeB, committerB, signingKeyB, Now.AddSeconds(2), cts.Token);
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
            await MessageOutbox.QueueAsync(
                sendSession, nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note,
                "only once", Now, cts.Token);
            await outbox.FlushAsync(sendSession, RepositoryPath, nodeA, committerA, signingKeyA, Now, cts.Token);
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
            await MessageOutbox.QueueAsync(
                sendSession, nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note,
                "seq one", Now, cts.Token);
            await outbox.FlushAsync(sendSession, RepositoryPath, nodeA, committerA, signingKeyA, Now, cts.Token);
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
            await MessageOutbox.QueueAsync(
                sendSession, nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note,
                "seq one", Now, cts.Token);
            await MessageOutbox.QueueAsync(
                sendSession, nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note,
                "seq two", Now.AddSeconds(1), cts.Token);
            await MessageOutbox.QueueAsync(
                sendSession, nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note,
                "seq three", Now.AddSeconds(2), cts.Token);
            await outbox.FlushAsync(sendSession, RepositoryPath, nodeA, committerA, signingKeyA, Now.AddSeconds(2), cts.Token);
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
            await MessageOutbox.QueueAsync(
                sendSession, nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeC), null, MessageKind.Note,
                "for C, not B", Now, cts.Token);
            await outbox.FlushAsync(sendSession, RepositoryPath, nodeA, committerA, signingKeyA, Now, cts.Token);
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
            await MessageOutbox.QueueAsync(
                sendSession, nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note,
                "for B this time", Now.AddSeconds(2), cts.Token);
            await outbox.FlushAsync(sendSession, RepositoryPath, nodeA, committerA, signingKeyA, Now.AddSeconds(2), cts.Token);
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
            await MessageOutbox.QueueAsync(
                sendSession, nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null,
                MessageKind.Parse("bookmark-announcement"), "a kind this version never learned", Now, cts.Token);
            await outbox.FlushAsync(sendSession, RepositoryPath, nodeA, committerA, signingKeyA, Now, cts.Token);
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
    public async Task A_clean_sweep_after_a_rejected_candidate_never_clears_the_verification_failure_mark()
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
        // this time — but the standing mark is for one specific envelope's own verification
        // failure, not for the sender being unvouched, so a sweep that merely finds nothing new
        // must never clear it: that would make a forgery visible for a single sweep interval only.
        RejectingMessageTransport clean = new(HighestSeqInspected: 5, RejectedSeqs: []);
        MessageInbox inboxOverClean = new(clean);

        await using (IDocumentSession secondSession = _postgres.Store.LightweightSession())
        {
            // The clean sweep's own return value correctly reports nothing rejected this time
            // (MessageInboxSweepResult.SenderIgnored is this sweep's own verdict, not the
            // persisted mark) — the fix is about the standing, persisted state asserted below.
            MessageInboxSweepResult second = await inboxOverClean.ReadFromAsync(
                secondSession, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(1), cancellationToken: cts.Token);
            second.SenderIgnored.Should().BeFalse("this sweep itself rejected nothing");
        }

        await using IDocumentSession assertSession = _postgres.Store.LightweightSession();
        MessageInboxDetails? inboxDoc = await assertSession.LoadAsync<MessageInboxDetails>(inboxStreamId, cts.Token);
        inboxDoc!.SenderIgnored.Should().BeTrue("a clean sweep alone must never clear an earlier verification-failure mark");
        inboxDoc.IgnoredForVerificationFailure.Should().BeTrue();
    }

    [Fact]
    public async Task A_genuine_cursor_advance_past_a_rejected_candidate_still_clears_the_verification_failure_mark()
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

        // A later sweep that genuinely advances the cursor past the rejected candidate — real
        // content this time — still clears the mark, exactly the way InboxCursorAdvanced's own
        // Apply always has: only a standing "nothing changed" sweep must never do it on its own.
        RejectingMessageTransport advanced = new(HighestSeqInspected: 7, RejectedSeqs: []);
        MessageInbox inboxOverAdvanced = new(advanced);

        await using (IDocumentSession secondSession = _postgres.Store.LightweightSession())
        {
            MessageInboxSweepResult second = await inboxOverAdvanced.ReadFromAsync(
                secondSession, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(1), cancellationToken: cts.Token);
            second.SenderIgnored.Should().BeFalse();
        }

        await using IDocumentSession assertSession = _postgres.Store.LightweightSession();
        MessageInboxDetails? inboxDoc = await assertSession.LoadAsync<MessageInboxDetails>(inboxStreamId, cts.Token);
        inboxDoc!.SenderIgnored.Should().BeFalse();
        inboxDoc.HighestSeqReceived.Should().Be(7);
    }

    [Fact]
    public async Task A_gap_in_the_senders_own_seq_sequence_stalls_the_reader_at_it()
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

        // Seq 1 never lands at all — a queue-then-flush batch either lands every pending envelope
        // or none of them (MessageOutbox.FlushAsync's own contract), so this node's outbox can never
        // hold seq 2 without seq 1 through the ordinary send path; landing seq 2 directly through the
        // raw transport stands in for whatever left seq 1 permanently missing.
        MessageEnvelopeV1 envelope = new(
            2, Now, nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note, "lands past the gap");
        await transport.SendAsync(
            RepositoryPath, nodeA, 2, MessageEnvelopeCodec.Encode(envelope), committerA, signingKeyA, cts.Token);

        // A receiver's sweep must never silently report "nothing new": seq 2 sits past a gap the
        // reader has not resolved, and the sweep must say so rather than let it look exactly like
        // the sender genuinely has nothing to deliver.
        await using IDocumentSession readSession = _postgres.Store.LightweightSession();
        MessageInboxSweepResult sweep = await inbox.ReadFromAsync(
            readSession, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(2), cancellationToken: cts.Token);

        sweep.EnvelopesStored.Should().Be(0, "seq 2 sits past a gap the reader has not resolved yet");
        sweep.StalledAtSeq.Should().Be(2, "seq 2 is the first seq this sweep could not reach");

        MessageInboxDetails? inboxDoc = await readSession.LoadAsync<MessageInboxDetails>(
            MessageStreamId.ForInbox(nodeA), cts.Token);
        inboxDoc.Should().BeNull("a stall alone never advances or creates a persisted cursor");
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

    [Fact]
    public async Task Flush_batches_every_queued_envelope_into_one_transport_call()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        MessageOutbox outbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        await MessageOutbox.QueueAsync(
            session, nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note, "one", Now, cts.Token);
        await MessageOutbox.QueueAsync(
            session, nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note, "two",
            Now.AddSeconds(1), cts.Token);
        await MessageOutbox.QueueAsync(
            session, nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note, "three",
            Now.AddSeconds(2), cts.Token);

        MessageFlushResult flush = await outbox.FlushAsync(
            session, RepositoryPath, nodeA, committerA, signingKeyA, Now.AddSeconds(3), cts.Token);

        flush.EnvelopesFlushed.Should().Be(3, "one flush call lands every envelope queued since the last flush");

        TransportReadResult read = await transport.ReadSinceAsync(RepositoryPath, nodeA, sinceSeq: 0, cts.Token);
        read.Envelopes.Should().HaveCount(3, "the batch landed in one transport call, not three");

        // InMemoryMessageTransport bumps its own version counter once per FlushAsync call — three
        // separate transport.FlushAsync calls (one per envelope) would leave the tip at "3", not
        // "1", so this is what actually distinguishes one batched call from three.
        IReadOnlyList<MessageOutboxTip> tips = await transport.ProbeAsync(RepositoryPath, cts.Token);
        tips.Should().ContainSingle(tip => tip.SenderNodeId == nodeA && tip.Tip == "1", "one flush call bumps the tip exactly once");

        foreach (long seq in new[] { 1L, 2L, 3L })
        {
            MessageDetails? message = await session.LoadAsync<MessageDetails>(MessageStreamId.ForMessage(nodeA, seq), cts.Token);
            message!.SentAt.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task A_squash_keeps_recently_sent_envelopes_and_a_readers_cursor_survives_it()
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

        DateTimeOffset oldSentAt = Now;
        DateTimeOffset youngSentAt = Now.AddHours(40);

        // "old" is queued and flushed (sent) right away; "young" is not even queued until long
        // after — each envelope's own SentAt, not its QueuedAt, is what the retention window
        // measures (independent pre-PR review, cycle 1, adversarial lens).
        await using (IDocumentSession sendOldSession = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                sendOldSession, nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note, "old",
                oldSentAt, cts.Token);
            await outbox.FlushAsync(sendOldSession, RepositoryPath, nodeA, committerA, signingKeyA, oldSentAt, cts.Token);
        }

        await using (IDocumentSession sendYoungSession = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                sendYoungSession, nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note, "young",
                youngSentAt, cts.Token);
            await outbox.FlushAsync(sendYoungSession, RepositoryPath, nodeA, committerA, signingKeyA, youngSentAt, cts.Token);
        }

        await using (IDocumentSession readSession = _postgres.Store.LightweightSession())
        {
            MessageInboxSweepResult sweep = await inbox.ReadFromAsync(
                readSession, RepositoryPath, nodeA, nodeB, ownerB, youngSentAt.AddSeconds(1), cancellationToken: cts.Token);
            sweep.EnvelopesStored.Should().Be(2);
        }

        // 48-hour retention measured from just past the old envelope's own SentAt: old (sent at
        // oldSentAt) falls outside the window, young (sent 40h later) stays in it.
        DateTimeOffset squashNow = oldSentAt.AddHours(48).AddMinutes(1);
        MessageSquashResult squash;
        await using (IDocumentSession squashSession = _postgres.Store.LightweightSession())
        {
            squash = await outbox.SquashAsync(
                squashSession, RepositoryPath, nodeA, TimeSpan.FromHours(48), committerA, signingKeyA, squashNow, cts.Token);
        }

        squash.EnvelopesKept.Should().Be(1, "only the envelope sent within the last 48 hours is still within the retention window");

        // sinceSeq: 1, not 0 — this same reader already inspected seq 1 in the sweep above, so this
        // mirrors a reader resuming from its own prior cursor rather than one starting cold. A reader
        // starting fresh at 0 after the squash already happened would read the now-missing seq 1 as
        // an unexplained gap and stall there (ReadSinceAsync's own gap-stop rule cannot yet tell a
        // deliberately squashed seq apart from a genuinely failed one) — a real interaction gap
        // between squash and gap-stop, tracked separately rather than papered over here.
        TransportReadResult afterSquash = await transport.ReadSinceAsync(RepositoryPath, nodeA, sinceSeq: 1, cts.Token);
        afterSquash.Envelopes.Should().ContainSingle(envelope => envelope.Seq == 2, "the old envelope (seq 1) was squashed away");

        // The reader's own cursor is untouched by the squash — it survives at seq 2 — and a fresh
        // read finds nothing new to store, proving no double-record and no corruption.
        await using IDocumentSession finalSession = _postgres.Store.LightweightSession();
        MessageInboxAggregate? cursor = await finalSession.Events.AggregateStreamAsync<MessageInboxAggregate>(
            MessageStreamId.ForInbox(nodeA), token: cts.Token);
        cursor!.HighestSeqReceived.Should().Be(2);

        MessageInboxSweepResult afterSweep = await inbox.ReadFromAsync(
            finalSession, RepositoryPath, nodeA, nodeB, ownerB, squashNow.AddSeconds(1), cancellationToken: cts.Token);
        afterSweep.EnvelopesConsidered.Should().Be(0, "the cursor already covers both original envelopes, squashed or not");
    }

    [Fact]
    public async Task A_squash_never_drops_an_envelope_flushed_moments_ago_even_if_it_sat_queued_far_longer_than_retention()
    {
        // Simulates a node offline long enough that its own push is rejected for longer than the
        // retention window: the envelope's QueuedAt is long before "now", but it only reaches the
        // transport in this sweep's own flush, seconds before that same sweep's own squash. Before
        // the fix, the cutoff measured QueuedAt and force-removed it from the outbox moments after
        // it first became fetchable at all (independent pre-PR review, cycle 1, adversarial lens).
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        MessageOutbox outbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");

        DateTimeOffset queuedAt = Now;
        DateTimeOffset sentAt = Now.AddHours(72);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        await MessageOutbox.QueueAsync(
            session, nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note, "delayed",
            queuedAt, cts.Token);
        await outbox.FlushAsync(session, RepositoryPath, nodeA, committerA, signingKeyA, sentAt, cts.Token);

        MessageSquashResult squash = await outbox.SquashAsync(
            session, RepositoryPath, nodeA, TimeSpan.FromHours(48), committerA, signingKeyA, sentAt.AddSeconds(5), cts.Token);

        squash.EnvelopesKept.Should().Be(
            1, "retention is measured from when the envelope was actually sent, never from how long it sat queued");
    }

    [Fact]
    public async Task A_squash_pushes_nothing_when_no_sent_envelope_has_aged_past_retention()
    {
        // Every tick's squash used to force-push a fresh orphan commit regardless of whether
        // anything had actually aged out — including for a node that has never sent a message at
        // all, moving the outbox tip every tick and defeating every reader's own unmoved-tip skip
        // (independent pre-PR review, cycle 1, both lenses).
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        MessageOutbox outbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        await MessageOutbox.QueueAsync(
            session, nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note, "hello",
            Now, cts.Token);
        await outbox.FlushAsync(session, RepositoryPath, nodeA, committerA, signingKeyA, Now, cts.Token);

        IReadOnlyList<MessageOutboxTip> tipsBeforeSquash = await transport.ProbeAsync(RepositoryPath, cts.Token);
        string tipBeforeSquash = tipsBeforeSquash.Single(tip => tip.SenderNodeId == nodeA).Tip;

        // A squash immediately after, and again a day later — both still well within the 48-hour
        // retention window, so nothing has aged out either time.
        await outbox.SquashAsync(
            session, RepositoryPath, nodeA, TimeSpan.FromHours(48), committerA, signingKeyA, Now.AddSeconds(1), cts.Token);
        await outbox.SquashAsync(
            session, RepositoryPath, nodeA, TimeSpan.FromHours(48), committerA, signingKeyA, Now.AddHours(24), cts.Token);

        IReadOnlyList<MessageOutboxTip> tipsAfterSquash = await transport.ProbeAsync(RepositoryPath, cts.Token);
        string tipAfterSquash = tipsAfterSquash.Single(tip => tip.SenderNodeId == nodeA).Tip;
        tipAfterSquash.Should().Be(tipBeforeSquash, "a squash with nothing to drop must never move the outbox tip");
    }

    [Fact]
    public async Task Send_then_handle_round_trips_through_the_actual_cli_commands()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeB = DomainId.New();
        const string ownerB = "owner-b-fingerprint";

        FakeLedger ledger = new();
        InMemoryMessageTransport transport = new(ledger);
        MessageOutbox outbox = new(transport);
        MessageInbox inbox = new(transport);

        // MessageSendCommand.RunAsync itself, not a re-implementation of it: this exercises
        // NodeBootstrap.EnsureAsync, the owner-fingerprint refusal path, and the --to/blank-body
        // checks the command's own ExecuteAsync would otherwise never see covered.
        await NodeBootstrapSeed.SeedGitHubConnectionAsync(_postgres.Store, cts.Token);

        Guid ownerId;
        await using (IDocumentSession bootstrapSession = _postgres.Store.LightweightSession())
        {
            BootstrapContext context = await NodeBootstrap.EnsureAsync(bootstrapSession, cts.Token);
            await bootstrapSession.SaveChangesAsync(cts.Token);
            ownerId = context.OwnerId;
        }

        // h9k project join is what actually claims this — MessageSendCommand.RunAsync itself
        // only reads it back and refuses when it is missing, so this stands in for that join.
        await using (IDocumentSession claimSession = _postgres.Store.LightweightSession())
        {
            OwnerAggregate owner = await claimSession.Events.AggregateStreamAsync<OwnerAggregate>(ownerId, token: cts.Token)
                ?? throw new InvalidOperationException("Expected the owner stream bootstrap just started to exist.");
            claimSession.Events.Append(ownerId, OwnerDecider.ClaimRoot(owner, "owner-a-fingerprint", verified: true, Now));
            await claimSession.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession sendSession = _postgres.Store.LightweightSession())
        {
            MessageSendCommand.Settings sendSettings = new()
            {
                Text = "pick this up",
                To = $"node:{nodeB}",
                About = "task-9",
            };
            int exitCode = await MessageSendCommand.RunAsync(sendSession, sendSettings, cts.Token);
            exitCode.Should().Be(ExitCodes.Ok);
        }

        Guid nodeA;
        await using (IDocumentSession lookupSession = _postgres.Store.LightweightSession())
        {
            MessageDetails queued = (await lookupSession.Query<MessageDetails>()
                .Where(message => message.Seq == 1).ToListAsync(cts.Token)).Single();
            queued.About.Should().Be("task-9");
            nodeA = queued.FromNodeId;
        }

        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);

        // The daemon's own flush is what actually lands it — MessageSendCommand.RunAsync only queues.
        await using (IDocumentSession flushSession = _postgres.Store.LightweightSession())
        {
            await outbox.FlushAsync(flushSession, RepositoryPath, nodeA, committerA, signingKeyA, Now, cts.Token);
        }

        // The daemon's own probe+read is what makes it visible to node B at all.
        await using (IDocumentSession readSession = _postgres.Store.LightweightSession())
        {
            await inbox.ReadFromAsync(readSession, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(1), cancellationToken: cts.Token);
        }

        // h9k message handle <id>: MessageHandleCommand.RunAsync itself, including its
        // already-handled early return and its own append onto the same stream id.
        string shortId = TaskListCommand.ShortId(MessageStreamId.ForMessage(nodeA, 1));
        await using (IDocumentSession handleSession = _postgres.Store.LightweightSession())
        {
            MessageHandleCommand.Settings handleSettings = new() { Id = shortId };
            int exitCode = await MessageHandleCommand.RunAsync(handleSession, handleSettings, cts.Token);
            exitCode.Should().Be(ExitCodes.Ok);
        }

        await using (IDocumentSession assertSession = _postgres.Store.LightweightSession())
        {
            MessageDetails? handled = await assertSession.LoadAsync<MessageDetails>(MessageStreamId.ForMessage(nodeA, 1), cts.Token);
            handled!.About.Should().Be("task-9");
            handled.HandledAt.Should().NotBeNull();
        }

        // Handling twice takes the already-handled early return rather than a second append.
        await using IDocumentSession secondHandleSession = _postgres.Store.LightweightSession();
        MessageHandleCommand.Settings secondHandleSettings = new() { Id = shortId };
        int secondExitCode = await MessageHandleCommand.RunAsync(secondHandleSession, secondHandleSettings, cts.Token);
        secondExitCode.Should().Be(ExitCodes.Ok);
        IReadOnlyList<JasperFx.Events.IEvent> events = await secondHandleSession.Events.FetchStreamAsync(
            MessageStreamId.ForMessage(nodeA, 1), token: cts.Token);
        events.Count(@event => @event.Data is MessageHandled).Should().Be(
            1, "handling an already-handled message must take the early return, not append a second MessageHandled event");
    }

    /// <summary>
    /// <c>MessageIdResolver.ResolveReceivedAsync</c> given a full id now resolves through
    /// <c>LoadAsync</c> rather than materializing every received message first (independent
    /// pre-PR review: a full id still paid for a full-history scan even though the id alone is
    /// already enough to answer the question).
    /// </summary>
    [Fact]
    public async Task ResolveReceivedAsync_WithAFullId_LoadsDirectlyAndRefusesAnUnreceivedOrUnknownOne()
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
            await MessageOutbox.QueueAsync(
                sendSession, nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note, "resolve me",
                Now, cts.Token);
            await outbox.FlushAsync(sendSession, RepositoryPath, nodeA, committerA, signingKeyA, Now, cts.Token);
        }

        await using (IDocumentSession readSession = _postgres.Store.LightweightSession())
        {
            await inbox.ReadFromAsync(readSession, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(1), cancellationToken: cts.Token);
        }

        Guid receivedId = MessageStreamId.ForMessage(nodeA, 1);
        await using IDocumentSession session = _postgres.Store.LightweightSession();

        MessageDetails resolved = await MessageIdResolver.ResolveReceivedAsync(session, receivedId.ToString(), cts.Token);
        resolved.Id.Should().Be(receivedId);

        Guid queuedButNeverReceivedId = MessageStreamId.ForMessage(nodeB, 1);
        Func<Task> unreceived = () => MessageIdResolver.ResolveReceivedAsync(session, queuedButNeverReceivedId.ToString(), cts.Token);
        await unreceived.Should().ThrowAsync<DomainNotFoundException>("that id was never received by this node");

        Func<Task> unknown = () => MessageIdResolver.ResolveReceivedAsync(session, DomainId.New().ToString(), cts.Token);
        await unknown.Should().ThrowAsync<DomainNotFoundException>("that id names no message stream at all");
    }

    /// <summary>
    /// Proves the fence <c>MessageHandleCommand.RunAsync</c> now takes — <c>FetchStreamStateAsync</c>
    /// then <c>AggregateStreamAsync</c> at that version, then <c>Append(expectedVersion: …)</c> — is
    /// what actually decides a race, the same deterministic two-session shape
    /// <c>ClaimAndLeaseArbitrationTests</c> already uses for a task claim: both sessions read the
    /// stream before either appends, both build a <see cref="MessageHandled"/> at the same expected
    /// version, and the database — not timing — lets exactly one land (independent pre-PR review,
    /// cycle 1: the previous unfenced projection-check-then-append let two concurrent
    /// <c>h9k message handle</c> invocations both observe an unhandled message and both append).
    /// </summary>
    [Fact]
    public async Task Two_racing_handles_of_the_same_message_produce_exactly_one_MessageHandled()
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
            await MessageOutbox.QueueAsync(
                sendSession, nodeA, "owner-a-fingerprint", MessageAudience.Node(nodeB), null, MessageKind.Note, "race me",
                Now, cts.Token);
            await outbox.FlushAsync(sendSession, RepositoryPath, nodeA, committerA, signingKeyA, Now, cts.Token);
        }

        await using (IDocumentSession readSession = _postgres.Store.LightweightSession())
        {
            await inbox.ReadFromAsync(readSession, RepositoryPath, nodeA, nodeB, ownerB, Now.AddSeconds(1), cancellationToken: cts.Token);
        }

        Guid streamId = MessageStreamId.ForMessage(nodeA, 1);

        // Two daemons — or two CLI invocations — reading the same received-but-unhandled message
        // before either one appends, exactly what a genuine race looks like.
        await using IDocumentSession first = _postgres.Store.LightweightSession();
        await using IDocumentSession second = _postgres.Store.LightweightSession();

        Marten.Events.StreamState fence1 = (await first.Events.FetchStreamStateAsync(streamId, cts.Token))!;
        Marten.Events.StreamState fence2 = (await second.Events.FetchStreamStateAsync(streamId, cts.Token))!;

        MessageAggregate view1 = (await first.Events.AggregateStreamAsync<MessageAggregate>(
            streamId, version: fence1.Version, token: cts.Token))!;
        MessageAggregate view2 = (await second.Events.AggregateStreamAsync<MessageAggregate>(
            streamId, version: fence2.Version, token: cts.Token))!;

        first.Events.Append(streamId, expectedVersion: fence1.Version + 1, MessageDecider.Handle(view1, Now));
        second.Events.Append(streamId, expectedVersion: fence2.Version + 1, MessageDecider.Handle(view2, Now));

        await first.SaveChangesAsync(cts.Token);
        Func<Task> losing = () => second.SaveChangesAsync(cts.Token);
        await losing.Should().ThrowAsync<JasperFx.Events.EventStreamUnexpectedMaxEventIdException>(
            "the second handle must lose at the database, not by luck");

        await using IDocumentSession verify = _postgres.Store.LightweightSession();
        IReadOnlyList<JasperFx.Events.IEvent> events = await verify.Events.FetchStreamAsync(streamId, token: cts.Token);
        events.Count(@event => @event.Data is MessageHandled).Should().Be(
            1, "exactly one of the two racing handle attempts may ever land");
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

        public Task FlushAsync(
            string repositoryPath, Guid fromNodeId, IReadOnlyList<TransportEnvelope> envelopes, LedgerCommitter committer,
            LedgerSigningKey signingKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This fake only stands in for a read.");

        public Task<TransportReadResult> ReadSinceAsync(
            string repositoryPath, Guid senderNodeId, long sinceSeq, CancellationToken cancellationToken) =>
            Task.FromResult(TransportReadResult.Ok([], HighestSeqInspected, RejectedSeqs));

        public Task<IReadOnlyList<MessageOutboxTip>> ProbeAsync(string repositoryPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This fake only stands in for a read.");

        public Task SquashAsync(
            string repositoryPath, Guid fromNodeId, IReadOnlyList<TransportEnvelope> survivors, LedgerCommitter committer,
            LedgerSigningKey signingKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This fake only stands in for a read.");
    }
}
