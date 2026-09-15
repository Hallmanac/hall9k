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

    /// <summary>
    /// <paramref name="ghIdentityReader"/> is <see langword="null"/> for every caller that never
    /// asked for a GitHub identity refresh at all — every test through
    /// <c>NodeBootstrapSeed.NewNodeAsync</c> (or the two exempted files that seed a connection ahead
    /// of a deliberately deferred call here) included, none of which pass one. <c>DispatchLoop</c>,
    /// the one place bootstrap actually happens on a real daemon start, passes
    /// <see cref="NodeBootstrap.RealGhIdentityReader"/> so this install's numeric id and login are
    /// current at daemon start too, not only at <c>h9k project add</c>/<c>h9k project join</c>. A
    /// plain, unseamed daemon-start call was tried here first and reverted (independent pre-PR
    /// review, cycle 1, conformance and adversarial lenses, both medium): it shelled to the real
    /// <c>gh</c> unconditionally, reaching the real network on every one of <c>NodeBootstrapSeed</c>'s
    /// roughly 280 integration-test call sites — the one path that seed exists specifically to keep
    /// off gh and the network (PLAN.md §16 #110). <see cref="NodeBootstrap.GhIdentityReader"/> is
    /// the seam that lets a real daemon start opt in without dragging every test along with it.
    /// <para>
    /// Bounded rather than blocking: <c>NodeBootstrap</c>'s own gh read (<c>RunQuick</c>) gives up
    /// after 3 seconds with both output streams drained on background callbacks, so a daemon start
    /// against a gh that cannot answer at all still completes — this method's own callers
    /// (<see cref="WaitForInitializationAsync"/>) wait at most that long longer than before, never
    /// unboundedly.
    /// </para>
    /// </summary>
    public async Task InitializeAsync(
        IDocumentStore store, CancellationToken cancellationToken, GhIdentityReader? ghIdentityReader = null)
    {
        await using IDocumentSession session = store.LightweightSession();
        _context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        if (ghIdentityReader is not null)
        {
            await NodeBootstrap.RefreshGitHubIdentityAsync(session, _context.ConnectionId, cancellationToken, ghIdentityReader);
            await session.SaveChangesAsync(cancellationToken);
        }

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
