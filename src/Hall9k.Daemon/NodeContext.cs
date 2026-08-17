using Hall9k.Domain.Infrastructure.Bootstrap;
using Marten;

namespace Hall9k.Daemon;

/// <summary>This daemon's resolved identity: which node it is and whose it is (§6.2).</summary>
public sealed class NodeContext
{
    private BootstrapContext? _context;

    public Guid NodeId => Resolved.NodeId;
    public Guid OwnerId => Resolved.OwnerId;

    private BootstrapContext Resolved =>
        _context ?? throw new InvalidOperationException("NodeContext not initialized yet.");

    public async Task InitializeAsync(IDocumentStore store, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        _context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }
}
