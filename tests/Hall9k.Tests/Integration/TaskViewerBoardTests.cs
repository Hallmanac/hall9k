using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.AutoPrReview;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Marten;
using Npgsql;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The board against the real store: the roots <see cref="TaskListItem"/> carries are written by the
/// inline projection, the viewer is found the way every pane finds this machine's own node, and the
/// answer for each task is held up against the task commands' own guard
/// (<see cref="TaskOwnerGuard"/>), because "the pane calls the same predicate as the commands and
/// gets the same answer" is the property that keeps a teammate's task out of this viewer's bands
/// without losing the viewer's own. The pure halves are in <c>ViewerBoardTests</c> and
/// <c>TaskListItemOwnerRootProjectionTests</c>.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class TaskViewerBoardTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string MyRoot = new('1', 64);
    private static readonly string TeammateRoot = new('2', 64);

    private readonly PostgresFixture _postgres;
    private readonly ScopedTestHome _scopedHome = new();

    public TaskViewerBoardTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync()
    {
        _scopedHome.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task The_board_and_the_task_commands_give_the_same_answer_for_every_shape_of_task()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        BootstrapContext context = await EstablishOwnRootAsync(cts.Token);
        Guid projectId = await SeedProjectAsync(cts.Token);

        Guid own = await SeedAssignedAsync(projectId, context.OwnerId, MyRoot, cts.Token);
        Guid fleetSibling = await SeedAssignedAsync(projectId, DomainId.New(), MyRoot, cts.Token);
        Guid assignedByIdToThisOwner = await SeedAssignedAsync(projectId, context.OwnerId, null, cts.Token);
        Guid heldByTeammate = await SeedHeldAsync(projectId, TeammateRoot, cts.Token);
        Guid assignedToTeammate = await SeedAssignedAsync(projectId, DomainId.New(), TeammateRoot, cts.Token);
        Guid unknownOwner = await SeedAssignedAsync(projectId, DomainId.New(), null, cts.Token);
        Guid nativeCreatorOnly = await SeedCreatorOnlyAsync(projectId, context.OwnerId, creatorRecordRoot: null, cts.Token);
        Guid replicatedFleetSiblingCreatorOnly = await SeedCreatorOnlyAsync(projectId, DomainId.New(), MyRoot, cts.Token);
        Guid replicatedTeammateCreatorOnly = await SeedCreatorOnlyAsync(projectId, DomainId.New(), TeammateRoot, cts.Token);
        Guid unverifiedCreatorOnly = await SeedCreatorOnlyAsync(projectId, DomainId.New(), string.Empty, cts.Token);

        (Guid Id, bool ExpectedViewers)[] shapes =
        [
            (own, true),
            (fleetSibling, true),
            (assignedByIdToThisOwner, true),
            (heldByTeammate, false),
            (assignedToTeammate, false),
            (unknownOwner, false),
            (nativeCreatorOnly, true),
            (replicatedFleetSiblingCreatorOnly, true),
            (replicatedTeammateCreatorOnly, false),
            (unverifiedCreatorOnly, false),
        ];

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        IReadOnlyList<TaskStatusRow> rows = await TaskStatusComposer.ComposeAllAsync(session, Now, cts.Token);

        foreach ((Guid id, bool expectedViewers) in shapes)
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(id, token: cts.Token))!;
            bool commandsMayAct = true;
            try
            {
                await TaskOwnerGuard.AssertMayActAsync(session, task, context, cts.Token);
            }
            catch (DomainBusinessRuleException)
            {
                commandsMayAct = false;
            }

            TaskStatusRow row = rows.Single(candidate => candidate.TaskId == id);
            row.IsTeammates.Should().Be(!expectedViewers, $"task {id}'s board row");
            commandsMayAct.Should().Be(expectedViewers, $"task {id}'s command guard");
            row.IsTeammates.Should().Be(!commandsMayAct, $"task {id}: the board and the commands must agree");
        }
    }

    /// <summary>
    /// The one place the board is deliberately wider than the commands: a task another owner holds,
    /// which this viewer's root has asked to take, is theirs to watch until the answer comes, though
    /// every command still refuses it.
    /// </summary>
    [Fact]
    public async Task A_task_the_viewers_root_has_asked_to_take_is_on_their_board_while_the_commands_still_refuse_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        BootstrapContext context = await EstablishOwnRootAsync(cts.Token);
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid held = await SeedHeldAsync(projectId, TeammateRoot, cts.Token);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.Append(held, new TaskTakeRequested(
                held, context.NodeId, context.OwnerId, MyRoot, "I need it", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using IDocumentSession read = _postgres.Store.LightweightSession();
        TaskStatusRow row = (await TaskStatusComposer.ComposeAllAsync(read, Now, cts.Token)).Single(r => r.TaskId == held);
        TaskAggregate task = (await read.Events.AggregateStreamAsync<TaskAggregate>(held, token: cts.Token))!;

        row.IsTeammates.Should().BeFalse();
        Func<Task> command = () => TaskOwnerGuard.AssertMayActAsync(read, task, context, cts.Token);
        await command.Should().ThrowAsync<DomainBusinessRuleException>();
    }

    [Fact]
    public async Task A_review_request_row_names_its_covering_task_only_when_the_task_is_the_viewers()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        BootstrapContext context = await EstablishOwnRootAsync(cts.Token);
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid mine = await SeedAssignedAsync(
            projectId, context.OwnerId, MyRoot, cts.Token, externalReference: "github-pr:acme/widgets#11");
        Guid theirs = await SeedAssignedAsync(
            projectId, DomainId.New(), TeammateRoot, cts.Token, externalReference: "github-pr:acme/widgets#12");
        await SeedObservedRequestAsync(projectId, context, 11, cts.Token);
        await SeedObservedRequestAsync(projectId, context, 12, cts.Token);

        await using IQuerySession query = _postgres.Store.QuerySession();
        IReadOnlyList<TaskStatusRow> rows = await TaskStatusComposer.ComposeAllAsync(query, Now, cts.Token);
        ReviewRequestPaneContents pane = await ReviewRequestPane.ComposeAllAsync(query, rows, Now, cts.Token);

        ReviewRequestRow ownRow = pane.Requests.Single(request => request.Number == 11);
        ReviewRequestRow teammatesRow = pane.Requests.Single(request => request.Number == 12);
        ownRow.Markup.Should().Contain(DomainId.Short(mine));
        teammatesRow.Markup.Should().Contain("a task this install cannot yet tell from a teammate's already covers it")
            .And.NotContain(DomainId.Short(theirs));
    }

    [Fact]
    public async Task A_row_projected_before_the_owner_roots_landed_is_rebuilt_and_a_current_one_is_left_alone()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await EstablishOwnRootAsync(cts.Token);
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid held = await SeedHeldAsync(projectId, TeammateRoot, cts.Token);
        Guid asked = await SeedHeldAsync(projectId, TeammateRoot, cts.Token);
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.Append(asked, new TaskTakeRequested(asked, DomainId.New(), DomainId.New(), MyRoot, "I need it", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession query = _postgres.Store.QuerySession())
        {
            (await query.LoadAsync<TaskListItem>(held, cts.Token))!.HolderOwnerRootFingerprint.Should().Be(TeammateRoot);
            (await query.LoadAsync<TaskListItem>(asked, cts.Token))!.PendingTakeRequestedByOwnerRootFingerprint
                .Should().Be(MyRoot);
        }

        (await TaskLifecycleProjectionBackfill.RunAsync(_postgres.Store, cts.Token)).Should().BeEmpty(
            "every key is present on a document the current projection wrote");

        foreach (string key in new[] { "holderOwnerRootFingerprint", "pendingTakeRequestedByOwnerRootFingerprint" })
        {
            await StripKeyAsync("mt_doc_tasklistitem", held, key, cts.Token);
            await StripKeyAsync("mt_doc_tasklistitem", asked, key, cts.Token);
        }

        (await TaskLifecycleProjectionBackfill.RunAsync(_postgres.Store, cts.Token))
            .Should().BeEquivalentTo([held, asked], "a missing owner-root key is a staleness marker");

        await using IQuerySession repaired = _postgres.Store.QuerySession();
        (await repaired.LoadAsync<TaskListItem>(held, cts.Token))!.HolderOwnerRootFingerprint.Should().Be(TeammateRoot);
        (await repaired.LoadAsync<TaskListItem>(asked, cts.Token))!.PendingTakeRequestedByOwnerRootFingerprint
            .Should().Be(MyRoot);
        (await TaskLifecycleProjectionBackfill.RunAsync(_postgres.Store, cts.Token)).Should().BeEmpty();
    }

    /// <summary>
    /// The creator is read once for the whole board, and has to say what the commands' own reader says
    /// for each task: the verified record first, then the stamp on a genesis this node wrote, and for
    /// a genesis stamped before any root was claimed the adding owner's root as it stands now.
    /// </summary>
    [Fact]
    public async Task The_boards_batched_creator_read_agrees_with_the_commands_reader_for_every_shape()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        BootstrapContext context;
        await using (IDocumentSession bootstrap = _postgres.Store.LightweightSession())
        {
            context = await NodeBootstrap.EnsureAsync(bootstrap, cts.Token);
            await bootstrap.SaveChangesAsync(cts.Token);
        }

        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid stampedBeforeAnyRoot = await SeedCreatorOnlyAsync(projectId, context.OwnerId, creatorRecordRoot: null, cts.Token);
        await ClaimOwnRootAsync(context, cts.Token);
        Guid stamped = await SeedCreatorOnlyAsync(projectId, context.OwnerId, creatorRecordRoot: null, cts.Token);
        Guid verifiedRecord = await SeedCreatorOnlyAsync(projectId, DomainId.New(), TeammateRoot, cts.Token);
        Guid unverifiedRecord = await SeedCreatorOnlyAsync(projectId, DomainId.New(), string.Empty, cts.Token);
        Guid[] tasks = [stampedBeforeAnyRoot, stamped, verifiedRecord, unverifiedRecord];

        await using IQuerySession query = _postgres.Store.QuerySession();
        IReadOnlyDictionary<Guid, OwnerRootFact> batched = await TaskOwnerFactsReader.ReadCreatorsAsync(
            query, tasks, _ => MyRoot, cts.Token);

        foreach (Guid id in tasks)
        {
            TaskAggregate aggregate = (await query.Events.AggregateStreamAsync<TaskAggregate>(id, token: cts.Token))!;
            OwnerRootFact single = (await TaskOwnerFactsReader.ReadAsync(query, aggregate, cts.Token)).Creator;
            batched[id].Should().Be(single, $"task {id}");
        }

        batched[stampedBeforeAnyRoot].RootFingerprint.Should().Be(MyRoot, "resolved through the adding owner's root as it stands");
        batched[stamped].RootFingerprint.Should().Be(MyRoot);
        batched[verifiedRecord].RootFingerprint.Should().Be(TeammateRoot);
        batched[unverifiedRecord].State.Should().Be(OwnerRootFactState.Unresolved);
    }

    private async Task<BootstrapContext> EstablishOwnRootAsync(CancellationToken cancellationToken)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        await ClaimOwnRootAsync(context, cancellationToken);
        return context;
    }

    private async Task ClaimOwnRootAsync(BootstrapContext context, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        OwnerAggregate owner = (await session.Events.AggregateStreamAsync<OwnerAggregate>(context.OwnerId, token: cancellationToken))!;
        session.Events.Append(context.OwnerId, OwnerDecider.ClaimRoot(owner, MyRoot, verified: true, Now));
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task<Guid> SeedProjectAsync(CancellationToken cancellationToken)
    {
        Guid projectId = DomainId.New();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Store(new ProjectDetails
        {
            Id = projectId,
            Name = $"viewer-{projectId:N}"[..12],
            RepositoryPath = "/does/not/matter",
            BaseBranch = "main",
            BranchNameTemplate = BranchNameTemplate.Default,
        });
        await session.SaveChangesAsync(cancellationToken);
        return projectId;
    }

    /// <summary>Assigned, with or without the fingerprint (an assignment by owner id alone is the older shape).</summary>
    private async Task<Guid> SeedAssignedAsync(
        Guid projectId, Guid assignedOwnerId, string? assignedRoot, CancellationToken cancellationToken,
        string? externalReference = null)
    {
        Guid taskId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            taskId, projectId, "A task", ["done"], TaskType.Chore, null, null,
            externalReference is null ? null : ExternalReference.Parse(externalReference), Now, assignedOwnerId);
        TaskAggregate task = new();
        task.Apply(added);
        TaskPublished published = TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, assignedOwnerId);
        task.Apply(published);
        TaskAssigned assigned = TaskDecider.Assign(
            task, assignedOwnerId, [], Now, assignedOwnerId, assignedOwnerRootFingerprint: assignedRoot);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, added, published, assigned);
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    private async Task<Guid> SeedHeldAsync(Guid projectId, string holderRoot, CancellationToken cancellationToken)
    {
        Guid taskId = await SeedAssignedAsync(projectId, DomainId.New(), holderRoot, cancellationToken);
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.Append(taskId, new TaskClaimed(
            taskId, DomainId.New(), DomainId.New(), LeaseGeneration: 1, DomainId.New(), Now, OwnerRootFingerprint: holderRoot));
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    /// <summary>
    /// Published and never assigned, so the creator decides. A <paramref name="creatorRecordRoot"/> of
    /// null is a task this node created itself (its genesis stamped by the store's own listener); any
    /// other value stores the verified <see cref="TaskCreatorRootRecord"/> a replicated task would carry.
    /// </summary>
    private async Task<Guid> SeedCreatorOnlyAsync(
        Guid projectId, Guid addedByOwnerId, string? creatorRecordRoot, CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            taskId, projectId, "A creator-only task", ["done"], TaskType.Chore, null, null, null, Now, addedByOwnerId);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, TaskSeed.Publishable(added, addedByOwnerId, Now));
        if (creatorRecordRoot is not null)
        {
            session.Store(new TaskCreatorRootRecord
            {
                Id = taskId,
                ProjectId = projectId,
                ClaimedOriginNodeId = DomainId.New(),
                CreatorRootFingerprint = creatorRecordRoot,
            });
        }

        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    private async Task SeedObservedRequestAsync(
        Guid projectId, BootstrapContext context, int number, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Store(new ObservedReviewRequest
        {
            Id = ObservedReviewRequest.ComputeId(context.NodeId, projectId, "acme/widgets", number, "brian"),
            ObservingNodeId = context.NodeId,
            ProjectId = projectId,
            Repository = "acme/widgets",
            Number = number,
            PullRequestUrl = $"https://github.com/acme/widgets/pull/{number}",
            ReviewerLogin = "brian",
            RequestedAt = Now,
            FirstObservedAt = Now,
            LastObservedAt = Now,
            Outcome = ReviewRequestOutcome.AlreadyCovered,
        });
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task StripKeyAsync(string table, Guid id, string key, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = new($"update {table} set data = data - '{key}' where id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        (await command.ExecuteNonQueryAsync(cancellationToken)).Should().Be(1);
    }
}
