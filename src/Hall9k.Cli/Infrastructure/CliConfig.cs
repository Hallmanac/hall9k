using Hall9k.Domain.Infrastructure.Persistence;

namespace Hall9k.Cli.Infrastructure;

public static class CliConfig
{
    public static string ConnectionString => Hall9kDatabase.ResolveConnectionString();
}
