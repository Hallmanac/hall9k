using Hall9k.Domain.Infrastructure.Persistence;

namespace Hall9k.Cli.Infrastructure;

public static class CliConfig
{
    /// <summary>
    /// The resolved connection string, or a refusal: an unconfigured install throws rather
    /// than falling back to a guessed default (Decisions Log #58), and Program's top-level
    /// catch turns that refusal into a doctor check instead of a raw exception.
    /// </summary>
    public static string ConnectionString =>
        Hall9kDatabase.Resolve().Value ?? throw new DatabaseNotConfiguredException();
}
