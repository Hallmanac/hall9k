namespace Hall9k.Domain.Infrastructure.Persistence;

public static class Hall9kDatabase
{
    /// <summary>Matches docker-compose and the Aspire AppHost's pinned Postgres.</summary>
    public const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=hall9k;Username=postgres;Password=hall9k";

    public static string ResolveConnectionString(string? configured = null) =>
        configured
        ?? Environment.GetEnvironmentVariable("HALL9K_CONNECTION_STRING")
        ?? DefaultConnectionString;
}
