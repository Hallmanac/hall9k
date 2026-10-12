using FluentAssertions;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon;
using Hall9k.Daemon.Dispatch;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The dispatch sweep gives back a ledger holder this node still has on a task that was moved to
/// another node (origin incident, 2026-10-08: three tasks failed on one node, were assigned to
/// another, and were stood down from about every 8 seconds for 66 hours). Driven through
/// <see cref="DispatchEngine"/>'s own <see cref="ILedger"/> seam with a <see cref="FakeLedger"/>,
/// so nothing here creates a branch, touches git, or calls GitHub. Every assertion is about the
/// task each test seeded itself, under a node of its own, because every test shares one database.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class PlacedElsewhereHolderReleaseTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private const string RepositoryPath = "/repos/placed-elsewhere-test";

    [Fact]
    public async Task A_queued_task_placed_on_another_node_has_its_holder_released_without_a_tracker_mirror()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        NodeContext node = await NodeBootstrapSeed.NewIsolatedNodeAsync(postgres.Store, cts.Token);
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid otherNodeId = DomainId.New();
        FakeLedger ledger = new();

        // The failure shape from the incident: claimed here, failed, moved to the other node, retried.
        Guid taskId = await SeedHeldHereAsync(
            node, projectId, otherNodeId, HeldHere.FailedMovedAndRetried, new ExternalReference(WorkItemProvider.Jira, "PROJ-9"), cts.Token);
        await SeedRecordAsync(ledger, taskId, HolderNamed(node), cts.Token);

        await NewEngine(node, ledger).SweepExpiredLeasesAsync(DateTimeOffset.UtcNow, cts.Token);

        (await ReadHolderAsync(ledger, taskId, cts.Token)).Should().BeNull(
            "this node holds the record of a task queued for another node, with nothing of it running here");
        await using IQuerySession query = postgres.Store.QuerySession();
        TaskAggregate task = (await query.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        task.HolderNodeId.Should().BeNull("the domain-side release lands with the ledger write");
        task.State.Should().Be(TaskState.Queued, "the release changes who holds the task, never where it stands");
        task.PlacedOnNodeId.Should().Be(otherNodeId);
        (await query.LoadAsync<TaskHolderReleasePending>(
            TaskHolderReleasePending.KeyFor(taskId, node.NodeId), cts.Token)).Should().BeNull(
            "the ledger write landed on the first attempt");
        (await query.LoadAsync<TaskTrackerReleaseMirrorPending>(
            TaskTrackerReleaseMirrorPending.KeyFor(taskId, node.NodeId), cts.Token)).Should().BeNull(
            "a task that is moving is not concluded, and a mirror would race the new node's claim-time assignee write");
    }

    [Fact]
    public async Task A_claimed_task_is_never_released_even_when_its_placement_names_another_node()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        NodeContext node = await NodeBootstrapSeed.NewIsolatedNodeAsync(postgres.Store, cts.Token);
        Guid projectId = await SeedProjectAsync(cts.Token);
        FakeLedger ledger = new();

        Guid taskId = await SeedHeldHereAsync(
            node, projectId, DomainId.New(), HeldHere.ClaimedAndMoved, externalReference: null, cts.Token);
        await SeedRecordAsync(ledger, taskId, HolderNamed(node), cts.Token);

        await NewEngine(node, ledger).SweepExpiredLeasesAsync(DateTimeOffset.UtcNow, cts.Token);

        (await ReadHolderAsync(ledger, taskId, cts.Token)).Should().NotBeNull(
            "a claim appends Claimed locally before any run starts, so a placement change mid-run releases nothing");
        (await LoadTaskAsync(taskId, cts.Token)).HolderNodeId.Should().Be(node.NodeId);
    }

    [Fact]
    public async Task A_task_claimed_by_another_node_is_never_released_from_here()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        NodeContext node = await NodeBootstrapSeed.NewIsolatedNodeAsync(postgres.Store, cts.Token);
        Guid projectId = await SeedProjectAsync(cts.Token);
        FakeLedger ledger = new();

        Guid taskId = await SeedHeldHereAsync(
            node, projectId, DomainId.New(), HeldHere.ClaimedByAnotherNodeThenRequeuedAndMoved, externalReference: null, cts.Token);
        TaskAggregate seeded = await LoadTaskAsync(taskId, cts.Token);
        Guid holderNodeId = seeded.HolderNodeId!.Value;
        holderNodeId.Should().NotBe(node.NodeId);
        await SeedRecordAsync(ledger, taskId, new TaskRecordHolder("other-fingerprint", holderNodeId, "OTHER", Now), cts.Token);

        await NewEngine(node, ledger).SweepExpiredLeasesAsync(DateTimeOffset.UtcNow, cts.Token);

        (await ReadHolderAsync(ledger, taskId, cts.Token))!.NodeId.Should().Be(holderNodeId, "this node never writes another node's holder");
        (await LoadTaskAsync(taskId, cts.Token)).HolderNodeId.Should().Be(holderNodeId);
    }

    [Fact]
    public async Task A_queued_task_this_node_still_has_a_lease_for_is_not_released()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        NodeContext node = await NodeBootstrapSeed.NewIsolatedNodeAsync(postgres.Store, cts.Token);
        Guid projectId = await SeedProjectAsync(cts.Token);
        FakeLedger ledger = new();

        Guid taskId = await SeedHeldHereAsync(
            node, projectId, DomainId.New(), HeldHere.FailedMovedAndRetried, externalReference: null, cts.Token);
        await SeedRecordAsync(ledger, taskId, HolderNamed(node), cts.Token);
        await using (IDocumentSession session = postgres.Store.LightweightSession())
        {
            session.Store(new TaskLease
            {
                Id = taskId,
                NodeId = node.NodeId,
                LeaseGeneration = 1,
                HeartbeatAt = DateTimeOffset.UtcNow,
            });
            await session.SaveChangesAsync(cts.Token);
        }

        await NewEngine(node, ledger).SweepExpiredLeasesAsync(DateTimeOffset.UtcNow, cts.Token);

        (await ReadHolderAsync(ledger, taskId, cts.Token)).Should().NotBeNull();
        (await LoadTaskAsync(taskId, cts.Token)).HolderNodeId.Should().Be(node.NodeId);
    }

    [Fact]
    public async Task A_queued_task_with_a_live_run_on_this_node_is_not_released()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        NodeContext node = await NodeBootstrapSeed.NewIsolatedNodeAsync(postgres.Store, cts.Token);
        Guid projectId = await SeedProjectAsync(cts.Token);
        FakeLedger ledger = new();

        Guid taskId = await SeedHeldHereAsync(
            node, projectId, DomainId.New(), HeldHere.FailedMovedAndRetried, externalReference: null, cts.Token);
        await SeedRecordAsync(ledger, taskId, HolderNamed(node), cts.Token);
        Guid runId = DomainId.New();
        await using (IDocumentSession session = postgres.Store.LightweightSession())
        {
            session.Events.StartStream<RunAggregate>(
                runId,
                new RunDispatched(
                    runId, taskId, node.NodeId, node.OwnerId, 1, DomainId.New(), "/wt", "task/live",
                    ExecutorMode.Subscription, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await NewEngine(node, ledger).SweepExpiredLeasesAsync(DateTimeOffset.UtcNow, cts.Token);

        (await ReadHolderAsync(ledger, taskId, cts.Token)).Should().NotBeNull(
            "a session still running here is this node's to see through");
        (await LoadTaskAsync(taskId, cts.Token)).HolderNodeId.Should().Be(node.NodeId);
    }

    [Fact]
    public async Task A_queued_task_placed_on_this_node_keeps_its_holder()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        NodeContext node = await NodeBootstrapSeed.NewIsolatedNodeAsync(postgres.Store, cts.Token);
        Guid projectId = await SeedProjectAsync(cts.Token);
        FakeLedger ledger = new();

        Guid taskId = await SeedHeldHereAsync(
            node, projectId, node.NodeId, HeldHere.FailedMovedAndRetried, externalReference: null, cts.Token);
        await SeedRecordAsync(ledger, taskId, HolderNamed(node), cts.Token);

        await NewEngine(node, ledger).SweepExpiredLeasesAsync(DateTimeOffset.UtcNow, cts.Token);

        (await ReadHolderAsync(ledger, taskId, cts.Token)).Should().NotBeNull("the task is this node's own to retry");
        (await LoadTaskAsync(taskId, cts.Token)).HolderNodeId.Should().Be(node.NodeId);
    }

    [Fact]
    public async Task A_release_whose_ledger_write_fails_is_retried_on_a_later_sweep()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        NodeContext node = await NodeBootstrapSeed.NewIsolatedNodeAsync(postgres.Store, cts.Token);
        Guid projectId = await SeedProjectAsync(cts.Token);
        FakeLedger inner = new();
        RefusingWritesLedger ledger = new(inner) { Refusing = true };

        Guid taskId = await SeedHeldHereAsync(
            node, projectId, DomainId.New(), HeldHere.FailedMovedAndRetried, externalReference: null, cts.Token);
        await SeedRecordAsync(inner, taskId, HolderNamed(node), cts.Token);
        DispatchEngine engine = NewEngine(node, ledger);

        await engine.SweepExpiredLeasesAsync(DateTimeOffset.UtcNow, cts.Token);

        (await ReadHolderAsync(inner, taskId, cts.Token)).Should().NotBeNull("the push was refused");
        await using (IQuerySession query = postgres.Store.QuerySession())
        {
            (await query.LoadAsync<TaskHolderReleasePending>(
                TaskHolderReleasePending.KeyFor(taskId, node.NodeId), cts.Token)).Should().NotBeNull(
                "a release that could not write is left for a later sweep, as a lease-expiry release is");
        }

        ledger.Refusing = false;
        await engine.SweepExpiredLeasesAsync(DateTimeOffset.UtcNow, cts.Token);

        (await ReadHolderAsync(inner, taskId, cts.Token)).Should().BeNull("the next sweep retried it");
        await using IQuerySession after = postgres.Store.QuerySession();
        (await after.LoadAsync<TaskHolderReleasePending>(
            TaskHolderReleasePending.KeyFor(taskId, node.NodeId), cts.Token)).Should().BeNull();
    }

    /// <summary>How a seeded task came to be held here and queued, claimed, or placed as it is.</summary>
    private enum HeldHere
    {
        /// <summary>Claimed here, failed, assigned to another node, and retried: Queued, placed elsewhere.</summary>
        FailedMovedAndRetried,

        /// <summary>Claimed here, then placed on another node while still running: Claimed, placed elsewhere.</summary>
        ClaimedAndMoved,

        /// <summary>Claimed by a node other than this one, requeued without a release, then placed on a third.</summary>
        ClaimedByAnotherNodeThenRequeuedAndMoved,
    }

    private DispatchEngine NewEngine(NodeContext node, ILedger ledger) =>
        new(
            postgres.Store, node, new DaemonConnection(postgres.ConnectionString), new FakeProcessManager(),
            new LaunchHoldEngine(postgres.Store, NullLogger<LaunchHoldEngine>.Instance),
            Options.Create(new DaemonOptions
            {
                MaxConcurrentTaskRuns = 10,
                LeaseTimeout = TimeSpan.FromMinutes(30),
            }),
            NullLogger<DispatchEngine>.Instance,
            trackerClaimGate: null,
            ledger);

    private static TaskRecordHolder HolderNamed(NodeContext node) =>
        new("owner-a-fingerprint", node.NodeId, "THIS-NODE", Now);

    private async Task<Guid> SeedHeldHereAsync(
        NodeContext node, Guid projectId, Guid placedOnNodeId, HeldHere shape, ExternalReference? externalReference,
        CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(
                taskId, projectId, "Moved off a node", ["done"], TaskType.Chore, null, null, externalReference, Now, node.OwnerId),
            node.OwnerId, Now);
        List<object> events = [.. lifecycle];

        Guid claimingNodeId = shape == HeldHere.ClaimedByAnotherNodeThenRequeuedAndMoved ? DomainId.New() : node.NodeId;
        TaskClaimed claimed = TaskDecider.Claim(task, claimingNodeId, node.OwnerId, runId, Now);
        task.Apply(claimed);
        events.Add(claimed);

        switch (shape)
        {
            case HeldHere.FailedMovedAndRetried:
                TaskFailed failed = TaskDecider.Fail(task, runId, "The host gate failed.", Now);
                task.Apply(failed);
                events.Add(failed);
                events.Add(new TaskPlacementChanged(taskId, placedOnNodeId, Now, node.OwnerId));
                events.Add(TaskDecider.Retry(task, runId, null, "Moved to the other node", Now, node.OwnerId));
                break;

            case HeldHere.ClaimedAndMoved:
                events.Add(new TaskPlacementChanged(taskId, placedOnNodeId, Now, node.OwnerId));
                break;

            case HeldHere.ClaimedByAnotherNodeThenRequeuedAndMoved:
                events.Add(TaskDecider.Requeue(task, RequeueReason.LeaseExpired, Now));
                events.Add(new TaskPlacementChanged(taskId, placedOnNodeId, Now, node.OwnerId));
                break;
        }

        await using IDocumentSession session = postgres.Store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, [.. events]);
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    private async Task<Guid> SeedProjectAsync(CancellationToken cancellationToken)
    {
        Guid projectId = DomainId.New();
        await using IDocumentSession session = postgres.Store.LightweightSession();
        session.Store(new ProjectDetails
        {
            Id = projectId,
            Name = $"moved-{projectId:N}"[..12],
            RepositoryPath = RepositoryPath,
            BaseBranch = "main",
            ClaimGate = ClaimGate.Off,
            BranchNameTemplate = BranchNameTemplate.Default,
        });
        await session.SaveChangesAsync(cancellationToken);
        return projectId;
    }

    private async Task<TaskAggregate> LoadTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = postgres.Store.QuerySession();
        return (await query.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken))!;
    }

    private static async Task<TaskRecordHolder?> ReadHolderAsync(
        ILedger ledger, Guid taskId, CancellationToken cancellationToken)
    {
        LedgerFile file = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId), cancellationToken);
        return TaskRecord.TryParse(file.Content)!.Holder;
    }

    private static async Task SeedRecordAsync(
        ILedger ledger, Guid taskId, TaskRecordHolder holder, CancellationToken cancellationToken)
    {
        TaskRecord record = new(
            taskId, "placed-elsewhere-test", "chore", "Moved off a node", ["done"], null, null,
            PreApprovalMode.Off, null, [], null, null, TaskRecordCaps.None, "owner-a-fingerprint",
            new TaskOrigin(DomainId.New(), "ORIGIN-NODE", taskId, "task/moved-off-a-node", Now), holder);
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId),
                record.ToYaml(), null, "seed", new LedgerCommitter("Test", "test@hall9k.local"),
                new LedgerSigningKey("/fake/signing-key-never-read-by-fakeledger")),
            cancellationToken);
    }

    /// <summary>A ledger whose writes throw while <see cref="Refusing"/> is set, the push failure a fake never produces on its own.</summary>
    private sealed class RefusingWritesLedger(ILedger inner) : ILedger
    {
        public bool Refusing { get; set; }

        public Task<LedgerFile> ReadAsync(string repositoryPath, string refName, string path, CancellationToken cancellationToken) =>
            inner.ReadAsync(repositoryPath, refName, path, cancellationToken);

        public Task<LedgerWriteOutcome> WriteAsync(LedgerWriteRequest request, CancellationToken cancellationToken) =>
            Refusing
                ? throw new InvalidOperationException("Simulated push failure: the remote refused every attempt.")
                : inner.WriteAsync(request, cancellationToken);

        public Task<LedgerWriteOutcome> WriteManyAsync(LedgerManyWriteRequest request, CancellationToken cancellationToken) =>
            Refusing
                ? throw new InvalidOperationException("Simulated push failure: the remote refused every attempt.")
                : inner.WriteManyAsync(request, cancellationToken);

        public Task<LedgerWriteOutcome> DeleteAsync(LedgerDeleteRequest request, CancellationToken cancellationToken) =>
            inner.DeleteAsync(request, cancellationToken);

        public Task<bool> HasAnyAsync(string repositoryPath, string refName, string pathPrefix, CancellationToken cancellationToken) =>
            inner.HasAnyAsync(repositoryPath, refName, pathPrefix, cancellationToken);

        public Task<IReadOnlyList<LedgerRef>> ListRefsAsync(string repositoryPath, string refPrefix, CancellationToken cancellationToken) =>
            inner.ListRefsAsync(repositoryPath, refPrefix, cancellationToken);

        public Task<IReadOnlyList<LedgerEntry>> ReadAllAsync(string repositoryPath, string refName, string pathPrefix, CancellationToken cancellationToken) =>
            inner.ReadAllAsync(repositoryPath, refName, pathPrefix, cancellationToken);
    }
}
