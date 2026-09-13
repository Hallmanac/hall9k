using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Dispatch;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The widened queue read, end to end (Decisions Log #188):
/// <see cref="TaskListItem.FollowUpBranch"/> and <see cref="TaskListItem.RetryBranch"/>, set by
/// real events on a real stream and selected straight off <c>DispatchEngine.ReadQueueAsync</c>'s
/// own six-column projection, reach <see cref="ProjectRotation.NextSlot"/> as a
/// <see cref="TaskRank"/> and decide the claim. This is the one integration test the ranking
/// task's own acceptance criteria caps this at; every other rank scenario is
/// <see cref="Hall9k.Tests.Domain.ProjectRotationTests"/>'s own, pure and database-free.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class RankedQueueDispatchTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_follow_up_lap_reopened_a_minute_ago_takes_the_slot_ahead_of_a_first_claim_assigned_two_hours_earlier()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        // This class's own database, wiped before the one test in it: the assertion below is a
        // ceiling of exactly one claim, and a sibling class's leftover lease would be counted as
        // one of them (the same reason DispatchCeilingTests and ProjectRunCeilingDispatchTests
        // each start from FreshNodeAsync's identical reset).
        await store.Advanced.ResetAllData(cts.Token);
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        DispatchEngine engine = new(
            store, node, new DaemonConnection(postgres.ConnectionString), new FakeProcessManager(),
            new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance),
            Options.Create(new DaemonOptions { MaxConcurrentTaskRuns = 1, LeaseTimeout = TimeSpan.FromSeconds(60) }),
            NullLogger<DispatchEngine>.Instance);

        Guid projectId = DomainId.New();

        // The first claim: never touched before, assigned two hours before this sweep runs —
        // older, by assignment, than the lap below. Age alone — the ordering this queue used
        // before this task — would hand the slot to this row; rank must not let that happen.
        Guid firstClaimId = DomainId.New();
        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<TaskAggregate>(firstClaimId, TaskSeed.Dispatchable(
                TaskDecider.Add(
                    firstClaimId, projectId, "A brand-new first claim", ["done"], TaskType.Chore,
                    null, null, null, Now.AddHours(-2), node.OwnerId),
                node.OwnerId, Now.AddHours(-2)));
            await seed.SaveChangesAsync(cts.Token);
        }

        // The follow-up lap: assigned an hour ago (younger, by assignment, than the first claim
        // above), claimed, completed with a pull request, and reopened for review feedback a
        // minute ago.
        Guid lapId = DomainId.New();
        await using (IDocumentSession seed = store.LightweightSession())
        {
            (TaskAggregate task, object[] events) = TaskSeed.Start(
                TaskDecider.Add(
                    lapId, projectId, "A task on its second lap", ["done"], TaskType.Chore,
                    null, null, null, Now.AddHours(-1), node.OwnerId),
                node.OwnerId, Now.AddHours(-1));
            seed.Events.StartStream<TaskAggregate>(lapId, events);

            Guid firstRunId = DomainId.New();
            TaskClaimed firstClaimEvent = TaskDecider.Claim(task, node.NodeId, node.OwnerId, firstRunId, Now.AddHours(-1));
            task.Apply(firstClaimEvent);
            seed.Events.Append(lapId, firstClaimEvent);

            TaskCompleted completed = TaskDecider.Complete(
                task, firstRunId, "https://github.com/example/hall9k/pull/1", Now.AddMinutes(-50));
            task.Apply(completed);
            seed.Events.Append(lapId, completed);

            TaskReopened reopened = TaskDecider.Reopen(
                task, firstRunId, "task/second-lap", "Unresolved review comments.",
                FollowUpKind.ReviewFeedback, automatic: false, Now.AddMinutes(-1), node.OwnerId);
            task.Apply(reopened);
            seed.Events.Append(lapId, reopened);

            await seed.SaveChangesAsync(cts.Token);
        }

        IReadOnlyList<ClaimedWork> claimed = await engine.ClaimEligibleAsync(cts.Token);

        claimed.Select(work => work.TaskId).Should().Equal(
            [lapId],
            "a follow-up lap past its first pull request outranks a first claim, whatever either "
            + "task's own assignment age says");

        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<TaskListItem>(firstClaimId, cts.Token))!.State.Value.Should().Be(
            "Queued", "the ceiling of one leaves the first claim waiting for the next free slot");
    }
}
