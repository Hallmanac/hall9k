using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using JasperFx.Events;
using Marten;
using Npgsql;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The migration the prompt feed's own projection change needs (idea d805fd8b, piece 5).
/// <see cref="LearningDetails"/> is Inline, and a lesson's only later event is its retirement,
/// which never touches <see cref="LearningDetails.RecordedOnNodeId"/> — so a row written before
/// that property existed keeps a null node forever, reads as
/// <see cref="LessonProvenanceMark.AgentOnUnobservedNode"/>, and is held out of every prompt while
/// being described to a reader as recorded on a node nobody observed. The event's own metadata
/// says otherwise, which is what makes this a repair rather than a gap (independent pre-PR review,
/// cycle 3, conformance lens). An older document is simulated the way
/// <see cref="IdeaProjectionBackfillTests"/> does: the current projection writes the row, then the
/// new key is stripped back off it in the database.
/// <para>
/// A real node is seeded first, and through <c>NewNodeAsync</c>'s machine-name lookup rather than
/// an isolated synthetic one, because the replay reads what <c>EventOriginStampingListener</c>
/// actually persisted and that listener resolves this machine's own <see cref="Node.NodeDetails"/>
/// row. With no such row the persisted header is the listener's still-bootstrapping
/// <see cref="Guid.Empty"/>, and this class would then prove the opposite of what it claims.
/// </para>
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class LearningProjectionBackfillTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly ScopedTestHome _scopedHome = new();

    public void Dispose() => _scopedHome.Dispose();

    [Fact]
    public async Task An_agent_recorded_lesson_projected_before_the_recording_node_landed_reaches_a_prompt_again()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid learningId = await RecordAsync(store, node, FromARun(node.OwnerId), cts.Token);

        await StripKeysAsync(learningId, ["recordedOnNodeId"], cts.Token);

        await using (IQuerySession query = store.QuerySession())
        {
            LearningDetails stale = (await query.LoadAsync<LearningDetails>(learningId, cts.Token))!;
            stale.RecordedOnNodeId.Should().BeNull("the pre-change document never had the key at all");
            LessonProvenanceMark.Of(stale.Provenance, stale.RecordedOnNodeId, node.NodeId).Should()
                .Be(LessonProvenanceMark.AgentOnUnobservedNode, "which is what holds it out of every prompt");
        }

        IReadOnlyList<Guid> rebuilt = await LearningDetailsProjectionBackfill.RunAsync(store, cts.Token);
        rebuilt.Should().Contain(learningId);

        await using (IQuerySession query = store.QuerySession())
        {
            LearningDetails repaired = (await query.LoadAsync<LearningDetails>(learningId, cts.Token))!;
            repaired.RecordedOnNodeId.Should().Be(
                node.NodeId, "the event's own metadata always named the node that appended it");
            LessonProvenanceMark.Of(repaired.Provenance, repaired.RecordedOnNodeId, node.NodeId).Should()
                .Be(LessonProvenanceMark.AgentOnThisNode);
        }
    }

    /// <summary>
    /// The case that makes this a repair rather than a convenience: a retired lesson is terminal,
    /// so nothing else in the platform would ever rewrite its row.
    /// </summary>
    [Fact]
    public async Task A_retired_lesson_is_repaired_too_because_nothing_else_will_ever_rewrite_its_row()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid learningId = await RecordAsync(store, node, FromARun(node.OwnerId), cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            LearningAggregate learning =
                (await session.Events.AggregateStreamAsync<LearningAggregate>(learningId, token: cts.Token))!;
            session.Events.Append(
                learningId, LearningDecider.Retire(learning, "No longer true", node.OwnerId, Now.AddDays(1)));
            await session.SaveChangesAsync(cts.Token);
        }

        await StripKeysAsync(learningId, ["recordedOnNodeId"], cts.Token);

        (await LearningDetailsProjectionBackfill.RunAsync(store, cts.Token)).Should().Contain(learningId);

        await using (IQuerySession query = store.QuerySession())
        {
            LearningDetails repaired = (await query.LoadAsync<LearningDetails>(learningId, cts.Token))!;
            repaired.RecordedOnNodeId.Should().Be(node.NodeId);
            repaired.Status.Should().Be(LearningStatus.Retired, "the replay restores the ending too");
            repaired.RetireReason.Should().Be("No longer true");
        }
    }

    [Fact]
    public async Task A_lesson_already_carrying_the_key_is_not_re_projected_on_every_start()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid learningId = await RecordAsync(
            store, node, RecordedProvenance.FromShell(node.OwnerId), cts.Token);

        (await LearningDetailsProjectionBackfill.RunAsync(store, cts.Token)).Should().NotContain(
            learningId,
            "the current projection always writes recordedOnNodeId, as a value or as an explicit "
            + "null, which is what makes the backfill self-terminating");
    }

    private static async Task<Guid> RecordAsync(
        DocumentStore store,
        NodeContext node,
        RecordedProvenance provenance,
        CancellationToken cancellationToken)
    {
        Guid learningId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        LearningRecorded recorded = LearningDecider.Record(
            learningId, KnowledgeScope.Project, DomainId.New(),
            "Integration tests need Docker running before dotnet test", provenance, Now);
        StreamAction stream = session.Events.StartStream<LearningAggregate>(learningId, recorded);
        EventRecordingNode.StampAtAppend(stream, node.NodeId);
        await session.SaveChangesAsync(cancellationToken);
        return learningId;
    }

    private static RecordedProvenance FromARun(Guid ownerId) =>
        new(ownerId, DomainId.New(), DomainId.New(), HumanAttendance.Unattended);

    private async Task StripKeysAsync(Guid learningId, IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (string key in keys)
        {
            await using NpgsqlCommand command = new(
                "update mt_doc_learningdetails set data = data - @key where id = @id", connection);
            command.Parameters.AddWithValue("key", key);
            command.Parameters.AddWithValue("id", learningId);
            (await command.ExecuteNonQueryAsync(cancellationToken)).Should().Be(1);
        }
    }
}
