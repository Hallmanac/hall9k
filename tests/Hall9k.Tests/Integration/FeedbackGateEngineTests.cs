using System.Text.Json;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Worktrees;
using Hall9k.Daemon;
using Hall9k.Daemon.AutoPrReview;
using Hall9k.Daemon.Closeout;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Orchestrator;
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
using Hall9k.Domain.Infrastructure.Storage;
using JasperFx.Events;
using Hall9k.Tests.Fakes;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The membership gate where it meets the closeout poll (task: a changes-requested review or an
/// unresolved review thread from someone outside the project's trust-chain roster never dispatches a
/// build lap; it raises a needs-you item, and only the owner's explicit go dispatches a fix lap for
/// it): real task and run streams in a real store, a scripted inspector standing in for GitHub, and a
/// worktree manager that refuses to be reached, so nothing here touches git or the network. The gate's
/// own decisions are pinned over pure data in <c>FeedbackMembershipGateTests</c>; this is what proves
/// the engine honours them in the order the poll runs.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class FeedbackGateEngineTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private const string PullRequestUrl = "https://github.com/x/y/pull/7";
    private const string Head = "cafe1";
    private const long MemberId = 1001;
    private const long StrangerId = 2001;

    private static string ReviewUrl(int number) => $"{PullRequestUrl}#pullrequestreview-{number}";

    private static ChangesRequestedReview Review(string login, long accountId, int number) =>
        new(login, ReviewUrl(number), Now.AddMinutes(-5),
            [new ChangesRequestedFinding($"{login}'s finding.", "src/Limiter.cs:42", $"PRRT_{number}")], accountId);

    private static PullRequestSnapshot Public(params ChangesRequestedReview[] reviews) =>
        new(IsMerged: false, IsClosed: false, MergedAt: null, ClosedAt: null,
            FailingChecks: [], HasPendingChecks: false, UnresolvedReviewThreadCount: 0,
            UnresolvedHumanThreadCount: 0, Reviewers: [], ErroredReview: null,
            CopilotReviewState: ExternalReviewState.None, CopilotReviewThreadCount: 0)
        {
            RepositoryIsPrivate = false,
            HeadCommit = Head,
            ChangesRequestedReviews = reviews,
        };

    [Fact]
    public async Task A_non_members_review_dispatches_no_lap_and_is_held_once_across_polls()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (Guid taskId, Guid runId, NodeContext node) = await SeedAwaitingReviewAsync(cts.Token);
        FakeInspector inspector = new(Public(Review("stranger", StrangerId, 42)));
        CloseoutEngine engine = NewEngine(node, inspector, roster: [MemberId], taskId);

        await engine.PollOnceAsync(cts.Token);
        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = postgres.Store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.State.Should().Be(TaskState.Done, "no lap was dispatched for a stranger's review");
        task.FollowUpKind.Should().Be(FollowUpKind.Unknown);
        NonMemberFeedbackItem item = task.NonMemberFeedback.Should().ContainSingle().Subject;
        item.Status.Should().Be(NonMemberFeedbackStatus.Undecided);
        item.Reviewer.Should().Be("stranger");
        item.Url.Should().Be(ReviewUrl(42));
        (await query.Events.FetchStreamAsync(taskId, token: cts.Token)).Count(e => e.Data is NonMemberFeedbackHeld)
            .Should().Be(1, "however many polls observe it, the item is raised once");
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.State.Should().Be(RunState.AwaitingReview);
        await RetireWatchAsync(runId, cts.Token);
    }

    [Fact]
    public async Task A_members_review_still_dispatches_its_lap_stamped_as_a_members_with_the_read_scope()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (Guid taskId, _, NodeContext node) = await SeedAwaitingReviewAsync(cts.Token);
        FakeInspector inspector = new(Public(Review("teammate", MemberId, 43)));

        await NewEngine(node, inspector, roster: [MemberId], taskId).PollOnceAsync(cts.Token);

        await using IQuerySession query = postgres.Store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.FollowUpKind.Should().Be(FollowUpKind.ReviewRequestedChanges);
        task.ChangesRequestedReviews.Should().ContainSingle().Which.AdmittedAs.Should().Be(FeedbackAdmission.Member);
        task.ReviewReadScope.Should().NotBeNull("the lap is told which comments it may read");
        task.NonMemberFeedback.Should().BeEmpty();
    }

    [Fact]
    public async Task A_mixed_poll_runs_the_members_lap_and_still_raises_the_strangers_item()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (Guid taskId, _, NodeContext node) = await SeedAwaitingReviewAsync(cts.Token);
        FakeInspector inspector = new(Public(Review("teammate", MemberId, 44), Review("stranger", StrangerId, 45)));

        await NewEngine(node, inspector, roster: [MemberId], taskId).PollOnceAsync(cts.Token);

        await using IQuerySession query = postgres.Store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.FollowUpKind.Should().Be(FollowUpKind.ReviewRequestedChanges);
        task.ChangesRequestedReviews.Should().ContainSingle(
            review => review.ReviewUrl == ReviewUrl(44), "the lap carries only the member's feedback");
        task.NonMemberFeedback.Should().ContainSingle(
            item => item.Key == ReviewUrl(45) && item.Status == NonMemberFeedbackStatus.Undecided,
            "the stranger's item is raised in the same poll, before the lap's push can move the head");
    }

    [Fact]
    public async Task With_the_roster_not_computed_a_poll_dispatches_nothing_raises_nothing_and_does_not_merge()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (Guid taskId, Guid runId, NodeContext node) = await SeedAwaitingReviewAsync(
            cts.Token, preApproval: PreApprovalMode.On);
        FakeInspector inspector = new(Public(Review("teammate", MemberId, 46)));

        await NewEngine(node, inspector, roster: null, taskId).PollOnceAsync(cts.Token);

        await using IQuerySession query = postgres.Store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.FollowUpKind.Should().Be(FollowUpKind.Unknown);
        task.NonMemberFeedback.Should().BeEmpty("it asks again on the next poll rather than guessing");
        inspector.MergeAttempts.Should().Be(0);
        await RetireWatchAsync(runId, cts.Token);
    }

    [Fact]
    public async Task A_pre_approved_task_holds_its_merge_while_an_item_is_undecided_and_merges_once_it_is_declined()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (Guid taskId, Guid runId, NodeContext node) = await SeedAwaitingReviewAsync(
            cts.Token, preApproval: PreApprovalMode.On);
        FakeInspector inspector = new(Public(Review("stranger", StrangerId, 47)));
        CloseoutEngine engine = NewEngine(node, inspector, roster: [MemberId], taskId);

        await engine.PollOnceAsync(cts.Token);
        await engine.PollOnceAsync(cts.Token);
        inspector.MergeAttempts.Should().Be(0, "a stranger can stall a pre-approved merge until the owner declines");

        await using (IDocumentSession decide = postgres.Store.LightweightSession())
        {
            TaskAggregate aggregate = (await decide.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            decide.Events.Append(
                taskId, TaskDecider.DeclineNonMemberFeedback(aggregate, ReviewUrl(47), Now, node.OwnerId));
            await decide.SaveChangesAsync(cts.Token);
        }

        await engine.PollOnceAsync(cts.Token);

        inspector.MergeAttempts.Should().Be(1, "declining a review releases the hold");
        await using IQuerySession query = postgres.Store.QuerySession();
        (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!.FollowUpKind.Should().Be(FollowUpKind.Unknown);
        await RetireWatchAsync(runId, cts.Token);
    }

    [Fact]
    public async Task The_owners_go_dispatches_one_lap_carrying_the_review_even_after_the_head_moved()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (Guid taskId, Guid runId, NodeContext node) = await SeedAwaitingReviewAsync(cts.Token);
        ChangesRequestedReview strangers = Review("stranger", StrangerId, 48);
        FakeInspector inspector = new(Public(strangers));
        CloseoutEngine engine = NewEngine(node, inspector, roster: [MemberId], taskId);
        await engine.PollOnceAsync(cts.Token);

        await using (IDocumentSession decide = postgres.Store.LightweightSession())
        {
            TaskAggregate aggregate = (await decide.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            decide.Events.Append(
                taskId, TaskDecider.AdmitNonMemberFeedback(aggregate, ReviewUrl(48), Now, node.OwnerId));
            await decide.SaveChangesAsync(cts.Token);
        }

        // The head has moved on: the review is no longer on it, though it still stands.
        inspector.Snapshot = Public() with
        {
            HeadCommit = "cafe2",
            StandingChangesRequestedReviews = [strangers],
        };
        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = postgres.Store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.FollowUpKind.Should().Be(FollowUpKind.ReviewRequestedChanges);
        ChangesRequestedReview carried = task.ChangesRequestedReviews.Should().ContainSingle().Subject;
        carried.ReviewUrl.Should().Be(ReviewUrl(48));
        carried.AdmittedAs.Should().Be(FeedbackAdmission.OwnerApproved);
        (await query.Events.FetchStreamAsync(taskId, token: cts.Token)).Count(e => e.Data is TaskReopened)
            .Should().Be(1, "the go buys exactly one lap");
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.State.Should().Be(RunState.Superseded);
    }


    // ─── The read-only evaluator that assesses held feedback before the owner decides on it ───────────

    private FeedbackEvaluators NewEvaluators(
        EvaluatorExecutor executor, FakeProcessManager processes, Action<DaemonOptions>? configure = null)
    {
        DaemonOptions daemon = new() { NonMemberFeedbackEvaluatorTimeout = TimeSpan.FromSeconds(30) };
        configure?.Invoke(daemon);
        return new FeedbackEvaluators(
            postgres.Store, executor, processes, Options.Create(daemon), NullLogger<FeedbackEvaluators>.Instance);
    }

    private async Task<TaskDetails> LoadTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = postgres.Store.QuerySession();
        return (await query.LoadAsync<TaskDetails>(taskId, cancellationToken))!;
    }

    private async Task<int> CountEventsAsync<T>(Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = postgres.Store.QuerySession();
        return (await query.Events.FetchStreamAsync(taskId, token: cancellationToken)).Count(e => e.Data is T);
    }

    private async Task GiveTheGoAsync(Guid taskId, string key, NodeContext node, CancellationToken cancellationToken)
    {
        await using IDocumentSession decide = postgres.Store.LightweightSession();
        TaskAggregate aggregate = (await decide.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken))!;
        decide.Events.Append(taskId, TaskDecider.AdmitNonMemberFeedback(aggregate, key, Now, node.OwnerId));
        await decide.SaveChangesAsync(cancellationToken);
    }

    [Fact]
    public async Task A_held_item_dispatches_one_evaluator_across_polls_and_a_restart_and_shows_its_recommendation()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (Guid taskId, Guid runId, NodeContext node) = await SeedAwaitingReviewAsync(cts.Token);
        FakeProcessManager processes = new();
        EvaluatorExecutor executor = new(processes) { Assessment = $"Recommendation: decline\n\n{AssessmentProse}\n" };
        FeedbackEvaluators evaluators = NewEvaluators(executor, processes);
        FakeInspector inspector = new(Public(Review("stranger", StrangerId, 60)));
        CloseoutEngine engine = NewEngine(node, inspector, roster: [MemberId], taskId, evaluators);

        await engine.PollOnceAsync(cts.Token);
        await engine.PollOnceAsync(cts.Token);
        await evaluators.WhenIdleAsync();
        // A daemon restart builds new engines over the same stream; nothing of the first one's memory survives.
        FeedbackEvaluators afterRestart = NewEvaluators(executor, processes);
        await NewEngine(node, inspector, roster: [MemberId], taskId, afterRestart).PollOnceAsync(cts.Token);
        await afterRestart.WhenIdleAsync();

        executor.Requests.Should().ContainSingle("one item, one evaluator, however many polls and restarts observe it");
        AgentSpawnRequest request = executor.Requests[0];
        request.SkipPermissions.Should().BeFalse("no pr-review session ever skips permissions");
        request.UsesReviewPermissions.Should().BeTrue("the evaluator runs under the pr-review permission file");
        request.UntrustedWorkingDirectory.Should().BeTrue("a stranger's text never meets the checkout's own project settings");
        request.MaxTurns.Should().Be(new DaemonOptions().NonMemberFeedbackEvaluatorMaxTurns, "a hard turn cap");
        request.AssessmentFile.Should().StartWith(RunDirectoryOf(runId)).And.EndWith(".md");
        request.Prompt.Should().Contain(StrangersWords).And.Contain("not addressed to you");
        (await CountEventsAsync<NonMemberFeedbackEvaluatorDispatched>(taskId, cts.Token)).Should().Be(1);

        TaskDetails task = await LoadTaskAsync(taskId, cts.Token);
        NonMemberFeedbackItem item = task.NonMemberFeedback.Should().ContainSingle().Subject;
        item.Status.Should().Be(NonMemberFeedbackStatus.Undecided);
        item.EvaluationState.Status.Should().Be(FeedbackEvaluationStatus.Assessed);
        item.EvaluationState.Recommendation.Should().Be(FeedbackRecommendation.Decline);
        item.EvaluationState.AssessmentPath.Should().Be(request.AssessmentFile);

        // What the owner's status line and the stored events carry: the word and the path, never the prose.
        string line = NonMemberFeedbackPane.ComposeLine("0e866341", "Bound the limiter", item);
        line.Should().Contain("evaluator recommends decline").And.Contain(request.AssessmentFile!);
        line.Should().NotContain(AssessmentProse).And.NotContain(StrangersWords);
        await using (IQuerySession query = postgres.Store.QuerySession())
        {
            foreach (IEvent stored in await query.Events.FetchStreamAsync(taskId, token: cts.Token))
            {
                if (stored.Data is NonMemberFeedbackEvaluatorDispatched or NonMemberFeedbackEvaluationRecorded)
                {
                    JsonSerializer.Serialize(stored.Data, stored.Data.GetType()).Should().NotContain(AssessmentProse);
                    (OrchestratorFeedDescription.Of(stored.Data) ?? "").Should().NotContain(AssessmentProse);
                }
            }
        }

        await RetireWatchAsync(runId, cts.Token);
        DeleteRunDirectory(runId);
    }

    [Fact]
    public async Task Feedback_past_the_per_task_cap_still_raises_its_item_with_no_evaluator()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (Guid taskId, Guid runId, NodeContext node) = await SeedAwaitingReviewAsync(cts.Token);
        FakeProcessManager processes = new();
        EvaluatorExecutor executor = new(processes);
        FeedbackEvaluators evaluators = NewEvaluators(executor, processes, daemon => daemon.NonMemberFeedbackEvaluatorCap = 2);
        FakeInspector inspector = new(Public(
            Review("stranger", StrangerId, 61), Review("stranger", StrangerId, 62), Review("stranger", StrangerId, 63)));
        CloseoutEngine engine = NewEngine(node, inspector, roster: [MemberId], taskId, evaluators);

        await engine.PollOnceAsync(cts.Token);
        await evaluators.WhenIdleAsync();
        // A fourth review arrives later: the allowance is a lifetime one, not a per-poll one.
        inspector.Snapshot = Public(
            Review("stranger", StrangerId, 61), Review("stranger", StrangerId, 62), Review("stranger", StrangerId, 63),
            Review("stranger", StrangerId, 64));
        await engine.PollOnceAsync(cts.Token);
        await evaluators.WhenIdleAsync();

        executor.Requests.Should().HaveCount(2, "the daemon option caps one task's evaluators for its lifetime");
        TaskDetails task = await LoadTaskAsync(taskId, cts.Token);
        task.NonMemberFeedback.Should().HaveCount(4, "every piece of feedback still raises its needs-you item");
        task.NonMemberFeedback.Should().OnlyContain(item => item.Status == NonMemberFeedbackStatus.Undecided);
        task.NonMemberFeedback.Count(item => item.EvaluationState.Status == FeedbackEvaluationStatus.Assessed).Should().Be(2);
        NonMemberFeedbackItem[] skipped = [.. task.NonMemberFeedback.Where(item => item.EvaluationState.Status == FeedbackEvaluationStatus.PastCap)];
        skipped.Should().HaveCount(2);
        skipped.Should().OnlyContain(item => item.EvaluationState.AssessmentPath == null);
        NonMemberFeedbackPane.ComposeLine("0e866341", "Bound the limiter", skipped[0]).Should().Contain("no evaluator ran");
        (await CountEventsAsync<NonMemberFeedbackEvaluatorDispatched>(taskId, cts.Token)).Should().Be(2);

        await RetireWatchAsync(runId, cts.Token);
        DeleteRunDirectory(runId);
    }

    [Fact]
    public async Task An_evaluator_that_fails_leaves_the_item_saying_so_and_the_owners_go_still_dispatches_the_lap()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (Guid taskId, Guid runId, NodeContext node) = await SeedAwaitingReviewAsync(cts.Token);
        FakeProcessManager processes = new();
        EvaluatorExecutor executor = new(processes) { SpawnFailure = new InvalidOperationException("claude is not installed") };
        FeedbackEvaluators evaluators = NewEvaluators(executor, processes);
        ChangesRequestedReview strangers = Review("stranger", StrangerId, 65);
        FakeInspector inspector = new(Public(strangers));
        CloseoutEngine engine = NewEngine(node, inspector, roster: [MemberId], taskId, evaluators);

        await engine.PollOnceAsync(cts.Token);
        await evaluators.WhenIdleAsync();

        NonMemberFeedbackItem item = (await LoadTaskAsync(taskId, cts.Token)).NonMemberFeedback.Should().ContainSingle().Subject;
        item.Status.Should().Be(NonMemberFeedbackStatus.Undecided, "the item stays");
        item.EvaluationState.Status.Should().Be(FeedbackEvaluationStatus.Failed);
        item.EvaluationState.Reason.Should().Be("the evaluator could not be started");
        NonMemberFeedbackPane.ComposeLine("0e866341", "Bound the limiter", item).Should().Contain("no assessment: the evaluator could not be started");

        await GiveTheGoAsync(taskId, ReviewUrl(65), node, cts.Token);
        await engine.PollOnceAsync(cts.Token);

        TaskDetails task = await LoadTaskAsync(taskId, cts.Token);
        task.FollowUpKind.Should().Be(FollowUpKind.ReviewRequestedChanges, "the go works exactly as without an evaluator");
        task.ChangesRequestedReviews.Should().ContainSingle().Which.AdmittedAs.Should().Be(FeedbackAdmission.OwnerApproved);
        DeleteRunDirectory(runId);
    }

    [Fact]
    public async Task An_evaluator_that_wrote_nothing_or_a_recommendation_outside_the_three_words_is_a_failure()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        foreach ((string? assessment, string expectedReason) in new (string?, string)[]
        {
            (null, "the evaluator wrote no assessment"),
            ("Recommendation: merge it now\n", "the evaluator's recommendation was not one of fix, decline or ask"),
        })
        {
            (Guid taskId, Guid runId, NodeContext node) = await SeedAwaitingReviewAsync(cts.Token);
            FakeProcessManager processes = new();
            EvaluatorExecutor executor = new(processes) { Assessment = assessment };
            FeedbackEvaluators evaluators = NewEvaluators(executor, processes);

            await NewEngine(node, new FakeInspector(Public(Review("stranger", StrangerId, 66))), [MemberId], taskId, evaluators)
                .PollOnceAsync(cts.Token);
            await evaluators.WhenIdleAsync();

            NonMemberFeedbackItem item = (await LoadTaskAsync(taskId, cts.Token)).NonMemberFeedback.Should().ContainSingle().Subject;
            item.Status.Should().Be(NonMemberFeedbackStatus.Undecided);
            item.EvaluationState.Status.Should().Be(FeedbackEvaluationStatus.Failed);
            item.EvaluationState.Reason.Should().Be(expectedReason);
            item.EvaluationState.Recommendation.Should().BeNull();
            await RetireWatchAsync(runId, cts.Token);
            DeleteRunDirectory(runId);
        }
    }

    [Fact]
    public async Task An_evaluator_past_its_timeout_is_terminated_and_recorded_as_failed()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (Guid taskId, Guid runId, NodeContext node) = await SeedAwaitingReviewAsync(cts.Token);
        FakeProcessManager processes = new();
        EvaluatorExecutor executor = new(processes) { HoldOpen = true };
        FeedbackEvaluators evaluators = NewEvaluators(
            executor, processes, daemon => daemon.NonMemberFeedbackEvaluatorTimeout = TimeSpan.FromMilliseconds(300));

        await NewEngine(node, new FakeInspector(Public(Review("stranger", StrangerId, 67))), [MemberId], taskId, evaluators)
            .PollOnceAsync(cts.Token);
        await evaluators.WhenIdleAsync();

        NonMemberFeedbackItem item = (await LoadTaskAsync(taskId, cts.Token)).NonMemberFeedback.Should().ContainSingle().Subject;
        item.EvaluationState.Status.Should().Be(FeedbackEvaluationStatus.Failed);
        item.EvaluationState.Reason.Should().Be("the evaluator timed out");
        processes.Terminations.Should().ContainSingle("the session that ran past its bound is ended");
        await RetireWatchAsync(runId, cts.Token);
        DeleteRunDirectory(runId);
    }

    [Fact]
    public async Task The_owners_go_dispatches_the_lap_while_the_evaluator_is_still_running_and_a_late_assessment_changes_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (Guid taskId, Guid runId, NodeContext node) = await SeedAwaitingReviewAsync(cts.Token);
        FakeProcessManager processes = new();
        EvaluatorExecutor executor = new(processes) { HoldOpen = true };
        FeedbackEvaluators evaluators = NewEvaluators(executor, processes);
        ChangesRequestedReview strangers = Review("stranger", StrangerId, 68);
        FakeInspector inspector = new(Public(strangers));
        CloseoutEngine engine = NewEngine(node, inspector, roster: [MemberId], taskId, evaluators);
        await engine.PollOnceAsync(cts.Token);
        (await LoadTaskAsync(taskId, cts.Token)).NonMemberFeedback.Should().ContainSingle()
            .Which.EvaluationState.Status.Should().Be(FeedbackEvaluationStatus.Running);

        await GiveTheGoAsync(taskId, ReviewUrl(68), node, cts.Token);
        inspector.Snapshot = Public() with { HeadCommit = "cafe2", StandingChangesRequestedReviews = [strangers] };
        await engine.PollOnceAsync(cts.Token);

        TaskDetails task = await LoadTaskAsync(taskId, cts.Token);
        task.FollowUpKind.Should().Be(FollowUpKind.ReviewRequestedChanges, "the go does not wait for the evaluator");
        (await CountEventsAsync<TaskReopened>(taskId, cts.Token)).Should().Be(1);

        await executor.ReleaseAsync();
        await evaluators.WhenIdleAsync();

        task = await LoadTaskAsync(taskId, cts.Token);
        task.NonMemberFeedback.Should().ContainSingle().Which.Status.Should().Be(NonMemberFeedbackStatus.Admitted);
        (await CountEventsAsync<NonMemberFeedbackEvaluationRecorded>(taskId, cts.Token)).Should().Be(0,
            "an assessment that lands after the owner acted changes nothing");
        task.FollowUpKind.Should().Be(FollowUpKind.ReviewRequestedChanges);
        DeleteRunDirectory(runId);
    }

    [Fact]
    public async Task A_decline_while_the_evaluator_is_still_running_works_and_the_late_assessment_changes_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (Guid taskId, Guid runId, NodeContext node) = await SeedAwaitingReviewAsync(cts.Token, preApproval: PreApprovalMode.On);
        FakeProcessManager processes = new();
        EvaluatorExecutor executor = new(processes) { HoldOpen = true };
        FeedbackEvaluators evaluators = NewEvaluators(executor, processes);
        FakeInspector inspector = new(Public(Review("stranger", StrangerId, 69)));
        CloseoutEngine engine = NewEngine(node, inspector, roster: [MemberId], taskId, evaluators);
        await engine.PollOnceAsync(cts.Token);
        inspector.MergeAttempts.Should().Be(0);

        await using (IDocumentSession decide = postgres.Store.LightweightSession())
        {
            TaskAggregate aggregate = (await decide.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            decide.Events.Append(taskId, TaskDecider.DeclineNonMemberFeedback(aggregate, ReviewUrl(69), Now, node.OwnerId));
            await decide.SaveChangesAsync(cts.Token);
        }

        await engine.PollOnceAsync(cts.Token);
        await executor.ReleaseAsync();
        await evaluators.WhenIdleAsync();

        inspector.MergeAttempts.Should().Be(1, "declining releases the merge hold with the evaluator still running");
        (await CountEventsAsync<NonMemberFeedbackEvaluationRecorded>(taskId, cts.Token)).Should().Be(0);
        DeleteRunDirectory(runId);
    }

    [Fact]
    public async Task A_mixed_poll_runs_the_members_lap_and_still_dispatches_the_strangers_evaluator()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (Guid taskId, Guid runId, NodeContext node) = await SeedAwaitingReviewAsync(cts.Token);
        FakeProcessManager processes = new();
        // Held open so the evaluator's own outcome cannot land in the middle of the poll it was dispatched in.
        EvaluatorExecutor executor = new(processes) { Assessment = "Recommendation: ask\n", HoldOpen = true };
        FeedbackEvaluators evaluators = NewEvaluators(executor, processes);
        FakeInspector inspector = new(Public(Review("teammate", MemberId, 70), Review("stranger", StrangerId, 71)));

        await NewEngine(node, inspector, roster: [MemberId], taskId, evaluators).PollOnceAsync(cts.Token);
        await executor.ReleaseAsync();
        await evaluators.WhenIdleAsync();

        TaskDetails task = await LoadTaskAsync(taskId, cts.Token);
        task.FollowUpKind.Should().Be(FollowUpKind.ReviewRequestedChanges);
        task.ChangesRequestedReviews.Should().ContainSingle(review => review.ReviewUrl == ReviewUrl(70));
        executor.Requests.Should().ContainSingle("the evaluator is for the stranger's review only");
        executor.Requests[0].Prompt.Should().Contain("stranger's finding").And.NotContain("teammate's finding");
        NonMemberFeedbackItem item = task.NonMemberFeedback.Should().ContainSingle(held => held.Key == ReviewUrl(71)).Subject;
        item.EvaluationState.Recommendation.Should().Be(FeedbackRecommendation.Ask);
        DeleteRunDirectory(runId);
    }

    [Fact]
    public async Task A_dispatch_nothing_is_running_after_a_restart_is_recorded_as_failed_and_never_run_again()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (Guid taskId, Guid runId, NodeContext node) = await SeedAwaitingReviewAsync(cts.Token);
        FakeProcessManager processes = new();
        EvaluatorExecutor executor = new(processes) { HoldOpen = true };
        FakeInspector inspector = new(Public(Review("stranger", StrangerId, 72)));
        await NewEngine(node, inspector, [MemberId], taskId, NewEvaluators(executor, processes)).PollOnceAsync(cts.Token);

        // The first daemon is gone with its session; a second one finds the dispatch on the stream and nothing running it.
        FeedbackEvaluators afterRestart = NewEvaluators(executor, processes);
        await NewEngine(node, inspector, [MemberId], taskId, afterRestart).PollOnceAsync(cts.Token);
        await NewEngine(node, inspector, [MemberId], taskId, afterRestart).PollOnceAsync(cts.Token);

        executor.Requests.Should().ContainSingle();
        NonMemberFeedbackItem item = (await LoadTaskAsync(taskId, cts.Token)).NonMemberFeedback.Should().ContainSingle().Subject;
        item.EvaluationState.Status.Should().Be(FeedbackEvaluationStatus.Failed);
        item.EvaluationState.Reason.Should().Contain("daemon restarted");
        await RetireWatchAsync(runId, cts.Token);
        await executor.ReleaseAsync();
        DeleteRunDirectory(runId);
    }

    private static void DeleteRunDirectory(Guid runId)
    {
        string directory = RunDirectoryOf(runId);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private const string StrangersWords = "stranger's finding.";
    private const string AssessmentProse = "This is the assessment prose that must never reach a status row or the feed.";

    /// <summary>
    /// A scripted evaluator session: it records every spawn, writes the assessment file the request
    /// names (or none) and its own stream's result line, and can be held open to stand for a session
    /// that is still running when the owner acts.
    /// </summary>
    private sealed class EvaluatorExecutor(FakeProcessManager processes) : IExecutor
    {
        private readonly object _gate = new();
        private readonly List<(AgentSpawnRequest Request, int ProcessId)> _held = [];
        private readonly List<AgentSpawnRequest> _requests = [];
        private int _nextProcessId = 41_000;

        /// <summary>Every spawn, as of now; sessions run side by side, so the list is guarded.</summary>
        public IReadOnlyList<AgentSpawnRequest> Requests
        {
            get { lock (_gate) { return [.. _requests]; } }
        }

        public string? Assessment { get; set; } = "Recommendation: fix\n\nThe change serves the task.\n";

        public bool HoldOpen { get; set; }

        public Exception? SpawnFailure { get; set; }

        public async Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
        {
            int processId;
            lock (_gate)
            {
                _requests.Add(request);
                if (SpawnFailure is { } failure)
                {
                    throw failure;
                }

                processId = _nextProcessId++;
                if (HoldOpen)
                {
                    processes.MarkAlive(processId);
                    _held.Add((request, processId));
                }
            }

            if (!HoldOpen)
            {
                await FinishAsync(request, cancellationToken);
            }

            return new SpawnedAgent(processId, DateTimeOffset.UtcNow);
        }

        /// <summary>Lets every held session finish the way a real one does: its file, its result line, then its exit.</summary>
        public async Task ReleaseAsync()
        {
            (AgentSpawnRequest Request, int ProcessId)[] held;
            lock (_gate)
            {
                held = [.. _held];
                _held.Clear();
            }

            foreach ((AgentSpawnRequest request, int processId) in held)
            {
                await FinishAsync(request, CancellationToken.None);
                processes.MarkDead(processId);
            }
        }

        private async Task FinishAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(request.RunDirectory);
            if (Assessment is not null)
            {
                await File.WriteAllTextAsync(request.AssessmentFile!, Assessment, cancellationToken);
            }

            string line = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "result",
                ["subtype"] = "success",
                ["is_error"] = false,
                ["usage"] = new Dictionary<string, long> { ["input_tokens"] = 100, ["output_tokens"] = 20 },
                ["total_cost_usd"] = 0.01,
                ["num_turns"] = 4,
                ["result"] = "Recommendation given.",
            });
            await File.WriteAllTextAsync(
                RunPaths.SessionStreamFile(request.RunDirectory, request.SessionArtifactName!), line + "\n", cancellationToken);
        }
    }

    /// <summary>
    /// A task finished with its pull request open and its run awaiting review, with no worktree and no
    /// repository behind either: the poll only ever reads GitHub through the scripted inspector.
    /// </summary>
    private async Task<(Guid TaskId, Guid RunId, NodeContext Node)> SeedAwaitingReviewAsync(
        CancellationToken cancellationToken, PreApprovalMode? preApproval = null)
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(postgres.Store, cancellationToken);
        Guid taskId = DomainId.New();
        Guid projectId = DomainId.New();
        await using IDocumentSession session = postgres.Store.LightweightSession();

        TaskAdded added = TaskDecider.Add(
            taskId, projectId, "Bound the limiter", ["the limiter resets per window"], TaskType.Feature,
            null, null, null, Now, node.OwnerId);
        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(added, node.OwnerId, Now);
        List<object> taskEvents = [.. lifecycle];
        if (preApproval is not null)
        {
            TaskPreApprovedSet set = TaskDecider.SetPreApproved(task, preApproval, Now, node.OwnerId, taskClosedOut: false);
            task.Apply(set);
            taskEvents.Add(set);
        }

        TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, DomainId.New(), Now);
        task.Apply(claimed);
        taskEvents.Add(claimed);
        taskEvents.Add(TaskDecider.Complete(task, claimed.RunId, PullRequestUrl, Now));
        session.Events.StartStream<TaskAggregate>(taskId, [.. taskEvents]);

        string worktree = Path.Combine(Path.GetTempPath(), $"hall9k-feedback-gate-{claimed.RunId:N}");
        session.Events.StartStream<RunAggregate>(claimed.RunId,
            new RunDispatched(
                claimed.RunId, taskId, node.NodeId, node.OwnerId, claimed.LeaseGeneration, DomainId.New(),
                worktree, "task/limiter", ExecutorMode.Subscription, Now, RunDirectory: RunDirectoryOf(claimed.RunId)),
            new AgentSessionCompleted(claimed.RunId, Now),
            new VerificationPassed(claimed.RunId, Now),
            new PullRequestOpened(claimed.RunId, PullRequestUrl, 7, Now));

        ProjectRegistered registered = ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), $"gate-{taskId:N}", "/tmp/feedback-gate-repo", null, "main", Now);
        session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

        await session.SaveChangesAsync(cancellationToken);
        return (taskId, claimed.RunId, node);
    }

    /// <summary>Where a seeded run's files land, under the temp directory so no test writes into the machine's own home.</summary>
    private static string RunDirectoryOf(Guid runId) =>
        Path.Combine(Path.GetTempPath(), $"hall9k-feedback-gate-run-{runId:N}");

    private CloseoutEngine NewEngine(
        NodeContext node, FakeInspector inspector, long[]? roster, Guid taskId, FeedbackEvaluators? evaluators = null)
    {
        EnrolledNodeSnapshots snapshots = new();
        if (roster is not null)
        {
            snapshots.Set(ProjectOf(taskId), new EnrolledFleetSnapshot([node.NodeId], roster, []));
        }

        StubWorktreeManager worktrees = new();
        return new CloseoutEngine(
            postgres.Store, node, new DaemonConnection(postgres.ConnectionString), inspector, worktrees,
            new StackedParentWatch(worktrees, new NoOpRemoteParentReader(), NullLogger<StackedParentWatch>.Instance),
            RecordingProcessRunner.Succeeding(string.Empty).Runner,
            RecordingJiraRequester.Succeeding(200, "{}").Requester,
            Options.Create(new DaemonOptions
            {
                MaxAutomaticCloseoutRuns = 4,
                MaxCloseoutLapsPerObstruction = 2,
                CopilotReviewSettleWindow = TimeSpan.FromHours(24),
                ChecksRegistrationSettleWindow = TimeSpan.FromMinutes(15),
            }),
            NullLogger<CloseoutEngine>.Instance,
            enrolledNodes: snapshots,
            feedbackEvaluators: evaluators);
    }

    private Guid ProjectOf(Guid taskId)
    {
        using IQuerySession query = postgres.Store.QuerySession();
        return query.LoadAsync<TaskDetails>(taskId).GetAwaiter().GetResult()!.ProjectId;
    }

    /// <summary>Leaves the watch set as the test found it, since one database backs every test in the class.</summary>
    private async Task RetireWatchAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = postgres.Store.LightweightSession();
        session.Events.Append(runId, new RunSuperseded(runId, 99, Now));
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A scripted GitHub: whatever snapshot the test sets, and a count of the merges attempted.</summary>
    private sealed class FakeInspector(PullRequestSnapshot snapshot) : IPullRequestInspector
    {
        public PullRequestSnapshot Snapshot { get; set; } = snapshot;

        public int MergeAttempts { get; private set; }

        public Task<PullRequestSnapshot> InspectAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, CancellationToken cancellationToken) =>
            Task.FromResult(Snapshot);

        public Task<PullRequestStateSnapshot> InspectStateAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, CancellationToken cancellationToken) =>
            Task.FromResult(new PullRequestStateSnapshot(Snapshot.IsMerged, Snapshot.IsClosed, Snapshot.MergedAt, Snapshot.ClosedAt));

        public Task RerequestReviewAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, PullRequestReviewer reviewer,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task MergeAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, string? expectedHeadCommit,
            CancellationToken cancellationToken)
        {
            MergeAttempts++;
            return Task.CompletedTask;
        }

        public Task RetargetAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, string baseBranch,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> DeletesHeadBranchOnMergeAsync(string repositoryPath, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class NoOpLock : IAsyncDisposable
    {
        public static readonly NoOpLock Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Reaches no git: every operation either does nothing or says it should not have been called.</summary>
    private sealed class StubWorktreeManager : IWorktreeManager
    {
        public Task<Worktree> CreateAsync(WorktreeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached by these tests.");

        public Task<Worktree> CheckoutExistingAsync(FollowUpWorktreeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached by these tests.");

        public Task<Worktree> CreatePrReviewCheckoutAsync(PrReviewWorktreeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached by these tests.");

        public Task RemoveAsync(string repositoryPath, string worktreePath, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeletePrReviewTrackingRefAsync(string repositoryPath, int pullRequestNumber, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteBranchEverywhereAsync(
            string repositoryPath, string branch, RemoteBranchDeletionOwner remoteDeletion,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PruneAsync(string repositoryPath, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<CheckoutRefresh> RefreshReadingCheckoutAsync(
            string checkoutPath, string branch, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached by these tests.");

        public Task<IAsyncDisposable> AcquireRepositoryLockAsync(string repositoryPath, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(NoOpLock.Instance);

        public Task<IAsyncDisposable> AcquireCheckoutLockAsync(string checkoutPath, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(NoOpLock.Instance);
    }
}
