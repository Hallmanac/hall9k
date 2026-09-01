using Hall9k.Daemon;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;

namespace Hall9k.Tests.Fakes;

/// <summary>
/// Registers a GitHub connection before <see cref="NodeContext.InitializeAsync"/> runs, so
/// <c>NodeBootstrap.EnsureAsync</c> finds one already on file and never falls through to
/// <c>NodeBootstrap.GhLogin</c> — the one path bootstrap takes that shells to the real `gh` and
/// reaches the real network, with no <c>ProcessRunner</c> seam a test could pin instead of it
/// (PLAN.md §16 #110, correcting #109's audit of this same path). A fresh
/// <see cref="PostgresFixture"/> database carries no connection at all, so every integration test
/// that bootstraps a node goes through here rather than calling
/// <see cref="NodeContext.InitializeAsync"/> directly, or it is the one dispatching the real
/// process this file exists to prevent.
/// </summary>
internal static class NodeBootstrapSeed
{
    public static async Task<NodeContext> NewNodeAsync(IDocumentStore store, CancellationToken cancellationToken)
    {
        await using (IDocumentSession session = store.LightweightSession())
        {
            ConnectionRegistered registered = ConnectionDecider.Register(
                DomainId.New(), DomainId.New(), WorkItemProvider.GitHub,
                "test-user", CredentialReference.GhCli, DateTimeOffset.UtcNow);
            session.Events.StartStream<ConnectionAggregate>(registered.Id, registered);
            await session.SaveChangesAsync(cancellationToken);
        }

        NodeContext node = new();
        await node.InitializeAsync(store, cancellationToken);
        return node;
    }
}
