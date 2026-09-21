using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Ledger;
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

    /// <summary>An empty in-memory ledger: <c>h9k task pull</c> reads task records only when it has
    /// to resolve a short id or pick among several projects, and every test here passes a full id
    /// with one project registered, so this stands in for a ledger none of them reaches (Brian's
    /// 2026-09-13 testing rule: no test outside <c>GitLedgerTests</c> touches a real repository).
    /// The one test that does reach it seeds a record of its own.</summary>
    private static readonly FakeLedger Ledger = new();

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

        (await TaskPullCommand.RunAsync(session, Ledger, settings, cts.Token)).Should().Be(0);
        (await TaskPullCommand.RunAsync(session, Ledger, settings, cts.Token)).Should().Be(0);

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

        Func<Task> pull = () => TaskPullCommand.RunAsync(session, Ledger, settings, cts.Token);

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
        (await TaskPullCommand.RunAsync(session, Ledger, new TaskPullCommand.Settings { TaskId = taskId.ToString() }, cts.Token))
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

        Func<Task> pull = () => TaskPullCommand.RunAsync(session, Ledger, settings, cts.Token);

        var thrown = await pull.Should().ThrowAsync<DomainValidationException>();
        thrown.Which.Message.Should().Contain("neither a task's nor a run's");

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
            session, Ledger, new TaskPullCommand.Settings { TaskId = taskId.ToString() }, cts.Token);

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

    /// <summary>
    /// Task 9eb5b245's own first criterion: a CLOSED request is not an ask in flight. The 2026-09-19
    /// re-run was told the old request was still on its way when a peer had declined it ten hours
    /// earlier, and the only lever that clears one genuinely still outstanding — including the
    /// pre-v0.10.5 request 01a0bac1, left standing by an inbox that applied a decline to a candidate
    /// cascade alone — is <c>--again</c>.
    /// </summary>
    [Fact]
    public async Task Task_pull_re_asks_once_the_earlier_request_closed_and_again_supersedes_one_still_outstanding()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid projectId = await SeedProjectAsync(claimOwnerRoot: true, cts.Token);
        Guid absentTaskId = DomainId.New();
        TaskPullCommand.Settings settings = new() { TaskId = absentTaskId.ToString() };

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            (await TaskPullCommand.RunAsync(session, Ledger, settings, cts.Token)).Should().Be(0);
        }

        // A peer declines it: EventCatchUpInbox closes a broadcast on a decline (v0.10.5), which is
        // the state the re-ask rule turns on.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            EventCatchUpRequest first = (await session.Query<EventCatchUpRequest>()
                .Where(request => request.ForStreamId == absentTaskId).FirstOrDefaultAsync(cts.Token))!;
            first.AnsweredAt = Now.AddMinutes(1);
            first.DeclinedReason = "nothing held here matches this request";
            session.Store(first);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            (await TaskPullCommand.RunAsync(session, Ledger, settings, cts.Token)).Should().Be(0);
        }

        await using (IQuerySession afterReAsk = _postgres.Store.QuerySession())
        {
            IReadOnlyList<EventCatchUpRequest> requests = await afterReAsk.Query<EventCatchUpRequest>()
                .Where(request => request.ForStreamId == absentTaskId).ToListAsync(cts.Token);
            requests.Should().HaveCount(2, "the declined request was not an ask in flight, so this run asked again");
            requests.Where(request => request.IsOutstanding).Should().ContainSingle();
        }

        // The fresh one IS outstanding, so an ordinary re-run must report it rather than pile a
        // third on top, and --again is what clears it.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            (await TaskPullCommand.RunAsync(session, Ledger, settings, cts.Token)).Should().Be(0);
        }

        await using (IQuerySession afterRepeat = _postgres.Store.QuerySession())
        {
            (await afterRepeat.Query<EventCatchUpRequest>()
                .Where(request => request.ForStreamId == absentTaskId).ToListAsync(cts.Token))
                .Should().HaveCount(2, "an outstanding request suppresses the ask");
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            (await TaskPullCommand.RunAsync(
                session, Ledger, new TaskPullCommand.Settings { TaskId = absentTaskId.ToString(), Again = true },
                cts.Token))
                .Should().Be(0);
        }

        await using IQuerySession read = _postgres.Store.QuerySession();
        IReadOnlyList<EventCatchUpRequest> all = await read.Query<EventCatchUpRequest>()
            .Where(request => request.ForStreamId == absentTaskId).ToListAsync(cts.Token);
        all.Should().HaveCount(3);
        EventCatchUpRequest fresh = all.Single(request => request.IsOutstanding);
        EventCatchUpRequest superseded = all.Single(request => request.SupersededAt is not null);
        superseded.SupersededByRequestId.Should().Be(fresh.Id, "the audit trail names the ask that replaced it");
        superseded.AnsweredAt.Should().BeNull("nothing answered it, and a supersede must never be recorded as an answer");
    }

    /// <summary>
    /// Task 9eb5b245: the id a human has in front of them is the SHORT one, off a board row or a
    /// branch name — the full id lived only on the node that held the task. The ledger's own task
    /// records resolve it, and the project whose ledger carries that record is the project to ask,
    /// which is what keeps a node with several projects registered from having to be told which one
    /// a task id belongs to.
    /// </summary>
    [Fact]
    public async Task Task_pull_resolves_a_short_id_off_the_ledger_and_defaults_the_project_that_names_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await SeedProjectAsync(claimOwnerRoot: true, cts.Token);
        const string SecondRepositoryPath = "/repo-pull-commands-second";
        Guid secondProjectId = await RegisterProjectAsync("second-project", SecondRepositoryPath, cts.Token);
        Guid absentTaskId = DomainId.New();

        FakeLedger ledger = new();
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                SecondRepositoryPath,
                LedgerRefRegistry.Records.RefspecSource,
                LedgerRefRegistry.RecordPath(absentTaskId),
                $"hall9k-task-record: 1\ntask-id: {absentTaskId}\nproject: second-project\nobjective: Ship the thing\n",
                ExpectedBlobId: null,
                "seed task record",
                new LedgerCommitter("Brian Hall", "brian@agelessrx.com"),
                new LedgerSigningKey("fake-key")),
            cts.Token);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (await TaskPullCommand.RunAsync(
            session, ledger, new TaskPullCommand.Settings { TaskId = DomainId.Short(absentTaskId) }, cts.Token))
            .Should().Be(0);

        await using IQuerySession read = _postgres.Store.QuerySession();
        EventCatchUpRequest request = (await read.Query<EventCatchUpRequest>()
            .Where(candidate => candidate.ForStreamId == absentTaskId).FirstOrDefaultAsync(cts.Token))!;
        request.ProjectId.Should().Be(
            secondProjectId, "two projects are eligible, and only one ledger names this task");
        request.IsOutstanding.Should().BeTrue();
    }

    /// <summary>
    /// Task 9eb5b245: <c>TaskDecider.Assign</c> refuses an assignment whose blockers this node holds
    /// no document for, which for a task pulled from a peer is a refusal over a stream the platform
    /// could have fetched. The platform mints those asks itself the moment such a task lands; this
    /// is the same ask for a task that landed before it did.
    /// </summary>
    [Fact]
    public async Task Task_pull_asks_for_a_missing_dependency_of_a_task_it_already_holds()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid projectId = await SeedProjectAsync(claimOwnerRoot: true, cts.Token);
        Guid taskId = DomainId.New();
        Guid absentBlockerId = DomainId.New();

        await using (IDocumentSession seed = _postgres.Store.LightweightSession())
        {
            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Pulled from a peer", ["it works"], TaskType.Feature, null, null, null, Now,
                DomainId.New(), blockedBy: [absentBlockerId]);
            seed.Events.StartStream<TaskAggregate>(taskId, added);
            await seed.SaveChangesAsync(cts.Token);
        }

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (await TaskPullCommand.RunAsync(
            session, Ledger, new TaskPullCommand.Settings { TaskId = taskId.ToString() }, cts.Token))
            .Should().Be(0);

        await using IQuerySession read = _postgres.Store.QuerySession();
        IReadOnlyList<EventCatchUpRequest> requests = await read.Query<EventCatchUpRequest>()
            .Where(request => request.ProjectId == projectId).ToListAsync(cts.Token);
        requests.Should().ContainSingle("the task itself is here; its blocker is the only thing to ask for");
        requests[0].ForStreamId.Should().Be(absentBlockerId);
        requests[0].ForDependencyOfTaskId.Should().Be(taskId, "h9k status says whose dependency it is fetching");
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

    /// <summary>A second messaging-eligible project, for the tests where having only one would make
    /// the default trivially right.</summary>
    private async Task<Guid> RegisterProjectAsync(
        string name, string repositoryPath, CancellationToken cancellationToken)
    {
        Guid projectId = DomainId.New();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        session.Events.StartStream<ProjectAggregate>(
            projectId,
            ProjectDecider.Register(projectId, context.OwnerId, DomainId.New(), name, repositoryPath, null, null, Now));
        await session.SaveChangesAsync(cancellationToken);
        return projectId;
    }
}
