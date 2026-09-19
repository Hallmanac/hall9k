using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// Task a56cf16e: <c>h9k task pull</c> and <c>h9k project pull</c>, driven through their own
/// <c>RunAsync</c> bodies rather than re-implemented inline, so what these assert is what the
/// shipped command actually does. Neither command touches git or a network of its own — both queue
/// an envelope in this node's store and stop, which is the whole reason they can be tested here
/// without a repository, a daemon, or a second node.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class PullCommandTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string RepositoryPath = "/repo-pull-commands";
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;

    public PullCommandTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Task_pull_queues_one_broadcast_events_request_and_a_second_run_reports_it_outstanding()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid projectId = await SeedProjectAsync(claimOwnerRoot: true, cts.Token);
        Guid absentTaskId = DomainId.New();

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        TaskPullCommand.Settings settings = new() { TaskId = absentTaskId.ToString() };

        (await TaskPullCommand.RunAsync(session, settings, cts.Token)).Should().Be(0);
        (await TaskPullCommand.RunAsync(session, settings, cts.Token)).Should().Be(0);

        await using IQuerySession read = _postgres.Store.QuerySession();
        IReadOnlyList<EventCatchUpRequest> requests = await read.Query<EventCatchUpRequest>()
            .Where(request => request.ProjectId == projectId && request.ForStreamId == absentTaskId)
            .ToListAsync(cts.Token);
        requests.Should().ContainSingle(
            "a broadcast never times out, so the second run reports the one already outstanding rather than "
            + "queueing a second identical ask");
        requests.Single().IsOutstanding.Should().BeTrue();
        requests.Single().Candidates.Should().BeEmpty("a CLI command has no live trust chain to rank peers from");

        IReadOnlyList<MessageDetails> queued = await read.Query<MessageDetails>()
            .Where(message => message.ProjectId == projectId && message.Kind == MessageKind.EventsRequest.Value)
            .ToListAsync(cts.Token);
        queued.Should().ContainSingle();
        queued.Single().To.Should().Be(MessageAudience.Project.Value);
        queued.Single().About.Should().Be(absentTaskId.ToString());
    }

    [Fact]
    public async Task Task_pull_on_a_node_with_no_owner_root_refuses_naming_project_join_and_queues_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid projectId = await SeedProjectAsync(claimOwnerRoot: false, cts.Token);
        Guid absentTaskId = DomainId.New();

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        TaskPullCommand.Settings settings = new() { TaskId = absentTaskId.ToString() };

        Func<Task> pull = () => TaskPullCommand.RunAsync(session, settings, cts.Token);

        var thrown = await pull.Should().ThrowAsync<DomainValidationException>();
        thrown.Which.Message.Should().Contain("h9k project join pull-project");
        thrown.Which.Message.Should().Contain("nothing was queued");

        await using IQuerySession read = _postgres.Store.QuerySession();
        (await read.Query<EventCatchUpRequest>().Where(request => request.ProjectId == projectId).ToListAsync(cts.Token))
            .Should().BeEmpty("the refusal says nothing was queued, so nothing must have been");
    }

    [Fact]
    public async Task Task_pull_for_a_stream_this_node_already_holds_asks_nobody()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid projectId = await SeedProjectAsync(claimOwnerRoot: true, cts.Token);

        Guid taskId = DomainId.New();
        await using (IDocumentSession seed = _postgres.Store.LightweightSession())
        {
            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Already here", ["it is here"], TaskType.Feature, null, null, null, Now, DomainId.New());
            seed.Events.StartStream<TaskAggregate>(taskId, added);
            await seed.SaveChangesAsync(cts.Token);
        }

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (await TaskPullCommand.RunAsync(session, new TaskPullCommand.Settings { TaskId = taskId.ToString() }, cts.Token))
            .Should().Be(0);

        await using IQuerySession read = _postgres.Store.QuerySession();
        (await read.Query<EventCatchUpRequest>().Where(request => request.ProjectId == projectId).ToListAsync(cts.Token))
            .Should().BeEmpty("there is nothing to pull, so nothing is asked of anyone");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, adversarial lens, low: every id in this platform is a
    /// stream id, so a project, idea, epic, run or node id pasted here finds a stream and used to
    /// be reported back as a task this node already holds, pointing at an <c>h9k task show</c> that
    /// then fails.
    /// </summary>
    [Fact]
    public async Task Task_pull_refuses_an_id_that_names_a_stream_which_is_not_a_tasks()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid projectId = await SeedProjectAsync(claimOwnerRoot: true, cts.Token);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        TaskPullCommand.Settings settings = new() { TaskId = projectId.ToString() };

        Func<Task> pull = () => TaskPullCommand.RunAsync(session, settings, cts.Token);

        var thrown = await pull.Should().ThrowAsync<DomainValidationException>();
        thrown.Which.Message.Should().Contain("not a task's");

        await using IQuerySession read = _postgres.Store.QuerySession();
        (await read.Query<EventCatchUpRequest>().Where(request => request.ProjectId == projectId).ToListAsync(cts.Token))
            .Should().BeEmpty("an id that names no task is a refusal, not an ask");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 4, adversarial lens, medium: a task open on its own node
    /// when that node switched replication on arrives here as a TAIL — the ordinary flush ships
    /// nothing from before the switch-on point — and that stream used to report back as already
    /// held, pointing at an <c>h9k task show</c> with no objective on it. Nor can an ask fix it: a
    /// replicated event is appended, so the older half would land behind the newer half and
    /// <c>EventReplicationInbox</c> refuses it on arrival.
    /// </summary>
    [Fact]
    public async Task Task_pull_refuses_a_task_stream_this_node_holds_only_the_tail_of()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid projectId = await SeedProjectAsync(claimOwnerRoot: true, cts.Token);

        Guid taskId = DomainId.New();
        await using (IDocumentSession seed = _postgres.Store.LightweightSession())
        {
            // No TaskAdded: exactly what an ordinary flush leaves behind for a task created before
            // its own node ever switched replication on.
            seed.Events.StartStream(taskId, new TaskUnassigned(taskId, "handed back", Now, DomainId.New()));
            await seed.SaveChangesAsync(cts.Token);
        }

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Func<Task> pull = () => TaskPullCommand.RunAsync(
            session, new TaskPullCommand.Settings { TaskId = taskId.ToString() }, cts.Token);

        var thrown = await pull.Should().ThrowAsync<DomainValidationException>();
        thrown.Which.Message.Should().Contain("only partly on this node");
        thrown.Which.Message.Should().Contain("cannot be put in front of it");

        await using IQuerySession read = _postgres.Store.QuerySession();
        (await read.Query<EventCatchUpRequest>().Where(request => request.ProjectId == projectId).ToListAsync(cts.Token))
            .Should().BeEmpty("an ask whose answer can only be refused on arrival is not worth queueing");
    }

    [Fact]
    public async Task Project_pull_queues_a_history_request_carrying_its_own_since_bound()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid projectId = await SeedProjectAsync(claimOwnerRoot: true, cts.Token);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        ProjectPullCommand.Settings settings = new() { Project = "pull-project", Since = "28273" };

        (await ProjectPullCommand.RunAsync(session, settings, cts.Token)).Should().Be(0);

        await using IQuerySession read = _postgres.Store.QuerySession();
        EventCatchUpRequest request = (await read.Query<EventCatchUpRequest>()
            .Where(candidate => candidate.ProjectId == projectId)
            .FirstOrDefaultAsync(cts.Token))!;
        request.SinceGlobalSequence.Should().Be(28273);
        request.ForStreamId.Should().BeNull();
        request.ForOriginNodeId.Should().BeNull("a history pull is its own shape, not a bootstrap and not a gap-fill");
        request.IsOutstanding.Should().BeTrue();

        MessageDetails queued = (await read.Query<MessageDetails>()
            .Where(message => message.ProjectId == projectId && message.Kind == MessageKind.EventsRequest.Value)
            .FirstOrDefaultAsync(cts.Token))!;
        EventReplicationCodec.EventsRequestRecord decoded = EventReplicationCodec.DecodeRequest(queued.Body!)!;
        decoded.SinceGlobalSequence.Should().Be(28273);
        decoded.IsExplicitAsk.Should().BeTrue("the answering node serves an explicit ask from below its switch-on point");
    }

    [Fact]
    public async Task Project_pull_on_a_node_with_no_owner_root_refuses_naming_project_join_and_queues_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid projectId = await SeedProjectAsync(claimOwnerRoot: false, cts.Token);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        ProjectPullCommand.Settings settings = new() { Project = "pull-project", Since = "all" };

        Func<Task> pull = () => ProjectPullCommand.RunAsync(session, settings, cts.Token);

        var thrown = await pull.Should().ThrowAsync<DomainValidationException>();
        thrown.Which.Message.Should().Contain("h9k project join pull-project");

        await using IQuerySession read = _postgres.Store.QuerySession();
        (await read.Query<EventCatchUpRequest>().Where(request => request.ProjectId == projectId).ToListAsync(cts.Token))
            .Should().BeEmpty();
    }

    /// <summary>One registered, messaging-eligible project named <c>pull-project</c>, with this
    /// node bootstrapped — and its owner's root fingerprint claimed only when
    /// <paramref name="claimOwnerRoot"/> says so, which is the single fact that decides whether
    /// either pull can address an envelope to anyone at all.</summary>
    private async Task<Guid> SeedProjectAsync(bool claimOwnerRoot, CancellationToken cancellationToken)
    {
        await NodeBootstrapSeed.SeedGitHubConnectionAsync(_postgres.Store, cancellationToken);

        Guid ownerId;
        await using (IDocumentSession bootstrapSession = _postgres.Store.LightweightSession())
        {
            BootstrapContext context = await NodeBootstrap.EnsureAsync(bootstrapSession, cancellationToken);
            await bootstrapSession.SaveChangesAsync(cancellationToken);
            ownerId = context.OwnerId;
        }

        Guid projectId = DomainId.New();
        await using (IDocumentSession registerSession = _postgres.Store.LightweightSession())
        {
            if (claimOwnerRoot)
            {
                OwnerAggregate owner = await registerSession.Events
                    .AggregateStreamAsync<OwnerAggregate>(ownerId, token: cancellationToken)
                    ?? throw new InvalidOperationException("Expected the owner stream bootstrap just started to exist.");
                registerSession.Events.Append(
                    ownerId, OwnerDecider.ClaimRoot(owner, "owner-pull-fingerprint", verified: true, Now));
            }

            registerSession.Events.StartStream<ProjectAggregate>(
                projectId,
                ProjectDecider.Register(projectId, ownerId, DomainId.New(), "pull-project", RepositoryPath, null, null, Now));
            await registerSession.SaveChangesAsync(cancellationToken);
        }

        return projectId;
    }
}
