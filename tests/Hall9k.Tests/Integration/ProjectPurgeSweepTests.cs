using FluentAssertions;
using Hall9k.Daemon.Purge;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The purge sweep against a real database (task: an archived project can be purged — the second
/// half of the two-tier project-removal design, PLAN.md §16 #182's purge follow-up). Its own class
/// gets its own container for the same reason every <c>PostgresFixture</c>-backed class does: a
/// purge is scoped by counting exactly what a project owns, and a sibling test's leftover rows
/// would be counted as some of them.
/// <para>
/// This exercises <see cref="ProjectPurgeEngine"/> directly rather than through the CLI process,
/// since what needs proving is the raw multi-table delete against a real Postgres — the one part
/// of this feature no unit test (pure, database-free deciders) can reach.
/// </para>
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class ProjectPurgeSweepTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_due_purge_destroys_every_stream_event_and_projection_row_it_owns_and_nothing_else()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        IDocumentStore store = postgres.Store;
        await store.Advanced.ResetAllData(cts.Token);
        Guid ownerId = DomainId.New();

        Guid purgedProjectId = await SeedProjectAsync(store, "to-purge", ownerId, cts.Token);
        Guid[] purgedTaskIds = await SeedTasksAsync(store, purgedProjectId, ownerId, count: 2, cts.Token);
        Guid purgedRunId = await SeedRunAsync(store, purgedTaskIds[0], ownerId, cts.Token);
        Guid purgedIdeaId = await SeedIdeaAsync(store, purgedProjectId, ownerId, cts.Token);
        Guid purgedEpicId = await SeedEpicAsync(store, purgedProjectId, ownerId, cts.Token);
        await SchedulePastDuePurgeAsync(store, purgedProjectId, ownerId, cts.Token);

        // A sibling project, untouched by this sweep — the control that proves the purge is
        // scoped to what it owns rather than sweeping the whole database.
        Guid survivingProjectId = await SeedProjectAsync(store, "survivor", ownerId, cts.Token);
        Guid[] survivingTaskIds = await SeedTasksAsync(store, survivingProjectId, ownerId, count: 1, cts.Token);
        Guid survivingRunId = await SeedRunAsync(store, survivingTaskIds[0], ownerId, cts.Token);
        Guid survivingIdeaId = await SeedIdeaAsync(store, survivingProjectId, ownerId, cts.Token);
        Guid survivingEpicId = await SeedEpicAsync(store, survivingProjectId, ownerId, cts.Token);

        ProjectPurgeEngine engine = new(store, NullLogger<ProjectPurgeEngine>.Instance);
        ProjectPurgeSweepResult result = await engine.SweepOnceAsync(cts.Token);

        result.ProjectsPurged.Should().Be(1);
        result.TasksDestroyed.Should().Be(2);
        result.RunsDestroyed.Should().Be(1);
        result.IdeasDestroyed.Should().Be(1);
        result.EpicsDestroyed.Should().Be(1);
        result.Failures.Should().Be(0);

        await using (IQuerySession query = store.QuerySession())
        {
            (await query.LoadAsync<ProjectDetails>(purgedProjectId, cts.Token)).Should().BeNull();
            foreach (Guid taskId in purgedTaskIds)
            {
                (await query.LoadAsync<TaskDetails>(taskId, cts.Token)).Should().BeNull();
                (await query.LoadAsync<TaskListItem>(taskId, cts.Token)).Should().BeNull();
            }

            (await query.LoadAsync<RunDetails>(purgedRunId, cts.Token)).Should().BeNull();
            (await query.LoadAsync<RunListItem>(purgedRunId, cts.Token)).Should().BeNull();
            (await query.LoadAsync<IdeaDetails>(purgedIdeaId, cts.Token)).Should().BeNull();
            (await query.LoadAsync<EpicDetails>(purgedEpicId, cts.Token)).Should().BeNull();

            Guid[] purgedStreamIds = [purgedProjectId, .. purgedTaskIds, purgedRunId, purgedIdeaId, purgedEpicId];
            long eventRows = (await query.QueryAsync<long>(
                "select count(*) from mt_events where stream_id = ANY(?)", cts.Token, purgedStreamIds)).Single();
            eventRows.Should().Be(0, "no event for the purged project or anything it owned should remain");
            long streamRows = (await query.QueryAsync<long>(
                "select count(*) from mt_streams where id = ANY(?)", cts.Token, purgedStreamIds)).Single();
            streamRows.Should().Be(0, "no stream row for the purged project or anything it owned should remain");

            // The survivor: every stream and projection row still exactly where it was.
            (await query.LoadAsync<ProjectDetails>(survivingProjectId, cts.Token)).Should().NotBeNull();
            (await query.LoadAsync<TaskDetails>(survivingTaskIds[0], cts.Token)).Should().NotBeNull();
            (await query.LoadAsync<RunDetails>(survivingRunId, cts.Token)).Should().NotBeNull();
            (await query.LoadAsync<IdeaDetails>(survivingIdeaId, cts.Token)).Should().NotBeNull();
            (await query.LoadAsync<EpicDetails>(survivingEpicId, cts.Token)).Should().NotBeNull();
            Guid[] survivingStreamIds = [survivingProjectId, survivingTaskIds[0], survivingRunId, survivingIdeaId, survivingEpicId];
            long survivingEventRows = (await query.QueryAsync<long>(
                "select count(*) from mt_events where stream_id = ANY(?)", cts.Token, survivingStreamIds)).Single();
            survivingEventRows.Should().BeGreaterThan(0, "the sibling project's own history is untouched");
        }

        // The name is free again for a fresh registration — no lingering uniqueness conflict.
        await using (IDocumentSession session = store.LightweightSession())
        {
            Guid reused = DomainId.New();
            session.Events.StartStream<ProjectAggregate>(reused, ProjectDecider.Register(
                reused, ownerId, DomainId.New(), "to-purge", "/repos/to-purge-reborn.git", null, "main", Now));
            await session.SaveChangesAsync(cts.Token);
        }
    }

    [Fact]
    public async Task A_purge_not_yet_due_is_left_alone()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        IDocumentStore store = postgres.Store;
        await store.Advanced.ResetAllData(cts.Token);
        Guid ownerId = DomainId.New();

        Guid projectId = await SeedProjectAsync(store, "not-yet", ownerId, cts.Token);
        await using (IDocumentSession session = store.LightweightSession())
        {
            ProjectAggregate aggregate = await session.Events.AggregateStreamAsync<ProjectAggregate>(projectId, token: cts.Token)
                ?? throw new InvalidOperationException("seeded project must exist");
            ProjectArchived archived = ProjectDecider.Archive(aggregate, null, Now, ownerId);
            session.Events.Append(projectId, archived);
            aggregate.Apply(archived);

            // Grace period measured from the real wall clock, not the fixed `Now` — the engine
            // under test selects due projects against DateTimeOffset.UtcNow, so a deadline pinned
            // to a frozen instant becomes genuinely due (and this test permanently red) once real
            // time passes it. A day's grace from "right now" never does.
            session.Events.Append(
                projectId,
                ProjectDecider.SchedulePurge(
                    aggregate, Now, ownerId, gracePeriod: DateTimeOffset.UtcNow.AddDays(1) - Now));
            await session.SaveChangesAsync(cts.Token);
        }

        ProjectPurgeEngine engine = new(store, NullLogger<ProjectPurgeEngine>.Instance);
        ProjectPurgeSweepResult result = await engine.SweepOnceAsync(cts.Token);

        result.ProjectsPurged.Should().Be(0, "a purge whose deadline has not passed must never fire early");
        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<ProjectDetails>(projectId, cts.Token)).Should().NotBeNull();
    }

    /// <summary>
    /// A plain cross-project BlockedBy edge is harmless while both projects live (TaskDependency's
    /// own doc comment: "it never reads the blocker's branch"), but a purge hard-deletes the
    /// blocker's stream and TaskListItem out from under whatever other project's task still names
    /// it — and TaskDependencyQuery silently dropped an id it could not find before this fix, so
    /// the dependent's re-evaluation pass walked an empty dependency list and never appended
    /// anything, leaving it Blocked forever with no blocker even named (independent pre-PR review,
    /// cycle 1, adversarial lens). This proves the fix: the missing id surfaces as a dead
    /// dependency, the same NeedsHuman hold a Failed or Abandoned blocker gets, with a remedy on
    /// the dependent's own side.
    /// </summary>
    [Fact]
    public async Task A_purge_that_destroys_another_projects_blocker_parks_the_dependent_rather_than_stranding_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        IDocumentStore store = postgres.Store;
        await store.Advanced.ResetAllData(cts.Token);
        Guid ownerId = DomainId.New();

        Guid blockerProjectId = await SeedProjectAsync(store, "blocker-project", ownerId, cts.Token);
        Guid[] blockerTaskIds = await SeedTasksAsync(store, blockerProjectId, ownerId, count: 1, cts.Token);
        Guid blockerTaskId = blockerTaskIds[0];

        Guid dependentProjectId = await SeedProjectAsync(store, "dependent-project", ownerId, cts.Token);
        Guid dependentTaskId = await SeedBlockedTaskAsync(store, dependentProjectId, ownerId, blockerTaskId, cts.Token);

        await SchedulePastDuePurgeAsync(store, blockerProjectId, ownerId, cts.Token);

        ProjectPurgeEngine engine = new(store, NullLogger<ProjectPurgeEngine>.Instance);
        ProjectPurgeSweepResult purgeResult = await engine.SweepOnceAsync(cts.Token);
        purgeResult.ProjectsPurged.Should().Be(1);

        await using (IQuerySession query = store.QuerySession())
        {
            (await query.LoadAsync<TaskListItem>(blockerTaskId, cts.Token)).Should()
                .BeNull("the blocker's own project was purged, taking its task stream with it");
        }

        DependencyReevaluation reevaluation;
        await using (IDocumentSession session = store.LightweightSession())
        {
            // The dispatch loop's own safety net (ForEveryBlockedTaskAsync) — no RunCompleted
            // ever arrives for a blocker that no longer exists, so nothing would ever call
            // ForDependencyAsync for it.
            reevaluation = await TaskDependencyResolver.ForEveryBlockedTaskAsync(session, DateTimeOffset.UtcNow, cts.Token);
        }

        reevaluation.Parked.Should().ContainSingle(hold => hold.TaskId == dependentTaskId)
            .Which.Reason.Should().Contain("no longer exists");

        await using (IQuerySession query = store.QuerySession())
        {
            TaskDetails? dependent = await query.LoadAsync<TaskDetails>(dependentTaskId, cts.Token);
            dependent.Should().NotBeNull();
            dependent!.State.Should().Be(TaskState.Blocked, "the dependency never completes — it is dead, not met");
            dependent.DependencyFailureReason.Should().NotBeNull().And.Contain("no longer exists");
        }
    }

    private static async Task<Guid> SeedBlockedTaskAsync(
        IDocumentStore store, Guid projectId, Guid ownerId, Guid blockedByTaskId, CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            taskId, projectId, "a task blocked on another project's task", ["done"], TaskType.Chore,
            null, null, null, Now, ownerId, blockedBy: [blockedByTaskId]);

        await using IDocumentSession session = store.LightweightSession();
        TaskDependencyGraph graph = await TaskSeed.DependencyGraphAsync(session, [blockedByTaskId], cancellationToken);
        session.Events.StartStream<TaskAggregate>(taskId, TaskSeed.Dispatchable(added, ownerId, Now, graph));
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    private static async Task<Guid> SeedProjectAsync(
        IDocumentStore store, string name, Guid ownerId, CancellationToken cancellationToken)
    {
        Guid id = DomainId.New();
        ProjectRegistered registered = ProjectDecider.Register(
            id, ownerId, DomainId.New(), name, $"/repos/{name}.git", null, "main", Now);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<ProjectAggregate>(id, registered);
        await session.SaveChangesAsync(cancellationToken);
        return id;
    }

    private static async Task<Guid[]> SeedTasksAsync(
        IDocumentStore store, Guid projectId, Guid ownerId, int count, CancellationToken cancellationToken)
    {
        Guid[] ids = [.. Enumerable.Range(0, count).Select(_ => DomainId.New())];
        await using IDocumentSession session = store.LightweightSession();
        for (int index = 0; index < ids.Length; index++)
        {
            session.Events.StartStream<TaskAggregate>(ids[index], TaskSeed.Dispatchable(
                TaskDecider.Add(
                    ids[index], projectId, $"Task {index}", ["done"], TaskType.Chore,
                    null, null, null, Now.AddSeconds(index), ownerId),
                ownerId, Now.AddSeconds(index)));
        }

        await session.SaveChangesAsync(cancellationToken);
        return ids;
    }

    private static async Task<Guid> SeedRunAsync(
        IDocumentStore store, Guid taskId, Guid ownerId, CancellationToken cancellationToken)
    {
        Guid runId = DomainId.New();
        RunDispatched dispatched = new(
            runId, taskId, DomainId.New(), ownerId, LeaseGeneration: 1, SessionId: DomainId.New(),
            WorktreePath: "/worktrees/whatever", Branch: "task/whatever", ExecutorMode.Subscription, Now);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<RunAggregate>(runId, dispatched);
        await session.SaveChangesAsync(cancellationToken);
        return runId;
    }

    private static async Task<Guid> SeedIdeaAsync(
        IDocumentStore store, Guid projectId, Guid ownerId, CancellationToken cancellationToken)
    {
        Guid ideaId = DomainId.New();
        IdeaCaptured captured = IdeaDecider.Capture(
            ideaId, ownerId, "an idea worth destroying", projectId, Now, ProjectHome.None);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<IdeaAggregate>(ideaId, captured);
        await session.SaveChangesAsync(cancellationToken);
        return ideaId;
    }

    private static async Task<Guid> SeedEpicAsync(
        IDocumentStore store, Guid projectId, Guid ownerId, CancellationToken cancellationToken)
    {
        Guid epicId = DomainId.New();
        EpicAdded added = EpicDecider.Add(epicId, projectId, "an epic worth destroying", Now, ownerId);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<EpicAggregate>(epicId, added);
        await session.SaveChangesAsync(cancellationToken);
        return epicId;
    }

    /// <summary>Archives (fresh) and schedules a purge whose deadline already passed — a negative grace period, so the sweep finds it due immediately.</summary>
    private static async Task SchedulePastDuePurgeAsync(
        IDocumentStore store, Guid projectId, Guid ownerId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        ProjectAggregate aggregate = await session.Events.AggregateStreamAsync<ProjectAggregate>(projectId, token: cancellationToken)
            ?? throw new InvalidOperationException("seeded project must exist");
        ProjectArchived archived = ProjectDecider.Archive(aggregate, null, Now, ownerId);
        session.Events.Append(projectId, archived);
        aggregate.Apply(archived);
        session.Events.Append(
            projectId, ProjectDecider.SchedulePurge(aggregate, Now, ownerId, gracePeriod: TimeSpan.FromHours(-1)));
        await session.SaveChangesAsync(cancellationToken);
    }
}
