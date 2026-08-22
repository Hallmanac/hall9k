using Hall9k.Domain.Infrastructure.Bootstrap;
using Marten;

namespace Hall9k.Daemon;

/// <summary>This daemon's resolved identity: which node it is and whose it is (§6.2).</summary>
public sealed class NodeContext
{
    private readonly TaskCompletionSource _initialized =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        _initialized.TrySetResult();
    }

    /// <summary>
    /// Completes once this node has an identity, for the hosted services that need one before
    /// their first sweep rather than after it.
    /// <para>
    /// Bootstrap happens in exactly one place — the dispatch loop, after it has waited for
    /// Postgres — and that wait yields, which returns from <c>StartAsync</c> and lets the host
    /// start every remaining hosted service. A service that reads <see cref="NodeId"/> before
    /// its own first await is therefore reading it before anything has set it. The services that
    /// tick before sweeping (LeaseHeartbeatService, PullRequestMonitor) hide that behind their
    /// interval; a service whose whole point is to sweep immediately cannot, so it waits on this
    /// instead. Origin incident (2026-08-21): the pre-PR review of the Jira branch traced every
    /// daemon start logging "Card publication sweep failed" with exactly this, which pushed the
    /// first real sweep out by a full poll interval.
    /// </para>
    /// </summary>
    public Task WaitForInitializationAsync(CancellationToken cancellationToken) =>
        _initialized.Task.WaitAsync(cancellationToken);
}
