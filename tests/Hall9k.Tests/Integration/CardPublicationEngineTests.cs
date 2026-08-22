using System.Text.Json;
using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.Publication;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using JasperFx;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The daemon half of h9k task push-to-jira (backlog 18): a request becomes one agent session,
/// and what is recorded afterwards is read off the task rather than off what the session said.
/// <para>
/// The assertions worth having here are all about the gap between claiming and being believed. A
/// session that reports a beautiful success and never gets a key past h9k task link-jira has
/// published nothing, and the record has to say so — because the alternative is a task that
/// looks linked and a card that does not exist.
/// </para>
/// </summary>
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class CardPublicationEngineTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private const string TokenVariable = "HALL9K_TEST_PUBLICATION_JIRA_TOKEN";

    private readonly string _home = SetTempHome();
    private readonly string _repository = Path.Combine(Path.GetTempPath(), $"hall9k-repo-{Guid.NewGuid():N}");

    private static string SetTempHome()
    {
        string home = Path.Combine(Path.GetTempPath(), $"hall9k-home-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("HALL9K_HOME", home);
        return home;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", null);
        Environment.SetEnvironmentVariable(TokenVariable, null);
        foreach (string directory in new[] { _home, _repository })
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// A session that ends with a scripted result. <paramref name="beforeFinishing"/> is what a
    /// real one does just before it ends: call h9k task link-jira, modelled here as the
    /// WorkItemLinked append that command makes once it has read the card back.
    /// <para>
    /// That work runs detached and only once the whole launch is on the stream, because that is
    /// the real order and the engine depends on it: the daemon records a launch in two appends,
    /// the dispatch before the spawn and the process after it, and a session that writes to the
    /// task in between is racing its own launch. Two writers on one stream is not a race either
    /// of them survives — Marten refuses the second append outright — so the wait is for the
    /// recorded process rather than the recorded dispatch, which is only the first half. Waiting
    /// on a condition rather than on a delay keeps the test off a guessed duration. Origin
    /// incident (2026-08-22): waiting on the dispatch alone left the link colliding with the
    /// process append, and on a loaded ubuntu CI runner the link lost, so the scripted session
    /// died before writing its result and the sweep sat to its twenty-second ceiling and reported
    /// a publication with no link.
    /// </para>
    /// </summary>
    private sealed class ScriptedSession(
        string? summary,
        FakeProcessManager processes,
        DocumentStore? store = null,
        Guid taskId = default,
        Func<Task>? beforeFinishing = null) : IExecutor
    {
        private int _nextPid = 9000;

        public List<AgentSpawnRequest> Spawns { get; } = [];

        /// <summary>
        /// What <paramref name="beforeFinishing"/> threw, if it threw. The work is detached, so
        /// nothing else would see it — and a scripted link that failed silently is a sweep that
        /// reports no link for a reason the assertion cannot name.
        /// </summary>
        public Exception? Failure { get; private set; }

        public Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
        {
            Spawns.Add(request);
            int pid = _nextPid++;
            Directory.CreateDirectory(RunPaths.RunDirectory(request.RunId));

            if (summary is null)
            {
                // Dead on arrival with nothing written: the died-without-a-result path.
                return Task.FromResult(new SpawnedAgent(pid, Now));
            }

            processes.MarkAlive(pid);
            _ = Task.Run(async () =>
            {
                try
                {
                    if (beforeFinishing is { } work)
                    {
                        await WaitForRecordedLaunchAsync(cancellationToken);
                        await work();
                    }
                }
                catch (Exception exception)
                {
                    Failure = exception;
                }

                // Written whatever became of the work above, because that is what a real session
                // does: one whose h9k task link-jira failed still ends and still says what it did.
                // Swallowing the result here would leave the engine waiting on a session that is
                // over, and the assertion reading a timeout instead of the failure that caused it.
                string line = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["type"] = "result",
                    ["subtype"] = "success",
                    ["is_error"] = false,
                    ["usage"] = new Dictionary<string, long> { ["input_tokens"] = 10, ["output_tokens"] = 20 },
                    ["result"] = summary,
                });
                await File.WriteAllTextAsync(RunPaths.StreamFile(request.RunId), line + "\n", cancellationToken);
            }, cancellationToken);

            return Task.FromResult(new SpawnedAgent(pid, Now));
        }

        /// <summary>
        /// Waits for the recorded process, which is the second and last append the daemon makes
        /// before it settles into watching the session — so past it, the scripted work is the
        /// task's only writer.
        /// </summary>
        private async Task WaitForRecordedLaunchAsync(CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < 100 && store is not null; attempt++)
            {
                await using IQuerySession query = store.QuerySession();
                if ((await query.LoadAsync<TaskDetails>(taskId, cancellationToken))?
                    .PublicationSessionProcessId is not null)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
        }
    }

    [Fact]
    public async Task A_request_becomes_one_session_in_the_projects_own_repository()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NewNodeAsync(store, cts.Token);
        Guid taskId = await SeedAsync(store, node, cts.Token);

        FakeProcessManager processes = new();
        ScriptedSession session = new(
            "Created PROJ-123.", processes, store, taskId, () => LinkAsync(store, taskId, cts.Token));

        CardPublicationSweepResult sweep = await NewEngine(store, node, session, processes)
            .PollOnceAsync(cts.Token);

        session.Failure.Should().BeNull("the scripted h9k task link-jira is what the sweep below is read against");
        sweep.Should().Be(new CardPublicationSweepResult(1, 1));
        AgentSpawnRequest spawn = session.Spawns.Should().ContainSingle().Subject;
        spawn.WorktreePath.Should().Be(_repository, "the card rules live in the project's own repository");
        spawn.Prompt.Should().Contain("Publish me").And.Contain($"h9k task link-jira {taskId}");

        await using IQuerySession query = store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.ExternalReference.Should().Be("jira:PROJ-123");
        task.PendingPublicationProvider.Should().BeNull("the errand is over");
        task.PublicationOutcome.Should().Contain("Created PROJ-123.");
    }

    /// <summary>
    /// The observation gate, seen from the other side. Nothing here reads what the agent claimed;
    /// the outcome is decided by whether the task came out carrying a reference, which only the
    /// verifying command can have set.
    /// </summary>
    [Fact]
    public async Task A_session_that_reports_success_without_verifying_publishes_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NewNodeAsync(store, cts.Token);
        Guid taskId = await SeedAsync(store, node, cts.Token);

        FakeProcessManager processes = new();
        ScriptedSession session = new("All done! I created PROJ-999 for you.", processes);

        CardPublicationSweepResult sweep = await NewEngine(store, node, session, processes)
            .PollOnceAsync(cts.Token);

        sweep.Linked.Should().Be(0);
        await using IQuerySession query = store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.ExternalReference.Should().BeNull("a claim is not a link");
        task.PublicationOutcome.Should().Contain("without a verified card key")
            .And.Contain("I created PROJ-999", "what it said is kept, as its words rather than as the record");
    }

    [Fact]
    public async Task A_session_that_dies_without_a_result_says_where_to_look()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NewNodeAsync(store, cts.Token);
        Guid taskId = await SeedAsync(store, node, cts.Token);

        FakeProcessManager processes = new();
        ScriptedSession session = new(null, processes);

        await NewEngine(store, node, session, processes).PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.PublicationOutcome.Should().Contain("left no result to read")
            .And.Contain(RunPaths.Root, "the transcript is somewhere, and the record says where");
        task.PendingPublicationProvider.Should().BeNull();
    }

    /// <summary>
    /// The failure that costs a human an afternoon is two cards for one task, so a request whose
    /// session has already been dispatched is never picked up again — even by a sweep that runs
    /// while the first session is still going.
    /// </summary>
    [Fact]
    public async Task A_request_whose_session_already_ran_is_not_dispatched_twice()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NewNodeAsync(store, cts.Token);
        Guid taskId = await SeedAsync(store, node, cts.Token);

        FakeProcessManager processes = new();
        ScriptedSession first = new("All done! I created PROJ-999 for you.", processes);
        await NewEngine(store, node, first, processes).PollOnceAsync(cts.Token);

        // The first sweep completed the request; a second sweep has nothing to do, and would not
        // pick it up even if the completion had not landed.
        ScriptedSession second = new("A second card nobody asked for.", processes);
        CardPublicationSweepResult sweep = await NewEngine(store, node, second, processes)
            .PollOnceAsync(cts.Token);

        sweep.Dispatched.Should().Be(0);
        second.Spawns.Should().BeEmpty();
        _ = taskId;
    }

    [Fact]
    public async Task A_request_made_by_another_owner_is_not_this_nodes_to_run()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NewNodeAsync(store, cts.Token);
        await SeedAsync(store, node, cts.Token, requestedBy: DomainId.New());

        FakeProcessManager processes = new();
        ScriptedSession session = new("Created PROJ-123.", processes);

        CardPublicationSweepResult sweep = await NewEngine(store, node, session, processes)
            .PollOnceAsync(cts.Token);

        sweep.Dispatched.Should().Be(0);
        session.Spawns.Should().BeEmpty();
    }

    [Fact]
    public async Task A_project_whose_repository_is_gone_is_reported_rather_than_dispatched_into()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NewNodeAsync(store, cts.Token);
        Guid taskId = await SeedAsync(store, node, cts.Token, repositoryPath: "/no/such/repository");

        FakeProcessManager processes = new();
        ScriptedSession session = new("Created PROJ-123.", processes);

        await NewEngine(store, node, session, processes).PollOnceAsync(cts.Token);

        session.Spawns.Should().BeEmpty();
        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!.PublicationOutcome
            .Should().Contain("/no/such/repository");
    }

    /// <summary>
    /// The daemon stopping mid-publication used to strand the task for good: the dispatch is on
    /// the stream and the completion never lands, and nothing else clears that marker — the sweep
    /// skips a request whose session already ran, push-to-jira refuses while one is outstanding,
    /// and link-jira needs a card key that may not exist. Origin incident (2026-08-21): the pre-PR
    /// review of this branch, which traced it from h9k daemon stop inside the timeout window.
    /// </summary>
    [Fact]
    public async Task A_session_the_daemon_never_saw_finish_is_adopted_rather_than_left_outstanding()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NewNodeAsync(store, cts.Token);
        Guid taskId = await SeedAsync(store, node, cts.Token);
        await DispatchedAsync(store, node, taskId, processId: 4242, cts.Token);

        // Nothing is marked alive: the session died with the daemon that spawned it.
        FakeProcessManager processes = new();
        ScriptedSession session = new("Created PROJ-123.", processes);

        CardPublicationSweepResult sweep = await NewEngine(store, node, session, processes)
            .PollOnceAsync(cts.Token);

        sweep.Adopted.Should().Be(1);
        sweep.Dispatched.Should().Be(0);
        session.Spawns.Should().BeEmpty("adoption finishes the session that ran; it never starts a second one");

        await using IQuerySession query = store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.PendingPublicationProvider.Should().BeNull("the task is no longer waiting on anything");
        task.PublicationOutcome.Should().Contain("daemon stopped while this session was running")
            .And.Contain("Check the board", "whether a card exists is the one thing nobody here observed");
    }

    /// <summary>
    /// And the way back is open again: the refusal that protects against two cards is exactly what
    /// made the stranded state permanent, so adoption has to leave the task able to be published.
    /// </summary>
    [Fact]
    public async Task An_adopted_task_can_be_published_again()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NewNodeAsync(store, cts.Token);
        Guid taskId = await SeedAsync(store, node, cts.Token);
        await DispatchedAsync(store, node, taskId, processId: 4242, cts.Token);

        FakeProcessManager processes = new();
        await NewEngine(store, node, new ScriptedSession(null, processes), processes).PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskAggregate task = (await query.Events
            .AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        Action request = () => TaskDecider.RequestWorkItemPublication(
            task, WorkItemProvider.Jira, JiraProjectKey.Parse("PROJ"), Now, node.OwnerId);

        request.Should().NotThrow("a publication nobody watched end must not block the next attempt");
    }

    /// <summary>
    /// A restarted daemon is not evidence that the session it spawned died. That session is
    /// detached, so it can outlive the daemon, and killing it would throw away a card it may be
    /// halfway through creating — it is waited on instead.
    /// </summary>
    [Fact]
    public async Task An_adopted_session_that_is_still_running_is_waited_on_rather_than_assumed_dead()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NewNodeAsync(store, cts.Token);
        Guid taskId = await SeedAsync(store, node, cts.Token);

        Guid sessionId = await DispatchedAsync(store, node, taskId, processId: 4242, cts.Token);
        FakeProcessManager processes = new();
        processes.MarkAlive(4242);
        await WriteResultAsync(sessionId, "I filed the card but never reported it back.", cts.Token);

        FakeProcessManager spawnProcesses = new();
        ScriptedSession session = new("Created PROJ-123.", spawnProcesses);
        CardPublicationSweepResult sweep = await NewEngine(store, node, session, processes)
            .PollOnceAsync(cts.Token);

        sweep.Adopted.Should().Be(1);
        processes.Terminations.Should().NotContain(
            termination => termination.ProcessId == 4242,
            "a live session is picked back up, not killed");

        await using IQuerySession query = store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.PublicationOutcome.Should().Contain("picked it back up")
            .And.Contain("I filed the card but never reported it back.");
        task.ExternalReference.Should().BeNull("nothing came back through the gate, so nothing is linked");
    }

    /// <summary>
    /// The loop's first sweep runs immediately rather than after a full interval, which is the
    /// whole reason a request made while the daemon was down is picked up the moment it comes back.
    /// That only works if the node has an identity by then: every query the sweep makes is scoped
    /// to this node or its owner, and node bootstrap happens in the dispatch loop, whose own first
    /// await lets the host start this one. Origin incident (2026-08-21): the pre-PR review of this
    /// branch found every daemon start logging "Card publication sweep failed" with "NodeContext
    /// not initialized yet", which pushed the first real sweep out by a full poll interval — the
    /// exact delay the immediate first sweep exists to avoid.
    /// </summary>
    [Fact]
    public async Task The_loop_waits_for_this_node_to_have_an_identity_before_its_first_sweep()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext bootstrapped = await NewNodeAsync(store, cts.Token);
        await SeedAsync(store, bootstrapped, cts.Token);

        // The loop gets a node nothing has initialized, the way the host hands it one.
        NodeContext node = new();
        FakeProcessManager processes = new();
        ScriptedSession session = new("Created PROJ-123.", processes);
        CardPublicationLoop loop = new(
            NewEngine(store, node, session, processes),
            node,
            new DaemonConnection(postgres.ConnectionString),
            Options.Create(new DaemonOptions()),
            NullLogger<CardPublicationLoop>.Instance);

        await loop.StartAsync(cts.Token);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cts.Token);
            session.Spawns.Should().BeEmpty(
                "a sweep before bootstrap would throw on NodeContext rather than dispatch anything");

            await node.InitializeAsync(store, cts.Token);

            for (int attempt = 0; attempt < 100 && session.Spawns.Count == 0; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cts.Token);
            }

            session.Spawns.Should().ContainSingle(
                "the sweep runs as soon as the node knows who it is, not a poll interval later");
        }
        finally
        {
            await loop.StopAsync(CancellationToken.None);
        }
    }
    /// <summary>What the daemon appends when it spawns the session, replayed here without one.</summary>
    private static async Task<Guid> DispatchedAsync(
        DocumentStore store, NodeContext node, Guid taskId, int processId, CancellationToken cancellationToken)
    {
        Guid sessionId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(taskId, new WorkItemPublicationDispatched(
            taskId, sessionId, node.NodeId, processId, Now, Now, AgentModel.Unknown));
        await session.SaveChangesAsync(cancellationToken);
        return sessionId;
    }

    /// <summary>The terminal result line a session leaves behind in its own stream file.</summary>
    private static async Task WriteResultAsync(Guid sessionId, string summary, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(RunPaths.RunDirectory(sessionId));
        string line = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "result",
            ["subtype"] = "success",
            ["is_error"] = false,
            ["usage"] = new Dictionary<string, long> { ["input_tokens"] = 10, ["output_tokens"] = 20 },
            ["result"] = summary,
        });
        await File.WriteAllTextAsync(RunPaths.StreamFile(sessionId), line + "\n", cancellationToken);
    }

    /// <summary>What h9k task link-jira appends once it has read the card back from Jira.</summary>
    private static async Task LinkAsync(DocumentStore store, Guid taskId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        TaskAggregate task = (await session.Events
            .AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken))!;
        session.Events.Append(taskId, TaskDecider.LinkWorkItem(
            task,
            new ExternalReference(WorkItemProvider.Jira, "PROJ-123"),
            "Publish me",
            "To Do (open)",
            Now,
            Now,
            task.AddedByOwnerId));
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task<Guid> SeedAsync(
        DocumentStore store,
        NodeContext node,
        CancellationToken cancellationToken,
        Guid? requestedBy = null,
        string? repositoryPath = null)
    {
        Directory.CreateDirectory(_repository);
        Environment.SetEnvironmentVariable(TokenVariable, "a-token");

        await using IDocumentSession session = store.LightweightSession();
        Guid ownerId = node.OwnerId;
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();

        if (await WorkItemConnections.FindJiraConnectionAsync(session, cancellationToken) is null)
        {
            Guid connectionId = DomainId.New();
            session.Events.StartStream<ConnectionAggregate>(connectionId, ConnectionDecider.Register(
                connectionId, ownerId, WorkItemProvider.Jira, "brian@example.com",
                CredentialReference.EnvironmentVariable(TokenVariable), Now,
                new Uri("https://hall9k.atlassian.net")));
        }

        ProjectRegisteredSeed(session, projectId, ownerId, repositoryPath ?? _repository);

        TaskAdded added = TaskDecider.Add(
            taskId, projectId, "Publish me", ["A criterion"], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null, Now, ownerId);
        TaskAggregate task = new();
        task.Apply(added);
        WorkItemPublicationRequested requested = TaskDecider.RequestWorkItemPublication(
            task, WorkItemProvider.Jira, JiraProjectKey.Parse("PROJ"), Now, requestedBy ?? ownerId);
        session.Events.StartStream<TaskAggregate>(taskId, added, requested);

        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    private static void ProjectRegisteredSeed(
        IDocumentSession session, Guid projectId, Guid ownerId, string repositoryPath)
    {
        Hall9k.Domain.Features.Project.Events.ProjectRegistered registered = ProjectDecider.Register(
            projectId, ownerId, DomainId.New(), $"publication-{projectId:N}", repositoryPath, null, "main", Now);
        session.Events.StartStream<ProjectAggregate>(projectId, registered);
    }

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    private static async Task<NodeContext> NewNodeAsync(DocumentStore store, CancellationToken cancellationToken)
    {
        NodeContext node = new();
        await node.InitializeAsync(store, cancellationToken);
        return node;
    }

    private static CardPublicationEngine NewEngine(
        DocumentStore store, NodeContext node, IExecutor executor, FakeProcessManager processes) =>
        new(store, node, executor, processes,
            Options.Create(new DaemonOptions { CardPublicationTimeout = TimeSpan.FromSeconds(20) }),
            NullLogger<CardPublicationEngine>.Instance);
}
