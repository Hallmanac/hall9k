using FluentAssertions;
using Hall9k.Daemon.Purge;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
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
        await SchedulePastDuePurgeAsync(store, purgedProjectId, ownerId, cts.Token);

        // A sibling project, untouched by this sweep — the control that proves the purge is
        // scoped to what it owns rather than sweeping the whole database.
        Guid survivingProjectId = await SeedProjectAsync(store, "survivor", ownerId, cts.Token);
        Guid[] survivingTaskIds = await SeedTasksAsync(store, survivingProjectId, ownerId, count: 1, cts.Token);
        Guid survivingRunId = await SeedRunAsync(store, survivingTaskIds[0], ownerId, cts.Token);
        Guid survivingIdeaId = await SeedIdeaAsync(store, survivingProjectId, ownerId, cts.Token);

        ProjectPurgeEngine engine = new(store, NullLogger<ProjectPurgeEngine>.Instance);
        ProjectPurgeSweepResult result = await engine.SweepOnceAsync(cts.Token);

        result.ProjectsPurged.Should().Be(1);
        result.TasksDestroyed.Should().Be(2);
        result.RunsDestroyed.Should().Be(1);
        result.IdeasDestroyed.Should().Be(1);
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

            Guid[] purgedStreamIds = [purgedProjectId, .. purgedTaskIds, purgedRunId, purgedIdeaId];
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
            Guid[] survivingStreamIds = [survivingProjectId, survivingTaskIds[0], survivingRunId, survivingIdeaId];
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
            session.Events.Append(
                projectId, ProjectDecider.SchedulePurge(aggregate, Now, ownerId, gracePeriod: TimeSpan.FromDays(1)));
            await session.SaveChangesAsync(cts.Token);
        }

        ProjectPurgeEngine engine = new(store, NullLogger<ProjectPurgeEngine>.Instance);
        ProjectPurgeSweepResult result = await engine.SweepOnceAsync(cts.Token);

        result.ProjectsPurged.Should().Be(0, "a purge whose deadline has not passed must never fire early");
        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<ProjectDetails>(projectId, cts.Token)).Should().NotBeNull();
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
