using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using JasperFx.Events;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The pure fold behind h9k task show's passage section (task: h9k task show tells a task's
/// passage in time): elapsed time per phase, human waits kept separate from the phase they
/// interrupted, and the closing counts — all computed from <see cref="FakeEvent{T}"/> lists
/// rather than a database, the same DB-free tier every other projection test in this project
/// uses.
/// </summary>
public sealed class TaskPassageQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private static IEvent<T> Ev<T>(T data) where T : notnull => new FakeEvent<T>(data);

    private static TaskPassage Compute(
        IReadOnlyList<IEvent> taskEvents, IReadOnlyList<RunEventSet> runs, TaskType? taskType = null,
        bool taskConcluded = false) =>
        TaskPassageQuery.Compute(taskEvents, runs, taskType ?? TaskType.Feature, taskConcluded, Now);

    [Fact]
    public void A_task_never_assigned_reports_nothing()
    {
        TaskPassage passage = Compute([], []);

        passage.Queued.Applicable.Should().BeFalse();
        passage.Building.Applicable.Should().BeFalse();
        passage.Delivery.Applicable.Should().BeFalse();
        passage.MergeWait.Applicable.Should().BeFalse();
        passage.ClaimToMerge.Applicable.Should().BeFalse();
        passage.HumanWaits.Should().BeEmpty();
        passage.Laps.Should().BeEmpty();
        passage.Sessions.Should().Be(0);
    }

    [Fact]
    public void Queued_sums_assigned_to_claimed()
    {
        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();
        DateTimeOffset assignedAt = Now.AddMinutes(-30);
        DateTimeOffset claimedAt = Now.AddMinutes(-18);

        List<IEvent> taskEvents =
        [
            Ev(new TaskAssigned(taskId, ownerId, [], assignedAt, ownerId)),
            Ev(new TaskClaimed(taskId, ownerId, ownerId, 1, DomainId.New(), claimedAt)),
        ];

        TaskPassage passage = Compute(taskEvents, []);

        passage.Queued.Applicable.Should().BeTrue();
        passage.Queued.StillOpen.Should().BeFalse();
        passage.Queued.Elapsed.Should().Be(TimeSpan.FromMinutes(12));
    }

    [Fact]
    public void Queued_excludes_time_blocked_on_a_dependency()
    {
        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid dependencyId = DomainId.New();
        DateTimeOffset assignedAt = Now.AddHours(-2);
        DateTimeOffset dependencyClearedAt = Now.AddHours(-1);
        DateTimeOffset claimedAt = Now.AddMinutes(-45);

        List<IEvent> taskEvents =
        [
            Ev(new TaskAssigned(taskId, ownerId, [dependencyId], assignedAt, ownerId)),
            Ev(new TaskDependencyCompleted(taskId, dependencyId, [], dependencyClearedAt)),
            Ev(new TaskClaimed(taskId, ownerId, ownerId, 1, DomainId.New(), claimedAt)),
        ];

        TaskPassage passage = Compute(taskEvents, []);

        // Blocked for the first hour (assigned -> dependency cleared); queued only for the
        // remaining 15 minutes up to the claim.
        passage.Queued.Elapsed.Should().Be(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void Queued_still_open_reports_elapsed_so_far()
    {
        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();
        DateTimeOffset assignedAt = Now.AddMinutes(-20);

        List<IEvent> taskEvents = [Ev(new TaskAssigned(taskId, ownerId, [], assignedAt, ownerId))];

        TaskPassage passage = Compute(taskEvents, []);

        passage.Queued.Applicable.Should().BeTrue();
        passage.Queued.StillOpen.Should().BeTrue();
        passage.Queued.Elapsed.Should().Be(TimeSpan.FromMinutes(20));
    }

    [Fact]
    public void Queued_closes_when_a_task_is_unassigned_before_being_claimed()
    {
        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();
        DateTimeOffset assignedAt = Now.AddDays(-5);
        DateTimeOffset unassignedAt = Now.AddDays(-4);
        DateTimeOffset reassignedAt = Now.AddHours(-2);
        DateTimeOffset claimedAt = Now.AddHours(-1);

        List<IEvent> taskEvents =
        [
            Ev(new TaskAssigned(taskId, ownerId, [], assignedAt, ownerId)),
            Ev(new TaskUnassigned(taskId, null, unassignedAt, ownerId)),
            Ev(new TaskAssigned(taskId, ownerId, [], reassignedAt, ownerId)),
            Ev(new TaskClaimed(taskId, ownerId, ownerId, 1, DomainId.New(), claimedAt)),
        ];

        TaskPassage passage = Compute(taskEvents, []);

        // The first assign-to-unassign day is real queued time and must neither vanish when the
        // second TaskAssigned overwrites segmentStart, nor keep growing after the task left the
        // queue entirely (independent pre-PR review, cycle 3, adversarial finding).
        passage.Queued.StillOpen.Should().BeFalse();
        passage.Queued.Elapsed.Should().Be(TimeSpan.FromDays(1) + TimeSpan.FromHours(1));
    }

    [Fact]
    public void Queued_closes_when_a_task_is_abandoned_before_being_claimed()
    {
        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();
        DateTimeOffset assignedAt = Now.AddDays(-12);
        DateTimeOffset abandonedAt = Now.AddDays(-5);

        List<IEvent> taskEvents =
        [
            Ev(new TaskAssigned(taskId, ownerId, [], assignedAt, ownerId)),
            Ev(new TaskAbandoned(taskId, "no longer needed", abandonedAt, ownerId)),
        ];

        TaskPassage passage = Compute(taskEvents, []);

        // Without a close on TaskAbandoned this kept reading "queued so far", growing on every
        // read of an archived task (independent pre-PR review, cycle 3, adversarial finding).
        passage.Queued.StillOpen.Should().BeFalse();
        passage.Queued.Elapsed.Should().Be(TimeSpan.FromDays(7));
    }

    [Fact]
    public void Queued_excludes_a_dependency_hold_that_carries_through_a_reopen_after_a_deliberate_claim()
    {
        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid dependencyId = DomainId.New();
        Guid runId = DomainId.New();
        DateTimeOffset assignedAt = Now.AddDays(-3);
        DateTimeOffset claimedAt = assignedAt.AddMinutes(10);
        DateTimeOffset reopenedAt = Now.AddHours(-2);
        DateTimeOffset reclaimedAt = Now.AddHours(-1);

        // The dependency never actually clears — no TaskDependencyCompleted ever lands — the
        // human simply claimed past it (h9k task start --acknowledge-unmet-dependencies), and
        // the task later reaches Done and is reopened for a follow-up lap while the dependency
        // is still genuinely unmet. TaskDetailsProjection.Apply(TaskReopened) would land this at
        // Blocked, not Queued, for exactly that reason (independent pre-PR review, cycle 3,
        // conformance finding).
        List<IEvent> taskEvents =
        [
            Ev(new TaskAssigned(taskId, ownerId, [dependencyId], assignedAt, ownerId)),
            Ev(new TaskClaimed(taskId, ownerId, ownerId, 1, runId, claimedAt)),
            Ev(new TaskReopened(taskId, runId, "task/x", null, reopenedAt, ownerId)),
            Ev(new TaskClaimed(taskId, ownerId, ownerId, 2, runId, reclaimedAt)),
        ];

        TaskPassage passage = Compute(taskEvents, []);

        passage.Queued.Elapsed.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Building_runs_from_dispatch_to_the_first_verification()
    {
        Guid runId = DomainId.New();
        DateTimeOffset dispatchedAt = Now.AddHours(-2);
        DateTimeOffset passedAt = Now.AddHours(-1);

        RunEventSet run = new(runId, dispatchedAt, Now.AddMinutes(-5),
            [Ev(new VerificationPassed(runId, passedAt))]);

        TaskPassage passage = Compute([], [run]);

        passage.Building.Applicable.Should().BeTrue();
        passage.Building.StillOpen.Should().BeFalse();
        passage.Building.Elapsed.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void Building_still_open_on_the_newest_run_with_no_verification_yet()
    {
        Guid runId = DomainId.New();
        DateTimeOffset dispatchedAt = Now.AddMinutes(-10);

        RunEventSet run = new(runId, dispatchedAt, null, []);

        TaskPassage passage = Compute([], [run]);

        passage.Building.StillOpen.Should().BeTrue();
        passage.Building.Elapsed.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void Building_closes_on_review_parked_for_a_pr_review_run_that_never_verifies_or_dispatches_review()
    {
        // RunSupervisor.HandleResultAsync routes a pr-review task's run to PrReviewEngine
        // entirely — it never appends VerificationPassed/Failed or ReviewDispatched, so
        // ReviewParked is the only boundary that closes what would otherwise read as "build"
        // forever, double-counted against the ReviewPark human wait below (independent pre-PR
        // review, cycle 3, both lenses).
        Guid runId = DomainId.New();
        Guid sessionId = DomainId.New();
        DateTimeOffset dispatchedAt = Now.AddDays(-3);
        DateTimeOffset conformanceDispatchedAt = dispatchedAt.AddMinutes(10);
        DateTimeOffset parkedAt = dispatchedAt.AddMinutes(25);

        RunEventSet run = new(runId, dispatchedAt, null,
        [
            Ev(new PrReviewConformanceDispatched(runId, sessionId, 123, conformanceDispatchedAt, conformanceDispatchedAt, AgentModel.Unknown)),
            Ev(new ReviewParked(runId, "findings ready", parkedAt)),
        ]);

        TaskPassage passage = Compute([], [run], TaskType.PrReview);

        passage.Building.StillOpen.Should().BeFalse();
        passage.Building.Elapsed.Should().Be(TimeSpan.FromMinutes(25));

        HumanWaitPassage wait = passage.HumanWaits.Should().ContainSingle(w => w.Kind == HumanWaitKind.ReviewPark).Subject;
        wait.Elapsed.StillOpen.Should().BeTrue();
    }

    [Fact]
    public void Review_park_closes_on_pr_review_delivered_for_a_completed_pr_review_run()
    {
        // ResolvePrReviewAsync (h9k review resolve --merge-ready on a pr-review task) appends only
        // PrReviewDelivered — never ReviewParkResolved/ReviewBoundaryApproved/ReviewHumanFixApplied,
        // the three events the review-park fold otherwise closes on — so the still-open park has to
        // close here or it is silently dropped once the run finishes right behind it (independent
        // pre-PR review, cycle 4, conformance finding).
        Guid runId = DomainId.New();
        Guid sessionId = DomainId.New();
        DateTimeOffset dispatchedAt = Now.AddDays(-3);
        DateTimeOffset conformanceDispatchedAt = dispatchedAt.AddMinutes(10);
        DateTimeOffset parkedAt = dispatchedAt.AddMinutes(25);
        DateTimeOffset deliveredAt = Now.AddHours(-1);

        RunEventSet run = new(runId, dispatchedAt, deliveredAt,
        [
            Ev(new PrReviewConformanceDispatched(runId, sessionId, 123, conformanceDispatchedAt, conformanceDispatchedAt, AgentModel.Unknown)),
            Ev(new ReviewParked(runId, "findings ready", parkedAt)),
            Ev(new PrReviewDelivered(runId, null, deliveredAt, DomainId.New())),
        ]);

        TaskPassage passage = Compute([], [run], TaskType.PrReview);

        HumanWaitPassage wait = passage.HumanWaits.Should().ContainSingle(w => w.Kind == HumanWaitKind.ReviewPark).Subject;
        wait.Elapsed.StillOpen.Should().BeFalse();
        wait.Elapsed.Elapsed.Should().Be(deliveredAt - parkedAt);
    }

    [Fact]
    public void Building_closes_on_review_parked_for_a_mention_follow_up_run_with_no_dispatch_recorded_at_all()
    {
        // DriveMentionFollowUpAsync appends only ReviewParked — no PrReviewConformanceDispatched,
        // no verification, nothing else — so that single event has to be the boundary on its own
        // (independent pre-PR review, cycle 3, conformance finding).
        Guid runId = DomainId.New();
        DateTimeOffset dispatchedAt = Now.AddHours(-1);
        DateTimeOffset parkedAt = Now.AddMinutes(-45);

        RunEventSet run = new(runId, dispatchedAt, null, [Ev(new ReviewParked(runId, "addendum ready", parkedAt))]);

        TaskPassage passage = Compute([], [run], TaskType.PrReview);

        passage.Building.StillOpen.Should().BeFalse();
        passage.Building.Elapsed.Should().Be(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void Gates_sum_every_recorded_verification_across_every_run_rather_than_only_the_last()
    {
        Guid runId = DomainId.New();
        DateTimeOffset dispatchedAt = Now.AddHours(-3);

        List<IEvent> runEvents =
        [
            Ev(new VerificationFailed(runId, ["test"], Now.AddHours(-2),
                [new GateDuration("test", TimeSpan.FromMinutes(4), Passed: false)])),
            Ev(new VerificationPassed(runId, Now.AddHours(-1), GateDurations:
                [new GateDuration("build", TimeSpan.FromMinutes(1), Passed: true), new GateDuration("test", TimeSpan.FromMinutes(3), Passed: true)])),
        ];
        RunEventSet run = new(runId, dispatchedAt, null, runEvents);

        TaskPassage passage = Compute([], [run]);

        passage.Gates.Should().Be(TimeSpan.FromMinutes(4 + 1 + 3));
    }

    [Fact]
    public void Review_sums_every_cycle_and_every_fix_session()
    {
        Guid runId = DomainId.New();
        DateTimeOffset dispatchedAt = Now.AddHours(-4);

        List<IEvent> runEvents =
        [
            Ev(new ReviewDispatched(runId, DomainId.New(), 1, 1, Now.AddHours(-3), Now.AddHours(-3))),
            Ev(new ReviewFixDispatched(runId, DomainId.New(), 1, 2, Now.AddHours(-2).AddMinutes(-30), Now.AddHours(-2).AddMinutes(-30))),
            Ev(new ReviewFixCompleted(runId, 1, ReviewFixOutcome.Fixed, Now.AddHours(-2))),
            Ev(new ReviewDispatched(runId, DomainId.New(), 2, 3, Now.AddHours(-2), Now.AddHours(-2))),
            Ev(new ReviewCompleted(runId, 2, ReviewVerdict.MergeReady, Now.AddHours(-1))),
        ];
        RunEventSet run = new(runId, dispatchedAt, Now.AddMinutes(-30), runEvents);

        TaskPassage passage = Compute([], [run]);

        // Cycle 1 was dispatched but never completed (superseded by cycle 2's own dispatch) — the
        // run has since finished, so cycle 1's own elapsed is dropped rather than guessed.
        passage.Review.Cycles.Should().Be(1);
        passage.Review.Elapsed.Should().Be(TimeSpan.FromHours(1));
        passage.Review.FixSessions.Should().Be(1);
        passage.Review.FixElapsed.Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Review_cycle_still_running_on_the_newest_run_reports_elapsed_so_far()
    {
        Guid runId = DomainId.New();
        DateTimeOffset dispatchedAt = Now.AddMinutes(-40);
        DateTimeOffset reviewDispatchedAt = Now.AddMinutes(-20);

        RunEventSet run = new(runId, dispatchedAt, null,
            [Ev(new ReviewDispatched(runId, DomainId.New(), 1, 1, reviewDispatchedAt, reviewDispatchedAt))]);

        TaskPassage passage = Compute([], [run]);

        passage.Review.Cycles.Should().Be(0);
        passage.Review.StillOpen.Should().BeTrue();
        passage.Review.Elapsed.Should().Be(TimeSpan.FromMinutes(20));
    }

    [Fact]
    public void Delivery_runs_from_the_final_review_verdict_to_taskcompleted()
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        DateTimeOffset reviewCompletedAt = Now.AddMinutes(-40);
        DateTimeOffset completedAt = Now.AddMinutes(-10);

        RunEventSet run = new(runId, Now.AddHours(-2), completedAt,
            [Ev(new ReviewCompleted(runId, 1, ReviewVerdict.MergeReady, reviewCompletedAt))]);
        List<IEvent> taskEvents = [Ev(new TaskCompleted(taskId, runId, "https://github.com/o/r/pull/1", completedAt))];

        TaskPassage passage = Compute(taskEvents, [run]);

        passage.Delivery.Applicable.Should().BeTrue();
        passage.Delivery.Elapsed.Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Delivery_is_not_applicable_when_no_push_ever_happened()
    {
        TaskPassage passage = Compute([], []);

        passage.Delivery.Applicable.Should().BeFalse();
    }

    [Fact]
    public void Merge_wait_runs_from_the_last_taskcompleted_to_the_merge()
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        DateTimeOffset completedAt = Now.AddHours(-3);
        DateTimeOffset mergedAt = Now.AddHours(-1);

        RunEventSet run = new(runId, Now.AddHours(-4), Now.AddHours(-1),
            [Ev(new PullRequestMerged(runId, mergedAt, mergedAt))]);
        List<IEvent> taskEvents = [Ev(new TaskCompleted(taskId, runId, "https://github.com/o/r/pull/1", completedAt))];

        TaskPassage passage = Compute(taskEvents, [run], taskConcluded: true);

        passage.MergeWait.Applicable.Should().BeTrue();
        passage.MergeWait.StillOpen.Should().BeFalse();
        passage.MergeWait.Elapsed.Should().Be(TimeSpan.FromHours(2));
    }

    [Fact]
    public void Merge_wait_still_open_when_the_task_has_not_concluded_yet()
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        DateTimeOffset completedAt = Now.AddHours(-1);

        List<IEvent> taskEvents = [Ev(new TaskCompleted(taskId, runId, "https://github.com/o/r/pull/1", completedAt))];

        TaskPassage passage = Compute(taskEvents, [], taskConcluded: false);

        passage.MergeWait.StillOpen.Should().BeTrue();
        passage.MergeWait.Elapsed.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void Merge_wait_says_unknown_rather_than_zero_when_the_task_concluded_with_no_merge_recorded()
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();

        List<IEvent> taskEvents = [Ev(new TaskCompleted(taskId, runId, "https://github.com/o/r/pull/1", Now.AddHours(-1)))];

        TaskPassage passage = Compute(taskEvents, [], taskConcluded: true);

        passage.MergeWait.Applicable.Should().BeTrue();
        passage.MergeWait.IsUnknown.Should().BeTrue();
    }

    [Fact]
    public void Merge_wait_is_unknown_when_the_pull_request_closed_without_a_merge_even_though_the_task_is_not_marked_concluded()
    {
        // CloseoutEngine.PollOnceAsync's own orphaned-run query permanently excludes a run once
        // it carries PullRequestClosed — nothing will ever watch this pull request again, so this
        // must read Unknown() regardless of the caller's own taskConcluded flag (independent
        // pre-PR review, cycle 3, conformance finding).
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        DateTimeOffset completedAt = Now.AddHours(-3);
        DateTimeOffset closedAt = Now.AddHours(-1);

        RunEventSet run = new(runId, Now.AddHours(-4), closedAt,
            [Ev(new PullRequestClosed(runId, closedAt, closedAt))]);
        List<IEvent> taskEvents = [Ev(new TaskCompleted(taskId, runId, "https://github.com/o/r/pull/1", completedAt))];

        TaskPassage passage = Compute(taskEvents, [run], taskConcluded: false);

        passage.MergeWait.Applicable.Should().BeTrue();
        passage.MergeWait.IsUnknown.Should().BeTrue();
    }

    [Fact]
    public void Claim_to_merge_runs_from_the_first_claim_to_the_merge()
    {
        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid runId = DomainId.New();
        DateTimeOffset claimedAt = Now.AddHours(-6);
        DateTimeOffset mergedAt = Now.AddHours(-1);

        List<IEvent> taskEvents = [Ev(new TaskClaimed(taskId, ownerId, ownerId, 1, runId, claimedAt))];
        RunEventSet run = new(runId, claimedAt, mergedAt, [Ev(new PullRequestMerged(runId, mergedAt, mergedAt))]);

        TaskPassage passage = Compute(taskEvents, [run], taskConcluded: true);

        passage.ClaimToMerge.Elapsed.Should().Be(TimeSpan.FromHours(5));
    }

    [Fact]
    public void Review_park_is_its_own_human_wait_row()
    {
        Guid runId = DomainId.New();
        DateTimeOffset parkedAt = Now.AddHours(-3);
        DateTimeOffset resolvedAt = Now.AddHours(-1);

        RunEventSet run = new(runId, Now.AddHours(-4), null,
        [
            Ev(new ReviewParked(runId, "cap reached", parkedAt)),
            Ev(new ReviewParkResolved(runId, ReviewVerdict.MergeReady, null, resolvedAt, DomainId.New())),
        ]);

        TaskPassage passage = Compute([], [run]);

        HumanWaitPassage wait = passage.HumanWaits.Should().ContainSingle(w => w.Kind == HumanWaitKind.ReviewPark).Subject;
        wait.Elapsed.Elapsed.Should().Be(TimeSpan.FromHours(2));
    }

    [Fact]
    public void Review_park_also_closes_on_a_bare_boundary_approval()
    {
        Guid runId = DomainId.New();
        DateTimeOffset parkedAt = Now.AddMinutes(-30);
        DateTimeOffset approvedAt = Now.AddMinutes(-5);

        RunEventSet run = new(runId, Now.AddHours(-1), null,
        [
            Ev(new ReviewParked(runId, "interactive gate", parkedAt, IsInteractiveGate: true)),
            Ev(new ReviewBoundaryApproved(runId, approvedAt, DomainId.New())),
        ]);

        TaskPassage passage = Compute([], [run]);

        HumanWaitPassage wait = passage.HumanWaits.Should().ContainSingle(w => w.Kind == HumanWaitKind.ReviewPark).Subject;
        wait.Elapsed.Elapsed.Should().Be(TimeSpan.FromMinutes(25));
    }

    [Fact]
    public void Closeout_park_is_its_own_human_wait_row()
    {
        Guid runId = DomainId.New();
        DateTimeOffset parkedAt = Now.AddHours(-2);
        DateTimeOffset grantedAt = Now.AddHours(-1);

        RunEventSet run = new(runId, Now.AddHours(-3), null,
        [
            Ev(new CloseoutParked(runId, "budget", parkedAt)),
            Ev(new CloseoutBudgetGranted(runId, null, grantedAt)),
        ]);

        TaskPassage passage = Compute([], [run]);

        HumanWaitPassage wait = passage.HumanWaits.Should().ContainSingle(w => w.Kind == HumanWaitKind.CloseoutPark).Subject;
        wait.Elapsed.Elapsed.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void A_question_asked_and_answered_is_its_own_human_wait_row()
    {
        Guid taskId = DomainId.New();
        Guid questionId = DomainId.New();
        DateTimeOffset askedAt = Now.AddMinutes(-50);
        DateTimeOffset answeredAt = Now.AddMinutes(-10);

        List<IEvent> taskEvents =
        [
            Ev(new QuestionAsked(taskId, questionId, DomainId.New(), "which approach?", askedAt)),
            Ev(new AnswerProvided(taskId, questionId, "the first one", answeredAt, DomainId.New())),
        ];

        TaskPassage passage = Compute(taskEvents, []);

        HumanWaitPassage wait = passage.HumanWaits.Should().ContainSingle(w => w.Kind == HumanWaitKind.Question).Subject;
        wait.Elapsed.Elapsed.Should().Be(TimeSpan.FromMinutes(40));
    }

    [Fact]
    public void An_unanswered_question_is_open_only_while_the_run_is_still_live()
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        DateTimeOffset askedAt = Now.AddMinutes(-15);

        List<IEvent> taskEvents = [Ev(new QuestionAsked(taskId, DomainId.New(), runId, "which approach?", askedAt))];
        RunEventSet run = new(runId, Now.AddMinutes(-20), null, []);

        TaskPassage passage = Compute(taskEvents, [run]);

        HumanWaitPassage wait = passage.HumanWaits.Should().ContainSingle(w => w.Kind == HumanWaitKind.Question).Subject;
        wait.Elapsed.StillOpen.Should().BeTrue();
        wait.Elapsed.Elapsed.Should().Be(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void Pending_external_review_only_ever_shows_for_a_pr_review_task()
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        DateTimeOffset requestedAt = Now.AddHours(-2);
        DateTimeOffset deliveredAt = Now.AddHours(-1);

        List<IEvent> taskEvents =
        [
            Ev(new PullRequestReviewAssignmentObserved(taskId, "https://github.com/o/r/pull/9", "alice", "bob", requestedAt, requestedAt)),
        ];
        RunEventSet run = new(runId, requestedAt, deliveredAt, [Ev(new PrReviewDelivered(runId, null, deliveredAt, DomainId.New()))]);

        TaskPassage ordinary = Compute(taskEvents, [run], TaskType.Feature);
        ordinary.HumanWaits.Should().NotContain(w => w.Kind == HumanWaitKind.PendingExternalReview);

        TaskPassage prReview = Compute(taskEvents, [run], TaskType.PrReview);
        HumanWaitPassage wait = prReview.HumanWaits.Should()
            .ContainSingle(w => w.Kind == HumanWaitKind.PendingExternalReview).Subject;
        wait.Elapsed.Elapsed.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void Pending_external_review_closes_on_a_concluded_recall_rather_than_reporting_unknown()
    {
        // AutoPrReviewEngine.ConcludeOneAsync appends PullRequestReviewAssignmentRecalled with
        // Concluded true when the request is withdrawn before the run ever dispatches — the
        // stream records exactly when the wait ended, and this must read it rather than falling
        // back to Unknown() (independent pre-PR review, cycle 3, adversarial finding).
        Guid taskId = DomainId.New();
        DateTimeOffset requestedAt = Now.AddHours(-2);
        DateTimeOffset recalledAt = Now.AddHours(-1);

        List<IEvent> taskEvents =
        [
            Ev(new PullRequestReviewAssignmentObserved(taskId, "https://github.com/o/r/pull/9", "alice", "bob", requestedAt, requestedAt)),
            Ev(new PullRequestReviewAssignmentRecalled(taskId, "https://github.com/o/r/pull/9", "alice", recalledAt, Concluded: true)),
        ];

        TaskPassage passage = Compute(taskEvents, [], TaskType.PrReview);

        HumanWaitPassage wait = passage.HumanWaits.Should()
            .ContainSingle(w => w.Kind == HumanWaitKind.PendingExternalReview).Subject;
        wait.Elapsed.IsUnknown.Should().BeFalse();
        wait.Elapsed.Elapsed.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void Pending_external_review_stays_open_on_an_unconcluded_recall_since_the_work_continues()
    {
        // Concluded false means the removal landed after the run already dispatched —
        // AutoPrReviewEngine's own comment: "recorded as an observation; the work continues" —
        // so the real close is still whatever PrReviewDelivered eventually lands, not this
        // recall. The run this dispatched is still live, the same as the production shape.
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        DateTimeOffset requestedAt = Now.AddHours(-2);
        DateTimeOffset recalledAt = Now.AddHours(-1);

        List<IEvent> taskEvents =
        [
            Ev(new PullRequestReviewAssignmentObserved(taskId, "https://github.com/o/r/pull/9", "alice", "bob", requestedAt, requestedAt)),
            Ev(new PullRequestReviewAssignmentRecalled(taskId, "https://github.com/o/r/pull/9", "alice", recalledAt, Concluded: false)),
        ];
        RunEventSet run = new(runId, requestedAt, null, []);

        TaskPassage passage = Compute(taskEvents, [run], TaskType.PrReview);

        HumanWaitPassage wait = passage.HumanWaits.Should()
            .ContainSingle(w => w.Kind == HumanWaitKind.PendingExternalReview).Subject;
        wait.Elapsed.StillOpen.Should().BeTrue();
    }

    [Fact]
    public void Laps_are_counted_by_kind_and_unknown_normalizes_to_review_feedback()
    {
        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();

        List<IEvent> taskEvents =
        [
            Ev(new TaskReopened(taskId, DomainId.New(), "task/x", null, Now.AddHours(-3), ownerId, FollowUpKind.Rebase)),
            Ev(new TaskReopened(taskId, DomainId.New(), "task/x", null, Now.AddHours(-2), ownerId, FollowUpKind.FailingChecks)),
            Ev(new TaskReopened(taskId, DomainId.New(), "task/x", null, Now.AddHours(-1), ownerId, null)),
        ];

        TaskPassage passage = Compute(taskEvents, []);

        passage.Laps.Sum(l => l.Count).Should().Be(3);
        passage.Laps.Should().ContainSingle(l => l.Kind == FollowUpKind.Rebase && l.Count == 1);
        passage.Laps.Should().ContainSingle(l => l.Kind == FollowUpKind.FailingChecks && l.Count == 1);
        passage.Laps.Should().ContainSingle(l => l.Kind == FollowUpKind.ReviewFeedback && l.Count == 1);
    }

    [Fact]
    public void Queued_sums_time_after_a_retry_even_with_no_requeue_event()
    {
        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();
        DateTimeOffset retriedAt = Now.AddMinutes(-30);
        DateTimeOffset claimedAt = Now.AddMinutes(-10);

        // h9k task retry appends only TaskRetried, never a TaskRequeued alongside it
        // (independent pre-PR review, cycle 1, adversarial lens) — the queued wait after a
        // retry has to open on TaskRetried's own timestamp or it is dropped entirely.
        List<IEvent> taskEvents =
        [
            Ev(new TaskRetried(taskId, null, null, "flaky infra", retriedAt, ownerId)),
            Ev(new TaskClaimed(taskId, ownerId, ownerId, 1, DomainId.New(), claimedAt)),
        ];

        TaskPassage passage = Compute(taskEvents, []);

        passage.Queued.Applicable.Should().BeTrue();
        passage.Queued.Elapsed.Should().Be(TimeSpan.FromMinutes(20));
    }

    [Fact]
    public void Queued_sums_time_after_a_handback_even_with_no_requeue_event()
    {
        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid runId = DomainId.New();
        DateTimeOffset handedBackAt = Now.AddMinutes(-25);
        DateTimeOffset claimedAt = Now.AddMinutes(-5);

        List<IEvent> taskEvents =
        [
            Ev(new TaskHandedBack(taskId, runId, "task/x", "back to headless", handedBackAt, ownerId)),
            Ev(new TaskClaimed(taskId, ownerId, ownerId, 1, DomainId.New(), claimedAt)),
        ];

        TaskPassage passage = Compute(taskEvents, []);

        passage.Queued.Applicable.Should().BeTrue();
        passage.Queued.Elapsed.Should().Be(TimeSpan.FromMinutes(20));
    }

    [Fact]
    public void Building_drops_an_older_run_whose_lease_expired_and_was_never_resolved()
    {
        // DispatchEngine.RequeueExpiredLeasesAsync reclaims a task whose lease expired on
        // another node but deliberately leaves that node's own run alone — no RunSuperseded,
        // no RunFailed — so its FinishedAt (and FoldRun's own BuildEnd fallback) never fires.
        // The task is reclaimed and a second run dispatches and finishes normally; the fold must
        // not let the first run's own dangling shape mark the whole phase still open.
        Guid stuckRunId = DomainId.New();
        Guid finishedRunId = DomainId.New();
        DateTimeOffset finishedRunDispatchedAt = Now.AddHours(-2);
        DateTimeOffset finishedRunPassedAt = Now.AddHours(-1);

        RunEventSet stuckRun = new(stuckRunId, Now.AddHours(-5), null, []);
        RunEventSet finishedRun = new(finishedRunId, finishedRunDispatchedAt, Now.AddMinutes(-50),
            [Ev(new VerificationPassed(finishedRunId, finishedRunPassedAt))]);

        TaskPassage passage = Compute([], [stuckRun, finishedRun]);

        passage.Building.Applicable.Should().BeTrue();
        passage.Building.StillOpen.Should().BeFalse();
        passage.Building.Elapsed.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void Merge_wait_clamps_to_zero_rather_than_a_negative_span()
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        DateTimeOffset mergedAt = Now.AddHours(-3);
        DateTimeOffset completedAt = Now.AddHours(-1);

        RunEventSet run = new(runId, Now.AddHours(-4), completedAt,
            [Ev(new PullRequestMerged(runId, mergedAt, mergedAt))]);
        List<IEvent> taskEvents = [Ev(new TaskCompleted(taskId, runId, "https://github.com/o/r/pull/1", completedAt))];

        TaskPassage passage = Compute(taskEvents, [run], taskConcluded: true);

        passage.MergeWait.Elapsed.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void A_second_taskcompleted_on_the_same_run_does_not_double_count_delivery_or_merge_wait()
    {
        // CloseoutEngine.CompleteCloseoutAsync re-appends TaskDecider.Complete on the identical
        // run id when a Blocked task's own merge-observation closeout lands, dated to the
        // observation rather than the earlier push PullRequestOpener already recorded a
        // TaskCompleted for.
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        DateTimeOffset reviewCompletedAt = Now.AddHours(-5);
        DateTimeOffset pushedAt = Now.AddHours(-4);
        DateTimeOffset mergedAt = Now.AddHours(-1);
        DateTimeOffset observedAt = Now;

        RunEventSet run = new(runId, Now.AddHours(-6), observedAt,
        [
            Ev(new ReviewCompleted(runId, 1, ReviewVerdict.MergeReady, reviewCompletedAt)),
            Ev(new PullRequestMerged(runId, mergedAt, observedAt)),
        ]);
        List<IEvent> taskEvents =
        [
            Ev(new TaskCompleted(taskId, runId, "https://github.com/o/r/pull/1", pushedAt)),
            Ev(new TaskCompleted(taskId, runId, "https://github.com/o/r/pull/1", observedAt)),
        ];

        TaskPassage passage = Compute(taskEvents, [run], taskConcluded: true);

        passage.Delivery.Elapsed.Should().Be(pushedAt - reviewCompletedAt);
        passage.MergeWait.Elapsed.Should().Be(mergedAt - pushedAt);
    }

    [Fact]
    public void A_pr_review_task_has_no_merge_wait_or_claim_to_merge_of_its_own()
    {
        // PrReviewFollowThroughEngine completes a pr-review task carrying the reviewed pull
        // request's own URL, but no pr-review run stream ever appends PullRequestMerged for it
        // — that pull request is never this task's own to merge.
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();

        List<IEvent> taskEvents = [Ev(new TaskCompleted(taskId, runId, "https://github.com/o/r/pull/9", Now.AddHours(-1)))];

        TaskPassage passage = Compute(taskEvents, [], TaskType.PrReview, taskConcluded: true);

        passage.MergeWait.Applicable.Should().BeFalse();
        passage.ClaimToMerge.Applicable.Should().BeFalse();
    }

    [Fact]
    public void An_unanswered_question_from_a_superseded_run_is_dropped_rather_than_attributed_to_a_later_live_run()
    {
        Guid taskId = DomainId.New();
        Guid oldRunId = DomainId.New();
        Guid newRunId = DomainId.New();
        DateTimeOffset askedAt = Now.AddDays(-3);

        List<IEvent> taskEvents = [Ev(new QuestionAsked(taskId, DomainId.New(), oldRunId, "which approach?", askedAt))];
        RunEventSet oldRun = new(oldRunId, Now.AddDays(-3).AddMinutes(-10), Now.AddDays(-2), []);
        RunEventSet newRun = new(newRunId, Now.AddDays(-2), null, []);

        TaskPassage passage = Compute(taskEvents, [oldRun, newRun]);

        passage.HumanWaits.Should().NotContain(w => w.Kind == HumanWaitKind.Question);
    }

    [Fact]
    public void An_unanswered_question_on_a_run_that_has_ended_reports_unknown_rather_than_zero()
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        DateTimeOffset askedAt = Now.AddHours(-3);

        List<IEvent> taskEvents = [Ev(new QuestionAsked(taskId, DomainId.New(), runId, "which approach?", askedAt))];
        RunEventSet run = new(runId, Now.AddHours(-4), Now.AddHours(-1), []);

        TaskPassage passage = Compute(taskEvents, [run]);

        HumanWaitPassage wait = passage.HumanWaits.Should().ContainSingle(w => w.Kind == HumanWaitKind.Question).Subject;
        wait.Elapsed.IsUnknown.Should().BeTrue();
    }

    [Fact]
    public void Sessions_count_every_dispatch_across_every_run()
    {
        Guid runId = DomainId.New();
        DateTimeOffset dispatchedAt = Now.AddHours(-2);

        List<IEvent> runEvents =
        [
            Ev(new ReviewDispatched(runId, DomainId.New(), 1, 1, dispatchedAt, dispatchedAt)),
            Ev(new ReviewFixDispatched(runId, DomainId.New(), 1, 2, dispatchedAt, dispatchedAt)),
        ];
        RunEventSet run = new(runId, dispatchedAt, null, runEvents);

        TaskPassage passage = Compute([], [run]);

        // The run's own RunDispatched is recorded on the run stream in production; this test's
        // RunEventSet starts after dispatch, so only the two mid-run dispatches count here.
        passage.Sessions.Should().Be(2);
    }

    [Fact]
    public void Sessions_count_uncommitted_work_recovery_verdict_reprompt_and_context_synthesis_dispatches()
    {
        // These three each spawn a real Claude Code process too (VerificationRunner's bounded
        // commit-only agent, a same-session claude -p --resume for an unparseable verdict, and
        // BlockerContextAssembler's synthesis dispatch) but the closing "sessions" enumeration
        // omitted all three (independent pre-PR review, cycle 6, conformance finding).
        Guid runId = DomainId.New();
        DateTimeOffset at = Now.AddHours(-2);

        List<IEvent> runEvents =
        [
            Ev(new RunUncommittedWorkRecoveryAttempted(runId, DomainId.New(), ["a.txt"], "meaningful uncommitted files", at)),
            Ev(new ReviewVerdictReprompted(runId, DomainId.New(), DomainId.New(), 1, 1, at, at)),
            Ev(new ContextSynthesisDispatched(runId, DomainId.New(), 2, 1, at, at)),
        ];
        RunEventSet run = new(runId, at, null, runEvents);

        TaskPassage passage = Compute([], [run]);

        passage.Sessions.Should().Be(3);
    }

    [Fact]
    public void Interactive_session_started_for_the_run_dispatched_process_does_not_double_count()
    {
        // h9k task work/start's own interactive claim appends InteractiveSessionStarted for the
        // identical process RunDispatched already named (the same ClaudeSessionId/SessionId) —
        // counting both doubles a single launch (independent pre-PR review, cycle 6, adversarial
        // finding).
        Guid runId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid claudeSessionId = DomainId.New();
        DateTimeOffset dispatchedAt = Now.AddHours(-2);

        List<IEvent> runEvents =
        [
            Ev(new RunDispatched(
                runId, taskId, DomainId.New(), DomainId.New(), 1, claudeSessionId,
                "/wt/interactive", "task/interactive", ExecutorMode.Subscription, dispatchedAt)),
            Ev(new InteractiveSessionStarted(runId, claudeSessionId, dispatchedAt.AddSeconds(2), 123)),
        ];
        RunEventSet run = new(runId, dispatchedAt, null, runEvents);

        TaskPassage passage = Compute([], [run]);

        passage.Sessions.Should().Be(1);
    }

    [Fact]
    public void Interactive_session_started_with_a_different_session_id_counts_as_a_genuine_reattach()
    {
        Guid runId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid claudeSessionId = DomainId.New();
        DateTimeOffset dispatchedAt = Now.AddHours(-2);

        List<IEvent> runEvents =
        [
            Ev(new RunDispatched(
                runId, taskId, DomainId.New(), DomainId.New(), 1, claudeSessionId,
                "/wt/interactive", "task/interactive", ExecutorMode.Subscription, dispatchedAt)),
            Ev(new InteractiveSessionStarted(runId, DomainId.New(), dispatchedAt.AddMinutes(30), 456)),
        ];
        RunEventSet run = new(runId, dispatchedAt, null, runEvents);

        TaskPassage passage = Compute([], [run]);

        passage.Sessions.Should().Be(2);
    }

    [Fact]
    public void A_verdict_missing_park_with_no_review_completed_does_not_grow_review_elapsed_forever()
    {
        // ReviewPhase.VerdictMissing's own re-prompt-exhausted arm parks the run without ever
        // appending ReviewCompleted for the cycle it just dispatched, and nothing further
        // re-dispatches that same cycle — the dangling dispatch must not keep reading as "still
        // running" (independent pre-PR review, cycle 6, adversarial finding).
        Guid runId = DomainId.New();
        DateTimeOffset dispatchedAt = Now.AddDays(-2);
        DateTimeOffset parkedAt = dispatchedAt.AddHours(1);

        RunEventSet run = new(runId, dispatchedAt, null,
        [
            Ev(new ReviewDispatched(runId, DomainId.New(), 1, 1, dispatchedAt, dispatchedAt)),
            Ev(new ReviewParked(runId, "no parseable verdict after one re-prompt", parkedAt)),
        ]);

        TaskPassage passage = Compute([], [run]);

        passage.Review.Cycles.Should().Be(0);
        passage.Review.Elapsed.Should().Be(TimeSpan.Zero);
        passage.Review.StillOpen.Should().BeFalse();

        HumanWaitPassage wait = passage.HumanWaits.Should().ContainSingle(w => w.Kind == HumanWaitKind.ReviewPark).Subject;
        wait.Elapsed.StillOpen.Should().BeTrue();
    }

    [Fact]
    public void An_unresolved_closeout_park_on_the_last_run_after_it_merges_reports_unknown_rather_than_vanishing()
    {
        // CompleteMergeAsync appends RunCompleted alongside PullRequestMerged, so FinishedAt is
        // set even though CloseoutBudgetGranted never landed — the wait genuinely happened and
        // must not disappear just because the run it happened on has since finished (independent
        // pre-PR review, cycle 6, conformance finding — the high-severity fix).
        Guid runId = DomainId.New();
        DateTimeOffset dispatchedAt = Now.AddDays(-3);
        DateTimeOffset parkedAt = Now.AddDays(-2);
        DateTimeOffset mergedAt = Now.AddHours(-1);

        RunEventSet run = new(runId, dispatchedAt, mergedAt,
        [
            Ev(new CloseoutParked(runId, "closeout budget spent — merge without it", parkedAt)),
            Ev(new PullRequestMerged(runId, mergedAt, mergedAt)),
        ]);

        TaskPassage passage = Compute([], [run]);

        HumanWaitPassage wait = passage.HumanWaits.Should().ContainSingle(w => w.Kind == HumanWaitKind.CloseoutPark).Subject;
        wait.Elapsed.IsUnknown.Should().BeTrue();
    }

    [Fact]
    public void An_unresolved_review_park_on_the_last_run_after_it_is_released_reports_unknown_rather_than_vanishing()
    {
        // h9k task release/abandon/handback on a parked run appends RunSuperseded, closing the
        // run (FinishedAt set) without ever resolving the park — the same class as the closeout
        // case above (independent pre-PR review, cycle 6, conformance finding).
        Guid runId = DomainId.New();
        DateTimeOffset dispatchedAt = Now.AddDays(-2);
        DateTimeOffset parkedAt = Now.AddDays(-1);
        DateTimeOffset supersededAt = Now.AddHours(-3);

        RunEventSet run = new(runId, dispatchedAt, supersededAt,
        [
            Ev(new ReviewParked(runId, "cap reached", parkedAt)),
        ]);

        TaskPassage passage = Compute([], [run]);

        HumanWaitPassage wait = passage.HumanWaits.Should().ContainSingle(w => w.Kind == HumanWaitKind.ReviewPark).Subject;
        wait.Elapsed.IsUnknown.Should().BeTrue();
    }

    [Fact]
    public void A_review_park_interval_recorded_by_two_skewed_clocks_clamps_to_zero_rather_than_going_negative()
    {
        Guid runId = DomainId.New();
        DateTimeOffset parkedAt = Now.AddMinutes(-10);
        DateTimeOffset resolvedAt = Now.AddMinutes(-20);

        RunEventSet run = new(runId, Now.AddMinutes(-30), null,
        [
            Ev(new ReviewParked(runId, "cap reached", parkedAt)),
            Ev(new ReviewParkResolved(runId, ReviewVerdict.MergeReady, null, resolvedAt, DomainId.New())),
        ]);

        TaskPassage passage = Compute([], [run]);

        HumanWaitPassage wait = passage.HumanWaits.Should().ContainSingle(w => w.Kind == HumanWaitKind.ReviewPark).Subject;
        wait.Elapsed.Elapsed.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void A_still_dispatched_review_cycle_started_after_the_query_s_own_clock_clamps_to_zero()
    {
        Guid runId = DomainId.New();
        DateTimeOffset dispatchedAt = Now.AddMinutes(5);

        RunEventSet run = new(runId, Now.AddMinutes(-10), null,
        [
            Ev(new ReviewDispatched(runId, DomainId.New(), 1, 1, dispatchedAt, dispatchedAt)),
        ]);

        TaskPassage passage = Compute([], [run]);

        passage.Review.StillOpen.Should().BeTrue();
        passage.Review.Elapsed.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Merge_wait_still_open_clamps_to_zero_when_the_claim_time_is_after_the_query_s_own_clock()
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        DateTimeOffset completedAt = Now.AddMinutes(5);

        List<IEvent> taskEvents = [Ev(new TaskCompleted(taskId, runId, "https://github.com/o/r/pull/1", completedAt))];

        TaskPassage passage = Compute(taskEvents, [], taskConcluded: false);

        passage.MergeWait.StillOpen.Should().BeTrue();
        passage.MergeWait.Elapsed.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void An_unanswered_question_on_the_newest_ended_run_reports_unknown_even_though_an_earlier_question_was_answered()
    {
        // SumOpenIntervals used to collapse this shape to Closed(the earlier answered duration),
        // printing a confident exact figure for a task whose record actually says a later
        // question was never answered (independent pre-PR review, cycle 6, adversarial finding).
        Guid taskId = DomainId.New();
        Guid oldRunId = DomainId.New();
        Guid newRunId = DomainId.New();
        Guid questionId1 = DomainId.New();
        Guid questionId2 = DomainId.New();
        DateTimeOffset askedAt1 = Now.AddDays(-2);
        DateTimeOffset answeredAt1 = askedAt1.AddMinutes(10);
        DateTimeOffset askedAt2 = Now.AddDays(-1);

        List<IEvent> taskEvents =
        [
            Ev(new QuestionAsked(taskId, questionId1, oldRunId, "first?", askedAt1)),
            Ev(new AnswerProvided(taskId, questionId1, "the first answer", answeredAt1, DomainId.New())),
            Ev(new QuestionAsked(taskId, questionId2, newRunId, "second?", askedAt2)),
        ];
        RunEventSet oldRun = new(oldRunId, askedAt1.AddMinutes(-5), askedAt1.AddMinutes(20), []);
        RunEventSet newRun = new(newRunId, askedAt2.AddMinutes(-5), Now.AddHours(-1), []);

        TaskPassage passage = Compute(taskEvents, [oldRun, newRun]);

        HumanWaitPassage wait = passage.HumanWaits.Should().ContainSingle(w => w.Kind == HumanWaitKind.Question).Subject;
        wait.Elapsed.IsUnknown.Should().BeTrue();
    }
}
