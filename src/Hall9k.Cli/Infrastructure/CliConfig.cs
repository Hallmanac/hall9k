namespace Hall9k.Cli.Infrastructure;

public static class CliConfig
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=hall9k;Username=postgres;Password=hall9k";

    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("HALL9K_CONNECTION_STRING") ?? DefaultConnectionString;
}
