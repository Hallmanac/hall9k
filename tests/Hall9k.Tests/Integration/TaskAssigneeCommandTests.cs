using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Daemon;
using Hall9k.Daemon.Dispatch;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Features.Trust;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Trust;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Spectre.Console.Cli;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// A member lays hold of a Draft or a Published task without dispatching it, against the real store:
/// who may assign and hand off (<see cref="TaskAssignCommand.AuthorizeAssignAsync"/>), what the board,
/// <c>h9k task show</c> and the owner readers say about a held task, and that no dispatcher ever sees one.
/// The pure halves are in <c>TaskAssigneeTests</c> and <c>EventReplicationInboxTaskActGateTests</c>.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class TaskAssigneeCommandTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly string MyRoot = new('1', 64);
    private static readonly string RyanRoot = new('2', 64);
    private static readonly string TaylorRoot = new('3', 64);

    private readonly PostgresFixture _postgres;
    private readonly ScopedTestHome _scopedHome = new();
    private readonly FakeLedgerChainReader _chain = new(TrustChain.Empty);
    private BootstrapContext _me = null!;
    private OwnerDetailsSeed _ryan = null!;
    private OwnerDetailsSeed _taylor = null!;
    private Guid _projectId;

    public TaskAssigneeCommandTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));

        await NodeBootstrapSeed.SeedGitHubConnectionAsync(_postgres.Store, cts.Token);
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            _me = await NodeBootstrap.EnsureAsync(session, cts.Token);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            OwnerAggregate owner = (await session.Events.AggregateStreamAsync<OwnerAggregate>(_me.OwnerId, token: cts.Token))!;
            session.Events.Append(_me.OwnerId, OwnerDecider.ClaimRoot(owner, MyRoot, verified: true, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        _ryan = await SeedOwnerAsync("Ryan", RyanRoot, cts.Token);
        _taylor = await SeedOwnerAsync("Taylor", TaylorRoot, cts.Token);
        _projectId = DomainId.New();
        await using IDocumentSession projectSession = _postgres.Store.LightweightSession();
        projectSession.Store(new ProjectDetails
        {
            Id = _projectId,
            Name = "assignee",
            RepositoryPath = "/does/not/matter",
            BaseBranch = "main",
            BranchNameTemplate = BranchNameTemplate.Default,
        });
        await projectSession.SaveChangesAsync(cts.Token);
    }

    public Task DisposeAsync()
    {
        _scopedHome.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_creator_hands_an_unassigned_draft_to_a_teammate_and_it_stays_a_draft_nothing_dispatches()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid taskId = await SeedDraftAsync(createdBy: _me.OwnerId, cts.Token);

        TaskAssigneeSet? set = await AssignAsync(taskId, _ryan, cts.Token);

        set.Should().NotBeNull();
        await using IQuerySession query = _postgres.Store.QuerySession();
        TaskAggregate task = (await query.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        task.State.Should().Be(TaskState.Draft);
        task.AssigneeOwnerId.Should().Be(_ryan.Id);
        task.AssigneeOwnerFingerprint.Should().Be(RyanRoot);
        task.AssignedOwnerId.Should().BeNull();
        TaskListItem row = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        row.State.Should().Be(TaskState.Draft);
        row.AssigneeOwnerId.Should().Be(_ryan.Id);
        row.AssignedOwnerId.Should().BeNull();

        await using IDocumentSession guardSession = _postgres.Store.LightweightSession();
        (await TaskOwnerGuard.MayActAsync(guardSession, task, _me, cts.Token))
            .Should().BeFalse("the creator handed it away, so it is Ryan's now, by the same rule the receive gate applies");
    }

    [Fact]
    public async Task An_assignee_hands_the_task_to_another_member_whoever_created_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid taskId = await SeedDraftAsync(createdBy: _ryan.Id, cts.Token, heldBy: Me);

        TaskOwnerOverrideDecision decision = await AuthorizeAsync(taskId, _taylor, cts.Token);
        TaskAssigneeSet? set = await AssignAsync(taskId, _taylor, cts.Token);

        decision.Outcome.Should().Be(TaskOwnerOverrideOutcome.OwnAct);
        set.Should().NotBeNull();
        await using IQuerySession query = _postgres.Store.QuerySession();
        (await query.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!
            .AssigneeOwnerFingerprint.Should().Be(TaylorRoot);
    }

    [Fact]
    public async Task A_member_taking_a_teammates_unassigned_draft_is_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid taskId = await SeedDraftAsync(createdBy: _ryan.Id, cts.Token, replicatedCreatorRoot: RyanRoot);

        Func<Task> take = () => AuthorizeAsync(taskId, Me, cts.Token);

        (await take.Should().ThrowAsync<DomainBusinessRuleException>()).Which.Message
            .Should().Contain("belongs to").And.Contain("Owner-role member");
    }

    [Fact]
    public async Task A_member_may_take_an_unassigned_published_task_for_themselves_but_not_name_someone_else()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid taskId = await SeedDraftAsync(createdBy: _ryan.Id, cts.Token, replicatedCreatorRoot: RyanRoot, publish: true);

        TaskOwnerOverrideDecision self = await AuthorizeAsync(taskId, Me, cts.Token);
        Func<Task> other = () => AuthorizeAsync(taskId, _taylor, cts.Token);

        self.Outcome.Should().Be(TaskOwnerOverrideOutcome.OwnAct, "a member may self-assign a free Published task, as h9k task start does");
        await other.Should().ThrowAsync<DomainBusinessRuleException>("a non-owner member may name only themselves");
    }

    [Fact]
    public async Task Queue_is_the_assignees_act_alone_and_a_refusal_names_the_assignees_hand_off()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid heldByRyan = await SeedDraftAsync(createdBy: _me.OwnerId, cts.Token, heldBy: _ryan, publish: true);
        Guid heldByMe = await SeedDraftAsync(createdBy: _me.OwnerId, cts.Token, heldBy: Me, publish: true);
        Guid free = await SeedDraftAsync(createdBy: _me.OwnerId, cts.Token, publish: true);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        OwnerDetails actor = (await session.LoadAsync<OwnerDetails>(_me.OwnerId, cts.Token))!;
        async Task Queue(Guid id) => await TaskQueueCommand.AuthorizeAsync(
            session, (await session.Events.AggregateStreamAsync<TaskAggregate>(id, token: cts.Token))!, _me, actor,
            cts.Token);

        Func<Task> another = () => Queue(heldByRyan);
        (await another.Should().ThrowAsync<DomainConflictException>()).Which.Message
            .Should().Contain("Ryan").And.Contain($"h9k task assign {DomainId.Short(heldByRyan)} <member>");
        await Queue(heldByMe);
        await Queue(free);
    }

    [Fact]
    public async Task A_task_a_teammate_holds_refuses_even_a_self_assign_without_the_override()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid taskId = await SeedDraftAsync(createdBy: _me.OwnerId, cts.Token, heldBy: _ryan, publish: true);

        Func<Task> take = () => AuthorizeAsync(taskId, Me, cts.Token);

        (await take.Should().ThrowAsync<DomainBusinessRuleException>()).Which.Message
            .Should().Contain("belongs to").And.Contain(RyanRoot[..12], "the refusal names whose task it is");
    }

    [Fact]
    public async Task Unassigning_a_draft_or_an_unqueued_published_task_clears_the_hold_and_the_dispatcher_never_sees_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid draft = await SeedDraftAsync(createdBy: _me.OwnerId, cts.Token, heldBy: Me);
        Guid published = await SeedDraftAsync(createdBy: _me.OwnerId, cts.Token, heldBy: Me, publish: true);

        foreach (Guid taskId in new[] { draft, published })
        {
            await using IDocumentSession session = _postgres.Store.LightweightSession();
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            TaskUnassignCommand.ReleasesAssigneeOnly(task).Should().BeTrue();
            (await TaskOwnerGuard.AuthorizeAsync(
                session, task, _me, "unassign", holder: null, reason: null, _chain, new NodeKeyStore(), cts.Token))
                .Outcome.Should().Be(TaskOwnerOverrideOutcome.OwnAct, "the assignee may let go of its own hold");
            session.Events.Append(taskId, TaskUnassignCommand.ClearAssignee(task, null, _me.OwnerId, TaskOwnerOverrideDecision.OwnAct, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession query = _postgres.Store.QuerySession();
        foreach (Guid taskId in new[] { draft, published })
        {
            TaskAggregate task = (await query.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            task.AssigneeOwnerId.Should().BeNull();
            (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.AssigneeOwnerId.Should().BeNull();
        }

        (await query.Events.AggregateStreamAsync<TaskAggregate>(draft, token: cts.Token))!.State.Should().Be(TaskState.Draft);
        (await query.Events.AggregateStreamAsync<TaskAggregate>(published, token: cts.Token))!.State.Should().Be(TaskState.Published);
    }

    [Fact]
    public async Task Only_the_assignee_may_let_go_of_a_hold_and_an_Owner_role_override_needs_the_holder_and_a_reason()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid taskId = await SeedDraftAsync(createdBy: _me.OwnerId, cts.Token, heldBy: _ryan);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        Func<Task> creatorLetsGo = () => TaskOwnerGuard.AuthorizeAsync(
            session, task, _me, "unassign", holder: null, reason: null, _chain, new NodeKeyStore(), cts.Token);

        (await creatorLetsGo.Should().ThrowAsync<DomainBusinessRuleException>()).Which.Message
            .Should().Contain("--holder").And.Contain("--reason");
    }

    [Fact]
    public async Task Start_work_and_publish_queue_refuse_a_task_another_owner_holds_through_the_shared_check()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid held = await SeedDraftAsync(createdBy: _me.OwnerId, cts.Token, heldBy: _ryan);
        Guid mine = await SeedDraftAsync(createdBy: _me.OwnerId, cts.Token, heldBy: Me);
        Guid free = await SeedDraftAsync(createdBy: _me.OwnerId, cts.Token);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        async Task Check(Guid id)
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(id, token: cts.Token))!;
            await TaskOwnerGuard.AssertNotHeldByAnotherOwnerAsync(session, task, _me, MyRoot, "it would queue over their hold.", cts.Token);
        }

        Func<Task> heldByRyan = () => Check(held);
        (await heldByRyan.Should().ThrowAsync<DomainConflictException>()).Which.Message
            .Should().Contain("Ryan").And.Contain("h9k task unassign");
        await Check(mine);
        await Check(free);
    }

    [Fact]
    public async Task The_board_row_and_task_show_say_a_held_draft_is_assigned_but_not_queued()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid mineDraft = await SeedDraftAsync(createdBy: _me.OwnerId, cts.Token, heldBy: Me);
        Guid queued = await SeedQueuedAsync(cts.Token);

        await using IQuerySession query = _postgres.Store.QuerySession();
        IReadOnlyList<TaskStatusRow> rows = await TaskStatusComposer.ComposeAllAsync(query, Now, cts.Token);

        rows.Single(row => row.TaskId == mineDraft).Assignee.Should().EndWith("· not queued");
        rows.Single(row => row.TaskId == queued).Assignee.Should().NotContain("not queued", "a queued task is held by the owner it is queued for");

        TaskDetails details = (await query.LoadAsync<TaskDetails>(mineDraft, cts.Token))!;
        (await TaskShowCommand.AssigneeMarkupAsync(query, details, MemberLabelLookup.Empty, cts.Token))
            .Should().Contain("not queued");
        TaskDetails queuedDetails = (await query.LoadAsync<TaskDetails>(queued, cts.Token))!;
        (await TaskShowCommand.AssigneeMarkupAsync(query, queuedDetails, MemberLabelLookup.Empty, cts.Token))
            .Should().NotContain("not queued");
    }

    [Fact]
    public async Task The_owner_readers_read_the_assignee_so_the_board_and_the_guard_give_one_answer_for_a_held_draft()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid heldByRyan = await SeedDraftAsync(createdBy: _me.OwnerId, cts.Token, heldBy: _ryan);

        await using IQuerySession query = _postgres.Store.QuerySession();
        TaskAggregate task = (await query.Events.AggregateStreamAsync<TaskAggregate>(heldByRyan, token: cts.Token))!;
        TaskOwnerFacts fromStream = await TaskOwnerFactsReader.ReadAsync(query, task, cts.Token);
        TaskListItem row = (await query.LoadAsync<TaskListItem>(heldByRyan, cts.Token))!;
        TaskOwnerFacts fromRow = TaskListItemOwnerFacts.From(row, _ => null, creator: null);

        fromStream.Assigned.RootFingerprint.Should().Be(RyanRoot);
        fromRow.Assigned.RootFingerprint.Should().Be(RyanRoot);
        fromStream.Creator.State.Should().Be(OwnerRootFactState.Absent, "the creator decides only when nobody holds the task");
        TaskListItemOwnerFacts.NeedsCreator(row).Should().BeFalse();

        IReadOnlyList<TaskStatusRow> rows = await TaskStatusComposer.ComposeAllAsync(query, Now, cts.Token);
        rows.Single(candidate => candidate.TaskId == heldByRyan).IsTeammates
            .Should().BeTrue("the creator's own board no longer counts a task they handed away as theirs");
    }

    [Fact]
    public async Task A_draft_or_a_published_task_somebody_holds_is_never_claimable_by_the_dispatcher()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, cts.Token);
        OwnerDetailsSeed self = new(node.OwnerId, "Me", MyRoot);
        await SeedDraftAsync(createdBy: node.OwnerId, cts.Token, heldBy: self);
        Guid heldPublished = await SeedDraftAsync(createdBy: node.OwnerId, cts.Token, heldBy: self, publish: true, projectId: DomainId.New());

        DispatchEngine engine = new(
            _postgres.Store, node, new DaemonConnection(_postgres.ConnectionString), new FakeProcessManager(),
            new LaunchHoldEngine(_postgres.Store, NullLogger<LaunchHoldEngine>.Instance),
            Options.Create(new DaemonOptions { MaxConcurrentTaskRuns = 100, LeaseTimeout = TimeSpan.FromSeconds(60) }),
            NullLogger<DispatchEngine>.Instance);

        (await engine.ClaimEligibleAsync(cts.Token)).Should().BeEmpty(
            "a hold records who holds the task and never queues it, so there is nothing for the dispatcher to claim");

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(heldPublished, token: cts.Token))!;
            session.Events.Append(heldPublished, TaskDecider.Assign(task, node.OwnerId, [], Now, node.OwnerId, MyRoot));
            await session.SaveChangesAsync(cts.Token);
        }

        (await engine.ClaimEligibleAsync(cts.Token)).Select(work => work.TaskId).Should().Equal(
            [heldPublished], "the go signal is what the dispatcher reads, and only the queued task qualifies");
    }

    private OwnerDetailsSeed Me => new(_me.OwnerId, "Me", MyRoot);

    private async Task<TaskOwnerOverrideDecision> AuthorizeAsync(Guid taskId, OwnerDetailsSeed target, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken))!;
        return await TaskAssignCommand.AuthorizeAssignAsync(
            session, task, _me, await target.LoadAsync(session, cancellationToken), holder: null, reason: null,
            _chain, new NodeKeyStore(), cancellationToken);
    }

    private async Task<TaskAssigneeSet?> AssignAsync(Guid taskId, OwnerDetailsSeed target, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken))!;
        TaskOwnerOverrideDecision decision = await TaskAssignCommand.AuthorizeAssignAsync(
            session, task, _me, await target.LoadAsync(session, cancellationToken), holder: null, reason: null,
            _chain, new NodeKeyStore(), cancellationToken);
        TaskAssigneeSet? set = await TaskAssignCommand.AppendAssigneeAsync(
            session, task, await target.LoadAsync(session, cancellationToken), _me.OwnerId, decision, Now, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return set;
    }

    /// <summary>
    /// A draft added by <paramref name="createdBy"/>, optionally already held by <paramref name="heldBy"/>,
    /// published, and shared with the team. A task created by a teammate is a replicated one here, so its creator
    /// is the verified record a replicated genesis would carry (<paramref name="replicatedCreatorRoot"/>).
    /// </summary>
    private async Task<Guid> SeedDraftAsync(
        Guid createdBy, CancellationToken cancellationToken, OwnerDetailsSeed? heldBy = null, bool publish = false,
        string? replicatedCreatorRoot = null, Guid? projectId = null)
    {
        Guid taskId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            taskId, projectId ?? _projectId, "Hold it", ["it is held"], TaskType.Chore, null, null, null, Now, createdBy);
        List<object> events = [added];
        TaskAggregate task = new();
        task.Apply(added);

        // A fresh draft is Fleet scope, which a teammate never receives, so a draft meant to be handed
        // to one is shared first, as the refusal says.
        events.Add(new TaskScopeSet(taskId, ReplicationScope.Team, Now, createdBy));

        if (heldBy is not null)
        {
            events.Add(new TaskAssigneeSet(taskId, heldBy.Id, heldBy.Root, Now, createdBy));
        }

        if (publish)
        {
            events.Add(TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, createdBy));
        }

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, [.. events]);
        if (replicatedCreatorRoot is not null)
        {
            session.Store(new TaskCreatorRootRecord
            {
                Id = taskId,
                ProjectId = _projectId,
                ClaimedOriginNodeId = DomainId.New(),
                CreatorRootFingerprint = replicatedCreatorRoot,
            });
        }

        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    /// <summary>
    /// The real command tree, end to end against the store: assign only records the hold (the task stays
    /// Published and nothing is claimable), queue is the go signal and makes the actor the assignee when
    /// nobody held it, dequeue keeps the hold, unassign lets go of it, and publish refuses the retired
    /// <c>--assign</c> without touching the draft.
    /// </summary>
    [Fact]
    public async Task The_real_commands_walk_assign_then_queue_then_dequeue_then_unassign()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using ScopedConnectionString connection = new(_postgres.ConnectionString);
        Guid taskId = await SeedDraftAsync(createdBy: _me.OwnerId, cts.Token, publish: true);
        string id = taskId.ToString();

        (await RunCliAsync(cts.Token, "task", "assign", id, _me.OwnerId.ToString())).Should().BeNull();
        TaskAggregate held = await ReadAsync(taskId, cts.Token);
        held.State.Should().Be(TaskState.Published, "assign records ownership only");
        held.AssigneeOwnerId.Should().Be(_me.OwnerId);
        held.AssignedOwnerId.Should().BeNull();

        (await RunCliAsync(cts.Token, "task", "queue", id)).Should().BeNull();
        TaskAggregate queued = await ReadAsync(taskId, cts.Token);
        queued.State.Should().Be(TaskState.Queued);
        queued.AssignedOwnerId.Should().Be(_me.OwnerId);
        queued.AssigneeOwnerId.Should().Be(_me.OwnerId);

        (await RunCliAsync(cts.Token, "task", "queue", id, "--node")).Should().BeOfType<DomainConflictException>()
            .Which.Message.Should().Contain($"h9k task assign {DomainId.Short(taskId)} --node", "placement of a queued task is assign's");

        (await RunCliAsync(cts.Token, "task", "dequeue", id)).Should().BeNull();
        TaskAggregate dequeued = await ReadAsync(taskId, cts.Token);
        dequeued.State.Should().Be(TaskState.Published);
        dequeued.AssignedOwnerId.Should().BeNull();
        dequeued.AssigneeOwnerId.Should().Be(_me.OwnerId, "dequeue keeps the assignee");

        (await RunCliAsync(cts.Token, "task", "queue", id)).Should().BeNull();
        (await RunCliAsync(cts.Token, "task", "unassign", id)).Should().BeNull();
        TaskAggregate released = await ReadAsync(taskId, cts.Token);
        released.State.Should().Be(TaskState.Published);
        released.AssigneeOwnerId.Should().BeNull("unassign on a queued task dequeues and clears the assignee in one append");
    }

    [Fact]
    public async Task Queue_with_nobody_holding_the_task_makes_the_actor_the_assignee_and_another_holder_refuses_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using ScopedConnectionString connection = new(_postgres.ConnectionString);
        Guid free = await SeedDraftAsync(createdBy: _me.OwnerId, cts.Token, publish: true);
        Guid heldByRyan = await SeedDraftAsync(createdBy: _me.OwnerId, cts.Token, heldBy: _ryan, publish: true);

        (await RunCliAsync(cts.Token, "task", "queue", free.ToString())).Should().BeNull();
        TaskAggregate queuedFree = await ReadAsync(free, cts.Token);
        queuedFree.State.Should().Be(TaskState.Queued);
        queuedFree.AssigneeOwnerId.Should().Be(_me.OwnerId, "the actor became the assignee through the same TaskAssigned");

        await using IQuerySession before = _postgres.Store.QuerySession();
        long versionBefore = (await before.Events.FetchStreamStateAsync(heldByRyan, token: cts.Token))!.Version;
        Exception? refusal = await RunCliAsync(cts.Token, "task", "queue", heldByRyan.ToString());

        refusal.Should().BeOfType<DomainConflictException>().Which.Message
            .Should().Contain("Ryan").And.Contain("h9k task assign").And.Contain("<member>");
        (await before.Events.FetchStreamStateAsync(heldByRyan, token: cts.Token))!.Version
            .Should().Be(versionBefore, "a refused queue appends nothing");
        (await ReadAsync(heldByRyan, cts.Token)).State.Should().Be(TaskState.Published);
    }

    [Fact]
    public async Task Publish_assign_refuses_and_leaves_the_draft_untouched_while_publish_queue_publishes_and_queues()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using ScopedConnectionString connection = new(_postgres.ConnectionString);
        Guid refused = await SeedDraftAsync(createdBy: _me.OwnerId, cts.Token);
        Guid queuedAtPublish = await SeedDraftAsync(createdBy: _me.OwnerId, cts.Token);

        Exception? refusal = await RunCliAsync(cts.Token, "task", "publish", refused.ToString(), "--assign");

        refusal.Should().BeOfType<DomainValidationException>().Which.Message.Should().Contain("--queue");
        (await ReadAsync(refused, cts.Token)).State.Should().Be(TaskState.Draft);

        (await RunCliAsync(cts.Token, "task", "publish", queuedAtPublish.ToString(), "--queue")).Should().BeNull();
        TaskAggregate queued = await ReadAsync(queuedAtPublish, cts.Token);
        queued.State.Should().Be(TaskState.Queued, "publish --queue publishes and queues in one transaction");
        queued.AssigneeOwnerId.Should().Be(_me.OwnerId);
    }

    private async Task<TaskAggregate> ReadAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = _postgres.Store.QuerySession();
        return (await query.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken))!;
    }

    /// <summary>Runs one command through the shipped tree and returns what it threw, or null when it succeeded.</summary>
    private static async Task<Exception?> RunCliAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        CommandApp app = new();
        // Never the real process environment: this test process can itself inherit the dispatched-session
        // marker from the run that is building it, which would refuse every command before it ran.
        app.Configure(config => CliCommandTree.Configure(config, _ => null));
        try
        {
            await app.RunAsync(arguments, cancellationToken);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return exception is CommandRuntimeException { InnerException: { } inner } ? inner : exception;
        }
    }

    private async Task<Guid> SeedQueuedAsync(CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            taskId, _projectId, "Queued", ["it runs"], TaskType.Chore, null, null, null, Now, _me.OwnerId);
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, TaskSeed.Dispatchable(added, _me.OwnerId, Now));
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    private async Task<OwnerDetailsSeed> SeedOwnerAsync(string name, string root, CancellationToken cancellationToken)
    {
        Guid id = DomainId.New();
        OwnerAggregate owner = new();
        OwnerRegistered registered = OwnerDecider.Register(id, name, $"{name.ToLowerInvariant()}@test.local", Now);
        owner.Apply(registered);
        OwnerRootClaimed claimed = OwnerDecider.ClaimRoot(owner, root, verified: true, Now);
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream<OwnerAggregate>(id, registered, claimed);
        await session.SaveChangesAsync(cancellationToken);
        return new OwnerDetailsSeed(id, name, root);
    }

    private sealed record OwnerDetailsSeed(Guid Id, string Name, string Root)
    {
        public async Task<OwnerDetails> LoadAsync(IQuerySession session, CancellationToken cancellationToken) =>
            (await session.LoadAsync<OwnerDetails>(Id, cancellationToken))!;
    }
}
