using Npgsql;

namespace Hall9k.Tests.Integration;

/// <summary>
/// Loads the committed Marten 8.17 hall9k schema dump (task 29b0ca1a) into a fresh database on a
/// <see cref="PostgresFixture"/>'s own container, shared by every test that needs to start from a
/// real pre-upgrade install rather than a schema this build created from scratch.
/// </summary>
internal static class Marten8SchemaFixtureLoader
{
    /// <summary>
    /// A database of the caller's own on the fixture's container (the same isolation
    /// <c>EventStoreSchemaGuardTests</c>'s own helper of the same name uses), loaded with the
    /// committed 8.17 schema dump before Marten ever opens it. <c>psql</c>'s <c>\restrict</c> and
    /// <c>\unrestrict</c> meta-commands that pg_dump 18 wraps the script in are not SQL and Npgsql
    /// cannot execute them, so they are stripped before the rest of the script — plain DDL — is
    /// sent as one batch; Postgres itself, not Npgsql, splits and executes the statements within
    /// it, including the dollar-quoted function bodies.
    /// </summary>
    public static async Task<string> LoadIntoFreshDatabaseAsync(PostgresFixture postgres, CancellationToken cancellationToken)
    {
        string name = $"h9k_marten9_fixture_{Guid.NewGuid():N}";

        await using (NpgsqlConnection admin = new(postgres.ConnectionString))
        {
            await admin.OpenAsync(cancellationToken);
            await using NpgsqlCommand create = new($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        string connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = name }.ConnectionString;

        string script = string.Join('\n', (await File.ReadAllLinesAsync(SchemaFixturePath(), cancellationToken))
            .Where(line => !line.StartsWith("\\restrict", StringComparison.Ordinal)
                && !line.StartsWith("\\unrestrict", StringComparison.Ordinal)));

        await using (NpgsqlConnection target = new(connectionString))
        {
            await target.OpenAsync(cancellationToken);
            await using NpgsqlCommand load = new(script, target);
            await load.ExecuteNonQueryAsync(cancellationToken);
        }

        return connectionString;
    }

    private static string SchemaFixturePath() =>
        Path.Combine(RepositoryRoot(), "tests", "Hall9k.Tests", "Fixtures", "Schema", "marten-8.17-hall9k-schema.sql");

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Hall9k.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate the repository root (Hall9k.slnx) from " + AppContext.BaseDirectory);
    }
}
