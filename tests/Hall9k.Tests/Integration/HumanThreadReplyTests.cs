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
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The operator lever between "a person asked" and "something posted as the owner" (task: a
/// review-feedback follow-up never answers a human reviewer in the owner's name on its own).
/// Two halves, both against a real store and a scripted <c>gh</c>: the three things the owner can
/// do with a drafted reply, and the posting path that refuses a session trying to skip them.
/// <para>
/// Origin incidents, arx-platform PR #2021 (2026-09-09) and PR #2042 (2026-09-15): accurate
/// replies to two people, posted under Brian's login minutes after each approved, with no step
/// where he saw the words first.
/// </para>
/// </summary>
// ReviewResolveCommand's merge-ready path rings the doorbell (Hall9k.Cli.Infrastructure.Doorbell),
// which resolves its connection off HALL9K_CONNECTION_STRING rather than this fixture, so this
// joins the collection every other test that redirects a process-wide variable does.
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class HumanThreadReplyTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 11, 30, 0, TimeSpan.Zero);

    private const string PullRequestUrl = "https://github.com/x/y/pull/2042";

    private const string HumanThreadId = "PRRT_human";

    private const string HumanThreadUrl = $"{PullRequestUrl}#discussion_r9";

    private const string BotThreadId = "PRRT_bot";

    /// <summary>
    /// Clean against the platform's default writing conventions on purpose: every arm below
    /// asserts the posted body verbatim, and a draft carrying an em dash would be testing the
    /// convention rewrite rather than the choice it was written for.
    /// </summary>
    private const string DraftedReply =
        "The sentinel already means unset in this projection, so a distinct canary is what keeps the two apart.";

    /// <summary>Choice one: the words the lap drafted, verbatim, in the reviewer's own thread.</summary>
    [Fact]
    public async Task Post_reply_as_written_posts_the_drafted_text_once()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid taskId, Guid runId) = await SeedParkedReplyAsync(cts.Token);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await ResolveAsync(
            taskId, new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true }, gh, cts.Token);

        (string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory) call =
            gh.Calls.Should().ContainSingle("the reviewer hears it once, not once per retry").Subject;
        call.Arguments.Should().Contain($"threadId={HumanThreadId}");
        call.Arguments.Should().Contain($"body={DraftedReply}");

        await using IQuerySession query = postgres.Store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        ReviewDisagreementReplyDirection directed =
            run.ChangesRequestedReplyDirections.Should().ContainSingle().Subject;
        directed.Choice.Should().Be(ReviewDisagreementReplyChoice.AsWritten);
        directed.PostedBody.Should().Be(DraftedReply);
        directed.PostedTarget.Should().Be(HumanThreadId);
    }

    /// <summary>Choice two: the owner's own words instead, which is what a trimmed clause looks like from here.</summary>
    [Fact]
    public async Task Post_reply_posts_the_edited_text_once_and_never_the_draft()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid taskId, Guid runId) = await SeedParkedReplyAsync(cts.Token);
        const string edited = "Deliberate: the sentinel already means unset here.";

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await ResolveAsync(
            taskId, new ReviewResolveCommand.Settings { MergeReady = true, PostReply = edited }, gh, cts.Token);

        (string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory) call =
            gh.Calls.Should().ContainSingle().Subject;
        call.Arguments.Should().Contain($"body={edited}");
        call.Arguments.Should().NotContain($"body={DraftedReply}", "the draft is what the owner replaced");

        await using IQuerySession query = postgres.Store.QuerySession();
        ReviewDisagreementReplyDirection directed = (await query.LoadAsync<RunDetails>(runId, cts.Token))!
            .ChangesRequestedReplyDirections.Should().ContainSingle().Subject;
        directed.Choice.Should().Be(ReviewDisagreementReplyChoice.Edited);
        directed.PostedBody.Should().Be(edited);
    }

    /// <summary>
    /// Choice three, and the one the first origin incident needed: the owner answers by hand, or
    /// not at all. Nothing is sent, the thread stays open, and the run moves on regardless.
    /// </summary>
    [Fact]
    public async Task Post_nothing_posts_nothing_and_leaves_the_thread_for_the_human()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid taskId, Guid runId) = await SeedParkedReplyAsync(cts.Token);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await ResolveAsync(
            taskId, new ReviewResolveCommand.Settings { MergeReady = true, PostNothing = true }, gh, cts.Token);

        gh.Calls.Should().BeEmpty("the reviewer hears nothing, and no thread is resolved on their behalf");

        await using IQuerySession query = postgres.Store.QuerySession();
        ReviewDisagreementReplyDirection directed = (await query.LoadAsync<RunDetails>(runId, cts.Token))!
            .ChangesRequestedReplyDirections.Should().ContainSingle().Subject;
        directed.Choice.Should().Be(ReviewDisagreementReplyChoice.Nothing);
        directed.PostedBody.Should().BeNull();
        directed.PostedTarget.Should().BeNull();
    }

    /// <summary>
    /// The park's own contract: a verdict alone will not move the run on, because the question it
    /// is asking is not "is this mergeable" but "what does the person hear".
    /// </summary>
    [Fact]
    public async Task A_verdict_with_no_reply_choice_is_refused_and_posts_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid taskId, _) = await SeedParkedReplyAsync(cts.Token);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await using IDocumentSession session = postgres.Store.LightweightSession();
        Func<Task> act = () => ReviewResolveCommand.ResolveAsync(
            session, taskId, new ReviewResolveCommand.Settings { MergeReady = true },
            new GitHubReviewReplies(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>())
            .WithMessage("*or routed a thread they opened*");
        gh.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// The enforcement half (criterion: the daemon, not only the prompt). A session that ignores
    /// its prompt and reaches for the platform's own posting path is refused, nothing reaches
    /// GitHub, and the attempt is on the run stream with the thread id in it.
    /// </summary>
    [Theory]
    [InlineData("decline")]
    [InlineData("route")]
    public async Task A_decline_or_route_into_a_human_thread_is_refused_and_recorded(string disposition)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid taskId, Guid runId) = await SeedParkedReplyAsync(cts.Token, parked: false);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await using (IDocumentSession session = postgres.Store.LightweightSession())
        {
            Func<Task> act = () => PullRequestReplyCommand.ReplyAsync(
                session, taskId,
                new PullRequestReplyCommand.Settings
                {
                    Thread = HumanThreadId, Disposition = disposition, Body = "It already handles that case.",
                },
                new GitHubReviewReplies(gh.Runner), cts.Token);

            (await act.Should().ThrowAsync<DomainValidationException>())
                .WithMessage($"*{HumanThreadId}*");
        }

        gh.Calls.Should().BeEmpty("the refusal happens before the provider write, so nothing half-reached anyone");

        await using IQuerySession query = postgres.Store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        RefusedThreadReplyRecord refused = run.RefusedHumanThreadReplies.Should().ContainSingle().Subject;
        refused.ThreadId.Should().Be(HumanThreadId, "a refusal that does not name the thread cannot be checked");
        refused.Disposition.Value.ToLowerInvariant().Should().Be(disposition);
        run.ReviewThreadRepliesPosted.Should().BeEmpty();
    }

    /// <summary>
    /// The window between the claim that records a run id and the dispatch that opens its stream:
    /// appending into it would have Marten silently create a run stream holding a reply record
    /// with no RunDispatched under it, an invalid run history. The same existence fence
    /// h9k task log-interaction takes, and the reply is refused before it reaches GitHub rather
    /// than after (Copilot, PR #397).
    /// </summary>
    [Fact]
    public async Task A_reply_against_a_run_whose_stream_does_not_exist_yet_is_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid taskId, Guid runId) = await SeedParkedReplyAsync(
            cts.Token, parked: false, runStreamStarted: false);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await using IDocumentSession session = postgres.Store.LightweightSession();
        Func<Task> act = () => PullRequestReplyCommand.ReplyAsync(
            session, taskId,
            new PullRequestReplyCommand.Settings
            {
                Thread = BotThreadId, Disposition = "fix", Body = "Renamed it in the commit above.",
            },
            new GitHubReviewReplies(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>()).WithMessage($"*{runId}*");
        gh.Calls.Should().BeEmpty("the fence is ahead of the provider write, so nothing half-reached anyone");
    }

    /// <summary>
    /// Decisions Log #159 is untouched: a bot's thread gets its evidence and its resolve on the
    /// automated path it has always been on, decline included.
    /// </summary>
    [Fact]
    public async Task A_decline_into_a_bot_thread_still_posts()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid taskId, Guid runId) = await SeedParkedReplyAsync(cts.Token, parked: false);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await using (IDocumentSession session = postgres.Store.LightweightSession())
        {
            int result = await PullRequestReplyCommand.ReplyAsync(
                session, taskId,
                new PullRequestReplyCommand.Settings
                {
                    Thread = BotThreadId,
                    Disposition = "decline",
                    Body = "Reproduced in a scratch repo: the ref is updated already.",
                },
                new GitHubReviewReplies(gh.Runner), cts.Token);
            result.Should().Be(0);
        }

        gh.Calls.Should().ContainSingle().Which.Arguments.Should().Contain($"threadId={BotThreadId}");

        await using IQuerySession query = postgres.Store.QuerySession();
        ReviewThreadReplyRecord posted = (await query.LoadAsync<RunDetails>(runId, cts.Token))!
            .ReviewThreadRepliesPosted.Should().ContainSingle().Subject;
        posted.ThreadIsHumanAuthored.Should().BeFalse("closeout never read this thread as a person's");
    }

    /// <summary>
    /// A fix's reply still reaches a person's thread, because "I changed it, here is what
    /// changed" invites no argument and the commit is its evidence. The claimed disposition is
    /// recorded so a false one is checkable afterwards.
    /// </summary>
    [Fact]
    public async Task A_fix_reply_into_a_human_thread_still_posts_and_records_the_claim()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid taskId, Guid runId) = await SeedParkedReplyAsync(cts.Token, parked: false);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await using (IDocumentSession session = postgres.Store.LightweightSession())
        {
            int result = await PullRequestReplyCommand.ReplyAsync(
                session, taskId,
                new PullRequestReplyCommand.Settings
                {
                    Thread = HumanThreadId, Disposition = "fix", Body = "Renamed it in the commit above.",
                },
                new GitHubReviewReplies(gh.Runner), cts.Token);
            result.Should().Be(0);
        }

        gh.Calls.Should().ContainSingle().Which.Arguments.Should().Contain($"threadId={HumanThreadId}");

        await using IQuerySession query = postgres.Store.QuerySession();
        ReviewThreadReplyRecord posted = (await query.LoadAsync<RunDetails>(runId, cts.Token))!
            .ReviewThreadRepliesPosted.Should().ContainSingle().Subject;
        posted.Disposition.Should().Be(ReviewThreadDisposition.Fix);
        posted.ThreadIsHumanAuthored.Should().BeTrue(
            "the author kind is the platform's own read, and recording it is what makes the claim checkable");
    }

    /// <summary>
    /// <c>h9k task show</c>'s own block, which is where the full drafted text lives: the park
    /// reason on the attention pane is one line and says so, and this is what it points at.
    /// </summary>
    [Fact]
    public async Task Task_show_renders_the_thread_url_the_disposition_and_the_whole_draft()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (_, Guid runId) = await SeedParkedReplyAsync(cts.Token);

        await using IQuerySession query = postgres.Store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        string rendered = string.Join('\n', TaskShowCommand.ComposeHumanThreadReplyDrafts([run]));

        rendered.Should().Contain("Replies drafted for a human reviewer's thread");
        rendered.Should().Contain(HumanThreadUrl);
        rendered.Should().Contain("decline");
        rendered.Should().Contain(DraftedReply, "the whole draft, not an excerpt — this is where it lives");
        rendered.Should().Contain("not posted — the reviewer has heard nothing");
        rendered.Should().Contain("h9k review resolve");
        rendered.Should().Contain("--post-nothing");
    }

    /// <summary>
    /// The same block once the owner has answered. The drafts are history and are never cleared,
    /// so a block that read them alone went on calling a reply the reviewer had already read one
    /// nobody sent, and went on inviting an action already taken — on the one surface that holds
    /// the full text, with the direction's own line rendering under a heading a thread park
    /// leaves empty (Copilot, PR #397).
    /// </summary>
    [Theory]
    [InlineData(false, "you sent the drafted reply as written")]
    [InlineData(true, "you sent your own words")]
    public async Task Task_show_says_a_draft_the_owner_sent_reached_the_reviewer(bool edited, string expected)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid taskId, Guid runId) = await SeedParkedReplyAsync(cts.Token);
        const string ownWords = "Deliberate: the sentinel already means unset here.";

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await ResolveAsync(
            taskId,
            edited
                ? new ReviewResolveCommand.Settings { MergeReady = true, PostReply = ownWords }
                : new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true },
            gh,
            cts.Token);

        await using IQuerySession query = postgres.Store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.HumanThreadReplyDrafts.Should().ContainSingle("the draft is history and is never cleared");
        string rendered = string.Join('\n', TaskShowCommand.ComposeHumanThreadReplyDrafts([run]));

        rendered.Should().Contain(expected);
        rendered.Should().NotContain(
            "not posted — the reviewer has heard nothing", "they have heard exactly this");
        rendered.Should().NotContain(
            "--post-nothing", "the choice is made; re-offering it invites a second reply");
        if (edited)
        {
            rendered.Should().Contain(ownWords, "what reached the reviewer is not what the lap drafted");
        }
    }

    /// <summary>
    /// The other half of that pairing, and the one that must not be guessed: a park the owner
    /// resolved with --post-nothing records a direction carrying no target, which says a reply
    /// was dropped somewhere on this run and never which one. So the draft stays rendered as
    /// unsent, because that is what it is.
    /// </summary>
    [Fact]
    public async Task Task_show_still_calls_a_dropped_draft_unsent()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid taskId, Guid runId) = await SeedParkedReplyAsync(cts.Token);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await ResolveAsync(
            taskId, new ReviewResolveCommand.Settings { MergeReady = true, PostNothing = true }, gh, cts.Token);

        await using IQuerySession query = postgres.Store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        string rendered = string.Join('\n', TaskShowCommand.ComposeHumanThreadReplyDrafts([run]));

        rendered.Should().Contain("not posted — the reviewer has heard nothing");
        rendered.Should().NotContain("you sent");
    }

    /// <summary>
    /// The shape that matters most and is easiest to hide: a session was refused and then did NOT
    /// park, so there is no drafted reply for the block to hang off. Gating the block on the
    /// drafts would leave exactly that run looking clean (self-review, this task).
    /// </summary>
    [Fact]
    public async Task Task_show_renders_a_refusal_even_when_the_lap_parked_no_draft()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid taskId, Guid runId) = await SeedParkedReplyAsync(cts.Token, parked: false);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await using (IDocumentSession session = postgres.Store.LightweightSession())
        {
            Func<Task> act = () => PullRequestReplyCommand.ReplyAsync(
                session, taskId,
                new PullRequestReplyCommand.Settings
                {
                    Thread = HumanThreadId, Disposition = "decline", Body = "No.",
                },
                new GitHubReviewReplies(gh.Runner), cts.Token);
            await act.Should().ThrowAsync<DomainValidationException>();
        }

        await using IQuerySession query = postgres.Store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.HumanThreadReplyDrafts.Should().BeEmpty("this lap never parked one");
        string rendered = string.Join('\n', TaskShowCommand.ComposeHumanThreadReplyDrafts([run]));

        rendered.Should().Contain("refused:");
        rendered.Should().Contain(HumanThreadId);
        rendered.Should().Contain("nothing was sent");
    }

    /// <summary>
    /// A follow-up lap on a pull request where one person opened a thread and a bot opened
    /// another, optionally already parked with the drafted reply the lap wrote for the person's.
    /// </summary>
    private async Task<(Guid TaskId, Guid RunId)> SeedParkedReplyAsync(
        CancellationToken cancellationToken, bool parked = true, bool runStreamStarted = true)
    {
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cancellationToken);
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();

        await using IDocumentSession session = store.LightweightSession();
        ProjectRegistered registered = ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), $"reply-{taskId:N}", "/tmp/reply-repo", null, "main", Now);
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
        // Only the human thread rides on the reopen: the bot's is deliberately absent, which is
        // exactly how the posting path tells them apart without trusting the session's own tag.
        TaskReopened reopened = TaskDecider.Reopen(
            task, claimed.RunId, "task/limiter", "1 unresolved review thread, 1 of them started by a human reviewer.",
            FollowUpKind.ReviewFeedback, automatic: true, Now, node.OwnerId,
            knownHumanReviewThreadIds: [HumanThreadId],
            humanReviewThreads: [new ReviewThreadReference(HumanThreadId, "jsmotherman", HumanThreadUrl)]);
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
                Path.Combine(Path.GetTempPath(), $"hall9k-reply-wt-{runId:N}"), "task/limiter",
                ExecutorMode.Subscription, Now, IsFollowUp: true),
            new AgentSessionCompleted(runId, Now),
        ];
        if (parked)
        {
            runEvents.Add(new HumanThreadReplyParked(
                runId,
                [
                    new ReviewDisagreement(
                        "why a canary value rather than reusing the sentinel",
                        "the sentinel already means unset in this projection",
                        DraftedReply, "src/Limiter.cs:42", HumanThreadId,
                        Disposition: ReviewThreadDisposition.Decline, ThreadUrl: HumanThreadUrl),
                ],
                Now));
            runEvents.Add(new ReviewParked(runId, "A review-feedback follow-up answered a thread a person opened.", Now));
        }

        // Skipped to stand in for the window between TaskClaimed, which records the run id, and
        // the RunDispatched that opens its stream: the claim above is already committed, so the
        // task names a run that does not exist yet.
        if (runStreamStarted)
        {
            session.Events.StartStream<RunAggregate>(runId, [.. runEvents]);
        }

        await session.SaveChangesAsync(cancellationToken);
        return (taskId, runId);
    }

    /// <summary>
    /// Runs the resolve with the doorbell pointed at this fixture: it resolves its connection off
    /// the environment rather than off the store handed in here.
    /// </summary>
    private async Task ResolveAsync(
        Guid taskId, ReviewResolveCommand.Settings settings, RecordingProcessRunner gh,
        CancellationToken cancellationToken)
    {
        string? previous = Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, postgres.ConnectionString);
        try
        {
            await using IDocumentSession session = postgres.Store.LightweightSession();
            int result = await ReviewResolveCommand.ResolveAsync(
                session, taskId, settings, new GitHubReviewReplies(gh.Runner), cancellationToken);
            result.Should().Be(0);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previous);
        }
    }
}
