using FluentAssertions;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using JasperFx;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// Promotion writes two streams in one transaction (Decisions Log #35): the idea's, which
/// records what it became, and the task's, which records where it came from. Either half
/// alone would be a broken provenance trail, so the append is atomic and this is what proves
/// the projections both land.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class IdeaPromotionTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Promoting_an_idea_records_provenance_in_both_directions()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid ideaId = DomainId.New();
        Guid taskId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<IdeaAggregate>(ideaId, IdeaDecider.Capture(
                ideaId, ownerId,
                "Ideas deserve their own discovery phase. The workspace is where research lands.",
                projectId: null, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            IdeaAggregate idea = (await session.Events.AggregateStreamAsync<IdeaAggregate>(
                ideaId, token: cts.Token))!;
            idea.Should().NotBeNull();

            session.Events.Append(ideaId, IdeaDecider.AssignToProject(idea, projectId, Now.AddHours(1), ownerId));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            IdeaAggregate idea = (await session.Events.AggregateStreamAsync<IdeaAggregate>(
                ideaId, token: cts.Token))!;
            IdeaSeed seed = IdeaText.Seed(idea.Text);

            IdeaPromoted promoted = IdeaDecider.Promote(
                idea, taskId, projectId: null, seed.Objective, Now.AddDays(1), ownerId);
            TaskAdded added = TaskDecider.Add(
                taskId, promoted.ProjectId, promoted.Objective, acceptanceCriteria: [], TaskType.Feature,
                seed.Context, constraints: null, externalReference: null, promoted.PromotedAt, ownerId,
                model: null, blockedBy: null, sourceIdeaId: ideaId);

            session.Events.StartStream<TaskAggregate>(taskId, added);
            session.Events.Append(ideaId, promoted);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession session = store.QuerySession())
        {
            IdeaDetails? idea = await session.LoadAsync<IdeaDetails>(ideaId, cts.Token);
            TaskDetails? task = await session.LoadAsync<TaskDetails>(taskId, cts.Token);

            idea!.State.Should().Be(IdeaState.Promoted);
            idea.PromotedTaskId.Should().Be(taskId, "the idea's stream names the task it became");
            idea.ProjectId.Should().Be(projectId);

            task!.SourceIdeaId.Should().Be(ideaId, "the task's stream names the idea it came from");
            task.State.Should().Be(TaskState.Draft, "promotion produces an ordinary draft (log #34)");
            task.Objective.Should().Be("Ideas deserve their own discovery phase.");
            task.AgentContext.Should().Be("The workspace is where research lands.");
        }
    }

    /// <summary>
    /// Promotion is a one-time transition that mints a second stream, so two of them racing
    /// must not both land: the loser is refused at the database, and because the two appends
    /// share one transaction, the task it would have created never exists. Provenance stays
    /// two-way or it does not happen at all.
    /// </summary>
    [Fact]
    public async Task Two_racing_promotions_leave_exactly_one_task_behind()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid ideaId = DomainId.New();

        await using (IDocumentSession setup = store.LightweightSession())
        {
            setup.Events.StartStream<IdeaAggregate>(ideaId, IdeaDecider.Capture(
                ideaId, ownerId, "Two promotions race. Exactly one may win.", projectId, Now));
            await setup.SaveChangesAsync(cts.Token);
        }

        // Two invocations read the same stream state, then both promote at the same expected
        // version — the database, not timing, decides which one becomes a task.
        await using IDocumentSession first = store.LightweightSession();
        await using IDocumentSession second = store.LightweightSession();

        StreamState fence1 = (await first.Events.FetchStreamStateAsync(ideaId, cts.Token))!;
        StreamState fence2 = (await second.Events.FetchStreamStateAsync(ideaId, cts.Token))!;
        IdeaAggregate view1 = (await first.Events.AggregateStreamAsync<IdeaAggregate>(
            ideaId, version: fence1.Version, token: cts.Token))!;
        IdeaAggregate view2 = (await second.Events.AggregateStreamAsync<IdeaAggregate>(
            ideaId, version: fence2.Version, token: cts.Token))!;

        Guid winningTaskId = DomainId.New();
        Guid losingTaskId = DomainId.New();
        Promote(first, view1, winningTaskId, fence1.Version, ownerId);
        Promote(second, view2, losingTaskId, fence2.Version, ownerId);

        await first.SaveChangesAsync(cts.Token);
        Func<Task> losing = () => second.SaveChangesAsync(cts.Token);
        await losing.Should().ThrowAsync<EventStreamUnexpectedMaxEventIdException>(
            "the second promotion must lose at the database, not by luck");

        await using IQuerySession verify = store.QuerySession();
        IdeaDetails? idea = await verify.LoadAsync<IdeaDetails>(ideaId, cts.Token);
        idea!.PromotedTaskId.Should().Be(winningTaskId, "the idea names the one task it became");

        TaskDetails? orphan = await verify.LoadAsync<TaskDetails>(losingTaskId, cts.Token);
        orphan.Should().BeNull("a task whose idea never recorded it would be provenance in one direction only");
    }

    /// <summary>What h9k idea promote writes: the task's first event and the idea's last, fenced together.</summary>
    private static void Promote(
        IDocumentSession session, IdeaAggregate idea, Guid taskId, long version, Guid ownerId)
    {
        IdeaSeed seed = IdeaText.Seed(idea.Text);
        IdeaPromoted promoted = IdeaDecider.Promote(
            idea, taskId, projectId: null, seed.Objective, Now.AddDays(1), ownerId);
        TaskAdded added = TaskDecider.Add(
            taskId, promoted.ProjectId, promoted.Objective, acceptanceCriteria: [], TaskType.Feature,
            seed.Context, constraints: null, externalReference: null, promoted.PromotedAt, ownerId,
            model: null, blockedBy: null, sourceIdeaId: idea.Id);

        session.Events.StartStream<TaskAggregate>(taskId, added);
        session.Events.Append(idea.Id, expectedVersion: version + 1, promoted);
    }
}
