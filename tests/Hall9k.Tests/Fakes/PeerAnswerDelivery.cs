using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Replication;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Tests.Integration;
using JasperFx;
using Marten;

namespace Hall9k.Tests.Fakes;

/// <summary>
/// Hands one receiving store the events a peer on an older build would still send: the peer
/// answers a catch-up request this receiver minted, through the same in-memory transport and fake
/// ledger <see cref="EventCatchUpTests"/> uses. The receiver is the store a test is about, so it is
/// passed in; the peer gets a store of its own in a throwaway schema on the same container.
/// </summary>
internal static class PeerAnswerDelivery
{
    private const string RepositoryPath = "/repo-peer-answer";
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    public static async Task<EventReplicationReadResult> DeliverAsync(
        PostgresFixture postgres, DocumentStore receiver, Guid receiverNodeId, Guid receiverProjectId,
        IReadOnlyList<(Guid StreamId, object Event)> events, CancellationToken cancellationToken)
    {
        Guid peerNodeId = DomainId.New();
        Guid peerProjectId = DomainId.New();
        Guid ownerId = DomainId.New();

        FakeLedger ledger = new();
        string nodeFile = $"node_id: \"{peerNodeId}\"\npublic_key: \"ssh-ed25519 AAAAFAKE{peerNodeId:N} test\"\n";
        LedgerCommitter committer = new("peer", "peer@hall9k.local");
        LedgerSigningKey signingKey = new("/dev/null/peer");
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, $"refs/hall9k/ledger/nodes/{peerNodeId}", $"nodes/{peerNodeId}/node.yaml", nodeFile,
                ExpectedBlobId: null, "seed node file", committer, signingKey),
            cancellationToken);
        InMemoryMessageTransport transport = new(ledger);

        // The receiver only admits another node's events inside an answer to a request it minted.
        Guid requestId = DomainId.New();
        await using (IDocumentSession session = receiver.LightweightSession())
        {
            session.Store(new EventCatchUpRequest
            {
                Id = requestId,
                ProjectId = receiverProjectId,
                Candidates = [peerNodeId],
                CandidateIndex = 0,
                SentAt = Now,
            });
            await session.SaveChangesAsync(cancellationToken);
        }

        List<EventReplicationCodec.ReplicatedEventRecord> answer = [.. events.Select((entry, index) => new EventReplicationCodec.ReplicatedEventRecord(
            entry.StreamId,
            entry.Event.GetType().FullName!,
            System.Text.Json.JsonSerializer.Serialize(entry.Event, entry.Event.GetType()),
            DomainId.New(),
            index + 1,
            peerNodeId,
            "owner-peer-fingerprint",
            Now,
            peerProjectId))];

        await using DocumentStore sender = DocumentStore.For(options =>
        {
            options.Connection(postgres.ConnectionString);
            options.DatabaseSchemaName = $"peer_answer_{DomainId.New():N}";
            options.ConfigureHall9k(AutoCreate.All);
        });
        await using (IDocumentSession session = sender.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(peerNodeId, new NodeRegistered(peerNodeId, ownerId, "peer", "macOS", Now));
            await MessageOutbox.QueueAsync(
                session, peerNodeId, peerProjectId, "owner-peer-fingerprint", MessageAudience.Node(receiverNodeId),
                about: requestId.ToString(), MessageKind.Events, EventReplicationCodec.EncodeBatch(answer), Now,
                cancellationToken);
            await new MessageOutbox(transport).FlushAsync(
                session, RepositoryPath, peerNodeId, peerProjectId, "shared-project-key", adoptUnassigned: false,
                committer, signingKey, Now.AddSeconds(1), cancellationToken);
        }

        string keyLine = $"ssh-ed25519 AAAAFAKE{peerNodeId:N} test";
        TrustedOwner owner = new(
            "owner-root-fingerprint", "ssh-ed25519 AAAAFAKEroot test",
            [new TrustedNode(peerNodeId.ToString(), keyLine, NodeKeyStore.Fingerprint(keyLine), Now)]);
        TrustChain trustChain = new(
            new Dictionary<string, TrustedOwner> { ["owner-root-fingerprint"] = owner },
            [new ProjectMember("owner-root-fingerprint", MembershipRole.Owner, Now)]);

        await using IDocumentSession reading = receiver.LightweightSession();
        return await new EventReplicationInbox(transport).ReadFromAsync(
            reading, RepositoryPath, peerNodeId, receiverProjectId, receiverNodeId, "owner-receiver-fingerprint",
            Now.AddSeconds(2), trustChain, cancellationToken);
    }
}
