using Hall9k.Daemon;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Marten.Linq.MatchesSql;

namespace Hall9k.Tests.Fakes;

/// <summary>
/// Registers a GitHub connection before <see cref="NodeContext.InitializeAsync"/> runs, so
/// <c>NodeBootstrap.EnsureAsync</c> finds one already on file and never falls through to its own
/// gh identity read (<c>GhLogin</c> when this doc comment was first written; idea 202383dc, A2b
/// widened the same call into a numeric-id-and-login read) — the one path bootstrap takes that
/// shells to the real `gh` and reaches the real network, with no <c>ProcessRunner</c> seam a test
/// could pin instead of it (PLAN.md §16 #110, correcting #109's audit of this same path). A fresh
/// <see cref="PostgresFixture"/> database carries no connection at all, so every integration test
/// that bootstraps a node goes through here rather than calling
/// <see cref="NodeContext.InitializeAsync"/> directly, or it seeds through
/// <see cref="SeedGitHubConnectionAsync"/> and initializes deliberately, or it is the one
/// dispatching the real process this file exists to prevent.
/// </summary>
internal static class NodeBootstrapSeed
{
    public static async Task<NodeContext> NewNodeAsync(IDocumentStore store, CancellationToken cancellationToken)
    {
        await SeedGitHubConnectionAsync(store, cancellationToken);

        NodeContext node = new();
        await node.InitializeAsync(store, cancellationToken);
        return node;
    }

    /// <summary>
    /// Mints a node genuinely private to the caller rather than the one <see cref="NewNodeAsync"/>
    /// resolves and reuses for the rest of the database (<c>NodeBootstrap.EnsureAsync</c> looks a
    /// node up by machine name, so every ordinary call in one test class or one Postgres database
    /// finds and shares the same row). A synthetic, per-call machine name forces a fresh node
    /// instead — for a test whose own ceiling or expectation must rest on state it alone owns,
    /// immune to a sibling test's leftover live run, or an orphaned, never-awaited monitor
    /// <c>Task</c> still mutating one, on whatever node every other test in the class shares.
    /// </summary>
    public static async Task<NodeContext> NewIsolatedNodeAsync(IDocumentStore store, CancellationToken cancellationToken)
    {
        await SeedGitHubConnectionAsync(store, cancellationToken);

        NodeContext node = new();
        await node.InitializeAsync(
            store, cancellationToken, machineNameOverride: $"test-isolated-node-{Guid.NewGuid():N}");
        return node;
    }

    /// <summary>
    /// The half of <see cref="NewNodeAsync"/> that matters for gh-safety, split out for a test
    /// that needs to call <see cref="NodeContext.InitializeAsync"/> directly (deferred, to
    /// exercise the loop's pre-bootstrap window) rather than through <see cref="NewNodeAsync"/>
    /// itself — so that call sees a connection already on file too, explicitly rather than by
    /// way of an unrelated earlier <see cref="NewNodeAsync"/> call in the same test happening to
    /// share its store. Idempotent against a store this has already seeded (the same
    /// GitHub-provider filter <see cref="NodeBootstrap.EnsureAsync"/> itself queries with), so a
    /// class calling this — or <see cref="NewNodeAsync"/> — more than once against one shared
    /// database does not accumulate a fresh connection per call.
    /// <para>
    /// Registered with <see cref="Guid.Empty"/> as its owner rather than a fresh random id: no
    /// <c>OwnerDetails</c> row exists yet at this point (<see cref="NodeBootstrap.EnsureAsync"/>
    /// creates the real owner one step later, inside <see cref="NodeContext.InitializeAsync"/>),
    /// so a random id would name an owner that was never observed and never will be. Nothing
    /// reads <see cref="ConnectionDetails.OwnerId"/> today, but a random id would read as a real
    /// observation rather than the honest "not yet known" it actually is.
    /// </para>
    /// </summary>
    public static async Task<Guid> SeedGitHubConnectionAsync(IDocumentStore store, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();

        ConnectionDetails? existing = (await session.Query<ConnectionDetails>()
            .Where(c => c.MatchesSql("d.data ->> 'provider' = ?", WorkItemProvider.GitHub.Value))
            .Take(1).ToListAsync(cancellationToken)).FirstOrDefault();
        if (existing is not null)
        {
            return existing.Id;
        }

        ConnectionRegistered registered = ConnectionDecider.Register(
            DomainId.New(), Guid.Empty, WorkItemProvider.GitHub,
            "test-user", CredentialReference.GhCli, DateTimeOffset.UtcNow);
        session.Events.StartStream<ConnectionAggregate>(registered.Id, registered);

        // Every caller of this seed gets a confirmed GitHub identity, not merely a placeholder
        // login (idea 202383dc, A2b, item 1) — a test that needs join or the access mirror to
        // actually run its own gh calls needs an account id to resolve in the first place, and one
        // that does not care about identity at all is unaffected either way.
        session.Events.Append(
            registered.Id, new ConnectionGitHubIdentityObserved(registered.Id, 1, "test-user", DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);
        return registered.Id;
    }
}
