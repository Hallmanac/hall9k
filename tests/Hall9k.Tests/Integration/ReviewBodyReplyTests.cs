using System.Text.Json;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Processes;
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
using Hall9k.Tests.TestSupport;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The review-body form of <c>h9k pr reply</c> (task: a dispatched session never speaks to a person
/// at the top level of a pull request on its own), against a real store and a scripted <c>gh</c>.
/// A review BODY is unthreadable, so its answer is a top-level comment, which used to be the one
/// reply route that skipped the question every other route asks: whose words am I answering?
/// <para>
/// The scripted <c>gh</c> answers the REST review read with whatever author the test chose and
/// accepts a <c>gh pr comment</c>, so each test can assert both what GitHub was asked and what was
/// posted.
/// </para>
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class ReviewBodyReplyTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 11, 30, 0, TimeSpan.Zero);

    private const string PullRequestUrl = "https://github.com/x/y/pull/2042";

    private const string ReviewUrl = $"{PullRequestUrl}#pullrequestreview-345";

    private const string DraftedReply =
        "The sentinel already means unset in this projection, so a distinct canary is what keeps the two apart.";

    [Fact]
    public async Task A_fix_answer_to_a_persons_review_body_is_held_after_the_conventions_check_and_posts_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid taskId, Guid runId) = await SeedAsync(cts.Token);
        RecordingProcessRunner gh = ScriptedGitHub(ReviewJson("jsmotherman", "User"));

        await ReplyAsync(taskId, "fix", "Every point is fixed above \u2014 the sentinel is reused now.", gh, cts.Token);

        gh.Calls.Select(call => call.Arguments[0]).Should().OnlyContain(
            first => first == "api", "only the author read reached GitHub; the comment waits for the push");

        await using IQuerySession query = postgres.Store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewBodyRepliesPosted.Should().BeEmpty();
        HeldReplyRecord held = run.HeldReplies.Should().ContainSingle().Subject;
        held.ReviewUrl.Should().Be(ReviewUrl);
        held.ThreadId.Should().BeNull();
        held.Disposition.Should().Be(ReviewThreadDisposition.Fix);
        held.TargetIsHumanAuthored.Should().BeTrue("the author came from GitHub's own record");
        held.Body.Should().NotContain("\u2014", "the writing-conventions check ran before the hold");
        run.RefusedReviewBodyReplies.Should().BeEmpty();
    }

    [Theory]
    [InlineData("decline")]
    [InlineData("route")]
    public async Task A_decline_or_route_on_a_persons_review_body_posts_nothing_and_is_recorded_as_refused(
        string disposition)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid taskId, Guid runId) = await SeedAsync(cts.Token);
        RecordingProcessRunner gh = ScriptedGitHub(ReviewJson("jsmotherman", "User"));

        Func<Task> act = () => ReplyAsync(taskId, disposition, "It already handles that case.", gh, cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>()).WithMessage($"*{ReviewUrl}*");
        gh.Calls.Should().ContainSingle("only the read reached GitHub; the refusal is ahead of the write")
            .Which.Arguments[0].Should().Be("api");

        await using IQuerySession query = postgres.Store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        RefusedReviewBodyReplyRecord refused = run.RefusedReviewBodyReplies.Should().ContainSingle().Subject;
        refused.ReviewUrl.Should().Be(ReviewUrl, "the url as GitHub reported it");
        refused.Author.Should().Be("jsmotherman");
        refused.Disposition.Value.ToLowerInvariant().Should().Be(disposition);
        run.ReviewBodyRepliesPosted.Should().BeEmpty();

        string rendered = string.Join('\n', TaskShowCommand.ComposeHumanThreadReplyDrafts([run]));
        rendered.Should().Contain("refused:").And.Contain(ReviewUrl).And.Contain("nothing was sent");
    }

    [Fact]
    public async Task A_decline_on_a_bots_review_body_posts()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid taskId, Guid runId) = await SeedAsync(cts.Token);
        RecordingProcessRunner gh = ScriptedGitHub(ReviewJson("copilot-pull-request-reviewer[bot]", "Bot"));

        await ReplyAsync(taskId, "decline", "Reproduced in a scratch repo: the ref is updated already.", gh, cts.Token);

        gh.Calls.Select(call => call.Arguments[0]).Should().Equal("api", "pr");
        await using IQuerySession query = postgres.Store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ReviewBodyRepliesPosted.Should().ContainSingle().Which.ReviewIsHumanAuthored.Should().BeFalse();
        run.RefusedReviewBodyReplies.Should().BeEmpty();
    }

    [Fact]
    public async Task A_review_returned_with_no_readable_author_is_treated_as_a_persons()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid taskId, Guid runId) = await SeedAsync(cts.Token);
        RecordingProcessRunner gh = ScriptedGitHub(ReviewJson(null, "User"));

        Func<Task> act = () => ReplyAsync(taskId, "decline", "No.", gh, cts.Token);

        await act.Should().ThrowAsync<DomainValidationException>();
        gh.Calls.Should().ContainSingle();
        await using IQuerySession query = postgres.Store.QuerySession();
        RefusedReviewBodyReplyRecord refused = (await query.LoadAsync<RunDetails>(runId, cts.Token))!
            .RefusedReviewBodyReplies.Should().ContainSingle().Subject;
        refused.Author.Should().BeNull("no author was reported, and none is invented");
    }

    [Fact]
    public async Task A_review_the_command_cannot_read_is_refused_with_nothing_posted_and_no_record()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid taskId, Guid runId) = await SeedAsync(cts.Token);
        RecordingProcessRunner gh = RecordingProcessRunner.Failing("gh: Not Found (HTTP 404)");

        Func<Task> act = () => ReplyAsync(taskId, "fix", "Fixed above.", gh, cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>()).WithMessage("*Nothing was posted*");
        gh.Calls.Should().ContainSingle("the read failed, so no write was attempted");
        await AssertNothingRecordedAsync(runId, cts.Token);
    }

    [Theory]
    [InlineData("https://github.com/x/y/pull/99#pullrequestreview-345", "another pull request")]
    [InlineData("https://github.com/other/repo/pull/2042#pullrequestreview-345", "another repository")]
    [InlineData("https://github.com/x/y/pull/2042#discussion_r9", "not a review at all")]
    [InlineData("not a url", "not a url")]
    public async Task A_review_url_that_is_not_on_the_tasks_own_pull_request_is_refused_before_any_read(
        string reviewUrl, string because)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid taskId, Guid runId) = await SeedAsync(cts.Token);
        RecordingProcessRunner gh = ScriptedGitHub(ReviewJson("jsmotherman", "User"));

        Func<Task> act = () => ReplyAsync(taskId, "fix", "Fixed above.", gh, cts.Token, reviewUrl);

        (await act.Should().ThrowAsync<DomainValidationException>(because)).WithMessage("*Nothing was posted*");
        gh.Calls.Should().BeEmpty(because);
        await AssertNothingRecordedAsync(runId, cts.Token);
    }

    [Fact]
    public async Task A_review_github_places_on_a_different_pull_request_is_refused_with_no_record()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid taskId, Guid runId) = await SeedAsync(cts.Token);
        RecordingProcessRunner gh = ScriptedGitHub(ReviewJson("jsmotherman", "User", pullRequestNumber: 99));

        Func<Task> act = () => ReplyAsync(taskId, "fix", "Fixed above.", gh, cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>()).WithMessage("*did not place review*");
        gh.Calls.Should().ContainSingle();
        await AssertNothingRecordedAsync(runId, cts.Token);
    }

    [Fact]
    public async Task Post_reply_as_written_on_a_review_body_park_posts_one_top_level_comment_naming_the_review()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid taskId, Guid runId) = await SeedAsync(cts.Token, parkedBodyDraft: true);
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");

        await ResolveAsync(
            taskId, new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true }, gh, cts.Token);

        (string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory) call =
            gh.Calls.Should().ContainSingle("the reviewer hears it once").Subject;
        call.Arguments.Should().Contain("comment").And.Contain("2042");
        call.Arguments.Should().Contain($"On {ReviewUrl}:\n\n{DraftedReply}");

        await using IQuerySession query = postgres.Store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        ReviewDisagreementReplyDirection directed = run.ChangesRequestedReplyDirections.Should().ContainSingle().Subject;
        directed.Choice.Should().Be(ReviewDisagreementReplyChoice.AsWritten);
        directed.PostedTarget.Should().Be(PullRequestUrl);

        string rendered = string.Join('\n', TaskShowCommand.ComposeHumanThreadReplyDrafts([run]));
        rendered.Should().Contain($"review body {ReviewUrl}");
        rendered.Should().Contain("you sent the drafted reply as written");
    }

    [Fact]
    public async Task Task_show_renders_a_parked_review_body_draft_whole_and_unsent()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (_, Guid runId) = await SeedAsync(cts.Token, parkedBodyDraft: true);

        await using IQuerySession query = postgres.Store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        string rendered = string.Join('\n', TaskShowCommand.ComposeHumanThreadReplyDrafts([run]));

        rendered.Should().Contain($"review body {ReviewUrl}");
        rendered.Should().Contain(DraftedReply, "the whole draft, not an excerpt");
        rendered.Should().Contain("not posted — the reviewer has heard nothing");
    }

    /// <summary>
    /// The review url on a parked draft is the session's word, and it is the one thing the sent
    /// comment vouches for to a real reviewer. With no refusal record behind it and no
    /// changes-requested read of it, the send is refused and nothing is posted.
    /// </summary>
    [Fact]
    public async Task A_parked_draft_naming_a_review_nothing_read_is_not_posted()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid taskId, _) = await SeedAsync(cts.Token, parkedBodyDraft: true, recordRefusal: false);
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await using IDocumentSession session = postgres.Store.LightweightSession();

        Func<Task> act = () => ReviewResolveCommand.ResolveAsync(
            session, taskId, new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true },
            new GitHubReviewReplies(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>()).WithMessage("*Nothing has been posted*");
        gh.Calls.Should().BeEmpty();
    }

    [Fact]
    public void The_form_takes_a_thread_or_a_review_and_never_both_or_neither()
    {
        new PullRequestReplyCommand.Settings { Disposition = "fix", Body = "x" }.Validate().Successful.Should().BeFalse();
        new PullRequestReplyCommand.Settings { Thread = "PRRT_1", Review = ReviewUrl, Disposition = "fix", Body = "x" }
            .Validate().Successful.Should().BeFalse();
        new PullRequestReplyCommand.Settings { Review = ReviewUrl, Disposition = "fix", Body = "x" }
            .Validate().Successful.Should().BeTrue();
        new PullRequestReplyCommand.Settings { Thread = "PRRT_1", Disposition = "fix", Body = "x" }
            .Validate().Successful.Should().BeTrue();
    }

    private async Task ReplyAsync(
        Guid taskId, string disposition, string body, RecordingProcessRunner gh, CancellationToken cancellationToken,
        string review = ReviewUrl)
    {
        await using IDocumentSession session = postgres.Store.LightweightSession();
        int result = await PullRequestReplyCommand.ReplyAsync(
            session, taskId,
            new PullRequestReplyCommand.Settings { Review = review, Disposition = disposition, Body = body },
            new GitHubReviewReplies(gh.Runner), new GitHubPullRequestReviews(gh.Runner), cancellationToken);
        result.Should().Be(0);
    }

    private async Task AssertNothingRecordedAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = postgres.Store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cancellationToken))!;
        run.RefusedReviewBodyReplies.Should().BeEmpty(
            "a review GitHub never reported is not something a later park may treat as GitHub's report");
        run.ReviewBodyRepliesPosted.Should().BeEmpty();
        run.HeldReplies.Should().BeEmpty("a review the command could not vouch for is not held either");
    }

    private async Task ResolveAsync(
        Guid taskId, ReviewResolveCommand.Settings settings, RecordingProcessRunner gh,
        CancellationToken cancellationToken)
    {
        using ScopedConnectionString scope = new(postgres.ConnectionString);
        await using IDocumentSession session = postgres.Store.LightweightSession();
        int result = await ReviewResolveCommand.ResolveAsync(
            session, taskId, settings, new GitHubReviewReplies(gh.Runner), cancellationToken);
        result.Should().Be(0);
    }

    /// <summary>A <c>gh</c> that answers the REST review read with <paramref name="reviewJson"/> and accepts a comment.</summary>
    private static RecordingProcessRunner ScriptedGitHub(string reviewJson) =>
        new(arguments => new ProcessResult(0, arguments[0] == "api" ? reviewJson : string.Empty, string.Empty));

    private static string ReviewJson(string? login, string type, int pullRequestNumber = 2042) =>
        JsonSerializer.Serialize(new
        {
            id = 345,
            html_url = $"https://github.com/x/y/pull/{pullRequestNumber}#pullrequestreview-345",
            pull_request_url = $"https://api.github.com/repos/x/y/pulls/{pullRequestNumber}",
            user = login is null ? null : new { login, type },
        });

    /// <summary>
    /// A follow-up lap on a pull request, optionally already parked with the draft the lap wrote
    /// for a person's review body, with or without the refusal record the posting path would have
    /// left behind it.
    /// </summary>
    private async Task<(Guid TaskId, Guid RunId)> SeedAsync(
        CancellationToken cancellationToken, bool parkedBodyDraft = false, bool recordRefusal = true)
    {
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cancellationToken);
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();

        await using IDocumentSession session = store.LightweightSession();
        ProjectRegistered registered = ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), $"body-reply-{taskId:N}", "/tmp/body-reply-repo", null, "main", Now);
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
        TaskReopened reopened = TaskDecider.Reopen(
            task, claimed.RunId, "task/limiter", "A review body asks for a change.",
            FollowUpKind.ReviewFeedback, automatic: true, Now, node.OwnerId);
        task.Apply(reopened);
        TaskClaimed followUpClaim = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now);
        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed, completed, reopened, followUpClaim]);
        session.Store(new TaskLease
        {
            Id = taskId,
            NodeId = node.NodeId,
            LeaseGeneration = followUpClaim.LeaseGeneration,
            HeartbeatAt = Now,
        });

        List<object> runEvents =
        [
            new RunDispatched(
                runId, taskId, node.NodeId, node.OwnerId, followUpClaim.LeaseGeneration, DomainId.New(),
                Path.Combine(Path.GetTempPath(), $"hall9k-body-reply-wt-{runId:N}"), "task/limiter",
                ExecutorMode.Subscription, Now, IsFollowUp: true),
            new AgentSessionCompleted(runId, Now),
        ];
        if (parkedBodyDraft)
        {
            if (recordRefusal)
            {
                runEvents.Add(new ReviewBodyReplyRefused(
                    runId, ReviewUrl, "jsmotherman", ReviewThreadDisposition.Decline, "refused", Now));
            }

            runEvents.Add(new HumanThreadReplyParked(
                runId,
                [
                    new ReviewDisagreement(
                        "why a canary value rather than reusing the sentinel",
                        "the sentinel already means unset in this projection",
                        DraftedReply,
                        ReviewUrl: ReviewUrl,
                        Disposition: ReviewThreadDisposition.Decline),
                ],
                Now));
            runEvents.Add(new ReviewParked(runId, "A follow-up answered a review a person wrote.", Now));
        }

        session.Events.StartStream<RunAggregate>(runId, [.. runEvents]);
        await session.SaveChangesAsync(cancellationToken);
        return (taskId, runId);
    }
}
