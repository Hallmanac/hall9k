using System.Diagnostics;
using FluentAssertions;
using Hall9k.Cli.DaemonControl;
using Hall9k.Daemon;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.Integration;
using Hall9k.Tests.TestSupport;
using Marten;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="LiveGateGuard.FindOnThisNodeAsync"/>'s real server-side query, against a real store
/// (independent pre-PR review, cycle 3, conformance and adversarial lenses, high): every other
/// <see cref="LiveGateGuardTests"/> case drives the injectable <c>findLiveGates</c> seam with a
/// fake, or points the real lookup at a store this CLI cannot reach at all — neither ever runs the
/// query's own SQL against a populated database. Marten writes a run's <c>activeGate</c> as an
/// explicit JSON <c>null</c> once a gate ends (no null-ignoring serializer option is configured),
/// and <c>d.data -&gt; 'activeGate' is not null</c> is true for that JSON <c>null</c> in Postgres —
/// which is the ordinary state of every non-terminal run between gates, not an edge case.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class LiveGateGuardStoreTests : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);

    private readonly ScopedTestHome scopedHome;

    public LiveGateGuardStoreTests(PostgresFixture postgres)
    {
        scopedHome = new ScopedTestHome(postgres.ConnectionString);
        Store = postgres.Store;
    }

    private DocumentStore Store { get; }

    public void Dispose() => scopedHome.Dispose();

    /// <summary>
    /// Two non-terminal runs on this node: one that never started a gate at all (the ordinary
    /// shape between gates, or before the first one) and one whose gate is genuinely alive right
    /// now. Before the fix, the first run's JSON-null <c>activeGate</c> passed the SQL filter and
    /// then threw a <see cref="NullReferenceException"/> reading <c>ActiveGate!.ProcessId</c> —
    /// caught by <see cref="LiveGateGuard.FindOnThisNodeAsync"/>'s own catch-all and reported as
    /// "could not check", silently skipping the real live gate too. This pins both halves at once:
    /// the gateless run is excluded without throwing, and the genuinely live one is still found.
    /// </summary>
    [Fact]
    public async Task A_non_terminal_run_with_no_gate_ever_started_is_excluded_without_throwing_while_a_genuinely_live_gate_is_still_found()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(Store, cts.Token);

        Guid gatelessRunId = DomainId.New();
        Guid liveGateRunId = DomainId.New();
        Process currentProcess = Process.GetCurrentProcess();
        DateTimeOffset currentProcessStartedAt = new(currentProcess.StartTime.ToUniversalTime(), TimeSpan.Zero);

        // Neither run's own task needs to exist: FindOnThisNodeAsync's query never joins to
        // TaskDetails, only to RunDetails.NodeId and RunDetails.State, so a fresh, otherwise
        // unobserved TaskId costs nothing here and keeps each seed to one event.
        await using (IDocumentSession session = Store.LightweightSession())
        {
            session.Events.StartStream<RunAggregate>(gatelessRunId, new RunDispatched(
                gatelessRunId, DomainId.New(), node.NodeId, node.OwnerId, 1, DomainId.New(),
                Path.Combine(Path.GetTempPath(), $"hall9k-livegate-wt-{gatelessRunId:N}"), "task/gateless",
                ExecutorMode.Subscription, Now, RunDirectory: RunPaths.GlobalDirectory(gatelessRunId),
                DispatchingNodeId: node.NodeId));

            session.Events.StartStream<RunAggregate>(liveGateRunId, new RunDispatched(
                liveGateRunId, DomainId.New(), node.NodeId, node.OwnerId, 1, DomainId.New(),
                Path.Combine(Path.GetTempPath(), $"hall9k-livegate-wt-{liveGateRunId:N}"), "task/live-gate",
                ExecutorMode.Subscription, Now, RunDirectory: RunPaths.GlobalDirectory(liveGateRunId),
                DispatchingNodeId: node.NodeId));
            session.Events.Append(
                liveGateRunId, new GateStarted(liveGateRunId, "test", currentProcess.Id, currentProcessStartedAt, Now));

            await session.SaveChangesAsync(cts.Token);
        }

        // node.NodeId was registered under this test host's own Environment.MachineName, the same
        // name FindOnThisNodeAsync's own NodeBootstrap.EnsureAsync call resolves against this same
        // database.
        IReadOnlyList<LiveGate>? result = await LiveGateGuard.FindOnThisNodeAsync(cts.Token);

        result.Should().NotBeNull("the query itself must succeed against a real, reachable store");
        result!.Select(gate => gate.RunId).Should().BeEquivalentTo([liveGateRunId],
            "the gateless run's JSON-null activeGate must never reach ActiveGate!.ProcessId, and the live gate must still be reported");
    }
}
