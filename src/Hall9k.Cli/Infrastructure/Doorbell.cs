using Npgsql;

namespace Hall9k.Cli.Infrastructure;

/// <summary>
/// Raw Postgres NOTIFY — the doorbell, never the payload (Decisions Log #8). The durable
/// truth is already committed; this only wakes a listening daemon sooner than its poll.
/// A dead daemon hears nothing and catches up from the database on startup.
/// </summary>
public static class Doorbell
{
    public const string Channel = "hall9k";

    public static async Task RingAsync(string reason, CancellationToken cancellationToken)
    {
        try
        {
            await using NpgsqlConnection connection = new(CliConfig.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlCommand command = new("SELECT pg_notify(@channel, @reason)", connection);
            command.Parameters.AddWithValue("channel", Channel);
            command.Parameters.AddWithValue("reason", reason);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (NpgsqlException)
        {
            // The write already committed; a failed doorbell is not a failed command.
        }
    }
}
