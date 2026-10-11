using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The enforcement behind the prompt (task: a changes-requested review or an unresolved review thread
/// from someone outside the project's trust-chain roster never reaches a build lap without the owner's
/// go): a lap told to leave a withheld thread, or a review the owner has not decided, alone is also
/// refused by the platform's own posting path when it answers one anyway, whatever the disposition. A
/// fix reply is the case that matters most, because the platform would otherwise post it and resolve
/// the stranger's thread itself after its push. Against a real store and a scripted <c>gh</c> that must
/// never be reached.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class NonMemberReplyRefusalTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private const string PullRequestUrl = "https://github.com/x/y/pull/7";
    private const string WithheldThreadId = "PRRT_withheld";
    private const string MembersThreadId = "PRRT_member";
    private const string HeldReviewUrl = $"{PullRequestUrl}#pullrequestreview-42";

    [Theory]
    [InlineData("fix")]
    [InlineData("decline")]
    [InlineData("route")]
    public async Task A_reply_into_a_thread_the_gate_withheld_is_refused_whatever_the_disposition(string disposition)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid taskId, Guid runId) = await SeedLapAsync(cts.Token);
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");

        await using (IDocumentSession session = postgres.Store.LightweightSession())
        {
            Func<Task> act = () => PullRequestReplyCommand.ReplyAsync(
                session, taskId,
                new PullRequestReplyCommand.Settings
                {
                    Thread = WithheldThreadId, Disposition = disposition, Body = "Fixed in the commit above.",
                },
                new GitHubReviewReplies(gh.Runner), new GitHubPullRequestReviews(gh.Runner), cts.Token);

            (await act.Should().ThrowAsync<DomainValidationException>())
                .WithMessage("*outside this project's roster*");
        }

        gh.Calls.Should().BeEmpty("nothing reached GitHub, and no fix reply was held to be posted after a push");
        await using IQuerySession query = postgres.Store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.RefusedHumanThreadReplies.Should().ContainSingle().Which.ThreadId.Should().Be(WithheldThreadId);
        run.ReviewThreadRepliesPosted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_reply_into_a_thread_the_lap_may_read_is_not_caught_by_the_refusal()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid taskId, Guid runId) = await SeedLapAsync(cts.Token);
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");

        await using IDocumentSession session = postgres.Store.LightweightSession();
        int result = await PullRequestReplyCommand.ReplyAsync(
            session, taskId,
            new PullRequestReplyCommand.Settings
            {
                Thread = MembersThreadId, Disposition = "fix", Body = "Fixed in the commit above.",
            },
            new GitHubReviewReplies(gh.Runner), new GitHubPullRequestReviews(gh.Runner), cts.Token);

        result.Should().Be(0);
        await using IQuerySession query = postgres.Store.QuerySession();
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.HeldReplies.Should().ContainSingle(
            reply => reply.ThreadId == MembersThreadId, "a member's thread keeps its ordinary held fix reply");
    }

    [Fact]
    public async Task An_answer_to_a_review_the_owner_has_not_decided_is_refused_before_it_is_read_from_github()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid taskId, Guid runId) = await SeedLapAsync(cts.Token);
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");

        await using (IDocumentSession session = postgres.Store.LightweightSession())
        {
            Func<Task> act = () => PullRequestReplyCommand.ReplyAsync(
                session, taskId,
                new PullRequestReplyCommand.Settings
                {
                    Review = HeldReviewUrl, Disposition = "fix", Body = "Fixed in the commit above.",
                },
                new GitHubReviewReplies(gh.Runner), new GitHubPullRequestReviews(gh.Runner), cts.Token);

            (await act.Should().ThrowAsync<DomainValidationException>())
                .WithMessage("*outside this project's roster*");
        }

        gh.Calls.Should().BeEmpty("not even the read of the review's author went out");
        await using IQuerySession query = postgres.Store.QuerySession();
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.RefusedReviewBodyReplies.Should().ContainSingle()
            .Which.ReviewUrl.Should().Be(HeldReviewUrl);
    }

    /// <summary>
    /// A review-feedback lap in flight whose recorded read scope withholds one thread, on a task that
    /// also holds one undecided review from outside the roster.
    /// </summary>
    private async Task<(Guid TaskId, Guid RunId)> SeedLapAsync(CancellationToken cancellationToken)
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(postgres.Store, cancellationToken);
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        await using IDocumentSession session = postgres.Store.LightweightSession();

        ProjectRegistered registered = ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), $"refusal-{taskId:N}", "/tmp/refusal-repo", null, "main", Now);
        session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(
                taskId, projectId, "Bound the limiter", ["the limiter resets per window"],
                TaskType.Feature, null, null, null, Now, node.OwnerId),
            node.OwnerId, Now);
        TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, DomainId.New(), Now);
        task.Apply(claimed);
        TaskCompleted completed = TaskDecider.Complete(task, claimed.RunId, PullRequestUrl, Now);
        task.Apply(completed);
        NonMemberFeedbackHeld held = new(
            taskId, HeldReviewUrl, NonMemberFeedbackKind.Review, "stranger", HeldReviewUrl, Now);
        task.Apply(held);
        TaskReopened reopened = TaskDecider.Reopen(
            task, claimed.RunId, "task/limiter", "2 unresolved review threads.",
            FollowUpKind.ReviewFeedback, automatic: true, Now, node.OwnerId,
            reviewReadScope: new ReviewReadScope(
                [new ReadableReviewThread(MembersThreadId, "teammate", FeedbackAdmission.Member, ["PRRC_1"])],
                [WithheldThreadId]));
        task.Apply(reopened);
        TaskClaimed followUpClaim = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now);
        session.Events.StartStream<TaskAggregate>(
            taskId, [.. lifecycle, claimed, completed, held, reopened, followUpClaim]);
        session.Store(new TaskLease
        {
            Id = taskId,
            NodeId = node.NodeId,
            LeaseGeneration = followUpClaim.LeaseGeneration,
            HeartbeatAt = Now,
        });

        session.Events.StartStream<RunAggregate>(
            runId,
            new RunDispatched(
                runId, taskId, node.NodeId, node.OwnerId, followUpClaim.LeaseGeneration, DomainId.New(),
                Path.Combine(Path.GetTempPath(), $"hall9k-refusal-wt-{runId:N}"), "task/limiter",
                ExecutorMode.Subscription, Now, IsFollowUp: true),
            new AgentSessionCompleted(runId, Now));

        await session.SaveChangesAsync(cancellationToken);
        return (taskId, runId);
    }
}
