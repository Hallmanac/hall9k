using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Domain.Features.Connection;
using Hall9k.Tests.Fakes;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <see cref="NodeContext.InitializeAsync"/>'s own <c>ghIdentityReader</c> parameter is the seam
/// that lets a real daemon start (<c>DispatchLoop</c>, which passes
/// <c>NodeBootstrap.RealGhIdentityReader</c>) refresh this install's GitHub identity without
/// dragging every other caller of <see cref="NodeContext.InitializeAsync"/> — <c>NodeBootstrapSeed</c>'s
/// roughly 280 integration-test call sites included — into shelling out to the real <c>gh</c>. This
/// pins a fake reader instead, proving the refresh actually runs and updates the connection when a
/// caller opts in, the way a caller that omits the parameter (every other test in this tree) never
/// does.
/// <para>
/// The connection is seeded explicitly with <see cref="NodeBootstrapSeed.SeedGitHubConnectionAsync"/>
/// ahead of a bare <c>NodeContext</c> construction and a direct <see cref="NodeContext.InitializeAsync"/>
/// call, the identical shape <c>RenderSweepTests</c> and <c>PrReviewTaskEngineTests</c> already use to
/// exercise a hosted service's pre-bootstrap window without ever reaching the real gh
/// (<see cref="Hall9k.Tests.Domain.NodeBootstrapConventionGuardTests"/> allows exactly one such
/// construction per file).
/// </para>
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class NodeContextGhIdentityRefreshTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    // SeedGitHubConnectionAsync is idempotent — it reuses whatever GitHub connection is already on
    // file — so a database left holding a prior test's connection would hand this test that one's
    // already-mutated identity instead of the fresh "test-user"/1 every assertion below assumes.
    public async Task InitializeAsync() => await postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task InitializeAsync_refreshes_the_connections_identity_when_a_reader_is_supplied()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        DocumentStore store = postgres.Store;
        Guid connectionId = await NodeBootstrapSeed.SeedGitHubConnectionAsync(store, cts.Token);

        // Same numeric id as NodeBootstrapSeed's own observation (1), a renamed login: a changed
        // id is refused outright as a different account (ConnectionDecider.ObserveGitHubIdentity),
        // so a renamed login under the identical id is the one change this refresh actually records.
        NodeContext node = new();
        await node.InitializeAsync(store, cts.Token, () => """{"id": 1, "login": "renamed-login"}""");

        await using IDocumentSession session = store.LightweightSession();
        ConnectionDetails connection = (await session.LoadAsync<ConnectionDetails>(connectionId, cts.Token))!;
        connection.GitHubAccountId.Should().Be(1, "the fake reader answered with the identical account");
        connection.GitHubLogin.Should().Be("renamed-login", "the fake reader answered with this login, and a reader was supplied");
    }

    [Fact]
    public async Task InitializeAsync_never_touches_the_connection_when_no_reader_is_supplied()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        DocumentStore store = postgres.Store;
        Guid connectionId = await NodeBootstrapSeed.SeedGitHubConnectionAsync(store, cts.Token);

        NodeContext node = new();
        await node.InitializeAsync(store, cts.Token);

        await using IDocumentSession session = store.LightweightSession();
        ConnectionDetails connection = (await session.LoadAsync<ConnectionDetails>(connectionId, cts.Token))!;
        connection.GitHubAccountId.Should().Be(1, "SeedGitHubConnectionAsync's own observation, unchanged: no reader means no refresh attempt at all");
        connection.GitHubLogin.Should().Be("test-user");
    }
}
