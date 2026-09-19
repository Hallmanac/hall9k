using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Trust;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Marten;
using Marten.Events;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <c>h9k task take</c> (no <c>--force</c>), <c>h9k task grant</c>, and <c>h9k task refuse</c>
/// (idea 202383dc, item 5) end to end, against real Marten/Postgres but a <see cref="FakeLedger"/>
/// stand-in for the ledger — the same seam <see cref="ClaimRequestEngineTests"/> and
/// <c>TaskTakeCommandTests</c> already use.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection("Hall9kHome")]
[Trait("Category", "Hall9kHome")]
public sealed class TaskTakeCooperativeCommandTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
    private const string RepositoryPath = "/does/not/matter/on/a/fake/ledger";

    private readonly PostgresFixture _postgres;
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"hall9k-take-cooperative-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    public TaskTakeCooperativeCommandTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", _home);
        await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();
    }

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", _previousHome);
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Take_without_force_and_without_reason_is_refused()
    {
        (ProjectDetails project, Guid taskId, Guid otherHolderNodeId) = await SeedTaskHeldByAnotherNodeAsync();

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        TaskTakeCommand.Settings settings = new() { Id = taskId.ToString(), Force = false, Reason = null };
        Func<Task> act = () => TaskTakeCommand.RunAsync(
            _postgres.Store, session, settings, new FakeLedger(), new FakeLedgerChainReader(TrustChainOf()),
            take: null, new NodeKeyStore(), CancellationToken.None);

        await act.Should().ThrowAsync<DomainValidationException>().WithMessage("*reason*");
    }

    [Fact]
    public async Task Take_without_force_against_another_holder_queues_a_claim_request()
    {
        (ProjectDetails project, Guid taskId, Guid otherHolderNodeId) = await SeedTaskHeldByAnotherNodeAsync();

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        TaskTakeCommand.Settings settings = new() { Id = taskId.ToString(), Force = false, Reason = "Picking this back up." };
        int result = await TaskTakeCommand.RunAsync(
            _postgres.Store, session, settings, new FakeLedger(), new FakeLedgerChainReader(TrustChainOf()),
            take: null, new NodeKeyStore(), CancellationToken.None);

        result.Should().Be(ExitCodes.Ok);
        IReadOnlyList<MessageDetails> queued = await session.Query<MessageDetails>().ToListAsync(CancellationToken.None);
        queued.Should().ContainSingle(message =>
            message.Kind == MessageKind.ClaimRequest.Value && message.To == MessageAudience.Node(otherHolderNodeId).Value
            && message.SentAt == null, "queued only — the daemon's own sweep sends it, never this command");
    }

    [Fact]
    public async Task Refuse_answers_a_parked_request_through_the_engine()
    {
        (ProjectDetails project, Guid taskId, BootstrapContext context) = await SeedHeldTaskWithPendingRequestAsync();

        await using IDocumentSession refuseSession = _postgres.Store.LightweightSession();
        TaskRefuseCommand.Settings refuseSettings = new() { Id = taskId.ToString(), Reason = "Still mid-refactor." };
        Func<Task> refuseAct = () => TaskRefuseCommand.RunAsync(_postgres.Store, refuseSession, refuseSettings, CancellationToken.None);
        await refuseAct.Should().NotThrowAsync();

        TaskAggregate afterRefuse = (await refuseSession.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: CancellationToken.None))!;
        afterRefuse.HolderNodeId.Should().Be(context.NodeId, "a refusal changes nothing about who holds the task");
        afterRefuse.LastTakeRefusedReason.Should().Be("Still mid-refactor.");
    }

    [Fact]
    public async Task Grant_answers_a_parked_request_through_the_engine()
    {
        (ProjectDetails project, Guid taskId, BootstrapContext context) = await SeedHeldTaskWithPendingRequestAsync();
        FakeLedger ledger = new();
        await SeedRecordAsync(ledger, taskId, new TaskRecordHolder("my-fingerprint", context.NodeId, "MY-NODE", Now.AddHours(-2)));

        await using IDocumentSession grantSession = _postgres.Store.LightweightSession();
        TaskGrantCommand.Settings grantSettings = new() { Id = taskId.ToString() };
        Func<Task> grantAct = () => TaskGrantCommand.RunAsync(
            _postgres.Store, grantSession, grantSettings, ledger, take: null, new NodeKeyStore(), CancellationToken.None);
        await grantAct.Should().NotThrowAsync();

        TaskAggregate afterGrant = (await grantSession.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: CancellationToken.None))!;
        afterGrant.HolderNodeId.Should().BeNull("a grant releases this node's own ledger holder");
        afterGrant.LastGrantedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Grant_refuses_when_a_run_is_live_for_this_task_on_this_node()
    {
        // The high-severity defect this test guards against (independent pre-PR review, cycle 5,
        // conformance lens): h9k task grant used to release the ledger holder unconditionally,
        // even while this node's own agent process was still working the task — landing it
        // Queued for the requester's next dispatch sweep to claim and launch a second run against
        // the same branch. ReceiveRequestAsync's own auto path already guards against exactly this
        // (ClaimRequestEngineTests.Auto_refuses_when_a_run_is_live); this is the identical guard on
        // the human-driven door.
        (ProjectDetails project, Guid taskId, BootstrapContext context) = await SeedHeldTaskWithPendingRequestAsync();
        FakeLedger ledger = new();
        await SeedRecordAsync(ledger, taskId, new TaskRecordHolder("my-fingerprint", context.NodeId, "MY-NODE", Now.AddHours(-2)));

        await using IDocumentSession runSession = _postgres.Store.LightweightSession();
        runSession.Store(new RunListItem
        {
            Id = DomainId.New(), TaskId = taskId, NodeId = context.NodeId, State = RunState.Running, DispatchedAt = Now.AddMinutes(-10),
        });
        await runSession.SaveChangesAsync(CancellationToken.None);

        int writesBeforeAttempt = ledger.Writes.Count;
        await using IDocumentSession grantSession = _postgres.Store.LightweightSession();
        TaskGrantCommand.Settings grantSettings = new() { Id = taskId.ToString() };
        Func<Task> grantAct = () => TaskGrantCommand.RunAsync(
            _postgres.Store, grantSession, grantSettings, ledger, take: null, new NodeKeyStore(), CancellationToken.None);

        await grantAct.Should().ThrowAsync<DomainConflictException>().WithMessage("*live*");

        TaskAggregate afterAttempt = (await grantSession.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: CancellationToken.None))!;
        afterAttempt.HolderNodeId.Should().Be(
            context.NodeId, "the grant must refuse before releasing the ledger holder while a run is still live");
        ledger.Writes.Should().HaveCount(writesBeforeAttempt, "the live-run check must run before any ledger write, not just before the grant's own append");
    }

    [Fact]
    public async Task Grant_command_requires_a_pending_request()
    {
        (ProjectDetails project, Guid taskId, Guid myNodeId, BootstrapContext context) = await SeedHeldTaskNoRequestAsync();

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        TaskGrantCommand.Settings settings = new() { Id = taskId.ToString() };
        Func<Task> act = () => TaskGrantCommand.RunAsync(
            _postgres.Store, session, settings, new FakeLedger(), take: null, new NodeKeyStore(), CancellationToken.None);

        await act.Should().ThrowAsync<DomainConflictException>().WithMessage("*no pending take request*");
    }

    private static TrustChain TrustChainOf() => new(new Dictionary<string, TrustedOwner>(), []);

    private async Task<(ProjectDetails Project, Guid TaskId, Guid OtherHolderNodeId)> SeedTaskHeldByAnotherNodeAsync()
    {
        await using IDocumentSession bootstrapSession = _postgres.Store.LightweightSession();
        BootstrapContext context = await NodeBootstrap.EnsureAsync(bootstrapSession, CancellationToken.None);
        await bootstrapSession.SaveChangesAsync(CancellationToken.None);
        await EstablishOwnRootAsync(bootstrapSession, context, CancellationToken.None);

        Guid projectId = DomainId.New();
        ProjectDetails project = new()
        {
            Id = projectId, Name = $"take-{projectId:N}"[..12], RepositoryPath = RepositoryPath, BaseBranch = "main",
            BranchNameTemplate = BranchNameTemplate.Default,
        };
        await using IDocumentSession projectSession = _postgres.Store.LightweightSession();
        projectSession.Store(project);
        await projectSession.SaveChangesAsync(CancellationToken.None);

        Guid taskId = DomainId.New();
        Guid otherHolderNodeId = DomainId.New();
        await using IDocumentSession taskSession = _postgres.Store.LightweightSession();
        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(taskId, projectId, "Close me out", ["done"], TaskType.Chore, null, null, null, Now, context.OwnerId),
            context.OwnerId, Now);
        var claimed = TaskDecider.Claim(task, otherHolderNodeId, context.OwnerId, DomainId.New(), Now.AddHours(-2));
        taskSession.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
        await taskSession.SaveChangesAsync(CancellationToken.None);

        return (project, taskId, otherHolderNodeId);
    }

    private async Task<(ProjectDetails Project, Guid TaskId, Guid MyNodeId, BootstrapContext Context)> SeedHeldTaskNoRequestAsync()
    {
        await using IDocumentSession bootstrapSession = _postgres.Store.LightweightSession();
        BootstrapContext context = await NodeBootstrap.EnsureAsync(bootstrapSession, CancellationToken.None);
        await bootstrapSession.SaveChangesAsync(CancellationToken.None);
        await EstablishOwnRootAsync(bootstrapSession, context, CancellationToken.None);

        Guid projectId = DomainId.New();
        ProjectDetails project = new()
        {
            Id = projectId, Name = $"take-{projectId:N}"[..12], RepositoryPath = RepositoryPath, BaseBranch = "main",
            BranchNameTemplate = BranchNameTemplate.Default,
        };
        await using IDocumentSession projectSession = _postgres.Store.LightweightSession();
        projectSession.Store(project);
        await projectSession.SaveChangesAsync(CancellationToken.None);

        Guid taskId = DomainId.New();
        await using IDocumentSession taskSession = _postgres.Store.LightweightSession();
        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(taskId, projectId, "Close me out", ["done"], TaskType.Chore, null, null, null, Now, context.OwnerId),
            context.OwnerId, Now);
        var claimed = TaskDecider.Claim(task, context.NodeId, context.OwnerId, DomainId.New(), Now.AddHours(-2));
        taskSession.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
        await taskSession.SaveChangesAsync(CancellationToken.None);

        return (project, taskId, context.NodeId, context);
    }

    private async Task<(ProjectDetails Project, Guid TaskId, BootstrapContext Context)> SeedHeldTaskWithPendingRequestAsync()
    {
        (ProjectDetails project, Guid taskId, Guid myNodeId, BootstrapContext context) = await SeedHeldTaskNoRequestAsync();

        Guid requesterNodeId = DomainId.New();
        Guid requesterOwnerId = DomainId.New();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        StreamState fence = (await session.Events.FetchStreamStateAsync(taskId, CancellationToken.None))!;
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, version: fence.Version, token: CancellationToken.None))!;
        TaskTakeRequested requested = TaskDecider.RequestTake(task, requesterNodeId, requesterOwnerId, "requester-fp", "Why", Now);
        session.Events.Append(taskId, expectedVersion: fence.Version + 1, requested);
        await session.SaveChangesAsync(CancellationToken.None);

        return (project, taskId, context);
    }

    private async Task EstablishOwnRootAsync(IDocumentSession session, BootstrapContext context, CancellationToken cancellationToken)
    {
        NodeSigningKey key = await new NodeKeyStore().EnsureAsync(context.NodeId, cancellationToken);
        OwnerAggregate owner = (await session.Events.AggregateStreamAsync<OwnerAggregate>(
            context.OwnerId, token: cancellationToken))!;
        session.Events.Append(context.OwnerId, OwnerDecider.ClaimRoot(owner, key.Fingerprint, verified: true, Now));
        await session.SaveChangesAsync(cancellationToken);
    }

    private static async Task SeedRecordAsync(FakeLedger ledger, Guid taskId, TaskRecordHolder? holder)
    {
        TaskRecord record = new(
            taskId, "take-test", "chore", "Close me out", ["done"], null, null,
            PreApprovalMode.Off, null, [], null, null, TaskRecordCaps.None, "owner-a-fingerprint",
            new TaskOrigin(DomainId.New(), "ORIGIN-NODE", taskId, "task/close-me-out", Now), holder);
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId),
                record.ToYaml(), null, "seed", new LedgerCommitter("Test", "test@hall9k.local"),
                new LedgerSigningKey("/does/not/matter/key")),
            CancellationToken.None);
    }
}
