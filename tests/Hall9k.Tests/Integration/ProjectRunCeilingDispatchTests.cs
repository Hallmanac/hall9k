using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Dispatch;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using JasperFx;
using Marten;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The per-project run ceiling as the dispatcher enforces it (Decisions Log #140). Its own class
/// so it gets its own database, for the same reason <see cref="DispatchCeilingTests"/> does: every
/// assertion here counts what a project is carrying, and a sibling test's leftover lease would be
/// counted as one of them.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class ProjectRunCeilingDispatchTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The headline contract: node at 2, one project capped at 1. That project never gets a
    /// second run, its own queue waits without erroring or parking, and a second project fills
    /// the node ceiling — a cap is a ceiling on its own project, never a reservation held back
    /// from anyone else.
    /// </summary>
    [Fact]
    public async Task A_project_capped_at_one_never_takes_a_second_slot_while_another_project_fills_the_node()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = Store();
        NodeContext node = await FreshNodeAsync(store, cts.Token);
        DispatchEngine engine = Engine(store, node, maxConcurrentRuns: 2);

        Guid capped = await SeedProjectAsync(store, node, "capped", maxParallelTasks: 1, cts.Token);
        Guid open = await SeedProjectAsync(store, node, "open", maxParallelTasks: null, cts.Token);

        // The capped project's tasks are assigned first, so they would take both slots if the
        // cap did nothing at all.
        Guid[] cappedTasks = await SeedQueuedAsync(store, node, capped, count: 2, from: Now, cts.Token);
        Guid[] openTasks = await SeedQueuedAsync(store, node, open, count: 2, from: Now.AddMinutes(10), cts.Token);

        IReadOnlyList<ClaimedWork> claimed = await engine.ClaimEligibleAsync(cts.Token);

        claimed.Select(work => work.TaskId).Should().Equal([cappedTasks[0], openTasks[0]],
            "the capped project takes one slot and the queue moves on to the project that can use the other");

        await using (IQuerySession query = store.QuerySession())
        {
            foreach (Guid waiting in new[] { cappedTasks[1], openTasks[1] })
            {
                TaskListItem task = (await query.LoadAsync<TaskListItem>(waiting, cts.Token))!;
                task.State.Value.Should().Be("Queued",
                    "waiting behind a cap is not a new state, and it is not a park or an error either");
            }
        }

        // Nothing has finished, so nothing more starts.
        (await engine.ClaimEligibleAsync(cts.Token)).Should().BeEmpty();

        // The open project's run ends: its own second task takes the freed slot, and the capped
        // project's second task still does not — the cap is not counting the node's runs.
        await ReleaseAsync(store, claimed[1].TaskId, cts.Token);

        (await engine.ClaimEligibleAsync(cts.Token)).Select(work => work.TaskId).Should().Equal(openTasks[1]);

        // Now the capped project's own run ends, and its queue moves at last.
        await ReleaseAsync(store, cappedTasks[0], cts.Token);
        await ReleaseAsync(store, openTasks[1], cts.Token);

        (await engine.ClaimEligibleAsync(cts.Token)).Select(work => work.TaskId).Should().Equal(cappedTasks[1]);
    }

    /// <summary>
    /// A cap of 0 is the pause: no new claim however idle the node is, nothing touched about a
    /// run already live, and nothing in the platform that raises it again on its own.
    /// </summary>
    [Fact]
    public async Task A_cap_of_zero_pauses_the_project_and_no_sweep_ever_unpauses_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = Store();
        NodeContext node = await FreshNodeAsync(store, cts.Token);
        DispatchEngine engine = Engine(store, node, maxConcurrentRuns: 2);

        Guid paused = await SeedProjectAsync(store, node, "paused", maxParallelTasks: 0, cts.Token);
        Guid open = await SeedProjectAsync(store, node, "open", maxParallelTasks: null, cts.Token);
        Guid[] held = await SeedQueuedAsync(store, node, paused, count: 2, from: Now, cts.Token);
        (Guid runningTask, Guid runningRun) = await SeedRunningAsync(store, node, paused, cts.Token);
        Guid[] elsewhere = await SeedQueuedAsync(store, node, open, count: 1, from: Now.AddMinutes(10), cts.Token);

        // Three sweeps, because a pause that leaked on the second or third would be worse than
        // one that never held at all.
        for (int sweep = 0; sweep < 3; sweep++)
        {
            IReadOnlyList<ClaimedWork> claimed = await engine.ClaimEligibleAsync(cts.Token);
            claimed.Select(work => work.TaskId).Should().NotIntersectWith(held,
                "a paused project claims nothing, and nothing in the daemon raises its cap");
            if (sweep == 0)
            {
                claimed.Select(work => work.TaskId).Should().Equal([elsewhere[0]],
                    "the pause is this project's own, not the node's");
            }
        }

        await using IQuerySession query = store.QuerySession();
        foreach (Guid waiting in held)
        {
            (await query.LoadAsync<TaskListItem>(waiting, cts.Token))!.State.Value.Should().Be("Queued");
        }

        RunDetails live = (await query.LoadAsync<RunDetails>(runningRun, cts.Token))!;
        live.State.Should().Be(RunState.Running, "a cap gates claims; it never kills work already running");
        (await query.LoadAsync<TaskLease>(runningTask, cts.Token)).Should().NotBeNull();
    }

    /// <summary>
    /// The cap is read from the project's own document on every sweep, so raising it lands on the
    /// next dispatch cycle — no daemon restart, unlike the node's own <c>config.json</c> settings.
    /// </summary>
    [Fact]
    public async Task A_cap_raised_between_sweeps_takes_effect_on_the_next_cycle_with_no_restart()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = Store();
        NodeContext node = await FreshNodeAsync(store, cts.Token);
        DispatchEngine engine = Engine(store, node, maxConcurrentRuns: 2);

        Guid project = await SeedProjectAsync(store, node, "paced", maxParallelTasks: 0, cts.Token);
        Guid[] queued = await SeedQueuedAsync(store, node, project, count: 2, from: Now, cts.Token);

        (await engine.ClaimEligibleAsync(cts.Token)).Should().BeEmpty("paused");

        await SetCapAsync(store, node, project, maxParallelTasks: 1, cts.Token);

        (await engine.ClaimEligibleAsync(cts.Token)).Select(work => work.TaskId).Should().Equal([queued[0]],
            "the same engine instance, no restart: the cap is re-read from the project every sweep");
    }

    /// <summary>
    /// Every deferred claim is visible with its reason, so a queue state never has to be
    /// reconstructed: the log names which limit held each task, and a pause reads as a pause
    /// rather than as a full cap.
    /// </summary>
    [Fact]
    public async Task Each_deferral_names_the_limit_that_held_it_and_says_it_once()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = Store();
        NodeContext node = await FreshNodeAsync(store, cts.Token);
        ListLogger<DispatchEngine> logger = new();
        DispatchEngine engine = Engine(store, node, maxConcurrentRuns: 1, logger);

        Guid capped = await SeedProjectAsync(store, node, "capped", maxParallelTasks: 1, cts.Token);
        Guid frozen = await SeedProjectAsync(store, node, "frozen", maxParallelTasks: 0, cts.Token);
        Guid[] cappedTasks = await SeedQueuedAsync(store, node, capped, count: 2, from: Now, cts.Token);
        Guid[] frozenTasks = await SeedQueuedAsync(store, node, frozen, count: 1, from: Now.AddMinutes(10), cts.Token);

        await engine.ClaimEligibleAsync(cts.Token);

        logger.Lines.Should().ContainSingle(line =>
                line.Contains($"Task {cappedTasks[1]}") && line.Contains("project cap 1 of 1"))
            .Which.Should().Contain("project capped", "the project is named, not just its id");

        // The exact command, rendered: the paused line names the project twice, so a template
        // whose placeholders were substituted by anything but position would print the cap or a
        // stray number where the project's own name belongs.
        logger.Lines.Should().ContainSingle(line => line.Contains($"Task {frozenTasks[0]}"))
            .Which.Should().Contain("project frozen is paused")
            .And.Contain("h9k project set frozen --max-parallel-tasks <n>");

        // Said once per episode, not once per sweep: at a five-second cadence a per-sweep line
        // would bury the dispatches it sits between.
        int lines = logger.Lines.Count;
        await engine.ClaimEligibleAsync(cts.Token);
        logger.Lines.Count.Should().Be(lines, "a deferral already announced goes quiet while it waits");
    }

    /// <summary>
    /// The sweep publishes what it admitted against, per project as well as per node (Decisions
    /// Log #64's rule applied to #140's cap), so <c>h9k status</c> never re-derives the counting
    /// rule — including for a paused project carrying nothing at all.
    /// </summary>
    [Fact]
    public async Task The_published_measurement_carries_each_projects_own_count_and_cap()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = Store();
        NodeContext node = await FreshNodeAsync(store, cts.Token);
        DispatchEngine engine = Engine(store, node, maxConcurrentRuns: 2);

        Guid capped = await SeedProjectAsync(store, node, "capped", maxParallelTasks: 1, cts.Token);
        Guid pausedProject = await SeedProjectAsync(store, node, "paused", maxParallelTasks: 0, cts.Token);
        await SeedQueuedAsync(store, node, capped, count: 2, from: Now, cts.Token);
        await SeedQueuedAsync(store, node, pausedProject, count: 1, from: Now.AddMinutes(10), cts.Token);

        await engine.ClaimEligibleAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        NodeDispatchLoad load = (await query.LoadAsync<NodeDispatchLoad>(node.NodeId, cts.Token))!;

        load.ProjectLoads.Should().ContainSingle(project => project.ProjectId == capped)
            .Which.Should().BeEquivalentTo(new ProjectRunLoad(capped, LiveRuns: 1, Cap: 1),
                "the sweep publishes the project as it leaves it, the same as the node's own count");
        load.ProjectLoads.Should().ContainSingle(project => project.ProjectId == pausedProject)
            .Which.Should().BeEquivalentTo(new ProjectRunLoad(pausedProject, LiveRuns: 0, Cap: 0),
                "a paused project carrying nothing still publishes, or the board has nothing to say about it");
    }

    /// <summary>
    /// An interactive claim (<c>h9k task work</c>, <c>h9k task start</c>) costs no project cap,
    /// consistent with its zero-run rule at the node level (Decisions Log #111) — structurally,
    /// through the same ceiling-exempt <see cref="Guid.Empty"/> node-id sentinel.
    /// </summary>
    [Fact]
    public async Task An_interactive_claim_costs_this_projects_cap_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = Store();
        NodeContext node = await FreshNodeAsync(store, cts.Token);
        DispatchEngine engine = Engine(store, node, maxConcurrentRuns: 2);

        Guid project = await SeedProjectAsync(store, node, "capped", maxParallelTasks: 1, cts.Token);
        await SeedInteractiveClaimAsync(store, node, project, cts.Token);
        Guid[] queued = await SeedQueuedAsync(store, node, project, count: 1, from: Now.AddMinutes(10), cts.Token);

        (await engine.ClaimEligibleAsync(cts.Token)).Select(work => work.TaskId).Should().Equal([queued[0]],
            "a human's own attended session occupies zero runs, at the project level as at the node's");
    }

    /// <summary>
    /// A task whose project has no document the sweep can read is uncapped rather than held: the
    /// cap lives on the project, so an unreadable project has no cap to enforce, and inventing
    /// one would hold work back for a rule nobody set. This is also the shape every task seeded
    /// before per-project caps existed takes.
    /// </summary>
    [Fact]
    public async Task A_task_whose_project_has_no_document_is_uncapped_rather_than_stuck()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = Store();
        NodeContext node = await FreshNodeAsync(store, cts.Token);
        DispatchEngine engine = Engine(store, node, maxConcurrentRuns: 2);

        Guid[] queued = await SeedQueuedAsync(store, node, DomainId.New(), count: 2, from: Now, cts.Token);

        (await engine.ClaimEligibleAsync(cts.Token)).Select(work => work.TaskId).Should().Equal(queued);
    }

    /// <summary>
    /// A sweep in which no project could admit a claim never asks the spend budget: that answer
    /// is summed live from every TokensRecorded event in the period, and a sweep whose outcome it
    /// cannot change would pay for it every PollInterval (PR review round 1). The skip is visible
    /// from the operator's side too, and honestly so — the held row is named against the cap that
    /// is actually holding it, since raising the spend budget would release nothing here.
    /// </summary>
    [Fact]
    public async Task A_sweep_no_project_can_claim_in_never_asks_the_spend_budget()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = Store();
        NodeContext node = await FreshNodeAsync(store, cts.Token);
        ListLogger<DispatchEngine> logger = new();
        DispatchEngine engine = Engine(store, node, maxConcurrentRuns: 2, logger, spendBudgetTokens: 500_000);

        Guid frozen = await SeedProjectAsync(store, node, "frozen", maxParallelTasks: 0, cts.Token);
        Guid[] held = await SeedQueuedAsync(store, node, frozen, count: 1, from: Now, cts.Token);
        await SeedSpentBudgetAsync(store, cts.Token);

        (await engine.ClaimEligibleAsync(cts.Token)).Should().BeEmpty("a paused project claims nothing regardless");

        logger.Lines.Should().ContainSingle(line => line.Contains($"Task {held[0]}"))
            .Which.Should().Contain("project frozen is paused",
                "the cap holds this row, and a budget the sweep never consulted cannot be reported as the cause");
        logger.Lines.Should().NotContain(
            line => line.Contains("spend budget"), "the scan was skipped, so there is no exhaustion to announce");
    }

    /// <summary>
    /// The mixed sweep the skip above cannot cover: one project paused, another admitting, on a
    /// spent budget. The budget really does hold the admitting project's rows, so the sweep asks
    /// it — and the paused project's rows must still be reported against the pause, not swept
    /// into the budget's line, which would promise them "claimed once the period rolls" when the
    /// rollover releases nothing and h9k status went on naming the pause for the same row
    /// (independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    [Fact]
    public async Task A_spent_budget_never_takes_the_blame_for_a_paused_projects_rows()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = Store();
        NodeContext node = await FreshNodeAsync(store, cts.Token);
        ListLogger<DispatchEngine> logger = new();
        DispatchEngine engine = Engine(store, node, maxConcurrentRuns: 2, logger, spendBudgetTokens: 500_000);

        Guid frozen = await SeedProjectAsync(store, node, "frozen", maxParallelTasks: 0, cts.Token);
        Guid open = await SeedProjectAsync(store, node, "open", maxParallelTasks: null, cts.Token);
        Guid[] paused = await SeedQueuedAsync(store, node, frozen, count: 1, from: Now, cts.Token);
        Guid[] budgeted = await SeedQueuedAsync(store, node, open, count: 1, from: Now.AddMinutes(10), cts.Token);
        await SeedSpentBudgetAsync(store, cts.Token);

        (await engine.ClaimEligibleAsync(cts.Token)).Should().BeEmpty(
            "the node has room, but the budget is spent and the only other project is paused");

        logger.Lines.Should().ContainSingle(line => line.Contains($"Task {paused[0]}"))
            .Which.Should().Contain("project frozen is paused",
                "a period that rolls releases nothing of a paused project's, so the budget must not claim this row");
        logger.Lines.Should().ContainSingle(line => line.Contains($"Task {budgeted[0]}"))
            .Which.Should().Contain("spend budget",
                "this project admits, so the budget is the limit that really is holding its row");
    }

    private DocumentStore Store() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    private static async Task<NodeContext> FreshNodeAsync(IDocumentStore store, CancellationToken cancellationToken)
    {
        await store.Advanced.ResetAllData(cancellationToken);
        return await NodeBootstrapSeed.NewNodeAsync(store, cancellationToken);
    }

    private DispatchEngine Engine(
        IDocumentStore store, NodeContext node, int maxConcurrentRuns, ILogger<DispatchEngine>? logger = null,
        long? spendBudgetTokens = null) => new(
        store, node, new DaemonConnection(postgres.ConnectionString), new FakeProcessManager(),
        Options.Create(new DaemonOptions
        {
            MaxConcurrentTaskRuns = maxConcurrentRuns,
            LeaseTimeout = TimeSpan.FromSeconds(60),
            SpendBudgetTokens = spendBudgetTokens,
            SpendPeriod = SpendPeriod.Week.Value,
        }),
        logger ?? NullLogger<DispatchEngine>.Instance);

    /// <summary>A registered project carrying the cap under test, through its own real events.</summary>
    private static async Task<Guid> SeedProjectAsync(
        IDocumentStore store, NodeContext node, string name, int? maxParallelTasks,
        CancellationToken cancellationToken)
    {
        Guid id = DomainId.New();
        ProjectRegistered registered = ProjectDecider.Register(
            id, node.OwnerId, DomainId.New(), name, $"/repos/{name}.git", null, "main", Now);
        ProjectAggregate aggregate = new();
        aggregate.Apply(registered);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<ProjectAggregate>(id, registered);
        if (maxParallelTasks is { } cap)
        {
            session.Events.Append(id, Cap(aggregate, node.OwnerId, cap));
        }

        await session.SaveChangesAsync(cancellationToken);
        return id;
    }

    private static async Task SetCapAsync(
        IDocumentStore store, NodeContext node, Guid projectId, int maxParallelTasks,
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        ProjectAggregate aggregate =
            (await session.Events.AggregateStreamAsync<ProjectAggregate>(projectId, token: cancellationToken))!;
        session.Events.Append(projectId, Cap(aggregate, node.OwnerId, maxParallelTasks));
        await session.SaveChangesAsync(cancellationToken);
    }

    private static ProjectSettingsChanged Cap(ProjectAggregate project, Guid ownerId, int maxParallelTasks) =>
        ProjectDecider.ChangeSettings(
            project,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: Optional<bool>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            changedAt: Now, changedByOwnerId: ownerId,
            maxParallelTasks: Optional<int?>.Of(maxParallelTasks));

    /// <summary>Queued, assigned work in one project, oldest first — the order the dispatcher claims in.</summary>
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
    /// One session's recorded spend, well past the budget the engine under test carries and dated
    /// the real wall clock rather than this class's fixed <see cref="Now"/>: the dispatcher has no
    /// injectable clock for that gate, so the period it would sum against is whatever week
    /// contains <see cref="DateTimeOffset.UtcNow"/> when the test actually runs.
    /// </summary>
    private static async Task SeedSpentBudgetAsync(IDocumentStore store, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<RunAggregate>(
            DomainId.New(),
            new TokensRecorded(DomainId.New(), 900_000, 10_000, null, DateTimeOffset.UtcNow, Model: AgentModel.Sonnet));
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>One of this project's tasks claimed by this node and running: a live slot the cap counts.</summary>
    private static async Task<(Guid TaskId, Guid RunId)> SeedRunningAsync(
        IDocumentStore store, NodeContext node, Guid projectId, CancellationToken cancellationToken)
    {
        (Guid taskId, Guid runId) = await SeedClaimAsync(store, node, projectId, node.NodeId, cancellationToken);
        return (taskId, runId);
    }

    /// <summary>
    /// A task an operator claimed interactively: the same <see cref="Guid.Empty"/> node-id
    /// sentinel <c>h9k task work</c> and <c>h9k task start</c> record, which is what makes it
    /// cost no slot anywhere.
    /// </summary>
    private static async Task SeedInteractiveClaimAsync(
        IDocumentStore store, NodeContext node, Guid projectId, CancellationToken cancellationToken) =>
        await SeedClaimAsync(store, node, projectId, Guid.Empty, cancellationToken);

    private static async Task<(Guid TaskId, Guid RunId)> SeedClaimAsync(
        IDocumentStore store, NodeContext node, Guid projectId, Guid claimingNodeId,
        CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();

        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(taskId, projectId, "Already running", ["done"], TaskType.Chore,
                null, null, null, Now, node.OwnerId),
            node.OwnerId, Now);
        session.Events.StartStream<TaskAggregate>(taskId,
            [.. lifecycle, TaskDecider.Claim(task, claimingNodeId, node.OwnerId, runId, Now)]);

        session.Events.StartStream<RunAggregate>(runId,
            new RunDispatched(runId, taskId, claimingNodeId, node.OwnerId, 1,
                DomainId.New(), "/wt/running", "task/running", ExecutorMode.Subscription, Now),
            new RunProcessStarted(runId, 4242, Now));

        session.Store(new TaskLease
        {
            Id = taskId,
            NodeId = claimingNodeId,
            LeaseGeneration = 1,
            HeartbeatAt = DateTimeOffset.UtcNow,
        });
        await session.SaveChangesAsync(cancellationToken);
        return (taskId, runId);
    }

    /// <summary>
    /// A claimed task's slot released the way the node-ceiling tests release one: the lease is
    /// what the dispatch handoff counts, so dropping it is "that session tree is gone" without
    /// having to drive a whole run to completion.
    /// </summary>
    private static async Task ReleaseAsync(IDocumentStore store, Guid taskId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Delete<TaskLease>(taskId);
        await session.SaveChangesAsync(cancellationToken);
    }
}
