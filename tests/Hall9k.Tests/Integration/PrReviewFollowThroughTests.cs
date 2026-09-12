using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon;
using Hall9k.Daemon.Closeout;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The follow-through a posted pull-request review gets instead of an immediate Done (task: a
/// pr-review task stays open while the pull request's review threads are unresolved), against a
/// real store with the two <c>gh</c> reads faked: the review-conversation read and the
/// <c>gh api user</c> login read.
/// <para>
/// Origin incident (2026-09-08, arx-platform #2023, task 2402246b): the pr-review task went Done
/// the moment the review was posted, so when the author answered all five threads and pushed
/// revisions the next day, nothing on the board watched the pull request. Every test here is one
/// arm of the decision table that failure produced.
/// </para>
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class PrReviewFollowThroughTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private const string Repository = "acme/widgets";
    private const string Reference = $"{Repository}#42";
    private const string PullRequestUrl = "https://github.com/acme/widgets/pull/42";
    private const string ReviewerLogin = "brian";
    private const string AuthorLogin = "fanzoo";
    private const string ReviewedHead = "aaaaaaaaaaaa";

    /// <summary>
    /// The review conversation, scripted. Every test states the pull request it wants to be
    /// looked at and asserts what the engine did with it, so the whole decision table is
    /// reachable without a GitHub account.
    /// </summary>
    private sealed class FakeConversations : IReviewConversationReader
    {
        public ReviewConversation Conversation { get; set; } = Quiet();

        /// <summary>How many reads happened — the assertion that a skipped task never calls gh at all.</summary>
        public int Reads { get; private set; }

        public Task<ReviewConversation> ReadAsync(
            string repository, int number, string workingDirectory, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(Conversation);
        }

        /// <summary>An open pull request with one unresolved thread of the reviewer's own, nothing since.</summary>
        public static ReviewConversation Quiet() => new(
            IsOpen: true,
            IsMerged: false,
            IsClosed: false,
            HeadSha: ReviewedHead,
            CommitCount: 3,
            Threads: [ReviewerThread("src/One.cs", 12, "t1", resolved: false, replies: 0)],
            OutstandingReviewerLogins: [],
            ThreadsTruncated: false);

        /// <summary>
        /// One of the reviewer's threads: its opener is the reviewer's own comment, which is what
        /// makes it theirs, and <paramref name="replies"/> author answers follow it.
        /// </summary>
        public static ReviewThread ReviewerThread(
            string path, int line, string id, bool resolved, int replies) => new(
                Id: id,
                IsResolved: resolved,
                StartedByLogin: ReviewerLogin,
                Path: path,
                Line: line,
                CommentCount: 1 + replies,
                Comments:
                [
                    new ReviewThreadComment(ReviewerLogin, $"this needs a look at {path}:{line}", Now.AddHours(-2)),
                    .. Enumerable.Range(0, replies).Select(index =>
                        new ReviewThreadComment(AuthorLogin, $"answered, take {index + 1}", Now.AddMinutes(index))),
                ]);

        /// <summary>
        /// One of the reviewer's threads whose LAST comment is the reviewer's own: they answered
        /// the author's reply themselves, or added a note to their own thread on GitHub. Nothing
        /// in it is waiting on them, which is what the reply count has to say about it — a count
        /// of every comment instead flipped the task to needs-you for an act nobody else took
        /// (independent pre-PR review, cycle 1, conformance lens).
        /// </summary>
        public static ReviewThread ThreadTheReviewerSpokeLastIn(string id, bool resolved) => new(
            Id: id,
            IsResolved: resolved,
            StartedByLogin: ReviewerLogin,
            Path: "src/One.cs",
            Line: 12,
            CommentCount: 3,
            Comments:
            [
                new ReviewThreadComment(ReviewerLogin, "this needs a look", Now.AddHours(-2)),
                new ReviewThreadComment(AuthorLogin, "answered", Now.AddHours(-1)),
                new ReviewThreadComment(ReviewerLogin, "and one more thing while I am here", Now),
            ]);

        /// <summary>One of the reviewer's threads answered by somebody who is neither of them — a teammate, or a bot.</summary>
        public static ReviewThread ThreadAnsweredByAThirdParty(string id) => new(
            Id: id,
            IsResolved: false,
            StartedByLogin: ReviewerLogin,
            Path: "src/One.cs",
            Line: 12,
            CommentCount: 2,
            Comments:
            [
                new ReviewThreadComment(ReviewerLogin, "this needs a look", Now.AddHours(-2)),
                new ReviewThreadComment("a-teammate", "I hit this too", Now),
            ]);
    }

    /// <summary>
    /// <c>gh api user -q .login</c>, answering with the reviewer's login. The engine re-reads it
    /// every sweep rather than remembering it, so every test needs it scripted.
    /// </summary>
    private static ProcessRunner LoginRunner(string login = ReviewerLogin) =>
        new RecordingProcessRunner(() => new ProcessResult(0, login + "\n", string.Empty)).Runner;

    private PrReviewFollowThroughEngine Engine(
        NodeContext node, IReviewConversationReader conversations, ProcessRunner? runner = null) =>
        new(postgres.Store, node, conversations, runner ?? LoginRunner(),
            NullLogger<PrReviewFollowThroughEngine>.Instance);

    /// <summary>
    /// A pr-review task whose review has been posted and whose follow-through is open: the state
    /// <c>PrReviewEngine.FinalizeAsync</c> now leaves behind instead of Done. Its run is Completed
    /// and carries this node's id, which is what makes the watch this node's.
    /// </summary>
    private async Task<(NodeContext Node, Guid TaskId, Guid RunId)> SeedWaitingReviewAsync(
        CancellationToken cancellationToken, Guid? nodeIdOnRun = null, string? registeredSession = null)
    {
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cancellationToken);
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();

        await using IDocumentSession session = store.LightweightSession();
        ProjectRegistered registered = ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), $"follow-through-{taskId:N}", "/tmp/follow-through-repo",
            new Uri($"https://github.com/{Repository}"), "main", Now);
        session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

        TaskAdded added = TaskDecider.Add(
            taskId, projectId, $"Review pull request {Reference}", ["every finding is directed"],
            TaskType.PrReview, null, null, new ExternalReference(WorkItemProvider.GitHubPullRequest, Reference),
            Now.AddDays(-1), node.OwnerId);
        TaskAggregate task = new();
        task.Apply(added);
        TaskPublished published = TaskDecider.Publish(
            task, TaskDependencyGraph.Empty, Now.AddDays(-1), node.OwnerId, BacklogPolicy.None);
        task.Apply(published);
        TaskAssigned assigned = TaskDecider.Assign(task, node.OwnerId, [], Now.AddDays(-1), node.OwnerId);
        task.Apply(assigned);
        TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now.AddDays(-1));
        task.Apply(claimed);
        PullRequestReviewFollowThroughOpened opened = TaskDecider.OpenPrReviewFollowThrough(
            task, runId, PullRequestUrl, ReviewedHead, Now.AddHours(-2));
        task.Apply(opened);
        session.Events.StartStream<TaskAggregate>(taskId, [added, published, assigned, claimed, opened]);

        RunDispatched dispatched = new(
            runId, taskId, nodeIdOnRun ?? node.NodeId, node.OwnerId, claimed.LeaseGeneration, DomainId.New(),
            "/tmp/pr-review-worktree", "pr/42", ExecutorMode.Subscription, Now.AddHours(-3),
            RunDirectory: "/tmp/pr-review-run",
            DispatchingNodeId: nodeIdOnRun ?? node.NodeId);
        List<object> runEvents = [dispatched];
        if (registeredSession is not null)
        {
            runEvents.Add(new InteractiveSessionStarted(
                runId, Guid.NewGuid(), Now.AddHours(-3), 4242, Environment.MachineName, registeredSession));
        }

        runEvents.Add(new RunCompleted(runId, Now.AddHours(-2)));
        session.Events.StartStream<RunAggregate>(runId, [.. runEvents]);
        await session.SaveChangesAsync(cancellationToken);

        return (node, taskId, runId);
    }

    private async Task<TaskDetails> ReadTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = postgres.Store.QuerySession();
        return (await query.LoadAsync<TaskDetails>(taskId, cancellationToken))!;
    }

    [Fact]
    public async Task A_posted_review_with_an_unresolved_thread_keeps_the_task_waiting()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (NodeContext node, Guid taskId, _) = await SeedWaitingReviewAsync(cts.Token);
        FakeConversations conversations = new();

        PrReviewFollowThroughResult sweep = await Engine(node, conversations).FollowThroughOnceAsync(taskId, cts.Token);

        sweep.Inspected.Should().Be(1);
        sweep.Concluded.Should().Be(0, "the reviewer's thread is still unresolved, so the review is not over");
        sweep.Surfaced.Should().Be(0, "nothing arrived since the review, so nothing wakes the reviewer");
        TaskDetails task = await ReadTaskAsync(taskId, cts.Token);
        task.State.Should().Be(TaskState.AwaitingAuthor);
        task.PrReviewOpenThreadCount.Should().Be(1);
        task.PrReviewFollowThroughObserved.Should().BeTrue("the first poll established the baseline");
        task.PrReviewReviewerLogin.Should().Be(ReviewerLogin);
    }

    [Fact]
    public async Task A_reply_in_one_of_the_reviewers_threads_flips_the_task_to_needs_you_with_the_counts()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (NodeContext node, Guid taskId, _) = await SeedWaitingReviewAsync(
            cts.Token, registeredSession: "hall9k-window");
        FakeConversations conversations = new();
        PrReviewFollowThroughEngine engine = Engine(node, conversations);

        // The first sweep sees a review nobody has answered yet — the reviewer's own thread with
        // nothing waiting on them in it — so it records the baseline and says nothing. A reply
        // already waiting there WOULD surface on it, which is its own test above.
        await engine.FollowThroughOnceAsync(taskId, cts.Token);
        (await ReadTaskAsync(taskId, cts.Token)).State.Should().Be(TaskState.AwaitingAuthor);

        conversations.Conversation = conversations.Conversation with
        {
            Threads = [FakeConversations.ReviewerThread("src/One.cs", 12, "t1", resolved: false, replies: 2)],
        };
        PrReviewFollowThroughResult sweep = await engine.FollowThroughOnceAsync(taskId, cts.Token);

        sweep.Surfaced.Should().Be(1);
        TaskDetails task = await ReadTaskAsync(taskId, cts.Token);
        task.State.Should().Be(TaskState.NeedsHuman);
        task.PrReviewAuthorActivitySummary.Should().Contain("2 replies in 1 thread")
            .And.Contain(Reference)
            .And.Contain("1 of your threads is still unresolved");
        task.PrReviewAuthorActivitySessionAddress.Should().Be(
            "hall9k-window", "the line is recorded against the session it was addressed to");
    }

    /// <summary>
    /// The gap the first cut swallowed (self-review, round one): the reviewer posts, the author
    /// pushes two minutes later, and the FIRST poll is the one that sees it. Suppressing that first
    /// look absorbed the push into the baseline and never told anybody, which is the exact class of
    /// miss this feature exists to close — and the reply half of the same window went the same way
    /// until cycle 1's review (above).
    /// </summary>
    [Fact]
    public async Task A_push_that_landed_before_the_first_poll_still_surfaces()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (NodeContext node, Guid taskId, _) = await SeedWaitingReviewAsync(cts.Token);
        FakeConversations conversations = new()
        {
            // The head has already moved off the one the review was posted against, and no poll has
            // run yet — the author got there first.
            Conversation = FakeConversations.Quiet() with { HeadSha = "bbbbbbbbbbbb", CommitCount = 5 },
        };

        PrReviewFollowThroughResult sweep = await Engine(node, conversations)
            .FollowThroughOnceAsync(taskId, cts.Token);

        sweep.Surfaced.Should().Be(1);
        TaskDetails task = await ReadTaskAsync(taskId, cts.Token);
        task.State.Should().Be(TaskState.NeedsHuman);
        // The push is named; its SIZE is not, and honestly so — nothing recorded a commit count
        // when the wait began (finalize spends no gh read), so there is nothing to subtract from.
        // An unobserved number stated as unknown rather than reported as one (AGENTS.md).
        task.PrReviewAuthorActivitySummary.Should().Contain("new commits pushed (the count is not readable)")
            .And.NotContain("repl", "nothing was said in the threads themselves, so the line claims nothing");
    }

    /// <summary>
    /// The reply half of the same window (independent pre-PR review, cycle 1, both lenses): the
    /// reviewer posts, the author answers a minute later, and the FIRST poll is the one that sees
    /// it. Suppressing that whole first look absorbed the answer into both watermarks — so no
    /// later poll ever diffed it, and the scoped lap's own anchor already contained it — and the
    /// reviewer was never told a word of it.
    /// </summary>
    [Fact]
    public async Task A_reply_that_landed_before_the_first_poll_still_surfaces()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (NodeContext node, Guid taskId, _) = await SeedWaitingReviewAsync(cts.Token);
        FakeConversations conversations = new()
        {
            Conversation = FakeConversations.Quiet() with
            {
                Threads = [FakeConversations.ReviewerThread("src/One.cs", 12, "t1", resolved: false, replies: 2)],
            },
        };

        PrReviewFollowThroughResult sweep = await Engine(node, conversations)
            .FollowThroughOnceAsync(taskId, cts.Token);

        sweep.Surfaced.Should().Be(1);
        TaskDetails task = await ReadTaskAsync(taskId, cts.Token);
        task.State.Should().Be(TaskState.NeedsHuman);
        task.PrReviewAuthorActivitySummary.Should().Contain("2 replies in 1 thread");
    }

    /// <summary>
    /// The worst shape of that window: the author answered every thread AND resolved them all
    /// inside the first poll interval. Suppressing the first look's replies took this straight to
    /// Done — the origin incident's silent miss, recurring inside the feature built to close it.
    /// </summary>
    [Fact]
    public async Task An_answer_that_also_resolved_everything_before_the_first_poll_wakes_the_reviewer()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (NodeContext node, Guid taskId, _) = await SeedWaitingReviewAsync(cts.Token);
        FakeConversations conversations = new()
        {
            Conversation = FakeConversations.Quiet() with
            {
                Threads = [FakeConversations.ReviewerThread("src/One.cs", 12, "t1", resolved: true, replies: 1)],
            },
        };

        PrReviewFollowThroughResult sweep = await Engine(node, conversations)
            .FollowThroughOnceAsync(taskId, cts.Token);

        sweep.Surfaced.Should().Be(
            1, "the answer is read once before the task closes out on resolutions the author made themselves");
        sweep.Concluded.Should().Be(0);
        TaskDetails task = await ReadTaskAsync(taskId, cts.Token);
        task.State.Should().Be(TaskState.NeedsHuman);
        task.PrReviewAuthorActivitySummary.Should()
            .Contain("1 reply in 1 thread")
            .And.Contain("every thread you opened is resolved now");
    }

    /// <summary>
    /// A comment of the REVIEWER's own in their own thread is not an answer to them, and must
    /// never flip the task to needs-you asserting that its author replied — an attribution nothing
    /// observed (independent pre-PR review, cycle 1, conformance lens).
    /// </summary>
    [Fact]
    public async Task The_reviewers_own_comment_in_their_own_thread_never_wakes_them()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (NodeContext node, Guid taskId, _) = await SeedWaitingReviewAsync(cts.Token);
        FakeConversations conversations = new();
        PrReviewFollowThroughEngine engine = Engine(node, conversations);
        await engine.FollowThroughOnceAsync(taskId, cts.Token);

        conversations.Conversation = conversations.Conversation with
        {
            Threads = [FakeConversations.ThreadTheReviewerSpokeLastIn("t1", resolved: false)],
        };
        PrReviewFollowThroughResult sweep = await engine.FollowThroughOnceAsync(taskId, cts.Token);

        sweep.Surfaced.Should().Be(0);
        TaskDetails task = await ReadTaskAsync(taskId, cts.Token);
        task.State.Should().Be(TaskState.AwaitingAuthor);
        task.PrReviewAuthorActivitySummary.Should().BeNull(
            "no line can be composed for an act the reviewer took themselves");
    }

    /// <summary>
    /// A third party answering the reviewer's thread — a teammate, a bot — IS movement worth a
    /// look, and the line says only what was observed: the pull request moved. It never names the
    /// author, because these counts do not say who wrote a comment (AGENTS.md's never-guess rule).
    /// </summary>
    [Fact]
    public async Task A_third_partys_reply_surfaces_without_claiming_the_author_wrote_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (NodeContext node, Guid taskId, _) = await SeedWaitingReviewAsync(cts.Token);
        FakeConversations conversations = new()
        {
            Conversation = FakeConversations.Quiet() with
            {
                Threads = [FakeConversations.ThreadAnsweredByAThirdParty("t1")],
            },
        };

        PrReviewFollowThroughResult sweep = await Engine(node, conversations)
            .FollowThroughOnceAsync(taskId, cts.Token);

        sweep.Surfaced.Should().Be(1);
        TaskDetails task = await ReadTaskAsync(taskId, cts.Token);
        task.PrReviewAuthorActivitySummary.Should()
            .Contain($"{Reference} moved since your review")
            .And.NotContain("author");
    }

    [Fact]
    public async Task The_same_replies_do_not_notify_twice()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (NodeContext node, Guid taskId, _) = await SeedWaitingReviewAsync(cts.Token);
        FakeConversations conversations = new();
        PrReviewFollowThroughEngine engine = Engine(node, conversations);
        await engine.FollowThroughOnceAsync(taskId, cts.Token);

        conversations.Conversation = conversations.Conversation with
        {
            Threads = [FakeConversations.ReviewerThread("src/One.cs", 12, "t1", resolved: false, replies: 1)],
        };
        (await engine.FollowThroughOnceAsync(taskId, cts.Token)).Surfaced.Should().Be(1);

        PrReviewFollowThroughResult again = await engine.FollowThroughOnceAsync(taskId, cts.Token);

        again.Surfaced.Should().Be(0, "the observation beside the response re-baselined the watermark");
        (await ReadTaskAsync(taskId, cts.Token)).State.Should().Be(
            TaskState.NeedsHuman, "the task stays needs-you; it simply does not re-fire");
    }

    [Fact]
    public async Task A_push_flips_the_task_to_needs_you()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (NodeContext node, Guid taskId, _) = await SeedWaitingReviewAsync(cts.Token);
        FakeConversations conversations = new();
        PrReviewFollowThroughEngine engine = Engine(node, conversations);
        await engine.FollowThroughOnceAsync(taskId, cts.Token);

        conversations.Conversation = conversations.Conversation with
        {
            HeadSha = "bbbbbbbbbbbb",
            CommitCount = 5,
        };
        PrReviewFollowThroughResult sweep = await engine.FollowThroughOnceAsync(taskId, cts.Token);

        sweep.Surfaced.Should().Be(1);
        TaskDetails task = await ReadTaskAsync(taskId, cts.Token);
        task.State.Should().Be(TaskState.NeedsHuman);
        task.PrReviewAuthorActivitySummary.Should().Contain("2 new commits");
    }

    /// <summary>
    /// Every review thread resolved, with nothing else outstanding, no longer ends the wait by
    /// itself (Decisions Log #178, amending #160): one pr-review task per pull
    /// request per install stays waiting until the pull request itself merges or closes, so a
    /// later GitHub mention of the install's own login always has a live task to attach to
    /// instead of minting a redundant second one. This used to reach Done on this very look; now
    /// it is recorded exactly like any other quiet observation and the wait stays open.
    /// </summary>
    [Fact]
    public async Task Every_thread_resolved_no_longer_concludes_the_follow_through_by_itself()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (NodeContext node, Guid taskId, _) = await SeedWaitingReviewAsync(cts.Token);
        FakeConversations conversations = new()
        {
            Conversation = FakeConversations.Quiet() with
            {
                Threads = [FakeConversations.ReviewerThread("src/One.cs", 12, "t1", resolved: true, replies: 0)],
            },
        };

        PrReviewFollowThroughResult sweep = await Engine(node, conversations).FollowThroughOnceAsync(taskId, cts.Token);

        sweep.Concluded.Should().Be(0, "thread resolution alone never ends the wait now — only a merge, a close, or an abandon does");
        TaskDetails task = await ReadTaskAsync(taskId, cts.Token);
        task.State.Should().Be(TaskState.AwaitingAuthor);
        task.PrReviewFollowThroughOpen.Should().BeTrue("the task keeps watching so a later mention has a live task to attach to");
    }

    /// <summary>
    /// The Done that must not happen (independent pre-PR review, cycle 2): every thread the read
    /// COULD see is resolved, but the provider's own 100-thread page cap left more unread, so
    /// "every thread the reviewer opened is resolved" is a claim about the first hundred and not
    /// about the pull request. Concluding on it would close the follow-through with unresolved
    /// threads of theirs still standing — the silent miss this whole watch exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_capped_thread_page_never_concludes_the_follow_through()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (NodeContext node, Guid taskId, _) = await SeedWaitingReviewAsync(cts.Token);
        FakeConversations conversations = new()
        {
            Conversation = FakeConversations.Quiet() with
            {
                Threads = [FakeConversations.ReviewerThread("src/One.cs", 12, "t1", resolved: true, replies: 0)],
                ThreadsTruncated = true,
            },
        };

        PrReviewFollowThroughResult sweep = await Engine(node, conversations).FollowThroughOnceAsync(taskId, cts.Token);

        sweep.Concluded.Should().Be(0, "a count off a capped page is a floor, and Done is not read off a floor");
        TaskDetails task = await ReadTaskAsync(taskId, cts.Token);
        task.State.Should().Be(TaskState.AwaitingAuthor);
        task.PrReviewFollowThroughOpen.Should().BeTrue(
            "the wait stays open rather than claiming a conversation the read cannot see the end of");
    }

    /// <summary>
    /// The standoff the first cut left (independent pre-PR review, cycle 1, adversarial lens): the
    /// author resolves every one of the reviewer's threads themselves, writes nothing, pushes
    /// nothing, and clicks re-request review. It held the wait open — correctly — but rendered as
    /// "waiting on its author", so the author waited on the reviewer while the reviewer's board
    /// said the ball was in the author's court. A review request is the one thing this watch reads
    /// that is an explicit ask OF the reviewer, so it wakes them like a reply does.
    /// </summary>
    [Fact]
    public async Task A_re_review_requested_of_the_reviewer_wakes_them_even_with_every_thread_resolved()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (NodeContext node, Guid taskId, _) = await SeedWaitingReviewAsync(cts.Token);
        FakeConversations conversations = new()
        {
            Conversation = FakeConversations.Quiet() with
            {
                Threads = [FakeConversations.ReviewerThread("src/One.cs", 12, "t1", resolved: true, replies: 0)],
                OutstandingReviewerLogins = [ReviewerLogin],
            },
        };

        PrReviewFollowThroughResult sweep = await Engine(node, conversations).FollowThroughOnceAsync(taskId, cts.Token);

        sweep.Surfaced.Should().Be(1);
        sweep.Concluded.Should().Be(0, "a conversation the author is asking the reviewer back into is not over");
        TaskDetails task = await ReadTaskAsync(taskId, cts.Token);
        task.State.Should().Be(TaskState.NeedsHuman);
        task.PrReviewReReviewRequested.Should().BeTrue();
        task.PrReviewAuthorActivitySummary.Should()
            .Contain("a re-review requested of you")
            .And.NotContain("repl", "nobody wrote a word, so the line claims nothing about replies");
    }

    /// <summary>
    /// The dedup half, the same rule the reply counts live by: the observation appended beside the
    /// wake records the standing request, so an ask that has been sitting there since is not
    /// re-announced every few minutes. What it still does is hold the wait open — a resolved
    /// conversation the author is asking the reviewer back into never reaches Done.
    /// </summary>
    [Fact]
    public async Task A_re_review_request_already_recorded_holds_the_wait_open_without_waking_again()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (NodeContext node, Guid taskId, _) = await SeedWaitingReviewAsync(cts.Token);
        FakeConversations conversations = new()
        {
            Conversation = FakeConversations.Quiet() with
            {
                Threads = [FakeConversations.ReviewerThread("src/One.cs", 12, "t1", resolved: true, replies: 0)],
                OutstandingReviewerLogins = [ReviewerLogin],
            },
        };
        PrReviewFollowThroughEngine engine = Engine(node, conversations);
        (await engine.FollowThroughOnceAsync(taskId, cts.Token)).Surfaced.Should().Be(1);

        PrReviewFollowThroughResult again = await engine.FollowThroughOnceAsync(taskId, cts.Token);

        again.Surfaced.Should().Be(0, "one ask wakes the reviewer once");
        again.Concluded.Should().Be(0, "and the standing request still holds the wait open");
        (await ReadTaskAsync(taskId, cts.Token)).PrReviewReReviewRequested.Should().BeTrue();
    }

    [Fact]
    public async Task The_pull_request_merging_reaches_done()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (NodeContext node, Guid taskId, _) = await SeedWaitingReviewAsync(cts.Token);
        FakeConversations conversations = new()
        {
            Conversation = FakeConversations.Quiet() with { IsOpen = false, IsMerged = true },
        };

        PrReviewFollowThroughResult sweep = await Engine(node, conversations).FollowThroughOnceAsync(taskId, cts.Token);

        sweep.Concluded.Should().Be(1);
        TaskDetails task = await ReadTaskAsync(taskId, cts.Token);
        task.State.Should().Be(
            TaskState.Done, "a merged pull request ends the follow-through whatever is left unanswered on it");
    }

    [Fact]
    public async Task The_pull_request_closing_without_a_merge_reaches_done()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (NodeContext node, Guid taskId, _) = await SeedWaitingReviewAsync(cts.Token);
        FakeConversations conversations = new()
        {
            Conversation = FakeConversations.Quiet() with { IsOpen = false, IsClosed = true },
        };

        await Engine(node, conversations).FollowThroughOnceAsync(taskId, cts.Token);

        (await ReadTaskAsync(taskId, cts.Token)).State.Should().Be(TaskState.Done);
    }

    [Fact]
    public async Task Another_nodes_watch_is_skipped_without_a_single_gh_read()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (NodeContext node, Guid taskId, _) = await SeedWaitingReviewAsync(
            cts.Token, nodeIdOnRun: DomainId.New());
        FakeConversations conversations = new();

        PrReviewFollowThroughResult sweep = await Engine(node, conversations).FollowThroughOnceAsync(taskId, cts.Token);

        sweep.Skipped.Should().Be(1);
        sweep.Inspected.Should().Be(0);
        conversations.Reads.Should().Be(
            0, "run provenance is the only honest owner of a watch, and a skip must cost no gh call");
        (await ReadTaskAsync(taskId, cts.Token)).State.Should().Be(TaskState.AwaitingAuthor);
    }

    [Fact]
    public async Task An_unreadable_login_is_a_failed_poll_rather_than_a_guess()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (NodeContext node, Guid taskId, _) = await SeedWaitingReviewAsync(cts.Token);
        FakeConversations conversations = new();
        ProcessRunner refusing = new RecordingProcessRunner(
            () => new ProcessResult(1, string.Empty, "gh auth login required")).Runner;

        PrReviewFollowThroughResult sweep = await Engine(node, conversations, refusing).FollowThroughOnceAsync(taskId, cts.Token);

        sweep.Failures.Should().Be(1, "an unreadable login counts toward the monitor's own backoff verdict");
        sweep.Inspected.Should().Be(0);
        TaskDetails task = await ReadTaskAsync(taskId, cts.Token);
        task.State.Should().Be(TaskState.AwaitingAuthor);
        task.PrReviewFollowThroughObserved.Should().BeFalse(
            "nothing is recorded from a poll that could not say whose threads it was counting");
    }

    [Fact]
    public async Task Another_reviewers_unresolved_thread_never_surfaces_or_holds_the_task_open()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (NodeContext node, Guid taskId, _) = await SeedWaitingReviewAsync(cts.Token);
        FakeConversations conversations = new()
        {
            Conversation = FakeConversations.Quiet() with
            {
                Threads =
                [
                    FakeConversations.ReviewerThread("src/One.cs", 12, "t1", resolved: true, replies: 0),
                    new ReviewThread(
                        "t2", IsResolved: false, StartedByLogin: "someone-else", Path: "src/Two.cs", Line: 3,
                        CommentCount: 1,
                        Comments: [new ReviewThreadComment("someone-else", "my own concern", Now)]),
                ],
            },
        };

        PrReviewFollowThroughResult sweep = await Engine(node, conversations).FollowThroughOnceAsync(taskId, cts.Token);

        sweep.Surfaced.Should().Be(0, "somebody else's own thread is not this reviewer's follow-through to be woken by");
        TaskDetails task = await ReadTaskAsync(taskId, cts.Token);
        task.State.Should().Be(
            TaskState.AwaitingAuthor,
            "the reviewer's own single thread being resolved no longer concludes the wait by itself");
        task.PrReviewFollowThroughOpen.Should().BeTrue();
    }

    [Fact]
    public async Task A_quiet_poll_writes_no_second_observation()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (NodeContext node, Guid taskId, _) = await SeedWaitingReviewAsync(cts.Token);
        FakeConversations conversations = new();
        PrReviewFollowThroughEngine engine = Engine(node, conversations);
        await engine.FollowThroughOnceAsync(taskId, cts.Token);

        long afterFirst = await StreamVersionAsync(taskId, cts.Token);
        await engine.FollowThroughOnceAsync(taskId, cts.Token);
        await engine.FollowThroughOnceAsync(taskId, cts.Token);

        (await StreamVersionAsync(taskId, cts.Token)).Should().Be(
            afterFirst, "a pull request polled every few minutes for a week must not write an event per tick");
    }

    private async Task<long> StreamVersionAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = postgres.Store.QuerySession();
        return (await query.Events.FetchStreamStateAsync(taskId, cancellationToken))!.Version;
    }

    [Fact]
    public async Task The_sweeps_own_query_finds_a_waiting_review_and_acts_on_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (NodeContext node, Guid taskId, _) = await SeedWaitingReviewAsync(cts.Token);
        FakeConversations conversations = new()
        {
            Conversation = FakeConversations.Quiet() with { IsOpen = false, IsMerged = true },
        };

        // The whole sweep rather than the per-task entry: this is the one assertion that the
        // waiting-state-plus-flag query actually selects a follow-through at all. Its counts are
        // deliberately not asserted — this class shares one database, so a sibling test's own
        // waiting review is in the same result set — and the task's own state is what is checked.
        await Engine(node, conversations).SweepOnceAsync(cts.Token);

        (await ReadTaskAsync(taskId, cts.Token)).State.Should().Be(TaskState.Done);
    }

    [Fact]
    public async Task A_needs_you_follow_through_still_reaches_done_when_the_pull_request_ends()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (NodeContext node, Guid taskId, _) = await SeedWaitingReviewAsync(cts.Token);
        FakeConversations conversations = new();
        PrReviewFollowThroughEngine engine = Engine(node, conversations);
        await engine.FollowThroughOnceAsync(taskId, cts.Token);

        conversations.Conversation = conversations.Conversation with
        {
            Threads = [FakeConversations.ReviewerThread("src/One.cs", 12, "t1", resolved: false, replies: 1)],
        };
        await engine.FollowThroughOnceAsync(taskId, cts.Token);
        (await ReadTaskAsync(taskId, cts.Token)).State.Should().Be(TaskState.NeedsHuman);

        // Resolving the thread alone no longer ends the watch (Decisions Log
        // #178) — the watch keeps running through needs-you, and only the pull
        // request itself merging or closing does.
        conversations.Conversation = conversations.Conversation with
        {
            Threads = [FakeConversations.ReviewerThread("src/One.cs", 12, "t1", resolved: true, replies: 1)],
            IsOpen = false,
            IsMerged = true,
        };
        PrReviewFollowThroughResult sweep = await engine.FollowThroughOnceAsync(taskId, cts.Token);

        sweep.Concluded.Should().Be(1, "the pull request merging is what ends it, not the thread resolving");
        (await ReadTaskAsync(taskId, cts.Token)).State.Should().Be(TaskState.Done);
    }
}
