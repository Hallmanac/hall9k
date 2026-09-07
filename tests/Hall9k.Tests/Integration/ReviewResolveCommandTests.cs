using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Processes;
using Hall9k.Daemon;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using JasperFx;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <see cref="ReviewResolveCommand.ResolvePrReviewAsync"/> against a real store, called
/// directly rather than through <c>CliStore.Open</c>'s ambient connection (this codebase's CLI
/// commands have no other test seam) — the pr-review verdict rules had no coverage at all before
/// this (cycle-1 conformance finding, `PrReviewEngine.cs:50`).
/// </summary>
// The merge-ready path rings the doorbell (Hall9k.Cli.Infrastructure.Doorbell), which resolves
// its connection off HALL9K_CONNECTION_STRING rather than this fixture, so that one test points
// it at the fixture for its duration. That is process-wide state, same as DatabaseDoctorTests and
// BacklogTrackingTests, so this joins the same collection to serialize against every other test
// that redirects it.
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class ReviewResolveCommandTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Needs_fixes_is_refused_outright_on_a_pr_review_tasks_park()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedPrReviewRunAsync(store, node, cts.Token);

        await using IDocumentSession session = store.LightweightSession();
        StreamState fence = (await session.Events.FetchStreamStateAsync(runId, cts.Token))!;

        Func<Task> act = () => ReviewResolveCommand.ResolvePrReviewAsync(
            session, runId, taskId, fence,
            new ReviewResolveCommand.Settings { NeedsFixes = "Fix the thing" }, cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>())
            .WithMessage("*nothing here for a fix session to apply*");
    }

    [Fact]
    public async Task A_missing_merge_ready_verdict_is_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedPrReviewRunAsync(store, node, cts.Token);

        await using IDocumentSession session = store.LightweightSession();
        StreamState fence = (await session.Events.FetchStreamStateAsync(runId, cts.Token))!;

        Func<Task> act = () => ReviewResolveCommand.ResolvePrReviewAsync(
            session, runId, taskId, fence, new ReviewResolveCommand.Settings(), cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>())
            .WithMessage("*Pass --merge-ready*");
    }

    [Fact]
    public async Task Merge_ready_delivers_the_review_without_ever_opening_a_pull_request()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedPrReviewRunAsync(store, node, cts.Token);

        // Resolving merge-ready rings the doorbell, which resolves its connection off
        // HALL9K_CONNECTION_STRING rather than this fixture, so it has to be pointed at the
        // fixture for the duration of the call.
        string? previousConnectionString =
            Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, postgres.ConnectionString);
        try
        {
            await using IDocumentSession session = store.LightweightSession();
            StreamState fence = (await session.Events.FetchStreamStateAsync(runId, cts.Token))!;
            int result = await ReviewResolveCommand.ResolvePrReviewAsync(
                session, runId, taskId, fence,
                new ReviewResolveCommand.Settings { MergeReady = true, Reason = "Walked and directed by hand." },
                cts.Token);

            result.Should().Be(0);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previousConnectionString);
        }

        await using IQuerySession query = store.QuerySession();
        RunAggregate run = (await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cts.Token))!;
        run.PrReviewDelivered.Should().BeTrue();
        run.State.Should().Be(RunState.UnderReview,
            "the run leaves its park the moment the verdict lands — PrReviewEngine's own resume finalizes it from here");
    }

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    /// <summary>A pr-review task whose adversarial and conformance lenses both ran and parked their report.</summary>
    private async Task<(Guid TaskId, Guid RunId)> SeedParkedPrReviewRunAsync(
        DocumentStore store, NodeContext node, CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid sessionId = DomainId.New();
        string worktreePath = Path.Combine(Path.GetTempPath(), $"hall9k-resolve-wt-{runId:N}");

        await using IDocumentSession session = store.LightweightSession();

        ProjectRegistered registered = ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), $"resolve-{taskId:N}", "/tmp/resolve-repo", null, "main", Now);
        session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(
                taskId, projectId, "Review pull request acme/web#7", ["every finding names a file and line"],
                TaskType.PrReview, null, null,
                new ExternalReference(WorkItemProvider.GitHubPullRequest, "acme/web#7"), Now, node.OwnerId),
            node.OwnerId, Now);
        TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now);
        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
        session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = Now });

        session.Events.StartStream<RunAggregate>(runId,
            new RunDispatched(
                runId, taskId, node.NodeId, node.OwnerId, 1, sessionId, worktreePath, "pr/7",
                ExecutorMode.Subscription, Now),
            new AgentSessionCompleted(runId, Now),
            new ReviewParked(runId, "Findings ready.", Now));
        await session.SaveChangesAsync(cancellationToken);

        return (taskId, runId);
    }

    /// <summary>
    /// The park's own contract (task: a changes-requested pull-request review from a human becomes
    /// a fix lap): the drafted reply sits there unsent, and a verdict alone will not move the run
    /// on — the implementer has to say what the reviewer hears.
    /// </summary>
    [Fact]
    public async Task A_disagreement_park_posts_nothing_and_refuses_a_verdict_with_no_reply_choice()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedDisagreementAsync(store, node, cts.Token);

        await using (IQuerySession query = store.QuerySession())
        {
            RunAggregate parked = (await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cts.Token))!;
            parked.State.Should().Be(RunState.ReviewParked);
            parked.ParkedOnReviewDisagreement.Should().BeTrue();
            ReviewDisagreement drafted = parked.ParkedDisagreements.Should().ContainSingle().Subject;
            drafted.ProposedReply.Should().Be(ProposedReply);
            drafted.ThreadId.Should().Be("PRRT_abc");
        }

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => ReviewResolveCommand.ResolveAsync(
            session, taskId, new ReviewResolveCommand.Settings { MergeReady = true },
            new GitHubReviewReplies(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>())
            .WithMessage("*that reply is yours to send*");
        gh.Calls.Should().BeEmpty("nothing reaches the reviewer until the implementer chooses it");
    }

    /// <summary>Choice one: the drafted reply, verbatim, inside the reviewer's own thread.</summary>
    [Fact]
    public async Task Post_reply_as_written_replies_in_the_reviewers_own_thread()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedDisagreementAsync(store, node, cts.Token);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await ResolveWithDoorbellAsync(
            store, taskId, new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true },
            gh, cts.Token);

        (string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory) call =
            gh.Calls.Should().ContainSingle().Subject;
        call.FileName.Should().Be("gh");
        call.Arguments.Should().Contain("graphql", "a thread reply is the GraphQL mutation, not a top-level comment");
        call.Arguments.Should().Contain("threadId=PRRT_abc");
        call.Arguments.Should().Contain($"body={ProposedReply}");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        ReviewDisagreementReplyDirection directed =
            run.ChangesRequestedReplyDirections.Should().ContainSingle().Subject;
        directed.Choice.Should().Be(ReviewDisagreementReplyChoice.AsWritten);
        directed.PostedBody.Should().Be(ProposedReply);
        directed.PostedTarget.Should().Be("PRRT_abc");
        run.ParkedOnReviewDisagreement.Should().BeFalse("the park is resolved; the draft is no longer pending");
        run.State.Should().Be(RunState.UnderReview);
    }

    /// <summary>Choice two: the implementer's own words instead of the draft.</summary>
    [Fact]
    public async Task Post_reply_sends_the_implementers_own_text_instead_of_the_draft()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedDisagreementAsync(store, node, cts.Token);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await ResolveWithDoorbellAsync(
            store, taskId,
            new ReviewResolveCommand.Settings { MergeReady = true, PostReply = "Fair point — let me think on it." },
            gh, cts.Token);

        gh.Calls.Should().ContainSingle().Which.Arguments.Should().Contain(
            "body=Fair point — let me think on it.");

        await using IQuerySession query = store.QuerySession();
        ReviewDisagreementReplyDirection directed = (await query.LoadAsync<RunDetails>(runId, cts.Token))!
            .ChangesRequestedReplyDirections.Should().ContainSingle().Subject;
        directed.Choice.Should().Be(ReviewDisagreementReplyChoice.Edited);
        directed.PostedBody.Should().Be("Fair point — let me think on it.");
    }

    /// <summary>Choice three: the reviewer hears nothing, and the record says so plainly.</summary>
    [Fact]
    public async Task Post_nothing_says_nothing_and_records_that_it_said_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedDisagreementAsync(store, node, cts.Token);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await ResolveWithDoorbellAsync(
            store, taskId,
            new ReviewResolveCommand.Settings { NeedsFixes = "Do it their way after all.", PostNothing = true },
            gh, cts.Token);

        gh.Calls.Should().BeEmpty();

        await using IQuerySession query = store.QuerySession();
        ReviewDisagreementReplyDirection directed = (await query.LoadAsync<RunDetails>(runId, cts.Token))!
            .ChangesRequestedReplyDirections.Should().ContainSingle().Subject;
        directed.Choice.Should().Be(ReviewDisagreementReplyChoice.Nothing);
        directed.PostedBody.Should().BeNull();
        directed.PostedTarget.Should().BeNull();
    }

    /// <summary>
    /// A failed post leaves the park unresolved rather than recorded as answered: a stream saying
    /// the reviewer was told when they were not is the one thing this whole path exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_refused_post_leaves_the_park_unresolved_and_records_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedDisagreementAsync(store, node, cts.Token);

        RecordingProcessRunner gh = RecordingProcessRunner.Failing("HTTP 404: Could not resolve to a node.");
        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => ReviewResolveCommand.ResolveAsync(
            session, taskId,
            new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true },
            new GitHubReviewReplies(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>())
            .WithMessage("*Nothing was posted.*");

        await using IQuerySession query = store.QuerySession();
        RunAggregate run = (await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked, "the verdict never landed");
        run.ParkedOnReviewDisagreement.Should().BeTrue();
    }

    /// <summary>
    /// The one window left between the post and the commit: the reply reaches the reviewer and the
    /// run stream moves under the resolve before its fenced append can land. Nothing is recorded —
    /// but the refusal has to SAY the reply was posted, because the operator's next move is the
    /// re-run the message itself invites, and a second identical reply under their own login is
    /// not something they can undo (independent pre-PR review, cycle 1, both lenses).
    /// </summary>
    [Fact]
    public async Task A_posted_reply_the_resolve_could_not_record_is_named_rather_than_left_to_a_second_post()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedDisagreementAsync(store, node, cts.Token);

        // Appended from inside the fake gh, which is the only moment that is genuinely after the
        // post and before the append. h9k task log-interaction is a real concurrent writer to this
        // very stream, so this is the race as it actually happens rather than a contrived one.
        RecordingProcessRunner gh = new(_ =>
        {
            using DocumentStore racing = NewStore();
            using IDocumentSession other = racing.LightweightSession();
            other.Events.Append(runId, new ExternalInteractionLogged(
                runId, Now, "another session", "logged while the resolve was posting", false, null, node.OwnerId));
            other.SaveChangesAsync(cts.Token).GetAwaiter().GetResult();
            return new ProcessResult(0, "{}", string.Empty);
        });

        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => ReviewResolveCommand.ResolveAsync(
            session, taskId,
            new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true },
            new GitHubReviewReplies(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>())
            .WithMessage("*Already posted, and NOT recorded on the run: PRRT_abc*")
            .WithMessage("*Do NOT re-run with a reply choice*");

        await using IQuerySession query = store.QuerySession();
        RunAggregate run = (await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked, "the verdict never landed");
        run.ParkedOnReviewDisagreement.Should().BeTrue("the park is still the implementer's to resolve");
        RunDetails details = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        details.ChangesRequestedReplyDirections.Should().BeEmpty(
            "the append was one transaction, so the reply the reviewer read is unrecorded — which is "
            + "exactly what the message has to admit");
    }

    /// <summary>
    /// The same window, entered the other way: Ctrl+C after the reply posted and before the commit.
    /// A cancellation used to be excluded from the arm above, so the operator got a bare
    /// cancellation, no mention of the reply the reviewer had already read, and a still-parked run
    /// that refuses a verdict without a reply choice — which makes the natural retry post the
    /// identical reply a second time under their own login (independent pre-PR review, cycle 1,
    /// adversarial finding). With nothing posted a cancellation still propagates untouched, which
    /// is what the ordinary-park path relies on.
    /// </summary>
    [Fact]
    public async Task A_cancellation_after_the_reply_posted_still_names_it_rather_than_inviting_a_second_post()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedDisagreementAsync(store, node, cts.Token);

        // Cancelled from inside the fake gh, which is the one moment that is genuinely after the
        // reply reached the reviewer and before the append could land — the same seam the racing
        // writer above uses, carrying the other failure this window can produce.
        RecordingProcessRunner gh = new(_ =>
        {
            cts.Cancel();
            return new ProcessResult(0, "{}", string.Empty);
        });

        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => ReviewResolveCommand.ResolveAsync(
            session, taskId,
            new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true },
            new GitHubReviewReplies(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>(
            "a cancellation the operator cannot see the reply behind is the double-post this arm prevents"))
            .WithMessage("*Already posted, and NOT recorded on the run: PRRT_abc*")
            .WithMessage("*Do NOT re-run with a reply choice*");

        await using IQuerySession query = store.QuerySession();
        RunAggregate run = (await query.Events.AggregateStreamAsync<RunAggregate>(
            runId, token: CancellationToken.None))!;
        run.State.Should().Be(RunState.ReviewParked, "the verdict never landed");
        run.ParkedOnReviewDisagreement.Should().BeTrue("the park is still the implementer's to resolve");
    }

    /// <summary>
    /// The reply choices are refused on every other park: they name a reviewer's thread, and on an
    /// ordinary park there is no drafted reply and no disputed finding to point at.
    /// </summary>
    [Fact]
    public async Task A_reply_choice_is_refused_on_a_park_that_is_not_a_disagreement()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, _) = await SeedParkedDisagreementAsync(store, node, cts.Token, disagreed: false);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => ReviewResolveCommand.ResolveAsync(
            session, taskId,
            new ReviewResolveCommand.Settings { MergeReady = true, PostNothing = true },
            new GitHubReviewReplies(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>())
            .WithMessage("*not a changes-requested disagreement*");
    }

    /// <summary>The three choices are three answers to one question, so passing two is refused up front.</summary>
    [Fact]
    public void Two_reply_choices_at_once_are_refused_by_validation() =>
        new ReviewResolveCommand.Settings { MergeReady = true, PostNothing = true, PostReplyAsWritten = true }
            .Validate().Successful.Should().BeFalse();

    /// <summary>
    /// One event per reply, not one per resolve (self-review, this task): two disagreements post
    /// two different drafts to two different places, and a single record carrying one body beside
    /// both targets would state that both reviewers read the same words.
    /// </summary>
    [Fact]
    public async Task Two_parked_disagreements_record_one_direction_each_with_its_own_body_and_target()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedDisagreementAsync(
            store, node, cts.Token,
            disagreements:
            [
                new ReviewDisagreement("first point", "first reasoning", "First reply.", "src/A.cs:1", "PRRT_a"),
                // No thread: the review's own body, which answers as a top-level comment instead.
                new ReviewDisagreement(
                    "the body's point", "second reasoning", "Second reply.",
                    Location: null, ThreadId: null, ReviewUrl: DisagreementReviewUrl),
            ]);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await ResolveWithDoorbellAsync(
            store, taskId, new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true },
            gh, cts.Token);

        gh.Calls.Should().HaveCount(2);
        gh.Calls[0].Arguments.Should().Contain("graphql").And.Contain("threadId=PRRT_a");
        gh.Calls[1].Arguments.Should().Contain("comment", "a review body has no thread to reply inside");

        await using IQuerySession query = store.QuerySession();
        List<ReviewDisagreementReplyDirection> directions =
            (await query.LoadAsync<RunDetails>(runId, cts.Token))!.ChangesRequestedReplyDirections;

        directions.Should().HaveCount(2);
        directions[0].PostedBody.Should().Be("First reply.");
        directions[0].PostedTarget.Should().Be("PRRT_a");
        directions[1].PostedBody.Should().Contain("Second reply.").And.Contain(
            DisagreementReviewUrl, "a top-level comment names the review it answers");
        directions[1].PostedTarget.Should().Be(DisagreementPullRequestUrl);
    }

    /// <summary>
    /// Every refusal is raised before the first provider write (self-review, this task): a park
    /// where only one disagreement carries a draft posts NEITHER, rather than posting the first
    /// and then failing the resolve — a reply a reviewer has read with nothing on the stream
    /// saying so is not something an operator can undo.
    /// </summary>
    [Fact]
    public async Task A_park_where_one_disagreement_has_no_draft_posts_neither()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedDisagreementAsync(
            store, node, cts.Token,
            disagreements:
            [
                new ReviewDisagreement("first point", "first reasoning", "First reply.", "src/A.cs:1", "PRRT_a"),
                new ReviewDisagreement("second point", "second reasoning", ProposedReply: "", "src/B.cs:2", "PRRT_b"),
            ]);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => ReviewResolveCommand.ResolveAsync(
            session, taskId,
            new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true },
            new GitHubReviewReplies(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>())
            .WithMessage("*Nothing has been posted*");
        gh.Calls.Should().BeEmpty();

        await using IQuerySession query = store.QuerySession();
        (await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cts.Token))!
            .State.Should().Be(RunState.ReviewParked);
    }

    /// <summary>
    /// A single --post-reply text has no one place to go across two disagreements, so it is
    /// refused rather than sent twice to two different reviewers.
    /// </summary>
    [Fact]
    public async Task An_edited_reply_is_refused_when_the_park_holds_more_than_one_disagreement()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, _) = await SeedParkedDisagreementAsync(
            store, node, cts.Token,
            disagreements:
            [
                new ReviewDisagreement("first", "reasoning", "First reply.", "src/A.cs:1", "PRRT_a"),
                new ReviewDisagreement("second", "reasoning", "Second reply.", "src/B.cs:2", "PRRT_b"),
            ]);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => ReviewResolveCommand.ResolveAsync(
            session, taskId,
            new ReviewResolveCommand.Settings { MergeReady = true, PostReply = "My own words." },
            new GitHubReviewReplies(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>())
            .WithMessage("*has no one place to go*");
        gh.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// A park whose marker landed with no parseable block still asks the implementer the question,
    /// but has no draft to send — so as-written is refused with the one lever that still applies.
    /// </summary>
    [Fact]
    public async Task An_unparseable_park_refuses_as_written_and_points_at_post_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedDisagreementAsync(
            store, node, cts.Token, disagreements: []);

        await using (IQuerySession query = store.QuerySession())
        {
            (await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cts.Token))!
                .ParkedOnReviewDisagreement.Should().BeTrue(
                    "the marker said a disagreement exists, so the implementer is still owed the question");
        }

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => ReviewResolveCommand.ResolveAsync(
            session, taskId,
            new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true },
            new GitHubReviewReplies(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>())
            .WithMessage("*no readable disagreement*");
        gh.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// The park's thread id is the fix session's own claim, parsed out of its free-text summary,
    /// and it is what selects where the implementer's reply lands under their own login. A session
    /// that copied the adjacent finding's tag — or invented a syntactically valid node id — would
    /// otherwise have the reply accepted into a thread nobody disputed, possibly on another pull
    /// request, with the disputed thread left unanswered and the run recording it as answered
    /// (independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    [Fact]
    public async Task A_thread_the_reviewer_never_opened_is_refused_rather_than_posted_to()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedDisagreementAsync(
            store, node, cts.Token,
            disagreements:
            [
                new ReviewDisagreement(
                    "reset the limiter on every request", "per-window is the documented contract",
                    ProposedReply, "src/Limiter.cs:42", "PRRT_somebody_elses", DisagreementReviewUrl),
            ],
            // What closeout actually read: one thread, and not the one the park names.
            reviewFindings: [new ChangesRequestedFinding("This limiter never resets.", "src/Limiter.cs:42", "PRRT_abc")]);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => ReviewResolveCommand.ResolveAsync(
            session, taskId,
            new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true },
            new GitHubReviewReplies(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>())
            .WithMessage("*PRRT_somebody_elses*")
            .WithMessage("*not one of the threads the reviewer opened*")
            .WithMessage("*PRRT_abc*", "the threads the review did leave are named, so the reply can be sent by hand")
            .WithMessage("*--post-nothing*");
        gh.Calls.Should().BeEmpty("the check is a refusal before the first write, not a report after it");

        await using IQuerySession query = store.QuerySession();
        RunAggregate run = (await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked, "the verdict never landed");
        run.ParkedOnReviewDisagreement.Should().BeTrue();
    }

    /// <summary>
    /// The same skepticism on the other branch: a disputed review BODY answers as a top-level
    /// comment whose text names the review it answers, so a review url closeout never read would
    /// tell a real reviewer they are being answered about a review that may not be theirs.
    /// </summary>
    [Fact]
    public async Task A_review_this_lap_was_not_dispatched_to_answer_is_refused_on_the_comment_branch()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, _) = await SeedParkedDisagreementAsync(
            store, node, cts.Token,
            disagreements:
            [
                new ReviewDisagreement(
                    "the body's point", "my reasoning", "Second reply.",
                    Location: null, ThreadId: null,
                    ReviewUrl: "https://github.com/x/y/pull/9#pullrequestreview-99"),
            ]);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => ReviewResolveCommand.ResolveAsync(
            session, taskId,
            new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true },
            new GitHubReviewReplies(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>())
            .WithMessage("*pullrequestreview-99*")
            .WithMessage("*not one of the changes-requested reviews this lap was dispatched to answer*")
            .WithMessage($"*{DisagreementReviewUrl}*", "the review this lap IS answering is named");
        gh.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// A review url is only what a top-level comment's own text names, so a thread reply — which
    /// posts by thread id and never reads the url — is not refused over one closeout did not read.
    /// Refusing there would refuse a post over a field the post does not use.
    /// </summary>
    [Fact]
    public async Task An_unrecognized_review_url_does_not_refuse_a_thread_reply()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, _) = await SeedParkedDisagreementAsync(
            store, node, cts.Token,
            disagreements:
            [
                new ReviewDisagreement(
                    "reset the limiter on every request", "per-window is the documented contract",
                    ProposedReply, "src/Limiter.cs:42", "PRRT_abc",
                    ReviewUrl: "https://github.com/x/y/pull/9#pullrequestreview-99"),
            ],
            reviewFindings: [new ChangesRequestedFinding("This limiter never resets.", "src/Limiter.cs:42", "PRRT_abc")]);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await ResolveWithDoorbellAsync(
            store, taskId, new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true },
            gh, cts.Token);

        gh.Calls.Should().ContainSingle().Which.Arguments.Should().Contain("threadId=PRRT_abc");
    }

    private const string ProposedReply =
        "Good catch on the naming — the reset really is per window, deliberately: PLAN.md 12.3 sets the contract.";

    private const string DisagreementPullRequestUrl = "https://github.com/x/y/pull/7";

    /// <summary>The one disagreement a lap's own prompt asks it to park: an inline finding with a drafted reply.</summary>
    private static readonly IReadOnlyList<ReviewDisagreement> DefaultDisagreements =
    [
        new ReviewDisagreement(
            "reset the limiter on every request", "per-window is the documented contract",
            ProposedReply, "src/Limiter.cs:42", "PRRT_abc", DisagreementReviewUrl),
    ];

    private const string DisagreementReviewUrl = $"{DisagreementPullRequestUrl}#pullrequestreview-42";

    /// <summary>
    /// Runs the resolve with the doorbell pointed at this fixture, the same way the pr-review
    /// merge-ready test does: <c>Hall9k.Cli.Infrastructure.Doorbell</c> resolves its connection off
    /// the environment rather than off the store handed in here.
    /// </summary>
    private async Task ResolveWithDoorbellAsync(
        DocumentStore store, Guid taskId, ReviewResolveCommand.Settings settings,
        RecordingProcessRunner gh, CancellationToken cancellationToken)
    {
        string? previous = Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, postgres.ConnectionString);
        try
        {
            await using IDocumentSession session = store.LightweightSession();
            int result = await ReviewResolveCommand.ResolveAsync(
                session, taskId,
                settings,
                new GitHubReviewReplies(gh.Runner), cancellationToken);
            result.Should().Be(0);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previous);
        }
    }

    /// <summary>
    /// A changes-requested fix lap that disagreed with one finding, posted nothing, and parked —
    /// or, with <paramref name="disagreed"/> false, the same lap parked the ordinary way, which is
    /// what the reply choices must be refused on.
    /// </summary>
    /// <param name="reviewFindings">
    /// What closeout read off the review, which is the truth the resolve checks a parked
    /// disagreement's own thread id against. Defaults to a finding per parked disagreement, so an
    /// ordinary park points at threads the reviewer really opened; a test about the mismatch itself
    /// names its own.
    /// </param>
    private async Task<(Guid TaskId, Guid RunId)> SeedParkedDisagreementAsync(
        DocumentStore store, NodeContext node, CancellationToken cancellationToken, bool disagreed = true,
        IReadOnlyList<ReviewDisagreement>? disagreements = null,
        IReadOnlyList<ChangesRequestedFinding>? reviewFindings = null)
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid sessionId = DomainId.New();
        string worktreePath = Path.Combine(Path.GetTempPath(), $"hall9k-disagree-wt-{runId:N}");

        await using IDocumentSession session = store.LightweightSession();

        ProjectRegistered registered = ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), $"disagree-{taskId:N}", "/tmp/disagree-repo", null, "main", Now);
        session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(
                taskId, projectId, "Bound the limiter", ["the limiter resets per window"],
                TaskType.Feature, null, null, null, Now, node.OwnerId),
            node.OwnerId, Now);

        // The real lifecycle, walked rather than shortcut: claimed, delivered with a pull request,
        // then reopened for the changes-requested lap this run answers — the reopen is what puts
        // the follow-up kind and the review itself on the task.
        TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, DomainId.New(), Now);
        task.Apply(claimed);
        TaskCompleted completed = TaskDecider.Complete(task, claimed.RunId, DisagreementPullRequestUrl, Now);
        task.Apply(completed);
        IReadOnlyList<ReviewDisagreement> parked = disagreements ?? DefaultDisagreements;
        IReadOnlyList<ChangesRequestedFinding> findings = reviewFindings
            ?? (parked.Count > 0
                ? [.. parked.Select(disagreement => new ChangesRequestedFinding(
                    disagreement.Finding, disagreement.Location, disagreement.ThreadId))]
                : [new ChangesRequestedFinding("This limiter never resets.", "src/Limiter.cs:42", "PRRT_abc")]);
        TaskReopened reopened = TaskDecider.Reopen(
            task, claimed.RunId, "task/limiter", "@teammate requested changes.",
            FollowUpKind.ReviewRequestedChanges, automatic: true, Now, node.OwnerId,
            changesRequestedReviews:
            [
                new ChangesRequestedReview("teammate", DisagreementReviewUrl, Now, findings),
            ]);
        task.Apply(reopened);
        TaskClaimed followUpClaim = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now);
        session.Events.StartStream<TaskAggregate>(
            taskId, [.. lifecycle, claimed, completed, reopened, followUpClaim]);
        session.Store(new TaskLease
        {
            Id = taskId, NodeId = node.NodeId, LeaseGeneration = followUpClaim.LeaseGeneration, HeartbeatAt = Now,
        });

        List<object> runEvents =
        [
            new RunDispatched(
                runId, taskId, node.NodeId, node.OwnerId, followUpClaim.LeaseGeneration, sessionId, worktreePath,
                "task/limiter", ExecutorMode.Subscription, Now, IsFollowUp: true),
            new AgentSessionCompleted(runId, Now),
        ];
        if (disagreed)
        {
            runEvents.Add(new ReviewDisagreementParked(runId, parked, Now));
        }

        runEvents.Add(new ReviewParked(runId, "A changes-requested fix lap disagreed.", Now));
        session.Events.StartStream<RunAggregate>(runId, [.. runEvents]);
        await session.SaveChangesAsync(cancellationToken);

        return (taskId, runId);
    }
}
