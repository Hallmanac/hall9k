using FluentAssertions;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using JasperFx;
using Marten;
using Weasel.Core;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// Task 29b0ca1a (Marten 9 security upgrade): proves <see cref="EventStoreSchemaGuard"/> brings a
/// real, pre-upgrade install current, not just a store this build created from scratch. The fixture
/// (<c>Fixtures/Schema/marten-8.17-hall9k-schema.sql</c>) is a <c>pg_dump --schema-only --no-owner
/// --no-privileges</c> of a live Marten 8.17 hall9k store, captured before this upgrade; loading it
/// into a fresh database on this class's own container and then running the guard is what an
/// install's own daemon restart does. One more database on the suite's shared container, never one
/// more container (Decisions Log #108).
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class Marten9SchemaFixtureTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_marten_8_17_schema_migrates_to_current_and_then_reads_and_writes()
    {
        string connectionString = await Marten8SchemaFixtureLoader.LoadIntoFreshDatabaseAsync(postgres, CancellationToken.None);

        await EventStoreSchemaGuard.EnsureCurrentAsync(connectionString, CancellationToken.None);

        using (DocumentStore migrated = DocumentStore.For(opts =>
        {
            opts.Connection(connectionString);
            opts.ConfigureHall9k(AutoCreate.None);
        }))
        {
            SchemaMigration second = await migrated.Storage.Database.CreateMigrationAsync(CancellationToken.None);
            second.Difference.Should().Be(SchemaPatchDifference.None,
                "a second migration against a database the guard just brought current has nothing left to do");
        }

        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(connectionString);
            opts.ConfigureHall9k(AutoCreate.CreateOnly);
        });

        await using (IQuerySession query = store.QuerySession())
        {
            await query.Query<OwnerDetails>().Take(1).ToListAsync(CancellationToken.None);
        }

        Guid ownerId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<OwnerAggregate>(ownerId, new OwnerRegistered(ownerId, "fixture-owner", null, DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(CancellationToken.None);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            StreamState fence = (await session.Events.FetchStreamStateAsync(ownerId, CancellationToken.None))!;
            OwnerAggregate owner = (await session.Events.AggregateStreamAsync<OwnerAggregate>(ownerId, token: CancellationToken.None))!;
            session.Events.Append(ownerId, expectedVersion: fence.Version + 1,
                OwnerDecider.ClaimRoot(owner, "fixture-fingerprint", verified: true, DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(CancellationToken.None);
        }
    }
}
