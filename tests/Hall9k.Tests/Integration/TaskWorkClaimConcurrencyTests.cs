using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// h9k task work's atomic entry from Published (task 688a1ccf-h9k): the assignment and the
/// interactive claim land as one event append, so the database's own optimistic concurrency is
/// what arbitrates a genuine collision — two operators, or an operator racing another owner's
/// plain <c>h9k task assign</c> — to exactly one winner, exactly the way <see cref="TaskClaimConcurrencyTests"/>
/// already proves for the ordinary Queued-entry claim. <see cref="TaskWorkCommand.PrepareInteractiveClaimFromPublished"/>
/// is what each racing side calls to build its own (Assigned, Claimed) pair; this class proves the
/// database decides between them, and that the loser's own honest-race-loss message
/// (<see cref="TaskWorkCommand.DescribeAssignAndClaimRaceLossAsync"/>) names who actually won.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class TaskWorkClaimConcurrencyTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Two_racing_interactive_claims_from_published_produce_exactly_one_winner()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();

        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();
        await SeedOwnerAsync(store, ownerId, "Operator One", cts.Token);
        await SeedPublishedTaskAsync(store, taskId, cts.Token);

        // Two operators (or one operator in two terminals) read the same Published task at the
        // same fence version, then both race to claim it interactively — exactly the collision
        // the old two-step assign-then-claim flow could lose to the dispatcher, now arbitrated by
        // one append per side instead.
        await using IDocumentSession first = store.LightweightSession();
        await using IDocumentSession second = store.LightweightSession();

        StreamState fence = (await first.Events.FetchStreamStateAsync(taskId, cts.Token))!;
        TaskAggregate view1 = (await first.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, version: fence.Version, token: cts.Token))!;
        TaskAggregate view2 = (await second.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, version: fence.Version, token: cts.Token))!;

        Guid runId1 = DomainId.New();
        Guid runId2 = DomainId.New();
        (TaskAssigned assigned1, TaskClaimed claimed1, _) = TaskWorkCommand.PrepareInteractiveClaimFromPublished(
            view1, ownerId, [], runId1, Now, acknowledgeUnmetDependencies: false);
        (TaskAssigned assigned2, TaskClaimed claimed2, _) = TaskWorkCommand.PrepareInteractiveClaimFromPublished(
            view2, ownerId, [], runId2, Now, acknowledgeUnmetDependencies: false);

        long expectedVersion = fence.Version + 2;
        first.Events.Append(taskId, expectedVersion: expectedVersion, assigned1, claimed1);
        second.Events.Append(taskId, expectedVersion: expectedVersion, assigned2, claimed2);

        await first.SaveChangesAsync(cts.Token);
        Func<Task> losing = () => second.SaveChangesAsync(cts.Token);
        await losing.Should().ThrowAsync<EventStreamUnexpectedMaxEventIdException>(
            "the second atomic claim must lose at the database, not by luck");

        await using IQuerySession verify = store.QuerySession();
        TaskAggregate final = (await verify.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        final.State.Should().Be(TaskState.Claimed);
        final.IsInteractiveClaim.Should().BeTrue();
        final.LeaseGeneration.Should().Be(1, "exactly one claim landed, from a fresh task");
        final.CurrentRunId.Should().Be(runId1, "the first append committed and the second never landed");
        final.RunIds.Should().ContainSingle();

        // The loser is told honestly who won, not just that something changed — read back from
        // what actually committed rather than guessed at.
        DomainConflictException raceLoss = await TaskWorkCommand.DescribeAssignAndClaimRaceLossAsync(
            store, taskId, cts.Token);
        raceLoss.Message.Should().Contain("Operator One");
        raceLoss.Message.Should().Contain("claimed it interactively first");
    }

    /// <summary>
    /// The other shape a collision can take: a plain <c>h9k task assign</c> (no claim) commits
    /// first, so the loser's atomic append fails against a task that only ever reached Queued.
    /// <see cref="TaskWorkCommand.DescribeAssignAndClaimRaceLossAsync"/> has nothing to name a
    /// winner by — a bare assignment claims nothing — so it says exactly that rather than
    /// inventing a claimant.
    /// </summary>
    [Fact]
    public async Task A_claim_that_races_a_plain_assign_is_told_the_task_moved_on_without_a_claimant()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();

        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();
        await SeedPublishedTaskAsync(store, taskId, cts.Token);

        await using IDocumentSession claiming = store.LightweightSession();
        await using IDocumentSession assigning = store.LightweightSession();

        StreamState fence = (await claiming.Events.FetchStreamStateAsync(taskId, cts.Token))!;
        TaskAggregate claimView = (await claiming.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, version: fence.Version, token: cts.Token))!;
        TaskAggregate assignView = (await assigning.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, version: fence.Version, token: cts.Token))!;

        (TaskAssigned assigned, TaskClaimed claimed, _) = TaskWorkCommand.PrepareInteractiveClaimFromPublished(
            claimView, ownerId, [], DomainId.New(), Now, acknowledgeUnmetDependencies: false);

        // The plain h9k task assign path: TaskDecider.Assign alone, no claim.
        TaskAssigned plainAssign = TaskDecider.Assign(assignView, ownerId, [], Now, ownerId);
        assigning.Events.Append(taskId, expectedVersion: fence.Version + 1, plainAssign);
        await assigning.SaveChangesAsync(cts.Token);

        claiming.Events.Append(taskId, expectedVersion: fence.Version + 2, assigned, claimed);
        Func<Task> losing = () => claiming.SaveChangesAsync(cts.Token);
        await losing.Should().ThrowAsync<EventStreamUnexpectedMaxEventIdException>(
            "the plain assign landed first, so the atomic claim's own expected version is already stale");

        await using IQuerySession verify = store.QuerySession();
        TaskAggregate final = (await verify.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        final.State.Should().Be(TaskState.Queued, "only the plain assign landed — nothing claimed it");

        DomainConflictException raceLoss = await TaskWorkCommand.DescribeAssignAndClaimRaceLossAsync(
            store, taskId, cts.Token);
        raceLoss.Message.Should().Contain("Queued", "nothing claimed it, so there is no winner to name");
        raceLoss.Message.Should().NotContain("claimed by", "the plain assign never claimed the task, so nothing claims that it did");
    }

    private static async Task SeedPublishedTaskAsync(DocumentStore store, Guid taskId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        TaskAdded added = TaskDecider.Add(
            taskId, DomainId.New(), "Prove the atomic claim's collision is arbitrated by the store",
            ["exactly one claim survives a genuine race"], TaskType.Chore,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: DomainId.New());
        TaskAggregate task = new();
        task.Apply(added);
        TaskPublished published = TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, DomainId.New());

        session.Events.StartStream<TaskAggregate>(taskId, added, published);
        await session.SaveChangesAsync(cancellationToken);
    }

    private static async Task SeedOwnerAsync(
        DocumentStore store, Guid ownerId, string name, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<OwnerAggregate>(ownerId, OwnerDecider.Register(ownerId, name, null, Now));
        await session.SaveChangesAsync(cancellationToken);
    }

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });
}
