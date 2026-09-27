using Npgsql;

namespace Hall9k.Cli.Diagnostics;

/// <summary>
/// A raw Npgsql connection attempt — no Wolverine host, no Marten document store — cheap
/// enough that running it before every command that needs a database survives the
/// thin-CLI rule (AGENTS.md §8; Decisions Log #58). This is questions 2 and 3 of the
/// doctor check: is it reachable, and is the schema there.
/// </summary>
public static class DatabaseReachability
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    public static async Task<ReachabilityReport> ProbeAsync(string connectionString, CancellationToken cancellationToken)
    {
        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return new ReachabilityReport(
                ReachabilityStatus.OtherError,
                $"The connection string could not be parsed: {exception.Message}",
                Host: string.Empty, Port: 0, Database: string.Empty);
        }

        string host = builder.Host is { Length: > 0 } configuredHost ? configuredHost : "localhost";
        int port = builder.Port is 0 ? 5432 : builder.Port;
        string database = builder.Database ?? string.Empty;

        // Pooling off, deliberately: a pooled idle connector survives the server side dying
        // underneath it (a container recreate, a restart) and Npgsql's own idle check never
        // notices, since PoolingDataSource.CheckIdleConnector only looks at IsBroken and
        // connection lifetime, never socket liveness — so a probe meant to prove a freshly
        // (re)started Postgres is actually answering would otherwise hand back a stale
        // connector that opens with no I/O at all and reads as Reachable (lesson ad0727c5;
        // field reports 2026-09-27, Mac and Windows).
        builder.Pooling = false;
        await using NpgsqlConnection connection = new(builder.ConnectionString);
        using CancellationTokenSource timeout = new(ProbeTimeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await connection.OpenAsync(linked.Token);
            return new ReachabilityReport(ReachabilityStatus.Reachable, string.Empty, host, port, database);
        }
        catch (PostgresException exception) when (exception.SqlState
            is PostgresErrorCodes.InvalidPassword or PostgresErrorCodes.InvalidAuthorizationSpecification)
        {
            return new ReachabilityReport(ReachabilityStatus.AuthenticationFailed, exception.MessageText, host, port, database);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            return new ReachabilityReport(ReachabilityStatus.DatabaseMissing, exception.MessageText, host, port, database);
        }
        catch (PostgresException exception)
        {
            return new ReachabilityReport(ReachabilityStatus.OtherError, exception.MessageText, host, port, database);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ReachabilityReport(
                ReachabilityStatus.RefusedConnection, $"No answer within {ProbeTimeout.TotalSeconds:0}s", host, port, database);
        }
        catch (NpgsqlException exception)
        {
            return new ReachabilityReport(ReachabilityStatus.RefusedConnection, exception.Message, host, port, database);
        }
    }

    /// <summary>
    /// Whether Marten's own schema is there — checked against <c>mt_streams</c>, the event
    /// store table every Hall9k stream lands in, present the moment any Marten session has
    /// touched this database once. Assumes the caller already knows the connection is
    /// reachable (question 2 answered yes) before asking question 3.
    /// </summary>
    public static async Task<bool> SchemaPresentAsync(string connectionString, CancellationToken cancellationToken)
    {
        // Pooling off here too, the same reason ProbeAsync just disabled it: an idle pooled
        // connector left by an earlier call in this same process (this doctor's own
        // ToolDoctor.GitHubCliRequirementAsync check, for one) can survive the server side
        // dying underneath it and read as open with no I/O, so this query would otherwise run
        // against a dead socket rather than the freshly (re)started Postgres it means to check.
        NpgsqlConnectionStringBuilder builder = new(connectionString) { Pooling = false };
        await using NpgsqlConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        // Cast to text: Npgsql has no default read mapping for the regclass type itself,
        // and all this needs is whether the cast produced a name or a null.
        await using NpgsqlCommand command = new("select to_regclass('public.mt_streams')::text", connection);
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null and not DBNull;
    }
}
