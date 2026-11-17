using System.Collections.Concurrent;
using Hall9k.Connectors.ClaudeCode;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Marten.Linq.MatchesSql;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.ClaudeAccounts;

/// <summary>
/// Re-checks every refused Claude Code account on its own, without a spawn or a person asking, until a
/// check passes, and then resumes the runs that account's refusal held. The node-wide launch hold's
/// monitor is the precedent (<see cref="LaunchHoldMonitor"/>): a short poll, because nothing on the
/// machine announces that someone just ran /login. Unlike that hold, a refusal holds only its own
/// account's work, so this resumes only runs whose account is no longer refused.
/// </summary>
public sealed class ClaudeAccountMonitor(
    ClaudeAccountGate gate,
    RunSupervisor supervisor,
    LaunchHoldEngine launchHold,
    NodeContext node,
    IDocumentStore store,
    IOptions<DaemonOptions> options,
    ILogger<ClaudeAccountMonitor> logger) : BackgroundService
{
    /// <summary>The least time between two re-checks of one refused account, so a poll tick never runs claude for nothing.</summary>
    internal static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastChecked = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await node.WaitForInitializationAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Claude Code account sweep failed; will check again next tick");
            }

            try
            {
                await Task.Delay(options.Value.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        IReadOnlyList<ConnectionDetails> refused = await RefusedConnectionsAsync(session, cancellationToken);

        HashSet<Guid> stillRefused = [];
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (ConnectionDetails connection in refused)
        {
            if (_lastChecked.TryGetValue(connection.Id, out DateTimeOffset last) && now - last < RecheckInterval)
            {
                stillRefused.Add(connection.Id);
                continue;
            }

            _lastChecked[connection.Id] = now;
            ClaudeAccountCheckResult result = await gate.RecheckAsync(ClaudeCodeAccount.For(connection), cancellationToken);
            if (!result.Passed)
            {
                stillRefused.Add(connection.Id);
            }
        }

        foreach (RunDetails run in Resumable(await AccountHeldRunsAsync(session, node.NodeId, cancellationToken), stillRefused))
        {
            // The same two waits LaunchHoldMonitor applies before it resumes a run: a node that cannot launch
            // sessions would only fail the resume, and a node at its ceiling has no slot for it yet. Either way
            // the run stays held and the next sweep tries again.
            if (await launchHold.CurrentHoldAsync(node.NodeId, cancellationToken) is { LaunchHoldActive: true }
                || await AtOrOverCeilingAsync(session, node.NodeId, options.Value.MaxConcurrentTaskRuns, cancellationToken))
            {
                return;
            }

            logger.LogInformation(
                "Run {RunId}: its Claude Code account {ConnectionId} passes its check again; resuming",
                run.Id, run.AccountHeldConnectionId);
            await supervisor.ResumeLaunchHeldRunAsync(run, cancellationToken);
        }
    }

    /// <summary>
    /// The account-held runs whose account is no longer refused, oldest hold first: the ones a sweep resumes. A run
    /// held for an account that still fails its check stays held, and so does a run some other hold parked.
    /// </summary>
    internal static IReadOnlyList<RunDetails> Resumable(IReadOnlyList<RunDetails> held, IReadOnlySet<Guid> stillRefused) =>
        [.. held.Where(run => run.AccountHeldConnectionId is { } connectionId && !stillRefused.Contains(connectionId))];

    /// <summary>Whether this node's live runs fill its concurrency ceiling, counted the way LaunchHoldMonitor counts them.</summary>
    private static async Task<bool> AtOrOverCeilingAsync(
        IQuerySession session, Guid nodeId, int ceiling, CancellationToken cancellationToken) =>
        await session.Query<RunDetails>()
            .Where(run => run.NodeId == nodeId)
            .Where(run => run.MatchesSql(
                "d.data ->> 'state' in (?, ?, ?, ?)",
                RunState.Dispatched.Value, RunState.Running.Value, RunState.Verifying.Value, RunState.UnderReview.Value))
            .CountAsync(cancellationToken) >= Math.Max(1, ceiling);

    /// <summary>The claude-code connections whose account is refused now.</summary>
    internal static async Task<IReadOnlyList<ConnectionDetails>> RefusedConnectionsAsync(
        IQuerySession session, CancellationToken cancellationToken) =>
        await session.Query<ConnectionDetails>()
            .Where(connection => connection.MatchesSql("d.data ->> 'provider' = ?", WorkItemProvider.ClaudeCode.Value))
            .Where(connection => connection.ClaudeCodeRefused)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Every run this node holds for an account, the auto-pr-review sentinel runs it dispatched included, the
    /// same widening <see cref="LaunchHoldEngine.HeldRunsAsync"/> applies.
    /// </summary>
    private static async Task<IReadOnlyList<RunDetails>> AccountHeldRunsAsync(
        IQuerySession session, Guid nodeId, CancellationToken cancellationToken) =>
        await session.Query<RunDetails>()
            .Where(run => run.NodeId == nodeId || (run.NodeId == Guid.Empty && run.DispatchingNodeId == nodeId))
            .Where(run => run.MatchesSql("d.data ->> 'state' = ?", RunState.LaunchHeld.Value))
            .Where(run => run.AccountHeldConnectionId != null)
            .OrderBy(run => run.LaunchHeldAt)
            .ToListAsync(cancellationToken);
}
