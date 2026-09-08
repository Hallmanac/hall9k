using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Dispatch;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Marten;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// Cross-project ordering as the dispatcher actually performs it (Decisions Log #141): the
/// acted-out semantics of the refinement ruling, sweep by sweep, against a real store. The rule
/// itself is unit-tested where it lives (<see cref="Hall9k.Tests.Domain.ProjectRotationTests"/>);
/// what these tests prove is that a whole dispatch cycle — measurement, claim, release, next
/// sweep — produces the split and the alternation the ruling describes, and says why in the log.
/// Its own class so it gets its own database, the reason
/// <see cref="ProjectRunCeilingDispatchTests"/> has one: every assertion here counts what a
/// project is carrying, and a sibling test's leftover lease would be counted as one of them.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class CrossProjectRotationDispatchTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The headline contract on a node of two: two projects with standing queues split the node
    /// one and one and stay that way, rather than the older project's whole wave running first.
    /// </summary>
    [Fact]
    public async Task Two_projects_with_ready_work_split_a_node_of_two_one_and_one_and_hold_that_split()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await FreshNodeAsync(store, cts.Token);
        ListLogger<DispatchEngine> logger = new();
        DispatchEngine engine = Engine(store, node, maxConcurrentRuns: 2, logger);

        Guid alpha = await SeedProjectAsync(store, node, "alpha", cts.Token);
        Guid beta = await SeedProjectAsync(store, node, "beta", cts.Token);

        // Alpha's whole wave is assigned before beta's single-file queue, so ordering blind to
        // the project would run alpha twice and leave beta behind the wave.
        Guid[] alphaTasks = await SeedQueuedAsync(store, node, alpha, count: 3, from: Now, cts.Token);
        Guid[] betaTasks = await SeedQueuedAsync(store, node, beta, count: 3, from: Now.AddMinutes(10), cts.Token);

        IReadOnlyList<ClaimedWork> first = await engine.ClaimEligibleAsync(cts.Token);

        first.Select(work => work.TaskId).Should().Equal([alphaTasks[0], betaTasks[0]],
            "one slot each, oldest task first within each project");

        (await engine.ClaimEligibleAsync(cts.Token)).Should().BeEmpty("the node is full and nothing preempts");

        // Alpha's run ends. Alpha was served first, so it is the longer-unserved of the two and
        // takes the freed slot — which keeps the split at one and one rather than tipping it.
        await ReleaseAsync(store, alphaTasks[0], cts.Token);
        (await engine.ClaimEligibleAsync(cts.Token)).Select(work => work.TaskId).Should().Equal(alphaTasks[1]);

        await ReleaseAsync(store, betaTasks[0], cts.Token);
        (await engine.ClaimEligibleAsync(cts.Token)).Select(work => work.TaskId).Should().Equal([betaTasks[1]],
            "beta is now the longest unserved, so the alternation continues");

        // With both projects served and both still holding work, the line states when the winner
        // was last dispatched for — so the decision reads back from the log alone.
        logger.Lines.Should().ContainSingle(line => line.Contains($"Free slot to project alpha (task {alphaTasks[1]})"))
            .Which.Should().Contain("longest unserved of 2 project(s)").And.Contain("last dispatched for at");
    }

    /// <summary>
    /// A project reaches beyond one slot only when no other eligible project has ready work —
    /// caps express entitlement-when-alone, never a share under contention.
    /// </summary>
    [Fact]
    public async Task A_project_takes_a_second_slot_only_once_the_other_has_nothing_ready()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await FreshNodeAsync(store, cts.Token);
        DispatchEngine engine = Engine(store, node, maxConcurrentRuns: 2);

        Guid alpha = await SeedProjectAsync(store, node, "alpha", cts.Token);
        Guid beta = await SeedProjectAsync(store, node, "beta", cts.Token);
        Guid[] alphaTasks = await SeedQueuedAsync(store, node, alpha, count: 3, from: Now, cts.Token);
        Guid[] betaTasks = await SeedQueuedAsync(store, node, beta, count: 1, from: Now.AddMinutes(10), cts.Token);

        (await engine.ClaimEligibleAsync(cts.Token)).Select(work => work.TaskId)
            .Should().Equal([alphaTasks[0], betaTasks[0]]);

        await ReleaseAsync(store, alphaTasks[0], cts.Token);
        await ReleaseAsync(store, betaTasks[0], cts.Token);

        (await engine.ClaimEligibleAsync(cts.Token)).Select(work => work.TaskId)
            .Should().Equal([alphaTasks[1], alphaTasks[2]],
                "with beta's queue empty, alpha fills both slots — nothing was ever held back for beta");
    }

    /// <summary>
    /// The ruling's own node-of-one walk-through: five tasks on alpha and one on beta run
    /// alpha, beta, alpha, alpha, alpha, alpha — the lone task cuts in at the first slot boundary
    /// rather than waiting behind the whole wave. The claim log is read here too, because the
    /// never-served and the last-dispatched-at wordings both occur in this one sequence.
    /// </summary>
    [Fact]
    public async Task A_node_of_one_alternates_task_by_task_so_a_lone_task_cuts_in_at_the_first_boundary()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await FreshNodeAsync(store, cts.Token);
        ListLogger<DispatchEngine> logger = new();
        DispatchEngine engine = Engine(store, node, maxConcurrentRuns: 1, logger);

        Guid alpha = await SeedProjectAsync(store, node, "alpha", cts.Token);
        Guid beta = await SeedProjectAsync(store, node, "beta", cts.Token);
        Guid[] wave = await SeedQueuedAsync(store, node, alpha, count: 5, from: Now, cts.Token);
        Guid[] lone = await SeedQueuedAsync(store, node, beta, count: 1, from: Now.AddMinutes(10), cts.Token);

        List<Guid> order = [];
        foreach (int _ in Enumerable.Range(0, 6))
        {
            IReadOnlyList<ClaimedWork> claimed = await engine.ClaimEligibleAsync(cts.Token);
            claimed.Should().HaveCount(1, "a node of one claims exactly one run per free slot");
            order.Add(claimed[0].TaskId);
            await ReleaseAsync(store, claimed[0].TaskId, cts.Token);
        }

        order.Should().Equal([wave[0], lone[0], wave[1], wave[2], wave[3], wave[4]]);

        logger.Lines.Should().ContainSingle(line => line.Contains($"Free slot to project beta (task {lone[0]})"))
            .Which.Should().Contain("longest unserved of 2 project(s)")
            .And.Contain("nothing has been dispatched for it since this daemon started",
                "a never-served project outranks a served one, and the line says so plainly");

        logger.Lines.Should().ContainSingle(line => line.Contains($"Free slot to project alpha (task {wave[1]})"))
            .Which.Should().Contain("the only project with ready work under every applicable limit",
                "beta's queue drained with its one task, so the rotation is back to a single member");
    }

    /// <summary>
    /// A single project on the node is indistinguishable from the behaviour before any of this:
    /// oldest first, no setting required, and the claim still explains itself.
    /// </summary>
    [Fact]
    public async Task One_project_on_the_node_is_oldest_first_with_no_setting_at_all()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await FreshNodeAsync(store, cts.Token);
        ListLogger<DispatchEngine> logger = new();
        DispatchEngine engine = Engine(store, node, maxConcurrentRuns: 2, logger);

        Guid only = await SeedProjectAsync(store, node, "solo", cts.Token);
        Guid[] queued = await SeedQueuedAsync(store, node, only, count: 3, from: Now, cts.Token);

        (await engine.ClaimEligibleAsync(cts.Token)).Select(work => work.TaskId)
            .Should().Equal([queued[0], queued[1]], "oldest assignment first, exactly as before");

        logger.Lines.Should().Contain(line =>
            line.Contains($"Free slot to project solo (task {queued[0]})")
            && line.Contains("the only project with ready work"));
    }

    /// <summary>
    /// Focus: a higher tier wins every free slot over lower tiers while it has eligible work, and
    /// releases itself the moment its queue drains — no operator action, in deliberate contrast to
    /// the cap-0 pause.
    /// </summary>
    [Fact]
    public async Task A_higher_tier_wins_every_free_slot_and_releases_itself_when_its_queue_drains()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await FreshNodeAsync(store, cts.Token);
        ListLogger<DispatchEngine> logger = new();
        DispatchEngine engine = Engine(store, node, maxConcurrentRuns: 1, logger);

        Guid background = await SeedProjectAsync(store, node, "background", cts.Token);
        Guid focused = await SeedProjectAsync(store, node, "focused", cts.Token, ProjectPriority.High);

        // The background project's work is older, so age alone would run it first.
        Guid[] backgroundTasks = await SeedQueuedAsync(store, node, background, count: 2, from: Now, cts.Token);
        Guid[] focusedTasks = await SeedQueuedAsync(store, node, focused, count: 2, from: Now.AddMinutes(10), cts.Token);

        List<Guid> order = [];
        foreach (int _ in Enumerable.Range(0, 4))
        {
            IReadOnlyList<ClaimedWork> claimed = await engine.ClaimEligibleAsync(cts.Token);
            order.Add(claimed.Single().TaskId);
            await ReleaseAsync(store, claimed[0].TaskId, cts.Token);
        }

        order.Should().Equal([focusedTasks[0], focusedTasks[1], backgroundTasks[0], backgroundTasks[1]],
            "the focused project drains first, then the lower tier resumes with no command at all");

        logger.Lines.Should().ContainSingle(line => line.Contains($"Free slot to project focused (task {focusedTasks[0]})"))
            .Which.Should().Contain("priority high outranks the rotation")
            .And.Contain("releases itself the moment its queue drains");

        logger.Lines.Should().ContainSingle(line => line.Contains($"Free slot to project background (task {backgroundTasks[0]})"))
            .Which.Should().NotContain("priority", "nothing outranks it any more, so the rotation decided this one");
    }

    /// <summary>
    /// A tier is read off the project document every sweep, so raising one lands on the next
    /// dispatch cycle — and it still never touches a run already live. Nothing preempts.
    /// </summary>
    [Fact]
    public async Task A_tier_set_mid_flight_reorders_the_next_free_slot_and_never_a_live_run()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await FreshNodeAsync(store, cts.Token);
        DispatchEngine engine = Engine(store, node, maxConcurrentRuns: 1);

        Guid running = await SeedProjectAsync(store, node, "running", cts.Token);
        Guid latecomer = await SeedProjectAsync(store, node, "latecomer", cts.Token);
        Guid[] runningTasks = await SeedQueuedAsync(store, node, running, count: 2, from: Now, cts.Token);
        Guid[] latecomerTasks = await SeedQueuedAsync(store, node, latecomer, count: 1, from: Now.AddMinutes(10), cts.Token);

        Guid live = (await engine.ClaimEligibleAsync(cts.Token)).Single().TaskId;
        live.Should().Be(runningTasks[0]);

        // Focus arrives while that run is live: it changes who gets the NEXT slot and nothing else.
        await SetPriorityAsync(store, node, latecomer, ProjectPriority.High, cts.Token);

        (await engine.ClaimEligibleAsync(cts.Token)).Should().BeEmpty("the node is at its ceiling; a tier is not a preemption");

        await using (IQuerySession query = store.QuerySession())
        {
            (await query.LoadAsync<TaskLease>(live, cts.Token)).Should().NotBeNull(
                "the live run keeps its lease regardless of what the tiers now say");
            (await query.LoadAsync<TaskListItem>(live, cts.Token))!.State.Value.Should().Be("Claimed");
        }

        await ReleaseAsync(store, live, cts.Token);

        (await engine.ClaimEligibleAsync(cts.Token)).Select(work => work.TaskId).Should().Equal([latecomerTasks[0]],
            "the freed slot goes to the newly focused project, with no daemon restart");
    }

    /// <summary>
    /// A tier orders who receives a free slot; it never lifts a limit. A focused project at its
    /// own cap — or paused at 0 — is skipped without consuming a turn, and the lower tier
    /// proceeds rather than the slot being held open.
    /// </summary>
    [Fact]
    public async Task A_focused_project_that_cannot_claim_is_skipped_rather_than_holding_the_slot()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await FreshNodeAsync(store, cts.Token);
        ListLogger<DispatchEngine> logger = new();
        DispatchEngine engine = Engine(store, node, maxConcurrentRuns: 2, logger);

        Guid frozen = await SeedProjectAsync(store, node, "frozen", cts.Token, ProjectPriority.High, maxParallelTasks: 0);
        Guid open = await SeedProjectAsync(store, node, "open", cts.Token);
        Guid[] held = await SeedQueuedAsync(store, node, frozen, count: 2, from: Now, cts.Token);
        Guid[] ready = await SeedQueuedAsync(store, node, open, count: 2, from: Now.AddMinutes(10), cts.Token);

        (await engine.ClaimEligibleAsync(cts.Token)).Select(work => work.TaskId).Should().Equal(ready,
            "a paused project claims nothing however it is prioritized, and nothing is reserved for it");

        logger.Lines.Should().ContainSingle(line => line.Contains($"Task {held[0]}"))
            .Which.Should().Contain("project frozen is paused",
                "the deferral names the cap that holds it — the tier is not what a reader has to fix");
    }

    /// <summary>
    /// The queue-first marker (Decisions Log #127) keeps its promise across projects: it takes the
    /// next free slot regardless of assignment age and regardless of whose turn the rotation says
    /// it is, and it clears itself as that claim commits.
    /// </summary>
    [Fact]
    public async Task A_queue_first_marked_task_takes_the_next_free_slot_across_projects()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await FreshNodeAsync(store, cts.Token);
        ListLogger<DispatchEngine> logger = new();
        DispatchEngine engine = Engine(store, node, maxConcurrentRuns: 1, logger);

        Guid alpha = await SeedProjectAsync(store, node, "alpha", cts.Token);
        Guid beta = await SeedProjectAsync(store, node, "beta", cts.Token, ProjectPriority.High);
        Guid[] alphaTasks = await SeedQueuedAsync(store, node, alpha, count: 2, from: Now, cts.Token);
        Guid[] betaTasks = await SeedQueuedAsync(store, node, beta, count: 1, from: Now.AddMinutes(10), cts.Token);

        await MarkQueueFirstAsync(store, node, alphaTasks[1], cts.Token);

        (await engine.ClaimEligibleAsync(cts.Token)).Select(work => work.TaskId).Should().Equal([alphaTasks[1]],
            "the marker outranks both the rotation and the focused tier, for exactly one slot");

        logger.Lines.Should().ContainSingle(line => line.Contains($"Free slot to project alpha (task {alphaTasks[1]})"))
            .Which.Should().Contain("marked this task queue-first");

        await ReleaseAsync(store, alphaTasks[1], cts.Token);

        (await engine.ClaimEligibleAsync(cts.Token)).Select(work => work.TaskId).Should().Equal([betaTasks[0]],
            "the marker cleared with its own claim, so the tier decides the next slot");
    }


    private static async Task<NodeContext> FreshNodeAsync(IDocumentStore store, CancellationToken cancellationToken)
    {
        await store.Advanced.ResetAllData(cancellationToken);
        return await NodeBootstrapSeed.NewNodeAsync(store, cancellationToken);
    }

    private DispatchEngine Engine(
        IDocumentStore store, NodeContext node, int maxConcurrentRuns,
        ILogger<DispatchEngine>? logger = null) => new(
        store, node, new DaemonConnection(postgres.ConnectionString), new FakeProcessManager(),
        Options.Create(new DaemonOptions
        {
            MaxConcurrentTaskRuns = maxConcurrentRuns,
            LeaseTimeout = TimeSpan.FromSeconds(60),
        }),
        logger ?? NullLogger<DispatchEngine>.Instance);

    /// <summary>A registered project carrying the tier (and cap) under test, through its own real events.</summary>
    private static async Task<Guid> SeedProjectAsync(
        IDocumentStore store, NodeContext node, string name, CancellationToken cancellationToken,
        ProjectPriority? priority = null, int? maxParallelTasks = null)
    {
        Guid id = DomainId.New();
        ProjectRegistered registered = ProjectDecider.Register(
            id, node.OwnerId, DomainId.New(), name, $"/repos/{name}.git", null, "main", Now);
        ProjectAggregate aggregate = new();
        aggregate.Apply(registered);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<ProjectAggregate>(id, registered);
        if (priority is not null || maxParallelTasks is not null)
        {
            session.Events.Append(id, Settings(aggregate, node.OwnerId, priority, maxParallelTasks));
        }

        await session.SaveChangesAsync(cancellationToken);
        return id;
    }

    private static async Task SetPriorityAsync(
        IDocumentStore store, NodeContext node, Guid projectId, ProjectPriority priority,
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        ProjectAggregate aggregate =
            (await session.Events.AggregateStreamAsync<ProjectAggregate>(projectId, token: cancellationToken))!;
        session.Events.Append(projectId, Settings(aggregate, node.OwnerId, priority, maxParallelTasks: null));
        await session.SaveChangesAsync(cancellationToken);
    }

    private static ProjectSettingsChanged Settings(
        ProjectAggregate project, Guid ownerId, ProjectPriority? priority, int? maxParallelTasks) =>
        ProjectDecider.ChangeSettings(
            project,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: Optional<bool>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            changedAt: Now, changedByOwnerId: ownerId,
            maxParallelTasks: maxParallelTasks is { } cap ? Optional<int?>.Of(cap) : Optional<int?>.None,
            priority: priority is null ? Optional<ProjectPriority>.None : Optional<ProjectPriority>.Of(priority));

    /// <summary>Queued, assigned work in one project, oldest first — the order the dispatcher serves within it.</summary>
    private static async Task<Guid[]> SeedQueuedAsync(
        IDocumentStore store, NodeContext node, Guid projectId, int count, DateTimeOffset from,
        CancellationToken cancellationToken)
    {
        Guid[] ids = [.. Enumerable.Range(0, count).Select(_ => DomainId.New())];
        await using IDocumentSession session = store.LightweightSession();
        for (int index = 0; index < ids.Length; index++)
        {
            session.Events.StartStream<TaskAggregate>(ids[index], TaskSeed.Dispatchable(
                TaskDecider.Add(
                    ids[index], projectId, $"Task {index}", ["done"], TaskType.Chore,
                    null, null, null, from.AddSeconds(index), node.OwnerId),
                node.OwnerId, from.AddSeconds(index)));
        }

        await session.SaveChangesAsync(cancellationToken);
        return ids;
    }

    /// <summary>
    /// The marker a human records with <c>h9k task revise --queue-first</c>, through the real
    /// decider: it is the one revision the Draft-only gate lets through on a Queued task.
    /// </summary>
    private static async Task MarkQueueFirstAsync(
        IDocumentStore store, NodeContext node, Guid taskId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        TaskAggregate task =
            (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken))!;
        TaskRevised revised = TaskDecider.Revise(
            task,
            objective: Optional<string>.None,
            acceptanceCriteria: Optional<IReadOnlyList<string>>.None,
            agentContext: Optional<string>.None,
            blockedBy: Optional<IReadOnlyList<Guid>>.None,
            type: Optional<TaskType>.None,
            model: Optional<AgentModel>.None,
            revisedAt: Now,
            revisedByOwnerId: node.OwnerId,
            queuePriority: Optional<bool>.Of(true));
        session.Events.Append(taskId, revised);
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// A claimed task's slot released the way the ceiling tests release one: the lease is what the
    /// dispatch handoff counts, so dropping it is "that session tree is gone" without having to
    /// drive a whole run to completion.
    /// </summary>
    private static async Task ReleaseAsync(IDocumentStore store, Guid taskId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Delete<TaskLease>(taskId);
        await session.SaveChangesAsync(cancellationToken);
    }
}
