using FluentAssertions;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon;
using Hall9k.Daemon.Dispatch;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The ledger record's holder as the truth about who has a task (idea 202383dc, A3b), through the
/// transport seam a fake ledger gives it (Brian's 2026-09-13 testing rule: no test here touches a
/// real repository or remote). <see cref="DispatchEngine"/>'s claim path is the writer; the tests
/// below walk its every branch: an empty holder claims, another node's holder stands the claim
/// down before any run, a write that cannot complete holds closed, and a release clears it again.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class TaskHolderClaimTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
    private const string RepositoryPath = "/repos/holder-test";

    [Fact]
    public async Task A_claim_stands_down_when_the_ledger_already_names_another_holder_before_any_run()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewIsolatedNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        Guid taskId = await SeedQueuedTaskAsync(store, node.OwnerId, projectId, externalReference: null, cts.Token);
        await SeedProjectAsync(store, projectId, ClaimGate.Off, cts.Token);

        FakeLedger ledger = new();
        Guid otherNodeId = DomainId.New();
        DateTimeOffset otherSince = Now.AddMinutes(-10);
        TaskRecordHolder otherHolder = new("owner-b-fingerprint", otherNodeId, "TEAMMATE-NODE", otherSince);
        await SeedRecordAsync(ledger, taskId, otherHolder, cts.Token);

        DispatchEngine engine = NewEngine(store, node, ledger);
        (await engine.ClaimEligibleAsync(cts.Token)).Should().NotContain(
            w => w.TaskId == taskId, "another node's ledger holder stands this claim down");

        await using IQuerySession query = store.QuerySession();
        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Should().Be(TaskState.Queued, "the stand-down leaves the task exactly where it was — nothing claims it");

        (await query.Query<TaskLease>().Where(l => l.Id == taskId).ToListAsync(cts.Token))
            .Should().BeEmpty("no run is ever launched for a claim the ledger did not grant");

        TaskHolderClaimHold hold = (await query.LoadAsync<TaskHolderClaimHold>(
            TaskHolderClaimHold.KeyFor(taskId, node.NodeId), cts.Token))!;
        hold.Should().NotBeNull("the stand-down publishes a hold naming the holder and time");
        hold.HolderNodeId.Should().Be(otherNodeId);
        hold.HolderOwnerFingerprint.Should().Be("owner-b-fingerprint");
        hold.HolderNodeName.Should().Be("TEAMMATE-NODE");
        hold.HolderSince.Should().Be(otherSince, "the hold sentence and the ledger record agree on holder and time");
        hold.Cause.Should().BeNull("a genuine HeldByOther stand-down observed something, not a failure");

        await ArchiveProjectAsync(store, projectId, cts.Token);
    }

    [Fact]
    public async Task An_empty_holder_claims_and_the_run_launches_only_after_the_ledger_write_lands()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewIsolatedNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        Guid taskId = await SeedQueuedTaskAsync(store, node.OwnerId, projectId, externalReference: null, cts.Token);
        await SeedProjectAsync(store, projectId, ClaimGate.Off, cts.Token);

        FakeLedger ledger = new();
        await SeedRecordAsync(ledger, taskId, holder: null, cts.Token);

        DispatchEngine engine = NewEngine(store, node, ledger);
        IReadOnlyList<ClaimedWork> claimed = await engine.ClaimEligibleAsync(cts.Token);
        claimed.Should().Contain(w => w.TaskId == taskId, "the ledger's holder was empty, so the claim proceeds");

        LedgerFile record = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId), cts.Token);
        TaskRecordHolder holder = TaskRecord.TryParse(record.Content)!.Holder!;
        holder.NodeId.Should().Be(node.NodeId, "the push landed before TaskClaimed was ever built");

        await using IQuerySession query = store.QuerySession();
        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Should().Be(TaskState.Claimed);
        (await query.Query<TaskLease>().Where(l => l.Id == taskId).ToListAsync(cts.Token)).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_ledger_write_failure_holds_the_claim_with_the_cause_and_no_run()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewIsolatedNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        Guid taskId = await SeedQueuedTaskAsync(store, node.OwnerId, projectId, externalReference: null, cts.Token);
        await SeedProjectAsync(store, projectId, ClaimGate.Off, cts.Token);

        FakeLedger inner = new();
        await SeedRecordAsync(inner, taskId, holder: null, cts.Token);
        ThrowingWriteLedger ledger = new(inner);

        DispatchEngine engine = NewEngine(store, node, ledger);
        (await engine.ClaimEligibleAsync(cts.Token)).Should().NotContain(w => w.TaskId == taskId,
            "fail closed: a holder write that cannot complete holds the task rather than proceeding unconfirmed");

        await using IQuerySession query = store.QuerySession();
        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Should().Be(TaskState.Queued);
        (await query.Query<TaskLease>().Where(l => l.Id == taskId).ToListAsync(cts.Token)).Should().BeEmpty();

        TaskHolderClaimHold hold = (await query.LoadAsync<TaskHolderClaimHold>(
            TaskHolderClaimHold.KeyFor(taskId, node.NodeId), cts.Token))!;
        hold.Should().NotBeNull();
        hold.HolderNodeId.Should().BeNull("a failed read observed no holder, so none is named");
        hold.Cause.Should().NotBeNullOrWhiteSpace("the hold names the cause a human or the next sweep can read");

        await ArchiveProjectAsync(store, projectId, cts.Token);
    }

    /// <summary>
    /// The existence read ahead of the conditional write itself (independent pre-PR review, this
    /// branch, cycle 1): <see cref="LedgerFile.Exists"/> reads false both for a genuinely absent
    /// record and for a fetch that could not reach origin at all, and treating the second as the
    /// first would let this claim proceed past a holder it never actually saw — exactly the fail-
    /// closed rule (Brian, 2026-09-13) the write side already enforces. The fake here never even
    /// reaches <see cref="TaskLedgerHolder.TryClaimAsync"/>'s own write: the existence guard has to
    /// hold the claim on <see cref="LedgerFile.FetchFailed"/> alone, before any write is attempted.
    /// </summary>
    [Fact]
    public async Task A_fetch_failure_on_the_existence_read_holds_the_claim_before_any_write_is_attempted()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewIsolatedNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        Guid taskId = await SeedQueuedTaskAsync(store, node.OwnerId, projectId, externalReference: null, cts.Token);
        await SeedProjectAsync(store, projectId, ClaimGate.Off, cts.Token);

        DispatchEngine engine = NewEngine(store, node, new FetchFailingLedger());
        (await engine.ClaimEligibleAsync(cts.Token)).Should().NotContain(w => w.TaskId == taskId,
            "a fetch failure cannot confirm a holder is absent, so the claim holds rather than assuming nothing to guard");

        await using IQuerySession query = store.QuerySession();
        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Should().Be(TaskState.Queued);
        (await query.Query<TaskLease>().Where(l => l.Id == taskId).ToListAsync(cts.Token)).Should().BeEmpty();

        TaskHolderClaimHold hold = (await query.LoadAsync<TaskHolderClaimHold>(
            TaskHolderClaimHold.KeyFor(taskId, node.NodeId), cts.Token))!;
        hold.Should().NotBeNull();
        hold.HolderNodeId.Should().BeNull("a fetch failure observed no holder, so none is named");
        hold.Cause.Should().NotBeNullOrWhiteSpace("the hold names the cause a human or the next sweep can read");

        await ArchiveProjectAsync(store, projectId, cts.Token);
    }

    [Fact]
    public async Task Release_clears_the_ledger_holder_when_the_lease_expires_with_the_run_gone()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewIsolatedNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        Guid taskId = await SeedQueuedTaskAsync(store, node.OwnerId, projectId, externalReference: null, cts.Token);
        await SeedProjectAsync(store, projectId, ClaimGate.Off, cts.Token);

        FakeLedger ledger = new();
        await SeedRecordAsync(ledger, taskId, holder: null, cts.Token);

        DispatchEngine engine = NewEngine(store, node, ledger, leaseTimeout: TimeSpan.FromMinutes(10));
        (await engine.ClaimEligibleAsync(cts.Token)).Should().Contain(w => w.TaskId == taskId);

        string path = LedgerRefRegistry.RecordPath(taskId);
        string refName = LedgerRefRegistry.Records.RefspecSource;
        (await ledger.ReadAsync(RepositoryPath, refName, path, cts.Token)).Content
            .Should().Contain("holder-node", "the claim's own write landed");

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskLease lease = (await session.Query<TaskLease>().Where(l => l.Id == taskId).ToListAsync(cts.Token))[0];
            lease.HeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-30);
            session.Store(lease);
            await session.SaveChangesAsync(cts.Token);
        }

        await engine.SweepExpiredLeasesAsync(DateTimeOffset.UtcNow, cts.Token);
        await using (IQuerySession afterSweep = store.QuerySession())
        {
            (await afterSweep.Query<TaskLease>().Where(l => l.Id == taskId).ToListAsync(cts.Token))
                .Should().BeEmpty("the run is gone (no lease, no live process) — the task requeues");
        }

        TaskRecord record = TaskRecord.TryParse((await ledger.ReadAsync(RepositoryPath, refName, path, cts.Token)).Content)!;
        record.Holder.Should().BeNull("a task whose holder is this node but whose run is gone is released on the next sweep");

        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<TaskHolderReleasePending>(
            TaskHolderReleasePending.KeyFor(taskId, node.NodeId), cts.Token)).Should().BeNull(
            "the release landed on the first attempt, so nothing is left pending");

        await ArchiveProjectAsync(store, projectId, cts.Token);
    }

    /// <summary>
    /// The release half of the mirror (criterion 5): once the lease-expiry sweep gives this node's
    /// own ledger holder back, its tracker identity is cleared off the linked item too, and a named
    /// outcome short of a confirmed clear — here, a Jira reference with no Jira connection
    /// registered anywhere in this database, so the read fails fast and deterministically — leaves
    /// a row for the next sweep, exactly as the claim-side mirror's own <c>TaskTrackerAssignMirrorPending</c>
    /// does.
    /// </summary>
    [Fact]
    public async Task A_release_mirror_failure_leaves_a_pending_row_for_the_next_sweep_to_retry()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewIsolatedNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        ExternalReference reference = new(WorkItemProvider.Jira, "PROJ-9");
        Guid taskId = await SeedQueuedTaskAsync(store, node.OwnerId, projectId, reference, cts.Token);
        await SeedProjectAsync(store, projectId, ClaimGate.Off, cts.Token);

        FakeLedger ledger = new();
        await SeedRecordAsync(ledger, taskId, holder: null, cts.Token);

        DispatchEngine engine = NewEngine(store, node, ledger, leaseTimeout: TimeSpan.FromMinutes(10));
        (await engine.ClaimEligibleAsync(cts.Token)).Should().Contain(w => w.TaskId == taskId);

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskLease lease = (await session.Query<TaskLease>().Where(l => l.Id == taskId).ToListAsync(cts.Token))[0];
            lease.HeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-30);
            session.Store(lease);
            await session.SaveChangesAsync(cts.Token);
        }

        await engine.SweepExpiredLeasesAsync(DateTimeOffset.UtcNow, cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskRecord record = TaskRecord.TryParse(
            (await ledger.ReadAsync(
                RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId),
                cts.Token)).Content)!;
        record.Holder.Should().BeNull("the ledger's own release is independent of the tracker mirror and lands regardless");

        (await query.LoadAsync<TaskTrackerReleaseMirrorPending>(
            TaskTrackerReleaseMirrorPending.KeyFor(taskId, node.NodeId), cts.Token)).Should().NotBeNull(
            "criterion 5's release half: a named outcome short of a confirmed clear is retried next sweep");

        await ArchiveProjectAsync(store, projectId, cts.Token);
    }

    [Fact]
    public async Task The_gated_project_holds_when_the_assignee_is_elsewhere_even_with_the_ledger_holder_empty()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewIsolatedNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        ExternalReference reference = new(WorkItemProvider.GitHub, "42");
        Guid taskId = await SeedQueuedTaskAsync(store, node.OwnerId, projectId, reference, cts.Token);
        await SeedProjectAsync(store, projectId, ClaimGate.TrackerAssignee, cts.Token);

        FakeLedger ledger = new();
        await SeedRecordAsync(ledger, taskId, holder: null, cts.Token);

        // The tracker shows the issue assigned to somebody else entirely — TrackerAssignee's own
        // read, ahead of the ledger holder write (idea 202383dc, A3b's "gate's new meaning"): the
        // claim never reaches the ledger at all.
        TrackerClaimGate gate = new(
            new RecordingProcessRunner(arguments =>
                arguments is ["api", "user", ..]
                    ? new ProcessResult(0, "this-install\n", string.Empty)
                    : new ProcessResult(0, """{"assignees":[{"login":"someone-else"}]}""", string.Empty)).Runner,
            FakeJiraRequester.NeverInvoked());

        DispatchEngine engine = NewEngine(store, node, ledger, trackerClaimGate: gate);
        (await engine.ClaimEligibleAsync(cts.Token)).Should().NotContain(
            w => w.TaskId == taskId, "the tracker gate holds ahead of the ledger");

        ledger.Writes.Should().ContainSingle(
            "only the seed write — the tracker gate refused before this claim ever wrote a holder")
            .Which.CommitMessage.Should().Be("seed");

        await using IQuerySession query = store.QuerySession();
        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Should().Be(TaskState.Queued);

        await ArchiveProjectAsync(store, projectId, cts.Token);
    }

    [Fact]
    public async Task ClaimGate_Off_never_consults_the_tracker_on_claim()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewIsolatedNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        ExternalReference reference = new(WorkItemProvider.GitHub, "42");
        Guid taskId = await SeedQueuedTaskAsync(store, node.OwnerId, projectId, reference, cts.Token);
        await SeedProjectAsync(store, projectId, ClaimGate.Off, cts.Token);

        FakeLedger ledger = new();
        await SeedRecordAsync(ledger, taskId, holder: null, cts.Token);

        RecordingProcessRunner recorder = new(_ => throw new InvalidOperationException(
            "ClaimGate.Off must never call the tracker on the claim path at all."));
        TrackerClaimGate gate = new(recorder.Runner, FakeJiraRequester.NeverInvoked());

        DispatchEngine engine = NewEngine(store, node, ledger, trackerClaimGate: gate);
        (await engine.ClaimEligibleAsync(cts.Token)).Should().Contain(
            w => w.TaskId == taskId, "an ungated project claims on the ledger alone");
    }

    [Fact]
    public async Task A_mirror_failure_never_fails_the_claim()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewIsolatedNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        // A Jira reference with no Jira connection registered anywhere in this database — the
        // mirror's own write fails fast and deterministically, no network involved either way.
        ExternalReference reference = new(WorkItemProvider.Jira, "PROJ-9");
        Guid taskId = await SeedQueuedTaskAsync(store, node.OwnerId, projectId, reference, cts.Token);
        await SeedProjectAsync(store, projectId, ClaimGate.Off, cts.Token);

        FakeLedger ledger = new();
        await SeedRecordAsync(ledger, taskId, holder: null, cts.Token);

        DispatchEngine engine = NewEngine(store, node, ledger);
        (await engine.ClaimEligibleAsync(cts.Token)).Should().Contain(w => w.TaskId == taskId,
            "the mirror runs only after the claim already committed, and its own failure is never allowed to undo that");

        await using IQuerySession query = store.QuerySession();
        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Should().Be(TaskState.Claimed);

        (await query.LoadAsync<TaskTrackerAssignMirrorPending>(
            TaskTrackerAssignMirrorPending.KeyFor(taskId, node.NodeId), cts.Token)).Should().NotBeNull(
            "criterion 5: a named outcome short of a confirmed mirror match is retried next sweep, not merely logged");
    }

    // ── seeding ──────────────────────────────────────────────────────────────────────────────

    private DispatchEngine NewEngine(
        DocumentStore store,
        NodeContext node,
        ILedger ledger,
        TrackerClaimGate? trackerClaimGate = null,
        TimeSpan? leaseTimeout = null) =>
        new(
            store, node, new DaemonConnection(postgres.ConnectionString), new FakeProcessManager(),
            new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance),
            Options.Create(new DaemonOptions
            {
                MaxConcurrentTaskRuns = 10,
                LeaseTimeout = leaseTimeout ?? TimeSpan.FromMinutes(30),
            }),
            NullLogger<DispatchEngine>.Instance,
            trackerClaimGate,
            ledger);

    private static async Task<Guid> SeedQueuedTaskAsync(
        DocumentStore store, Guid ownerId, Guid projectId, ExternalReference? externalReference,
        CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, TaskSeed.Dispatchable(
            TaskDecider.Add(
                taskId, projectId, "Close me out", ["done"], TaskType.Chore, null, null,
                externalReference, Now, ownerId),
            ownerId, Now));
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    private static async Task SeedProjectAsync(
        DocumentStore store, Guid projectId, ClaimGate gate, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Store(new ProjectDetails
        {
            Id = projectId,
            Name = $"holder-{projectId:N}"[..12],
            RepositoryPath = RepositoryPath,
            BaseBranch = "main",
            ClaimGate = gate,
            BranchNameTemplate = BranchNameTemplate.Default,
        });
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Every test in this class shares one owner: <c>NodeBootstrapSeed.NewIsolatedNodeAsync</c>
    /// mints a fresh node id per call, but <c>NodeBootstrap.EnsureAsync</c> resolves and reuses
    /// whichever <c>OwnerDetails</c> row already exists in this shared Postgres database
    /// regardless, so a task a test deliberately leaves Queued (a stand-down, a fail-closed hold,
    /// a gated refusal, or a lease-expiry requeue) stays a live, eligible candidate for every
    /// later test's own <see cref="DispatchEngine.ClaimEligibleAsync"/> sweep in this same class —
    /// each test's own fresh, empty <see cref="FakeLedger"/> answers <c>NoRecord</c> for a task it
    /// never itself seeded, letting an ordinary claim through past a task that test never meant to
    /// touch (origin: a gate run took a sibling test's own leftover Queued task under this shared
    /// owner instead of the task the failing test had actually seeded). Archiving the project the
    /// moment a test is done asserting against its own Queued task removes it from every later
    /// sweep's own eligible set for good, through DispatchEngine's own pre-existing
    /// archived-project filter, without needing this shared owner to be isolated in the first
    /// place.
    /// </summary>
    private static async Task ArchiveProjectAsync(DocumentStore store, Guid projectId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        ProjectDetails project = (await session.LoadAsync<ProjectDetails>(projectId, cancellationToken))!;
        project.IsArchived = true;
        session.Store(project);
        await session.SaveChangesAsync(cancellationToken);
    }

    private static async Task SeedRecordAsync(
        FakeLedger ledger, Guid taskId, TaskRecordHolder? holder, CancellationToken cancellationToken)
    {
        TaskRecord record = new(
            taskId, "holder-test", "chore", "Close me out", ["done"], null, null,
            PreApprovalMode.Off, null, [], null, null, TaskRecordCaps.None, "owner-a-fingerprint",
            new TaskOrigin(DomainId.New(), "ORIGIN-NODE", taskId, "task/close-me-out", Now), holder);
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId),
                record.ToYaml(), null, "seed", new LedgerCommitter("Test", "test@hall9k.local"),
                new LedgerSigningKey("/fake/signing-key-never-read-by-fakeledger")),
            cancellationToken);
    }

    /// <summary>An <see cref="ILedger"/> whose write always throws — the fail-closed path's own seam, since a fake ledger otherwise never fails a write on its own.</summary>
    private sealed class ThrowingWriteLedger(ILedger inner) : ILedger
    {
        public Task<LedgerFile> ReadAsync(string repositoryPath, string refName, string path, CancellationToken cancellationToken) =>
            inner.ReadAsync(repositoryPath, refName, path, cancellationToken);

        public Task<LedgerWriteOutcome> WriteAsync(LedgerWriteRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated push failure — the remote refused every attempt.");

        public Task<LedgerWriteOutcome> DeleteAsync(LedgerDeleteRequest request, CancellationToken cancellationToken) =>
            inner.DeleteAsync(request, cancellationToken);

        public Task<bool> HasAnyAsync(string repositoryPath, string refName, string pathPrefix, CancellationToken cancellationToken) =>
            inner.HasAnyAsync(repositoryPath, refName, pathPrefix, cancellationToken);

        public Task<IReadOnlyList<LedgerRef>> ListRefsAsync(string repositoryPath, string refPrefix, CancellationToken cancellationToken) =>
            inner.ListRefsAsync(repositoryPath, refPrefix, cancellationToken);

        public Task<IReadOnlyList<LedgerEntry>> ReadAllAsync(string repositoryPath, string refName, string pathPrefix, CancellationToken cancellationToken) =>
            inner.ReadAllAsync(repositoryPath, refName, pathPrefix, cancellationToken);
    }

    /// <summary>
    /// An <see cref="ILedger"/> whose read always answers a fetch failure — a real <c>GitLedger</c>
    /// pointed at a bogus or unreachable repository path, without git in the loop. Every other
    /// member throws: this claim must never reach a write, or any other operation, once the
    /// existence read's own fetch failure has already held it.
    /// </summary>
    private sealed class FetchFailingLedger : ILedger
    {
        public Task<LedgerFile> ReadAsync(string repositoryPath, string refName, string path, CancellationToken cancellationToken) =>
            Task.FromResult(LedgerFile.Absent with { FetchFailed = true });

        public Task<LedgerWriteOutcome> WriteAsync(LedgerWriteRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This claim must never reach a write — the existence read's own fetch failure should hold it first.");

        public Task<LedgerWriteOutcome> DeleteAsync(LedgerDeleteRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This claim must never reach a delete — the existence read's own fetch failure should hold it first.");

        public Task<bool> HasAnyAsync(string repositoryPath, string refName, string pathPrefix, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This claim must never reach HasAnyAsync — the existence read's own fetch failure should hold it first.");

        public Task<IReadOnlyList<LedgerRef>> ListRefsAsync(string repositoryPath, string refPrefix, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This claim must never reach ListRefsAsync — the existence read's own fetch failure should hold it first.");

        public Task<IReadOnlyList<LedgerEntry>> ReadAllAsync(string repositoryPath, string refName, string pathPrefix, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This claim must never reach ReadAllAsync — the existence read's own fetch failure should hold it first.");
    }
}
