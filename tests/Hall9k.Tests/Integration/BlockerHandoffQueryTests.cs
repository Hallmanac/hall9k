using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Tests.Fakes;
using JasperFx;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// Assembling a dependent's starting context from its BlockedBy edges (Decisions Log #36),
/// against a real store: depth one and no further, the successful run's handoff after a
/// retry, and an honest fallback for every blocker that handed nothing down.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class BlockerHandoffQueryTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private const string PullRequestUrl = "https://github.com/x/y/pull/42";

    /// <summary>
    /// The dead-blocker case #34 left open, pinned. A blocker whose first run died and was
    /// retried to a merge has two runs on one task, and only one of them describes work that
    /// exists — so the handoff the dependent reads must come from the run that merged. The
    /// query never inspects the failed run's text at all: selection is on the run's own
    /// terminal state, which the closeout monitor alone produces.
    /// </summary>
    [Fact]
    public async Task A_retried_blocker_hands_down_the_successful_runs_summary_never_the_failed_ones()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();

        Guid ownerId = DomainId.New();
        Guid blockerId = DomainId.New();
        Guid failedRunId = DomainId.New();
        Guid mergedRunId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<TaskAggregate>(blockerId, TaskSeed.Dispatchable(
                TaskDecider.Add(
                    blockerId, DomainId.New(), "Ship the schema", ["the migration applies"],
                    TaskType.Chore, null, null, null, Now, ownerId),
                ownerId, Now));

            // Attempt one died. It wrote a handoff before failing, and that handoff describes
            // work nobody can build on.
            session.Events.StartStream<RunAggregate>(failedRunId,
                Dispatch(failedRunId, blockerId, ownerId, Now),
                new RunHandoffRecorded(
                    failedRunId, HandoffOutcome.Captured, "I renamed the column to Legacy.", Now),
                new RunFailed(failedRunId, "The gates never passed.", Now));

            // The retry merged an hour later.
            session.Events.StartStream<RunAggregate>(mergedRunId,
                Dispatch(mergedRunId, blockerId, ownerId, Now.AddHours(1)),
                new PullRequestOpened(mergedRunId, PullRequestUrl, 42, Now.AddHours(1)),
                new PullRequestMerged(mergedRunId, Now.AddHours(2), Now.AddHours(2)),
                new RunHandoffRecorded(
                    mergedRunId, HandoffOutcome.Captured, "The column is named Canonical.", Now.AddHours(2)),
                new RunCompleted(mergedRunId, Now.AddHours(2)));

            await session.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<BlockerHandoff> handoffs =
            await BlockerHandoffQuery.LoadAsync(query, [blockerId], cts.Token);

        handoffs.Should().ContainSingle();
        handoffs[0].Summary.Should().Be("The column is named Canonical.",
            "only the run that reached true closeout describes work the dependent can build on");
        handoffs[0].Summary.Should().NotContain("Legacy", "the failed attempt's handoff never travels");
        handoffs[0].HasSummary.Should().BeTrue();
    }

    /// <summary>
    /// The depth-one rule, enforced where context is assembled rather than where the graph is
    /// walked. TaskDependencyQuery still loads the transitive closure for cycle detection at
    /// publish; this reads the first hop and stops (Decisions Log #36).
    /// </summary>
    [Fact]
    public async Task Context_stops_at_the_first_hop_even_when_the_chain_runs_deeper()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();

        Guid ownerId = DomainId.New();
        Guid grandparentId = DomainId.New();
        Guid parentId = DomainId.New();
        Guid childId = DomainId.New();

        // Each hop is committed before the next declares an edge to it: Publish reads the
        // committed graph, and a human declaring a dependency does the same.
        await using (IDocumentSession session = store.LightweightSession())
        {
            SeedClosedOutBlocker(
                session, grandparentId, ownerId, "Ship the schema", TaskDependencyGraph.Empty, [], "Two hops back.");
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskDependencyGraph graph =
                await TaskSeed.DependencyGraphAsync(session, [grandparentId], cts.Token);
            SeedClosedOutBlocker(
                session, parentId, ownerId, "Ship the projection", graph, [grandparentId], "One hop back.");
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskDependencyGraph graph = await TaskSeed.DependencyGraphAsync(session, [parentId], cts.Token);
            session.Events.StartStream<TaskAggregate>(childId, TaskSeed.Dispatchable(
                TaskDecider.Add(
                    childId, DomainId.New(), "Ship the CLI surface", ["it prints"], TaskType.Chore,
                    null, null, null, Now, ownerId, blockedBy: [parentId]),
                ownerId, Now, graph));
            await session.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<BlockerHandoff> handoffs =
            await BlockerHandoffQuery.LoadAsync(query, [parentId], cts.Token);

        handoffs.Should().ContainSingle("the child's own edge names one blocker");
        handoffs[0].Summary.Should().Be("One hop back.");

        string document = BlockerContextDocument.Render(handoffs)!;
        document.Should().NotContain("Two hops back.",
            "a needed two-hop fact is evidence of a missing edge, not a context gap to paper over");
        document.Should().NotContain("Ship the schema");
    }

    [Fact]
    public async Task A_blocker_still_in_flight_reports_a_recorded_absence_and_its_own_intent()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();

        Guid ownerId = DomainId.New();
        Guid blockerId = DomainId.New();
        Guid runId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<TaskAggregate>(blockerId, TaskSeed.Dispatchable(
                TaskDecider.Add(
                    blockerId, DomainId.New(), "Ship the schema", ["the migration applies", "it is indexed"],
                    TaskType.Chore, null, null, null, Now, ownerId),
                ownerId, Now));
            session.Events.StartStream<RunAggregate>(runId, Dispatch(runId, blockerId, ownerId, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<BlockerHandoff> handoffs =
            await BlockerHandoffQuery.LoadAsync(query, [blockerId], cts.Token);

        handoffs.Should().ContainSingle();
        handoffs[0].Outcome.Should().Be(HandoffOutcome.NotClosedOut,
            "no run carried it to true closeout, which is a different absence from one that closed out with nothing to say");
        handoffs[0].HasSummary.Should().BeFalse();
        BlockerContextDocument.Render(handoffs).Should().NotContain("the run that closed this out",
            "a blocker still in flight has no such run, and the document may not imply one");
        handoffs[0].AcceptanceCriteria.Should().Equal("the migration applies", "it is indexed");
    }

    /// <summary>
    /// A run that merged before handoffs existed is the historical case: it closed out, it
    /// unblocks its dependents, and it carries no handoff event. It must read as an absence
    /// with the blocker's intent behind it, not as a broken context assembly.
    /// </summary>
    [Fact]
    public async Task A_pre_handoff_stream_closes_out_and_reads_as_unknown_rather_than_failing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();

        Guid ownerId = DomainId.New();
        Guid blockerId = DomainId.New();
        Guid runId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<TaskAggregate>(blockerId, TaskSeed.Dispatchable(
                TaskDecider.Add(
                    blockerId, DomainId.New(), "Ship the schema", ["the migration applies"],
                    TaskType.Chore, null, null, null, Now, ownerId),
                ownerId, Now));
            session.Events.StartStream<RunAggregate>(runId,
                Dispatch(runId, blockerId, ownerId, Now),
                new PullRequestOpened(runId, PullRequestUrl, 42, Now),
                new PullRequestMerged(runId, Now, Now),
                new RunCompleted(runId, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<BlockerHandoff> handoffs =
            await BlockerHandoffQuery.LoadAsync(query, [blockerId], cts.Token);

        handoffs[0].Outcome.Should().Be(HandoffOutcome.Unknown,
            "a stream written before handoffs existed says it does not know");
        handoffs[0].HasSummary.Should().BeFalse();
        BlockerContextDocument.Render(handoffs).Should().Contain("the migration applies",
            "the fallback is the blocker's own intent, which every blocker has");
    }

    [Fact]
    public async Task An_edge_naming_no_known_task_is_skipped_rather_than_invented()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();

        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<BlockerHandoff> handoffs =
            await BlockerHandoffQuery.LoadAsync(query, [DomainId.New()], cts.Token);

        handoffs.Should().BeEmpty("a dangling edge is the dependency query's story; inventing a blocker here would be a guess");
    }

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    private static void SeedClosedOutBlocker(
        IDocumentSession session, Guid taskId, Guid ownerId, string objective,
        TaskDependencyGraph graph, IReadOnlyList<Guid> blockedBy, string handoff)
    {
        session.Events.StartStream<TaskAggregate>(taskId, TaskSeed.Dispatchable(
            TaskDecider.Add(
                taskId, DomainId.New(), objective, ["merged"], TaskType.Chore,
                null, null, null, Now, ownerId, blockedBy: blockedBy),
            ownerId, Now, graph));

        Guid runId = DomainId.New();
        session.Events.StartStream<RunAggregate>(runId,
            Dispatch(runId, taskId, ownerId, Now),
            new PullRequestOpened(runId, PullRequestUrl, 42, Now),
            new PullRequestMerged(runId, Now, Now),
            new RunHandoffRecorded(runId, HandoffOutcome.Captured, handoff, Now),
            new RunCompleted(runId, Now));
    }

    private static RunDispatched Dispatch(Guid runId, Guid taskId, Guid ownerId, DateTimeOffset at) => new(
        runId, taskId, DomainId.New(), ownerId, 1, DomainId.New(),
        $"/tmp/hall9k-{runId:N}", $"task/{runId:N}", ExecutorMode.Subscription, at);
}
