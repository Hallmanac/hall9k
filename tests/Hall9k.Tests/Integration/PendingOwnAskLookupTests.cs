using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Messaging;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Infrastructure.Ids;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <see cref="PendingOwnAskLookup"/> (idea 202383dc, item 5): the requester's own outbox already
/// knows it asked, independent of any reply, flush, or task-stream replication back from the
/// holder — the fact <c>h9k status</c>'s own <c>WriteCooperativeTakeAsync</c> and <c>h9k task
/// show</c> both now fall back to for a holder that never received or processed the request at all
/// (independent pre-PR review, cycle 5, adversarial lens, medium).
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection("Hall9kHome")]
[Trait("Category", "Hall9kHome")]
public sealed class PendingOwnAskLookupTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"hall9k-pending-own-ask-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    public PendingOwnAskLookupTests(PostgresFixture postgres) => _postgres = postgres;

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
    public async Task An_unanswered_own_request_is_found_with_no_local_task_stream_record_at_all()
    {
        // The exact case a holder that never received or processed the request leaves behind:
        // nothing is ever appended to the task's own stream anywhere, on either node, so this
        // lookup is the only local fact this node has that it ever asked.
        Guid myNodeId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid projectId = DomainId.New();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        ClaimEnvelopeCodec.ClaimRequestRecord request = new(
            taskId, myNodeId, DomainId.New(), "my-fingerprint", "Picking this back up.", null);
        await MessageOutbox.QueueAsync(
            session, myNodeId, projectId, "my-fingerprint", MessageAudience.Node(DomainId.New()),
            taskId.ToString(), MessageKind.ClaimRequest, ClaimEnvelopeCodec.Encode(request), Now, CancellationToken.None);

        IReadOnlyList<PendingOwnAskLookup.PendingOwnAsk> asks =
            await PendingOwnAskLookup.FindUnansweredAsync(session, myNodeId, CancellationToken.None);

        asks.Should().ContainSingle(ask => ask.TaskId == taskId && ask.Reason == "Picking this back up.");
    }

    [Fact]
    public async Task A_received_grant_reply_clears_the_own_ask()
    {
        Guid myNodeId = DomainId.New();
        Guid holderNodeId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid projectId = DomainId.New();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        ClaimEnvelopeCodec.ClaimRequestRecord request = new(
            taskId, myNodeId, DomainId.New(), "my-fingerprint", "Picking this back up.", null);
        await MessageOutbox.QueueAsync(
            session, myNodeId, projectId, "my-fingerprint", MessageAudience.Node(holderNodeId),
            taskId.ToString(), MessageKind.ClaimRequest, ClaimEnvelopeCodec.Encode(request), Now, CancellationToken.None);

        // The reply itself, recorded on this node the same way an ordinary received message is —
        // never through the task's own stream, which is exactly the point: this fact is available
        // before any task-stream replication ever catches up.
        MessageEnvelopeV1 grantedEnvelope = new(
            1, Now.AddMinutes(1), holderNodeId, "holder-fingerprint", MessageAudience.Node(myNodeId),
            taskId.ToString(), MessageKind.ClaimGranted, ClaimEnvelopeCodec.Encode(new ClaimEnvelopeCodec.ClaimGrantedRecord(taskId)));
        session.Events.StartStream<MessageAggregate>(
            MessageStreamId.ForMessage(holderNodeId, projectId, 1),
            MessageDecider.Receive(holderNodeId, projectId, grantedEnvelope, Now.AddMinutes(2)));
        await session.SaveChangesAsync(CancellationToken.None);

        IReadOnlyList<PendingOwnAskLookup.PendingOwnAsk> asks =
            await PendingOwnAskLookup.FindUnansweredAsync(session, myNodeId, CancellationToken.None);

        asks.Should().BeEmpty("the reply already answers it, even though nothing has replicated back onto the task's own stream yet");
    }

    [Fact]
    public async Task No_own_requests_at_all_returns_empty_without_querying_replies()
    {
        await using IQuerySession session = _postgres.Store.QuerySession();
        IReadOnlyList<PendingOwnAskLookup.PendingOwnAsk> asks =
            await PendingOwnAskLookup.FindUnansweredAsync(session, DomainId.New(), CancellationToken.None);

        asks.Should().BeEmpty();
    }
}
