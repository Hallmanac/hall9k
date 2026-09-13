using FluentAssertions;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Marten;
using Npgsql;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The migration the fan-out redesign's field rename needs (backlog 31): <see cref="IdeaDetails"/>
/// is Inline, so a finished idea's document — last written by an old build's projection, under the
/// retired <c>discardReason</c>/<c>discardedAt</c>/<c>promotedTaskId</c>/<c>promotedAt</c> keys —
/// never gets another event to trigger a re-materialization under the new
/// <c>archiveReason</c>/<c>archivedAt</c>/<c>cutTaskIds</c>/<c>concludedAt</c> shape. An older
/// document is simulated the way <see cref="TaskProjectionBackfillTests"/> does: the current
/// projection writes the document, then the new keys are stripped back off it in the database.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class IdeaProjectionBackfillTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_archived_idea_projected_before_the_field_rename_carries_its_reason_and_date_again()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;

        Guid ownerId = DomainId.New();
        Guid ideaId = DomainId.New();
        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<IdeaAggregate>(ideaId, IdeaDecider.Capture(
                ideaId, ownerId, "A thought that did not survive", projectId: null, Now, ProjectHome.None));
            await seed.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession seed = store.LightweightSession())
        {
            IdeaAggregate idea = (await seed.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cts.Token))!;
            seed.Events.Append(ideaId, IdeaDecider.Archive(idea, "Superseded by attachments", Now.AddDays(1), ownerId));
            await seed.SaveChangesAsync(cts.Token);
        }

        await StripKeysAsync(ideaId, ["archiveReason", "archivedAt", "cutTaskIds", "concludedAt"], cts.Token);

        await using (IQuerySession query = store.QuerySession())
        {
            IdeaDetails stale = (await query.LoadAsync<IdeaDetails>(ideaId, cts.Token))!;
            stale.ArchiveReason.Should().BeNull("the pre-rename document carries the reason under the old key name");
            stale.ArchivedAt.Should().BeNull();
        }

        IReadOnlyList<Guid> rebuilt = await IdeaDetailsProjectionBackfill.RunAsync(store, cts.Token);
        rebuilt.Should().Contain(ideaId);

        await using (IQuerySession query = store.QuerySession())
        {
            IdeaDetails repaired = (await query.LoadAsync<IdeaDetails>(ideaId, cts.Token))!;
            repaired.State.Should().Be(IdeaState.Archived);
            repaired.ArchiveReason.Should().Be("Superseded by attachments", "the stream always recorded why");
            repaired.ArchivedAt.Should().Be(Now.AddDays(1));
        }
    }

    [Fact]
    public async Task A_legacy_promoted_idea_projected_before_the_field_rename_carries_its_task_link_again()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;

        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid ideaId = DomainId.New();
        Guid taskId = DomainId.New();
        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<IdeaAggregate>(ideaId, IdeaDecider.Capture(
                ideaId, ownerId, "An idea promoted before the fan-out redesign shipped.",
                projectId, Now, ProjectHome.None));
            seed.Events.Append(ideaId, new IdeaPromoted(
                ideaId, taskId, projectId, "An idea promoted before the fan-out redesign shipped.",
                Now.AddDays(1), ownerId));
            await seed.SaveChangesAsync(cts.Token);
        }

        await StripKeysAsync(ideaId, ["archiveReason", "cutTaskIds", "concludedAt"], cts.Token);

        await using (IQuerySession query = store.QuerySession())
        {
            IdeaDetails stale = (await query.LoadAsync<IdeaDetails>(ideaId, cts.Token))!;
            stale.CutTaskIds.Should().BeEmpty("the pre-rename document never had a cutTaskIds key at all");
            stale.ConcludedAt.Should().BeNull();
        }

        IReadOnlyList<Guid> rebuilt = await IdeaDetailsProjectionBackfill.RunAsync(store, cts.Token);
        rebuilt.Should().Contain(ideaId);

        await using (IQuerySession query = store.QuerySession())
        {
            IdeaDetails repaired = (await query.LoadAsync<IdeaDetails>(ideaId, cts.Token))!;
            repaired.State.Should().Be(IdeaState.Concluded);
            repaired.CutTaskIds.Should().Equal([taskId], "the stream always named the task this idea became");
            repaired.ConcludedAt.Should().Be(Now.AddDays(1));
        }
    }

    [Fact]
    public async Task An_idea_already_carrying_every_current_key_is_not_re_projected_on_every_start()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;

        Guid ownerId = DomainId.New();
        Guid ideaId = DomainId.New();
        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<IdeaAggregate>(ideaId, IdeaDecider.Capture(
                ideaId, ownerId, "Still in discovery", projectId: null, Now, ProjectHome.None));
            await seed.SaveChangesAsync(cts.Token);
        }

        (await IdeaDetailsProjectionBackfill.RunAsync(store, cts.Token)).Should().BeEmpty(
            "the current projection always writes cutTaskIds, archiveReason, and concludedAt — "
            + "an empty array or an explicit null here, which is what makes an absent key a sound "
            + "marker for the pre-rename shape, and the backfill self-terminating");
    }

    private async Task StripKeysAsync(Guid ideaId, IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (string key in keys)
        {
            await using NpgsqlCommand command = new(
                "update mt_doc_ideadetails set data = data - @key where id = @id", connection);
            command.Parameters.AddWithValue("key", key);
            command.Parameters.AddWithValue("id", ideaId);
            (await command.ExecuteNonQueryAsync(cancellationToken)).Should().Be(1);
        }
    }
}
