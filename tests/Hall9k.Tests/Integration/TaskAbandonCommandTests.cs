using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <c>h9k task abandon</c>'s owner-root guard and its Owner-role override, against real
/// Marten/Postgres but a <see cref="FakeLedgerChainReader"/> for the chain read, the same shape
/// <see cref="TaskTakeCommandTests"/> uses for <c>take --force</c>. The decisions themselves are
/// pure and covered without Docker in <c>TaskOwnerRuleTests</c> and <c>TaskOwnerOverrideTests</c>;
/// this proves the command gathers the facts and stamps the event.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class TaskAbandonCommandTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string OtherRoot = new('a', 64);

    private readonly PostgresFixture _postgres;
    private readonly ScopedTestHome _scopedHome = new();

    public TaskAbandonCommandTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync()
    {
        _scopedHome.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_plain_abandon_of_another_owners_task_is_refused_and_leaves_the_task_alone()
    {
        Guid taskId = await SeedTaskAssignedToAnotherOwnerAsync();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        FakeLedgerChainReader chainReader = await OwnerRoleChainAsync(session, MembershipRole.Owner);

        TaskAbandonCommand.Settings settings = new() { Id = taskId.ToString(), Reason = "tidying" };
        Func<Task> act = () => RunAsync(session, settings, chainReader);

        (await act.Should().ThrowAsync<DomainBusinessRuleException>())
            .WithMessage("*only that owner's nodes may act on it*")
            .Which.Message.Should().Contain(OtherRoot[..12], "the unlabelled owner is named by its short fingerprint");
        (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: CancellationToken.None))!
            .State.Should().NotBe(TaskState.Abandoned);
    }

    [Fact]
    public async Task An_owner_role_override_naming_the_holder_abandons_it_and_records_whose_behalf_and_why()
    {
        Guid taskId = await SeedTaskAssignedToAnotherOwnerAsync();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        FakeLedgerChainReader chainReader = await OwnerRoleChainAsync(session, MembershipRole.Owner);

        TaskAbandonCommand.Settings settings = new()
        {
            Id = taskId.ToString(),
            Holder = OtherRoot[..8],
            Reason = "Their node is gone; the card was asked to be closed.",
        };
        int exitCode = await RunAsync(session, settings, chainReader);

        exitCode.Should().Be(ExitCodes.Ok);
        TaskAbandoned abandoned = (await session.Events.FetchStreamAsync(taskId, token: CancellationToken.None))
            .Select(e => e.Data).OfType<TaskAbandoned>().Single();
        abandoned.OnBehalfOfOwnerRootFingerprint.Should().Be(OtherRoot);
        abandoned.OverrideReason.Should().Be("Their node is gone; the card was asked to be closed.");
    }

    [Fact]
    public async Task A_member_role_node_cannot_override_even_when_it_names_the_holder()
    {
        Guid taskId = await SeedTaskAssignedToAnotherOwnerAsync();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        FakeLedgerChainReader chainReader = await OwnerRoleChainAsync(session, MembershipRole.Member);

        TaskAbandonCommand.Settings settings = new() { Id = taskId.ToString(), Holder = OtherRoot[..8], Reason = "tidying" };
        Func<Task> act = () => RunAsync(session, settings, chainReader);

        await act.Should().ThrowAsync<DomainBusinessRuleException>().WithMessage("*Owner role*");
    }

    [Fact]
    public async Task An_owners_own_task_abandons_without_the_override_and_leaves_the_override_fields_empty()
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (string myRoot, BootstrapContext context) = await EstablishOwnRootAsync(session);
        Guid taskId = await SeedTaskAsync(context, taskOwnerRoot: myRoot);
        FakeLedgerChainReader chainReader = new(TrustChain.Empty);

        int exitCode = await RunAsync(session, new TaskAbandonCommand.Settings { Id = taskId.ToString(), Reason = "done with it" }, chainReader);

        exitCode.Should().Be(ExitCodes.Ok);
        TaskAbandoned abandoned = (await session.Events.FetchStreamAsync(taskId, token: CancellationToken.None))
            .Select(e => e.Data).OfType<TaskAbandoned>().Single();
        abandoned.OnBehalfOfOwnerRootFingerprint.Should().BeNull();
        abandoned.OverrideReason.Should().BeNull();
    }

    [Fact]
    public async Task An_unassigned_replicated_task_whose_creator_root_is_unverified_needs_the_word_unknown()
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        FakeLedgerChainReader chainReader = await OwnerRoleChainAsync(session, MembershipRole.Owner);
        Guid taskId = await SeedPublishedTaskWithUnverifiedCreatorAsync();

        Func<Task> wrongName = () => RunAsync(
            session, new TaskAbandonCommand.Settings { Id = taskId.ToString(), Holder = OtherRoot[..8], Reason = "stranded" }, chainReader);
        await wrongName.Should().ThrowAsync<DomainBusinessRuleException>().WithMessage("*must be the word unknown*");

        int exitCode = await RunAsync(
            session, new TaskAbandonCommand.Settings { Id = taskId.ToString(), Holder = "unknown", Reason = "stranded" }, chainReader);

        exitCode.Should().Be(ExitCodes.Ok);
        TaskAbandoned abandoned = (await session.Events.FetchStreamAsync(taskId, token: CancellationToken.None))
            .Select(e => e.Data).OfType<TaskAbandoned>().Single();
        abandoned.OnBehalfOfOwnerRootFingerprint.Should().BeNull("an unknown owner has no root to record");
        abandoned.OverrideReason.Should().Be("stranded");
    }

    /// <summary>
    /// An assignment recorded by owner id alone (no fingerprint on the event) resolves through this
    /// node's own owner row, which is the only owner row a node holds besides ones it has never
    /// heard of: owner events never replicate.
    /// </summary>
    [Fact]
    public async Task A_task_assigned_to_this_nodes_own_owner_id_alone_abandons_without_the_override()
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (_, BootstrapContext context) = await EstablishOwnRootAsync(session);
        Guid taskId = await SeedTaskAsync(context, taskOwnerRoot: null, assignedOwnerId: context.OwnerId);

        await AssertAbandonsAsOwnActAsync(session, taskId);
    }

    [Fact]
    public async Task An_unassigned_replicated_task_with_a_verified_creator_root_of_this_owner_abandons_without_the_override()
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (string myRoot, _) = await EstablishOwnRootAsync(session);
        Guid taskId = await SeedPublishedTaskWithUnverifiedCreatorAsync(creatorRoot: myRoot);

        await AssertAbandonsAsOwnActAsync(session, taskId);
    }

    /// <summary>
    /// The receive gate judges a natively created, unassigned task against the root its genesis was
    /// stamped with, so once the owner's root has moved on the guard refuses what peers would drop.
    /// </summary>
    [Fact]
    public async Task A_native_unassigned_task_is_judged_by_the_root_its_genesis_was_stamped_with()
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (string stampedRoot, BootstrapContext context) = await EstablishOwnRootAsync(session);
        Guid taskId = await SeedNativeUnassignedTaskAsync(context);
        OwnerAggregate owner = (await session.Events.AggregateStreamAsync<OwnerAggregate>(context.OwnerId, token: CancellationToken.None))!;
        session.Events.Append(context.OwnerId, OwnerDecider.ClaimRoot(owner, OtherRoot, verified: true, Now));
        await session.SaveChangesAsync(CancellationToken.None);

        Func<Task> act = () => RunAsync(
            session, new TaskAbandonCommand.Settings { Id = taskId.ToString(), Reason = "tidying" }, new FakeLedgerChainReader(TrustChain.Empty));

        (await act.Should().ThrowAsync<DomainBusinessRuleException>())
            .Which.Message.Should().Contain(stampedRoot[..12], "the task belongs to the root its genesis carries, not the owner's current one");
    }

    [Fact]
    public async Task A_task_assigned_to_an_owner_this_node_has_never_heard_of_is_unknown_not_the_creators()
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (string myRoot, BootstrapContext context) = await EstablishOwnRootAsync(session);
        Guid taskId = await SeedTaskAsync(context, taskOwnerRoot: null, assignedOwnerId: DomainId.New());

        Func<Task> act = () => RunAsync(
            session, new TaskAbandonCommand.Settings { Id = taskId.ToString(), Reason = "tidying" }, new FakeLedgerChainReader(TrustChain.Empty));

        await act.Should().ThrowAsync<DomainBusinessRuleException>().WithMessage("*owner is unknown*assignee*");
    }

    private async Task AssertAbandonsAsOwnActAsync(IDocumentSession session, Guid taskId)
    {
        int exitCode = await RunAsync(
            session, new TaskAbandonCommand.Settings { Id = taskId.ToString(), Reason = "done" }, new FakeLedgerChainReader(TrustChain.Empty));

        exitCode.Should().Be(ExitCodes.Ok);
        TaskAbandoned abandoned = (await session.Events.FetchStreamAsync(taskId, token: CancellationToken.None))
            .Select(e => e.Data).OfType<TaskAbandoned>().Single();
        abandoned.OnBehalfOfOwnerRootFingerprint.Should().BeNull();
        abandoned.OverrideReason.Should().BeNull();
    }

    private async Task<int> RunAsync(
        IDocumentSession session, TaskAbandonCommand.Settings settings, FakeLedgerChainReader chainReader)
    {
        using ScopedConnectionString scope = new(_postgres.ConnectionString);
        return await TaskAbandonCommand.RunAsync(
            _postgres.Store, session, settings, chainReader, new NodeKeyStore(), CancellationToken.None);
    }

    private async Task<Guid> SeedTaskAssignedToAnotherOwnerAsync()
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, CancellationToken.None);
        await session.SaveChangesAsync(CancellationToken.None);
        return await SeedTaskAsync(context, taskOwnerRoot: OtherRoot);
    }

    private async Task<Guid> SeedTaskAsync(BootstrapContext context, string? taskOwnerRoot, Guid? assignedOwnerId = null)
    {
        Guid projectId = await SeedProjectAsync();
        Guid taskId = DomainId.New();
        Guid otherOwnerId = assignedOwnerId ?? DomainId.New();
        TaskAdded added = TaskDecider.Add(
            taskId, projectId, "Close me out", ["done"], TaskType.Chore, null, null, null, Now, otherOwnerId);
        TaskAggregate task = new();
        task.Apply(added);
        TaskPublished published = TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, otherOwnerId);
        task.Apply(published);
        TaskAssigned assigned = TaskDecider.Assign(
            task, otherOwnerId, [], Now, otherOwnerId, assignedOwnerRootFingerprint: taskOwnerRoot);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, added, published, assigned);
        await session.SaveChangesAsync(CancellationToken.None);
        return taskId;
    }

    private async Task<Guid> SeedPublishedTaskWithUnverifiedCreatorAsync(string creatorRoot = "")
    {
        Guid projectId = await SeedProjectAsync();
        Guid taskId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            taskId, projectId, "Close me out", ["done"], TaskType.Chore, null, null, null, Now, DomainId.New());

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, TaskSeed.Publishable(added, added.AddedByOwnerId, Now));
        session.Store(new TaskCreatorRootRecord
        {
            Id = taskId,
            ProjectId = projectId,
            ClaimedOriginNodeId = DomainId.New(),
            CreatorRootFingerprint = creatorRoot,
        });
        await session.SaveChangesAsync(CancellationToken.None);
        return taskId;
    }

    private async Task<Guid> SeedNativeUnassignedTaskAsync(BootstrapContext context)
    {
        Guid projectId = await SeedProjectAsync();
        Guid taskId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            taskId, projectId, "Close me out", ["done"], TaskType.Chore, null, null, null, Now, context.OwnerId);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, TaskSeed.Publishable(added, added.AddedByOwnerId, Now));
        await session.SaveChangesAsync(CancellationToken.None);
        return taskId;
    }

    private async Task<Guid> SeedProjectAsync()
    {
        Guid projectId = DomainId.New();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Store(new ProjectDetails
        {
            Id = projectId,
            Name = $"abandon-{projectId:N}"[..12],
            RepositoryPath = "/does/not/matter/on/a/fake/chain",
            BaseBranch = "main",
            BranchNameTemplate = BranchNameTemplate.Default,
        });
        await session.SaveChangesAsync(CancellationToken.None);
        return projectId;
    }

    private async Task<(string Root, BootstrapContext Context)> EstablishOwnRootAsync(IDocumentSession session)
    {
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, CancellationToken.None);
        await session.SaveChangesAsync(CancellationToken.None);
        NodeSigningKey key = await new NodeKeyStore().EnsureAsync(context.NodeId, CancellationToken.None);
        OwnerAggregate owner = (await session.Events.AggregateStreamAsync<OwnerAggregate>(context.OwnerId, token: CancellationToken.None))!;
        session.Events.Append(context.OwnerId, OwnerDecider.ClaimRoot(owner, key.Fingerprint, verified: true, Now));
        await session.SaveChangesAsync(CancellationToken.None);
        return (key.Fingerprint, context);
    }

    private async Task<FakeLedgerChainReader> OwnerRoleChainAsync(IDocumentSession session, MembershipRole role)
    {
        (string myRoot, BootstrapContext context) = await EstablishOwnRootAsync(session);
        NodeSigningKey key = await new NodeKeyStore().EnsureAsync(context.NodeId, CancellationToken.None);
        return new FakeLedgerChainReader(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myRoot] = new(myRoot, key.PublicKeyLine, []) },
            [new ProjectMember(myRoot, role, Now)]));
    }
}
