using FluentAssertions;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Tests.Fakes;
using JasperFx;
using Marten;
using Npgsql;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <see cref="EventStoreSchemaGuard"/> is the headless half of the same repair
/// <c>DatabaseDoctorTests.A_schema_that_predates_this_build_is_detected_and_updated_by_assume_yes</c>
/// already proves for <c>h9k doctor</c>/<c>h9k daemon start</c> (event stamping, idea 202383dc,
/// PLAN.md §16 #192, follow-up review finding on PR #370): <c>h9kd</c> launched
/// directly by an OS autostart manager after a reboot never goes through either of those, so this
/// is what stands between that path and a raw <c>SchemaMigrationException</c> against a schema
/// that predates event metadata headers.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class EventStoreSchemaGuardTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_schema_that_predates_this_build_is_updated_before_the_real_store_ever_opens()
    {
        string stale = await FreshDatabaseAsync(CancellationToken.None);

        using (DocumentStore oldBuild = DocumentStore.For(opts =>
        {
            opts.Connection(stale);
            opts.AutoCreateSchemaObjects = AutoCreate.All;
        }))
        {
            await using IDocumentSession session = oldBuild.LightweightSession();
            session.Events.StartStream(Guid.NewGuid(), new object[] { new OwnerRegisteredForTest() });
            await session.SaveChangesAsync(CancellationToken.None);
        }

        await EventStoreSchemaGuard.EnsureCurrentAsync(stale, CancellationToken.None);

        // The proof that matters: the daemon's own store, opened exactly as Hall9k.Daemon's
        // Program.cs opens it (headers enabled, CreateOnly), now writes against the once-stale
        // database without the SchemaMigrationException an unguarded daemon start would still hit.
        using (DocumentStore daemonStore = DocumentStore.For(opts =>
        {
            opts.Connection(stale);
            opts.ConfigureHall9k(AutoCreate.CreateOnly);
        }))
        {
            await using IDocumentSession session = daemonStore.LightweightSession();
            session.Events.StartStream(Guid.NewGuid(), new object[] { new OwnerRegisteredForTest() });
            await session.SaveChangesAsync(CancellationToken.None);
        }
    }

    /// <summary>A minimal stand-in event: this test only needs something to append, not a real aggregate.</summary>
    private sealed record OwnerRegisteredForTest;

    /// <summary>
    /// A database of the caller's own on this fixture's container, the identical isolation
    /// <c>DatabaseDoctorTests</c>'s own helper of the same name uses, so this schema-staleness
    /// question is asked without depending on nothing else having touched the container first.
    /// </summary>
    private async Task<string> FreshDatabaseAsync(CancellationToken cancellationToken)
    {
        string name = $"h9k_schema_guard_{Guid.NewGuid():N}";

        await using (NpgsqlConnection admin = new(postgres.ConnectionString))
        {
            await admin.OpenAsync(cancellationToken);
            await using NpgsqlCommand create = new($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        return new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = name }.ConnectionString;
    }
}
