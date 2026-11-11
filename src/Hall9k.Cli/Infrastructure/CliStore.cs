using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
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
    /// <summary>
    /// The one client every remote git call in this process reads its token through, so its cache
    /// holds and <c>gh auth token</c> starts once per process, not once per git call.
    /// </summary>
    private static readonly ProjectGitHubClient GitHubClient = new();

    public static DocumentStore Open() => Open(CliConfig.ConnectionString, AutoCreate.CreateOnly);

    /// <summary>
    /// The same store every ordinary command opens through <see cref="Open()"/>, for a caller that
    /// already resolved its own connection string and needs a schema policy other than
    /// <see cref="AutoCreate.CreateOnly"/> — <c>ToolDoctor</c>'s read is the first such caller: it
    /// must never create schema, so it opens with <see cref="AutoCreate.None"/>.
    /// </summary>
    public static DocumentStore Open(string connectionString, AutoCreate autoCreate)
    {
        DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(connectionString);
            opts.ConfigureHall9k(autoCreate);
        });

        // The ledger and message calls a command makes through the shared connector code reach
        // github.com the same way the daemon's do: the project's own token on that one git call,
        // never the owner's credential helper starting sh and gh for it. A source whose store
        // has since been disposed answers null, and the call runs as it always has.
        GitRemoteAuthentication.Configure(new ProjectGitRemoteTokenSource(store, GitHubClient));
        return store;
    }
}
