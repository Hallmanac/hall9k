using System.Text.Json;
using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Daemon.Publication;
using Hall9k.Daemon.Worktrees;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Node;
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
using JasperFx.Events;
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
            Directory.CreateDirectory(request.RunDirectory);

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
                await File.WriteAllTextAsync(RunPaths.StreamFile(request.RunDirectory), line + "\n", cancellationToken);
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

        sweep.Should().Be(
            new CardPublicationSweepResult(Dispatched: 1, Linked: 0),
            "a session ran; what it did not do is produce a card anybody verified");
        await using IQuerySession query = store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.ExternalReference.Should().BeNull("a claim is not a link");
        task.PublicationOutcome.Should().Contain("without a verified card key")
            .And.Contain("I created PROJ-999", "what it said is kept, as its words rather than as the record")
            .And.Contain("Check the board", "PROJ-999 may well exist; what nobody has is proof of it");
    }

    /// <summary>
    /// Completing clears the pending marker, which is what makes the task publishable again — so
    /// an outcome that reports no link has to say that no card was <em>seen</em> rather than that
    /// none exists. A session that died mid-flight may have filed one first, and an operator who
    /// reads "no card" and runs push-to-jira again gets the duplicate. Origin incident
    /// (2026-08-21): the pre-PR review of this branch found the caution on the adoption and
    /// shutdown paths and missing from the ordinary ones.
    /// </summary>
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
            .And.Contain(RunPaths.Root, "the transcript is somewhere, and the record says where")
            .And.Contain("Check the board", "it may have filed a card before it died");
        task.PendingPublicationProvider.Should().BeNull();
    }

    /// <summary>
    /// The seam failing underneath a session that is still running: what a transient IOException
    /// out of the tail read, or a dropped connection out of the linked check, looks like from the
    /// engine. Kills are passed through to the real fake, because whether the session was stopped
    /// is the assertion.
    /// </summary>
    private sealed class UnwatchableProcesses(FakeProcessManager processes) : IProcessManager
    {
        public SpawnedProcess Spawn(ProcessSpawnRequest request) => processes.Spawn(request);

        public bool IsAlive(int processId, DateTimeOffset startedAt) =>
            throw new IOException("the session's stream could not be read");

        public void Terminate(int processId, DateTimeOffset startedAt) => processes.Terminate(processId, startedAt);
    }

    /// <summary>
    /// Losing track of a running session is not the same fact as never having started one, and
    /// only the first is what happened here. The session is stopped for the reason the timeout
    /// path stops one — nobody is watching it and nothing will record what it does next — and the
    /// outcome says a card may exist, because completing makes the task publishable again and an
    /// operator told the session could not be run would publish a second time. Origin incident
    /// (2026-08-21): the second cycle of this branch's pre-PR review found the sweep's catch-all
    /// recording "the daemon could not run the publication session", with no kill and no caution,
    /// while the agent it had spawned was still working.
    /// </summary>
    [Fact]
    public async Task A_session_the_daemon_loses_track_of_is_stopped_rather_than_reported_as_never_run()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NewNodeAsync(store, cts.Token);
        Guid taskId = await SeedAsync(store, node, cts.Token);

        FakeProcessManager processes = new();
        ScriptedSession session = new(null, processes);

        CardPublicationSweepResult sweep = await NewEngine(store, node, session, new UnwatchableProcesses(processes))
            .PollOnceAsync(cts.Token);

        sweep.Dispatched.Should().Be(1, "a session was spawned, whatever became of watching it");
        sweep.Linked.Should().Be(0);
        session.Spawns.Should().ContainSingle();
        processes.Terminations.Should().ContainSingle(
            "a session nobody is watching any more is stopped, not left detached with a card to file");

        await using IQuerySession query = store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.PublicationOutcome.Should().Contain("lost track of it")
            .And.NotContain("could not run the publication session", "it ran; what failed was watching it")
            .And.Contain(RunPaths.Root, "the transcript is somewhere, and the record says where")
            .And.Contain("Check the board", "it was mid-flight, so it may have filed a card first");
        task.PendingPublicationProvider.Should().BeNull("the task is publishable again, which is why the caution is there");
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

    /// <summary>
    /// A project that owns a home records its repository as the bare clone inside <c>repo/</c>,
    /// which has refs and objects and not one file of the project's code. The session is here to
    /// read the project's card-authoring skills, so it runs in <c>repo/dev</c>, the worktree the
    /// home keeps on the primary branch for exactly that. Origin incident (2026-08-23): the
    /// pre-PR review of the project-home branch found the session spawned inside the bare clone,
    /// where the prompt's "read them from the repository you are in" cannot be followed.
    /// </summary>
    [Fact]
    public async Task A_project_with_a_home_runs_its_session_in_the_dev_worktree_not_the_bare_clone()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NewNodeAsync(store, cts.Token);

        string home = Path.Combine(_repository, "home");
        string bare = ProjectHomePaths.BareRepository(home, "publication");
        Directory.CreateDirectory(Path.Combine(bare, "objects"));
        Directory.CreateDirectory(Path.Combine(bare, "refs"));
        Directory.CreateDirectory(Path.Combine(ProjectHomePaths.DevWorktree(home), ".git"));

        Guid taskId = await SeedAsync(
            store, node, cts.Token, repositoryPath: bare, homeDirectory: ProjectHome.Parse(home));

        FakeProcessManager processes = new();
        ScriptedSession session = new(
            "Created PROJ-123.", processes, store, taskId, () => LinkAsync(store, taskId, cts.Token));

        StubWorktreeManager worktrees = new();
        await NewEngine(store, node, session, processes, worktrees: worktrees).PollOnceAsync(cts.Token);

        session.Spawns.Should().ContainSingle().Subject.WorktreePath
            .Should().Be(ProjectHomePaths.DevWorktree(home));

        // repo/dev is cut once by h9k project init and otherwise never touched, so a session
        // spawned there to read this project's card rules reads them as of whenever the worktree
        // was made unless something brings it forward first.
        worktrees.Refreshed.Should().Equal([ProjectHomePaths.DevWorktree(home)]);
    }

    /// <summary>
    /// The other half of that rule. A project registered before homes existed reads from an
    /// ordinary clone that belongs to whoever made it, and moving somebody's working directory
    /// under them is not housekeeping the platform gets to do on its own account.
    /// </summary>
    [Fact]
    public async Task A_project_with_no_home_has_its_own_checkout_left_where_it_stands()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NewNodeAsync(store, cts.Token);

        Guid taskId = await SeedAsync(store, node, cts.Token);

        FakeProcessManager processes = new();
        ScriptedSession session = new(
            "Created PROJ-123.", processes, store, taskId, () => LinkAsync(store, taskId, cts.Token));

        StubWorktreeManager worktrees = new();
        await NewEngine(store, node, session, processes, worktrees: worktrees).PollOnceAsync(cts.Token);

        session.Spawns.Should().ContainSingle().Subject.WorktreePath.Should().Be(_repository);
        worktrees.Refreshed.Should().BeEmpty("that clone is somebody's, not the home's own dev/");
    }

    /// <summary>
    /// The same project before <c>h9k project init</c> has cut the worktree: the bare clone is
    /// there and exists, so an existence check alone passes it. There is still nothing to read.
    /// </summary>
    [Fact]
    public async Task A_bare_clone_with_no_worktree_is_refused_rather_than_dispatched_into()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NewNodeAsync(store, cts.Token);

        string home = Path.Combine(_repository, "home");
        string bare = ProjectHomePaths.BareRepository(home, "publication");
        Directory.CreateDirectory(Path.Combine(bare, "objects"));
        Directory.CreateDirectory(Path.Combine(bare, "refs"));

        Guid taskId = await SeedAsync(
            store, node, cts.Token, repositoryPath: bare, homeDirectory: ProjectHome.Parse(home));

        FakeProcessManager processes = new();
        ScriptedSession session = new("Created PROJ-123.", processes);

        CardPublicationSweepResult sweep = await NewEngine(store, node, session, processes)
            .PollOnceAsync(cts.Token);

        sweep.Should().Be(new CardPublicationSweepResult(Dispatched: 0, Linked: 0, Adopted: 0, Refused: 1));
        session.Spawns.Should().BeEmpty();
        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!.PublicationOutcome
            .Should().Contain("h9k project init", "the refusal names the command that makes a checkout");
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

        CardPublicationSweepResult sweep = await NewEngine(store, node, session, processes)
            .PollOnceAsync(cts.Token);

        sweep.Should().Be(
            new CardPublicationSweepResult(Dispatched: 0, Linked: 0, Adopted: 0, Refused: 1),
            "there was no repository to dispatch into, so no session ran");
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
            .And.Contain(
                "Check the board",
                Exactly.Once(),
                "whether a card exists is the one thing nobody here observed, said once");
    }

    /// <summary>
    /// The one stranding adoption cannot cover: a dispatch recorded against a node that never
    /// comes back. Adoption is scoped to the node that spawned the session because a pid means
    /// nothing off the machine that issued it, so a node identity that stops existing leaves the
    /// task reading "a session is writing the card" with nothing able to clear it — the dispatch
    /// sweep skips it, push-to-jira refuses while it is outstanding, link-jira needs a card key
    /// that may not exist, and abandoning keeps the marker on purpose. Origin incident
    /// (2026-08-22): the pre-PR review of this branch traced it from a machine rename, which gives
    /// the same install a new node identity through NodeBootstrap's machine-name lookup.
    /// </summary>
    [Fact]
    public async Task A_dispatch_belonging_to_a_node_that_never_came_back_is_ended_on_the_ceiling()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NewNodeAsync(store, cts.Token);
        Guid taskId = await SeedAsync(store, node, cts.Token);
        Guid gone = await ForeignNodeAsync(store, node, "the-old-machine-name", cts.Token);
        await DispatchedAsync(
            store, node, taskId, processId: 4242, cts.Token,
            nodeId: gone, dispatchedAt: DateTimeOffset.UtcNow - TimeSpan.FromHours(2));

        FakeProcessManager processes = new();
        ScriptedSession session = new("Created PROJ-123.", processes);

        CardPublicationSweepResult sweep = await NewEngine(store, node, session, processes)
            .PollOnceAsync(cts.Token);

        sweep.Expired.Should().Be(1, "it is counted apart from adoption: nobody watched that session, only a clock");
        session.Spawns.Should().BeEmpty("the request is ended, not retried into a second card");
        processes.Terminations.Should().NotContain(
            termination => termination.ProcessId == 4242,
            "the pid on the task belongs to another machine, and judging it from here is the rule this "
            + "engine does not break");

        await using IQuerySession query = store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.PendingPublicationProvider.Should().BeNull("the way out is the point");
        task.PublicationOutcome.Should().Contain("the-old-machine-name", "the machine it belonged to is nameable")
            .And.Contain("Only the node that spawned a session can judge it")
            .And.Contain("Check the board", Exactly.Once(),
                "no card was seen created here, which is not the same as no card");

        TaskAggregate aggregate = (await query.Events
            .AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        Action request = () => TaskDecider.RequestWorkItemPublication(
            aggregate, WorkItemProvider.Jira, JiraProjectKey.Parse("PROJ"), Now, node.OwnerId);
        request.Should().NotThrow("a request ended on the ceiling must leave the task publishable again");
    }

    /// <summary>
    /// And the ceiling is what keeps that from cutting a live session short. Another node running
    /// a publication right now looks exactly the same from here — dispatched, no outcome, a pid
    /// this machine cannot ask about — so the only thing separating the two is how long it has
    /// stood, and inside the ceiling the answer is to leave it alone.
    /// </summary>
    [Fact]
    public async Task A_publication_another_node_is_still_running_is_left_alone_until_the_ceiling()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NewNodeAsync(store, cts.Token);
        Guid taskId = await SeedAsync(store, node, cts.Token);
        Guid other = await ForeignNodeAsync(store, node, "the-other-machine", cts.Token);
        await DispatchedAsync(
            store, node, taskId, processId: 4242, cts.Token,
            nodeId: other, dispatchedAt: DateTimeOffset.UtcNow);

        FakeProcessManager processes = new();
        ScriptedSession session = new("Created PROJ-123.", processes);

        CardPublicationSweepResult sweep = await NewEngine(store, node, session, processes)
            .PollOnceAsync(cts.Token);

        sweep.Expired.Should().Be(0, "inside the ceiling the other node is still the one to finish it");
        session.Spawns.Should().BeEmpty("a second session would be the second card this engine exists to avoid");

        await using IQuerySession query = store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.PendingPublicationProvider.Should().Be(WorkItemProvider.Jira.Value,
            "the publication is still somebody's to finish");
        task.PublicationOutcome.Should().BeNull("nothing has come of it yet, and saying otherwise would be a guess");
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
            .And.Contain("I filed the card but never reported it back.")
            .And.Contain(
                "Check the board",
                Exactly.Once(),
                "the adopted outcome carries the caution the session's own outcome already ended with, "
                + "and carrying it twice reads as a defect in the record");
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

    /// <summary>
    /// The window that two cards come out of, closed. A session spawned before its dispatch is on
    /// the stream is a live card-writer nothing has a record of, so a lost commit or a kill -9 in
    /// that window leaves the next sweep free to start a second one against the same request.
    /// Origin incident (2026-08-21): the pre-PR review of this branch traced both paths.
    /// </summary>
    [Fact]
    public async Task The_dispatch_is_on_the_stream_before_anything_is_spawned()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NewNodeAsync(store, cts.Token);
        Guid taskId = await SeedAsync(store, node, cts.Token);

        FakeProcessManager processes = new();
        ObservingSession session = new(store, taskId);

        await NewEngine(store, node, session, processes).PollOnceAsync(cts.Token);

        session.DispatchedWhenSpawned.Should().BeTrue(
            "the guard that refuses a second session has to be up before one exists that could file a card");

        // And the process is recorded once there is one — read off the stream rather than the
        // projection, which drops the session's identity the moment the errand ends.
        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<IEvent> stream = await query.Events.FetchStreamAsync(taskId, token: cts.Token);
        stream.Select(recorded => recorded.Data).OfType<WorkItemPublicationSessionStarted>()
            .Should().ContainSingle().Which.ProcessId.Should().Be(9100);
    }

    /// <summary>
    /// A session spawned as the daemon is told to stop, which is the one window where losing the
    /// session is silent. The stop is fired the moment the agent is live and before the daemon has
    /// recorded which process it is, so the append that would name it is cancelled — and the
    /// recording of a spawned process is deliberately best-effort, so a cancelled save comes back
    /// out. Left alone the agent outlives the daemon, and the restart reads a dispatch with no
    /// process beside it, which by contract terminates nothing and completes the publication, so
    /// the task is publishable again while a detached session is still writing its card. Origin
    /// incident (2026-08-22): the pre-PR review of this branch traced it from h9k daemon stop
    /// inside that window.
    /// </summary>
    private sealed class SessionSpawnedAsTheDaemonStops(
        FakeProcessManager processes, CancellationTokenSource stopping) : IExecutor
    {
        public int ProcessId => 9200;

        public Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(request.RunDirectory);
            processes.MarkAlive(ProcessId);
            stopping.Cancel();
            return Task.FromResult(new SpawnedAgent(ProcessId, Now));
        }
    }

    /// <summary>
    /// And what that window has to end as: the session stopped with the daemon and the outcome
    /// recorded on a token of its own, which is the same answer the shutdown path gives a session
    /// it was already watching.
    /// </summary>
    [Fact]
    public async Task A_session_spawned_as_the_daemon_stops_is_stopped_with_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NewNodeAsync(store, cts.Token);
        Guid taskId = await SeedAsync(store, node, cts.Token);

        FakeProcessManager processes = new();
        using CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        SessionSpawnedAsTheDaemonStops session = new(processes, stopping);

        Func<Task> sweep = () => NewEngine(store, node, session, processes).PollOnceAsync(stopping.Token);

        await sweep.Should().ThrowAsync<OperationCanceledException>(
            "the daemon is stopping, and a sweep that swallowed that would be asked for another one");

        processes.Terminations.Should().Contain(
            termination => termination.ProcessId == session.ProcessId,
            "nothing is left watching it, and a detached session still writing a card is how a "
            + "surprise card arrives on a board");

        await using IQuerySession query = store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.PublicationOutcome.Should().Contain("The daemon stopped while this session was writing the card")
            .And.Contain("Check the board", "it was stopped without a verified key, and may have filed one");
        task.PendingPublicationProvider.Should().BeNull("the request is over, however it ended");
    }

    /// <summary>
    /// The other side of that split: a daemon that died between committing the dispatch and
    /// recording the process it spawned. Nobody can now say whether a session ever ran, so the
    /// outcome says that rather than picking an answer, and the task is left publishable again.
    /// </summary>
    [Fact]
    public async Task A_dispatch_with_no_process_recorded_beside_it_is_reported_as_unknown()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NewNodeAsync(store, cts.Token);
        Guid taskId = await SeedAsync(store, node, cts.Token);
        await DispatchedAsync(store, node, taskId, processId: null, cts.Token);

        FakeProcessManager processes = new();
        ScriptedSession session = new("Created PROJ-123.", processes);

        CardPublicationSweepResult sweep = await NewEngine(store, node, session, processes)
            .PollOnceAsync(cts.Token);

        sweep.Adopted.Should().Be(1);
        session.Spawns.Should().BeEmpty("a session may be running; starting a second is the failure to avoid");

        await using IQuerySession query = store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.PublicationOutcome.Should().Contain("nothing can say whether it ran")
            .And.Contain("Check the board", "no card was seen, which is not the same as no card");
        task.PendingPublicationProvider.Should().BeNull("the task is publishable again");
    }

    /// <summary>
    /// A publication ended after something went wrong still has to say what the task carries,
    /// rather than assume it carries nothing. The flag on
    /// <see cref="WorkItemPublicationCompleted"/> is read off the task's own state by contract,
    /// and the paths that end a publication on a failure are the ones where assuming is easiest
    /// and wrong: a session's own h9k task link-jira may already have landed. The state seeded
    /// here is the one the projection can hold — a request appended behind a link, then
    /// dispatched — because it is the one an outcome can be observed against without breaking
    /// the store underneath the engine. Origin incident (2026-08-22): the third cycle of this
    /// branch's pre-PR review found every one of those paths recording "no card produced", plus
    /// the caution to go hunting for an unrecorded one, whatever the task said.
    /// </summary>
    [Fact]
    public async Task An_outcome_recorded_after_a_failure_says_what_the_task_actually_carries()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NewNodeAsync(store, cts.Token);
        Guid taskId = await SeedAsync(store, node, cts.Token);
        await LinkAsync(store, taskId, cts.Token);
        await RequestedBehindTheLinkAsync(store, node, taskId, cts.Token);
        await DispatchedAsync(store, node, taskId, processId: null, cts.Token);

        FakeProcessManager processes = new();
        ScriptedSession session = new("Created PROJ-123.", processes);

        CardPublicationSweepResult sweep = await NewEngine(store, node, session, processes)
            .PollOnceAsync(cts.Token);

        sweep.Adopted.Should().Be(1);

        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<IEvent> stream = await query.Events.FetchStreamAsync(taskId, token: cts.Token);
        WorkItemPublicationCompleted completed = stream
            .Select(@event => @event.Data)
            .OfType<WorkItemPublicationCompleted>()
            .Last();
        completed.Linked.Should().BeTrue(
            "the task carries a verified key, and the flag reads the task rather than the failure");
        completed.Outcome.Should().Contain("verified card key")
            .And.NotContain("Check the board", "there is nothing to go looking for: the card is recorded");
    }

    /// <summary>
    /// The decider's other rule, enforced where the card would actually be written. A task that
    /// already carries a card gets no session, whatever the pending marker says: the sweep is the
    /// last gate before an agent is told to file one, and one task carries one external item.
    /// Origin incident (2026-08-21): the pre-PR review of this branch found h9k task push-to-jira
    /// appending its request unfenced, so a link landing between its read and its append left a
    /// task both linked and pending — the state this test seeds directly.
    /// </summary>
    [Fact]
    public async Task A_task_that_is_already_linked_gets_no_session_however_it_came_to_be_pending()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NewNodeAsync(store, cts.Token);
        Guid taskId = await SeedAsync(store, node, cts.Token);
        await LinkAsync(store, taskId, cts.Token);
        await RequestedBehindTheLinkAsync(store, node, taskId, cts.Token);

        FakeProcessManager processes = new();
        ScriptedSession session = new("A second card nobody asked for.", processes);

        CardPublicationSweepResult sweep = await NewEngine(store, node, session, processes)
            .PollOnceAsync(cts.Token);

        sweep.Should().Be(
            new CardPublicationSweepResult(Dispatched: 0, Linked: 0, Adopted: 0, Refused: 1),
            "the request was answered by a guard, and counting it as a session that ran would put an "
            + "agent in somebody's repository in the daemon's log that was never there");
        session.Spawns.Should().BeEmpty("a session dispatched here would file a second card for one task");

        await using IQuerySession query = store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.ExternalReference.Should().Be("jira:PROJ-123", "the card it already had is untouched");
        task.PendingPublicationProvider.Should().BeNull("the request is answered rather than left hanging");
        task.PublicationOutcome.Should().Contain("already linked to jira:PROJ-123")
            .And.Contain("second card", "the refusal says what it was protecting against");

        IReadOnlyList<IEvent> stream = await query.Events.FetchStreamAsync(taskId, token: cts.Token);
        stream.Select(@event => @event.Data).OfType<WorkItemPublicationCompleted>().Last()
            .Linked.Should().BeTrue(
                "the flag reads the task's own state by contract, and this is the one refusal that "
                + "read a verified key off the task to decide it was a refusal at all");
    }

    /// <summary>
    /// Abandoning is walking away from the work, and an errand nobody has started yet goes with
    /// it. The sweep reads the pending marker and nothing else, so a request that outlived the
    /// intent behind it would still become an agent session filing a real card — for work nobody
    /// means to do, on a task that could not then record it, because linking an abandoned task is
    /// refused too. Origin incident (2026-08-22): the pre-PR review of this branch traced it from
    /// h9k task push-to-jira with the daemon stopped, then h9k task abandon, then the daemon
    /// starting.
    /// </summary>
    [Fact]
    public async Task Abandoning_before_the_daemon_sweeps_takes_the_request_with_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NewNodeAsync(store, cts.Token);
        Guid taskId = await SeedAsync(store, node, cts.Token);
        await AbandonAsync(store, taskId, cts.Token);

        FakeProcessManager processes = new();
        ScriptedSession session = new("A card for work nobody is doing.", processes);

        CardPublicationSweepResult sweep = await NewEngine(store, node, session, processes)
            .PollOnceAsync(cts.Token);

        sweep.Should().Be(new CardPublicationSweepResult(0, 0));
        session.Spawns.Should().BeEmpty("the request died with the task it was about");

        await using IQuerySession query = store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.PendingPublicationProvider.Should().BeNull();
        task.ExternalReference.Should().BeNull("no card was ever asked for");
    }

    /// <summary>
    /// The same rule enforced where the consequence is, for a marker that reaches the sweep on an
    /// abandoned task anyway — the request appended behind the abandon, which is the shape a
    /// stream written before that rule existed has.
    /// </summary>
    [Fact]
    public async Task An_abandoned_task_gets_no_session_however_it_came_to_be_pending()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NewNodeAsync(store, cts.Token);
        Guid taskId = await SeedAsync(store, node, cts.Token);
        await AbandonAsync(store, taskId, cts.Token);
        await RequestedBehindTheLinkAsync(store, node, taskId, cts.Token);

        FakeProcessManager processes = new();
        ScriptedSession session = new("A card for work nobody is doing.", processes);

        CardPublicationSweepResult sweep = await NewEngine(store, node, session, processes)
            .PollOnceAsync(cts.Token);

        sweep.Should().Be(
            new CardPublicationSweepResult(Dispatched: 0, Linked: 0, Adopted: 0, Refused: 1),
            "a refusal is counted as a refusal; nothing ran");
        session.Spawns.Should().BeEmpty("a card filed here is one nobody can link and nobody wants");

        await using IQuerySession query = store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.ExternalReference.Should().BeNull();
        task.PendingPublicationProvider.Should().BeNull("the request is answered rather than left hanging");
        task.PublicationOutcome.Should().Contain("abandoned before the daemon picked the request up")
            .And.Contain("nobody here intends to do", "the refusal says what it was protecting against");
    }

    /// <summary>
    /// The card says what the task says at the moment the session is dispatched, not what it said
    /// when the sweep began. A sweep reads its pending requests once and then works through them one
    /// at a time, each publication blocking on an agent session for up to the publication timeout, so
    /// a later request can sit for many minutes before its turn — and a task is published from Draft,
    /// which is the one state h9k task revise edits. Origin incident (2026-08-22): the pre-PR review
    /// of this branch found the prompt built from the sweep's opening snapshot while every guard
    /// beside it re-read the aggregate, so a task revised during an earlier session would have had its
    /// card written from a contract it no longer carried, with nothing downstream to catch it:
    /// h9k task link-jira verifies that the card exists, never that it matches the task.
    /// </summary>
    [Fact]
    public async Task A_task_revised_while_an_earlier_publication_ran_is_written_up_as_it_now_stands()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NewNodeAsync(store, cts.Token);
        Guid first = await SeedAsync(store, node, cts.Token);
        Guid second = await SeedAsync(store, node, cts.Token, requestedAt: Now.AddMinutes(1));

        // The owner revises the second task while the first one's session is still running, which is
        // the window the sweep's serial processing opens.
        int revised = 0;
        FakeProcessManager processes = new();
        ScriptedSession session = new(
            "Created PROJ-123.", processes, store, first, async () =>
            {
                if (Interlocked.Exchange(ref revised, 1) == 0)
                {
                    await ReviseAsync(
                        store, second, "Rewrite the exporter", ["The rewritten criterion"], cts.Token);
                }
            });

        await NewEngine(store, node, session, processes).PollOnceAsync(cts.Token);

        session.Failure.Should().BeNull("the scripted revision is what the prompt below is read against");
        session.Spawns.Should().HaveCount(2, "both requests were the sweep's to run, oldest first");
        session.Spawns[1].Prompt.Should()
            .Contain("Rewrite the exporter", "the card is written from the objective the task carries now")
            .And.Contain("The rewritten criterion")
            .And.NotContain("Publish me", "the pre-revision contract is not what anybody asked for a card about")
            .And.NotContain("A criterion");
    }

    /// <summary>What h9k task revise appends: a draft's contract, rewritten in place.</summary>
    private static async Task ReviseAsync(
        DocumentStore store,
        Guid taskId,
        string objective,
        IReadOnlyList<string> criteria,
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        TaskAggregate task = (await session.Events
            .AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken))!;
        session.Events.Append(taskId, TaskDecider.Revise(
            task,
            Optional<string>.Of(objective),
            Optional<IReadOnlyList<string>>.Of(criteria),
            Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None,
            Optional<TaskType>.None,
            Optional<AgentModel>.None,
            Now,
            task.AddedByOwnerId));
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>What h9k task abandon appends: the walk-away ending, mid-publication or not.</summary>
    private static async Task AbandonAsync(DocumentStore store, Guid taskId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        TaskAggregate task = (await session.Events
            .AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken))!;
        session.Events.Append(taskId, TaskDecider.Abandon(task, "Superseded", Now, task.AddedByOwnerId));
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The request the unfenced command used to be able to append after a link had landed: built
    /// by hand because <see cref="TaskDecider.RequestWorkItemPublication"/> refuses to produce it,
    /// which is exactly the rule the daemon is being asked to enforce a second time.
    /// </summary>
    private static async Task RequestedBehindTheLinkAsync(
        DocumentStore store, NodeContext node, Guid taskId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(taskId, new WorkItemPublicationRequested(
            taskId, WorkItemProvider.Jira, JiraProjectKey.Parse("PROJ"), Now, node.OwnerId));
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the task at the moment of the spawn, which is the whole assertion.
    /// </summary>
    private sealed class ObservingSession(DocumentStore store, Guid taskId) : IExecutor
    {
        public bool DispatchedWhenSpawned { get; private set; }

        public async Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
        {
            await using (IQuerySession query = store.QuerySession())
            {
                TaskAggregate? task = await query.Events
                    .AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken);
                DispatchedWhenSpawned = task?.PublicationSessionDispatched is true;
            }

            await WriteResultAsync(request.RunId, "I filed a card.", cancellationToken);
            return new SpawnedAgent(9100, Now);
        }
    }

    /// <summary>
    /// What the daemon appends when it dispatches a session, replayed here without one. Two events
    /// because the daemon writes two: the dispatch is committed before the spawn, so that a crash
    /// in between cannot leave a live session with nothing on the stream to stop the next sweep
    /// starting a second one. Pass a null <paramref name="processId"/> to replay a daemon that
    /// died inside that window.
    /// </summary>
    private static async Task<Guid> DispatchedAsync(
        DocumentStore store,
        NodeContext node,
        Guid taskId,
        int? processId,
        CancellationToken cancellationToken,
        Guid? nodeId = null,
        DateTimeOffset? dispatchedAt = null)
    {
        Guid sessionId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(taskId, new WorkItemPublicationDispatched(
            taskId, sessionId, nodeId ?? node.NodeId, dispatchedAt ?? Now, AgentModel.Unknown));
        if (processId is { } pid)
        {
            session.Events.Append(taskId, new WorkItemPublicationSessionStarted(taskId, sessionId, pid, Now));
        }

        await session.SaveChangesAsync(cancellationToken);
        return sessionId;
    }

    /// <summary>The terminal result line a session leaves behind in its own stream file.</summary>
    private static async Task WriteResultAsync(Guid sessionId, string summary, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(RunPaths.GlobalDirectory(sessionId));
        string line = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "result",
            ["subtype"] = "success",
            ["is_error"] = false,
            ["usage"] = new Dictionary<string, long> { ["input_tokens"] = 10, ["output_tokens"] = 20 },
            ["result"] = summary,
        });
        await File.WriteAllTextAsync(RunPaths.StreamFile(RunPaths.GlobalDirectory(sessionId)), line + "\n", cancellationToken);
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
        string? repositoryPath = null,
        DateTimeOffset? requestedAt = null,
        ProjectHome? homeDirectory = null)
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

        ProjectRegisteredSeed(session, projectId, ownerId, repositoryPath ?? _repository, homeDirectory);

        TaskAdded added = TaskDecider.Add(
            taskId, projectId, "Publish me", ["A criterion"], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null, Now, ownerId);
        TaskAggregate task = new();
        task.Apply(added);
        WorkItemPublicationRequested requested = TaskDecider.RequestWorkItemPublication(
            task, WorkItemProvider.Jira, JiraProjectKey.Parse("PROJ"), requestedAt ?? Now, requestedBy ?? ownerId);
        session.Events.StartStream<TaskAggregate>(taskId, added, requested);

        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    private static void ProjectRegisteredSeed(
        IDocumentSession session, Guid projectId, Guid ownerId, string repositoryPath,
        ProjectHome? homeDirectory = null)
    {
        Hall9k.Domain.Features.Project.Events.ProjectRegistered registered = ProjectDecider.Register(
            projectId, ownerId, DomainId.New(), $"publication-{projectId:N}", repositoryPath, null, "main", Now,
            homeDirectory);
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
        DocumentStore store,
        NodeContext node,
        IExecutor executor,
        IProcessManager processes,
        TimeSpan? foreignCeiling = null,
        StubWorktreeManager? worktrees = null) =>
        new(store, node, executor, processes, worktrees ?? new StubWorktreeManager(),
            Options.Create(new DaemonOptions
            {
                CardPublicationTimeout = TimeSpan.FromSeconds(20),
                ForeignPublicationCeiling = foreignCeiling ?? TimeSpan.FromHours(1),
            }),
            NullLogger<CardPublicationEngine>.Instance);

    /// <summary>
    /// Records what the engine asked to be refreshed, and touches no git. The refresh itself is
    /// GitWorktreeManager's, proved against a real repository by RepoMaterialiserTests' sibling
    /// path; what matters here is which checkout the engine hands it, and that it hands it one.
    /// </summary>
    private sealed class StubWorktreeManager : IWorktreeManager
    {
        public List<string> Refreshed { get; } = [];

        public Task<CheckoutRefresh> RefreshReadingCheckoutAsync(
            string checkoutPath, string branch, CancellationToken cancellationToken)
        {
            Refreshed.Add(checkoutPath);
            return Task.FromResult(new CheckoutRefresh(UpToDate: true, $"already at origin/{branch}"));
        }

        public Task<Worktree> CreateAsync(WorktreeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A publication session works in an existing checkout.");

        public Task<Worktree> CheckoutExistingAsync(FollowUpWorktreeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A publication session works in an existing checkout.");

        public Task RemoveAsync(string repositoryPath, string worktreePath, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteBranchEverywhereAsync(string repositoryPath, string branch, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PruneAsync(string repositoryPath, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// A node that is not this one, registered so the record can name the machine it belonged to.
    /// A machine rename is the realistic way this arises: the same install comes back with a new
    /// node identity, and every publication the old identity dispatched is now foreign to it.
    /// </summary>
    private static async Task<Guid> ForeignNodeAsync(
        DocumentStore store, NodeContext node, string machineName, CancellationToken cancellationToken)
    {
        Guid nodeId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<NodeAggregate>(nodeId, NodeDecider.Register(
            nodeId, node.OwnerId, machineName, "macos", Now));
        await session.SaveChangesAsync(cancellationToken);
        return nodeId;
    }
}
