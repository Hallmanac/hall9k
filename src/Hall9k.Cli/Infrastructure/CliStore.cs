using Hall9k.Domain.Infrastructure.Persistence;
using JasperFx;
using Marten;

namespace Hall9k.Cli.Infrastructure;

/// <summary>
/// The thin-writer store (Decisions Log #8): plain DocumentStore, no DI container, no
/// Wolverine host — h9k is execute-and-exit and gets called by agents mid-run.
/// </summary>
public static class CliStore
{
    public static DocumentStore Open() => Open(CliConfig.ConnectionString, AutoCreate.CreateOnly);

    /// <summary>
    /// The same store every ordinary command opens through <see cref="Open()"/>, for a caller that
    /// already resolved its own connection string and needs a schema policy other than
    /// <see cref="AutoCreate.CreateOnly"/> — <c>ToolDoctor</c>'s read is the first such caller: it
    /// must never create schema, so it opens with <see cref="AutoCreate.None"/>.
    /// </summary>
    public static DocumentStore Open(string connectionString, AutoCreate autoCreate) => DocumentStore.For(opts =>
    {
        opts.Connection(connectionString);
        opts.ConfigureHall9k(autoCreate);
    });
}
