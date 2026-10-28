using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// What <c>h9k task drive</c> reads from the record before it claims a task whose pull request is open,
/// against a real store: a Done task and a reopened lap that has not started both meet the claim now, where
/// the follow-up branch used to refuse it outright. No repository, branch, or GitHub is touched.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class PullRequestStageDriveTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private const string Branch = "task/abc12345-thing";
    private const string PullRequestUrl = "https://github.com/acme/web/pull/42";

    private async Task<(Guid TaskId, Guid RunId, Guid OwnerId)> SeedAsync(bool reopened, string? failureReason = null, bool merged = false)
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid nodeId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            taskId, DomainId.New(), "Bound the limiter", ["it resets per window"], TaskType.Feature, null, null, null, Now, ownerId);
        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(added, ownerId, Now);
        List<object> events = [.. lifecycle];
        TaskClaimed claimed = TaskDecider.Claim(task, nodeId, ownerId, runId, Now);
        task.Apply(claimed);
        events.Add(claimed);
        TaskCompleted completed = TaskDecider.Complete(task, runId, PullRequestUrl, Now);
        task.Apply(completed);
        events.Add(completed);
        if (reopened)
        {
            events.Add(TaskDecider.Reopen(
                task, runId, Branch, "CI failing", FollowUpKind.FailingChecks, automatic: true, Now, ownerId));
        }

        await using IDocumentSession session = postgres.Store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, [.. events]);
        List<object> runEvents =
        [
            new RunDispatched(
                runId, taskId, nodeId, ownerId, 1, DomainId.New(), "/wt/none", Branch, ExecutorMode.Subscription, Now),
            new AgentSessionCompleted(runId, Now),
            new VerificationPassed(runId, Now),
            new PullRequestOpened(runId, PullRequestUrl, 42, Now),
        ];
        if (merged)
        {
            runEvents.Add(new PullRequestMerged(runId, Now, Now));
            runEvents.Add(new RunCompleted(runId, Now));
        }

        session.Events.StartStream<RunAggregate>(runId, [.. runEvents]);
        await session.SaveChangesAsync();
        return (taskId, runId, ownerId);
    }

    private async Task<TaskDriveCommand.PullRequestStageClaim> PrepareAsync(Guid taskId)
    {
        await using IDocumentSession session = postgres.Store.LightweightSession();
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId))!;
        TaskDetails details = (await session.LoadAsync<TaskDetails>(taskId))!;
        return await TaskDriveCommand.PreparePullRequestStageAsync(session, task, details, CancellationToken.None);
    }

    [Fact]
    public async Task A_done_task_with_an_open_pull_request_is_prepared_on_the_pull_requests_branch()
    {
        (Guid taskId, Guid runId, _) = await SeedAsync(reopened: false);

        TaskDriveCommand.PullRequestStageClaim stage = await PrepareAsync(taskId);

        stage.PullRequestRun.Id.Should().Be(runId);
        stage.Branch.Should().Be(Branch);
        stage.PullRequestIsOpen.Should().BeTrue();
        stage.WasReopenedLap.Should().BeFalse();
        stage.LiveHeadlessSession.Should().BeNull();
    }

    [Fact]
    public async Task A_reopened_lap_that_has_not_started_is_no_longer_refused_for_its_follow_up_branch()
    {
        (Guid taskId, _, _) = await SeedAsync(reopened: true);

        TaskDriveCommand.PullRequestStageClaim stage = await PrepareAsync(taskId);

        stage.WasReopenedLap.Should().BeTrue();
        stage.Branch.Should().Be(Branch, "the branch the reopen recorded");
    }

    [Fact]
    public async Task A_merged_pull_request_is_read_as_closed_to_iteration()
    {
        (Guid taskId, _, _) = await SeedAsync(reopened: false, merged: true);

        TaskDriveCommand.PullRequestStageClaim stage = await PrepareAsync(taskId);

        stage.PullRequestIsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task A_task_with_no_recorded_pull_request_run_is_refused_with_a_way_forward()
    {
        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            taskId, DomainId.New(), "Not built", ["it works"], TaskType.Chore, null, null, null, Now, ownerId);
        (_, object[] lifecycle) = TaskSeed.Start(added, ownerId, Now);
        await using (IDocumentSession seed = postgres.Store.LightweightSession())
        {
            seed.Events.StartStream<TaskAggregate>(taskId, lifecycle);
            await seed.SaveChangesAsync();
        }

        Func<Task> act = () => PrepareAsync(taskId);

        await act.Should().ThrowAsync<DomainConflictException>().WithMessage("*no recorded run for its pull request*");
    }
}
