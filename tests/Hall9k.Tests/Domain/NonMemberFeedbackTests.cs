using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The task-stream record of feedback held for the owner (task: a changes-requested review or an
/// unresolved review thread from someone outside the project's trust-chain roster never dispatches a
/// build lap): the aggregate and its projection fold the four events identically, the owner's
/// answer resolves a key or an unambiguous fragment of it, and the two replication registries
/// classify the go as something a Member-role teammate can never apply.
/// </summary>
public sealed class NonMemberFeedbackTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private const string PullRequest = "https://github.com/x/y/pull/7";
    private const string ReviewKey = $"{PullRequest}#pullrequestreview-42";
    private const string OtherReviewKey = $"{PullRequest}#pullrequestreview-43";

    private static TaskAggregate DoneTask()
    {
        Guid ownerId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Do the thing", ["it is done"], TaskType.Chore, null, null, null, Now, ownerId);
        (TaskAggregate task, _) = TaskSeed.Start(added, ownerId, Now);
        task.Apply(TaskDecider.Claim(task, DomainId.New(), ownerId, DomainId.New(), Now));
        task.Apply(TaskDecider.Complete(task, task.CurrentRunId!.Value, PullRequest, Now));
        return task;
    }

    private static ChangesRequestedReview Review(string url = ReviewKey) =>
        new("stranger", url, Now, [new ChangesRequestedFinding("The words nobody should print.")], 2001);

    private static NonMemberFeedbackHeld HeldReview(TaskAggregate task, string url = ReviewKey) =>
        new(task.Id, url, NonMemberFeedbackKind.Review, "stranger", url, Now, Review(url));

    private static NonMemberFeedbackHeld HeldThread(TaskAggregate task, string id = "PRRT_kwDO1") =>
        new(task.Id, id, NonMemberFeedbackKind.Thread, "stranger", $"{PullRequest}#discussion_r9", Now);

    [Fact]
    public void Held_feedback_is_one_undecided_item_per_key_however_often_it_is_recorded()
    {
        TaskAggregate task = DoneTask();

        task.Apply(HeldReview(task));
        task.Apply(HeldReview(task) with { ObservedAt = Now.AddMinutes(3) });

        NonMemberFeedbackItem item = task.NonMemberFeedback.Values.Should().ContainSingle().Subject;
        item.Status.Should().Be(NonMemberFeedbackStatus.Undecided);
        item.ObservedAt.Should().Be(Now, "the first observation stands");
        item.Review!.Findings.Should().ContainSingle("the review is kept so the go still works after the head has moved");
    }

    [Fact]
    public void The_owners_answer_and_a_withdrawal_each_clear_the_item_without_losing_its_key()
    {
        TaskAggregate task = DoneTask();
        task.Apply(HeldReview(task));
        task.Apply(HeldReview(task, OtherReviewKey));
        task.Apply(HeldThread(task));

        task.Apply(TaskDecider.AdmitNonMemberFeedback(task, ReviewKey, Now, DomainId.New()));
        task.Apply(TaskDecider.DeclineNonMemberFeedback(task, OtherReviewKey, Now, DomainId.New()));
        task.Apply(new NonMemberFeedbackWithdrawn(task.Id, "PRRT_kwDO1", Now));

        task.NonMemberFeedback[ReviewKey].Status.Should().Be(NonMemberFeedbackStatus.Admitted);
        task.NonMemberFeedback[OtherReviewKey].Status.Should().Be(NonMemberFeedbackStatus.Declined);
        task.NonMemberFeedback["PRRT_kwDO1"].Status.Should().Be(NonMemberFeedbackStatus.Withdrawn);
        task.NonMemberFeedback.Values.Should().OnlyContain(item => !item.Status.NeedsOwner);
    }

    [Fact]
    public void A_withdrawn_key_that_is_held_again_is_a_fresh_undecided_item()
    {
        TaskAggregate task = DoneTask();
        task.Apply(HeldThread(task));
        task.Apply(new NonMemberFeedbackWithdrawn(task.Id, "PRRT_kwDO1", Now));

        task.Apply(HeldThread(task) with { ObservedAt = Now.AddHours(1) });

        task.NonMemberFeedback["PRRT_kwDO1"].Status.Should().Be(NonMemberFeedbackStatus.Undecided);
        task.NonMemberFeedback["PRRT_kwDO1"].ObservedAt.Should().Be(Now.AddHours(1));
    }

    [Fact]
    public void A_lap_that_carries_a_held_review_marks_it_dispatched_so_a_go_buys_one_lap()
    {
        TaskAggregate task = DoneTask();
        task.Apply(HeldReview(task));
        task.Apply(TaskDecider.AdmitNonMemberFeedback(task, ReviewKey, Now, DomainId.New()));
        task.NonMemberFeedback[ReviewKey].Dispatched.Should().BeFalse();

        task.Apply(TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/abc-do-the-thing", "A human requested changes.",
            FollowUpKind.ReviewRequestedChanges, automatic: true, Now, DomainId.New(),
            changesRequestedReviews: [Review() with { Admission = FeedbackAdmission.OwnerApproved }]));

        task.NonMemberFeedback[ReviewKey].Dispatched.Should().BeTrue();
    }

    [Fact]
    public void A_reopen_records_the_read_scope_the_lap_may_read_and_the_laps_completion_keeps_it_for_a_manual_resolve()
    {
        TaskAggregate task = DoneTask();
        ReviewReadScope scope = new(
            [new ReadableReviewThread("PRRT_1", "member", FeedbackAdmission.Member, ["PRRC_1"])], ["PRRT_2"]);

        task.Apply(TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/abc-do-the-thing", "Threads.", FollowUpKind.ReviewFeedback,
            automatic: true, Now, DomainId.New(), reviewReadScope: scope));
        task.ReviewReadScope.Should().Be(scope);

        task.Apply(TaskDecider.Claim(task, DomainId.New(), task.AssignedOwnerId!.Value, DomainId.New(), Now));
        task.Apply(TaskDecider.Complete(task, task.CurrentRunId!.Value, PullRequest, Now));

        task.ReviewReadScope.Should().Be(
            scope, "h9k pr resolve reads it from a task that is Done by the time anyone runs it");
    }

    [Fact]
    public void The_owners_answer_resolves_the_full_key_the_link_or_an_unambiguous_fragment()
    {
        TaskAggregate task = DoneTask();
        task.Apply(HeldReview(task));
        task.Apply(HeldReview(task, OtherReviewKey));
        task.Apply(HeldThread(task));

        TaskDecider.AdmitNonMemberFeedback(task, ReviewKey, Now, DomainId.New()).FeedbackKey.Should().Be(ReviewKey);
        TaskDecider.AdmitNonMemberFeedback(task, "pullrequestreview-42", Now, DomainId.New())
            .FeedbackKey.Should().Be(ReviewKey);
        TaskDecider.DeclineNonMemberFeedback(task, "PRRT_kwDO", Now, DomainId.New())
            .FeedbackKey.Should().Be("PRRT_kwDO1");
        TaskDecider.AdmitNonMemberFeedback(task, $"{PullRequest}#discussion_r9", Now, DomainId.New())
            .FeedbackKey.Should().Be("PRRT_kwDO1", "a thread's link names it as well as its id does");
    }

    [Fact]
    public void An_ambiguous_missing_blank_or_already_decided_answer_is_refused_with_the_reason()
    {
        TaskAggregate task = DoneTask();
        task.Apply(HeldReview(task));
        task.Apply(HeldReview(task, OtherReviewKey));
        task.Apply(TaskDecider.DeclineNonMemberFeedback(task, ReviewKey, Now, DomainId.New()));
        task.Apply(new NonMemberFeedbackWithdrawn(task.Id, OtherReviewKey, Now));

        Action ambiguous = () => TaskDecider.FindUndecidedNonMemberFeedback(task, "pullrequestreview-4");
        Action missing = () => TaskDecider.FindUndecidedNonMemberFeedback(task, "PRRT_nothing");
        Action blank = () => TaskDecider.FindUndecidedNonMemberFeedback(task, " ");
        Action decided = () => TaskDecider.FindUndecidedNonMemberFeedback(task, ReviewKey);
        Action withdrawn = () => TaskDecider.FindUndecidedNonMemberFeedback(task, OtherReviewKey);

        ambiguous.Should().Throw<DomainValidationException>().WithMessage("*matches 2 pieces*");
        missing.Should().Throw<DomainNotFoundException>();
        blank.Should().Throw<DomainValidationException>();
        decided.Should().Throw<DomainConflictException>().WithMessage("*already declined*");
        withdrawn.Should().Throw<DomainConflictException>().WithMessage("*gone from GitHub*");
    }

    [Fact]
    public void The_projection_folds_the_same_events_the_aggregate_does_and_flags_what_still_waits()
    {
        TaskDetailsProjection projection = new();
        Guid id = DomainId.New();
        TaskDetails view = projection.Create(new FakeEvent<TaskAdded>(new TaskAdded(
            id, DomainId.New(), "Do the thing", ["it is done"], TaskType.Chore, null, null, null, Now, DomainId.New())));

        projection.Apply(new FakeEvent<NonMemberFeedbackHeld>(
            new NonMemberFeedbackHeld(id, ReviewKey, NonMemberFeedbackKind.Review, "stranger", ReviewKey, Now, Review())), view);
        projection.Apply(new FakeEvent<NonMemberFeedbackHeld>(
            new NonMemberFeedbackHeld(id, "PRRT_1", NonMemberFeedbackKind.Thread, "stranger", "https://x/t", Now)), view);
        view.HasUndecidedNonMemberFeedback.Should().BeTrue();
        view.NonMemberFeedback.Should().HaveCount(2);

        projection.Apply(new FakeEvent<NonMemberFeedbackAdmitted>(new NonMemberFeedbackAdmitted(id, ReviewKey, Now, DomainId.New())), view);
        view.HasUndecidedNonMemberFeedback.Should().BeTrue("the thread still waits");

        projection.Apply(new FakeEvent<NonMemberFeedbackDeclined>(new NonMemberFeedbackDeclined(id, "PRRT_1", Now, DomainId.New())), view);
        view.HasUndecidedNonMemberFeedback.Should().BeFalse();
        view.NonMemberFeedback.Single(item => item.Key == ReviewKey).Status.Should().Be(NonMemberFeedbackStatus.Admitted);

        projection.Apply(new FakeEvent<NonMemberFeedbackWithdrawn>(new NonMemberFeedbackWithdrawn(id, ReviewKey, Now)), view);
        view.NonMemberFeedback.Single(item => item.Key == ReviewKey).Status.Should().Be(NonMemberFeedbackStatus.Withdrawn);
    }

    [Fact]
    public void The_go_and_the_decline_are_owner_only_acts_while_the_observations_are_member_safe()
    {
        TaskActClassificationRegistry.TryClassificationOf(typeof(NonMemberFeedbackAdmitted))
            .Should().Be(TaskActClassification.OwnerOnly, "the go is what lets a stranger's text reach a session");
        TaskActClassificationRegistry.TryClassificationOf(typeof(NonMemberFeedbackDeclined))
            .Should().Be(TaskActClassification.OwnerOnly);
        TaskActClassificationRegistry.TryClassificationOf(typeof(NonMemberFeedbackHeld))
            .Should().Be(TaskActClassification.MemberSafe);
        TaskActClassificationRegistry.TryClassificationOf(typeof(NonMemberFeedbackWithdrawn))
            .Should().Be(TaskActClassification.MemberSafe);
        EventScopeRegistry.ClassificationOf(typeof(NonMemberFeedbackHeld)).Should().Be(EventScope.ProjectScoped);
    }

    [Fact]
    public void A_review_stored_before_the_gate_carries_no_admission_and_may_not_be_read()
    {
        ChangesRequestedReview legacy = new("teammate", ReviewKey, Now, [new ChangesRequestedFinding("old")]);

        legacy.Admission.Should().BeNull();
        legacy.AdmittedAs.MayBeRead.Should().BeFalse();
        legacy.ReviewerAccountId.Should().BeNull();
        (legacy with { Admission = FeedbackAdmission.Member }).AdmittedAs.MayBeRead.Should().BeTrue();
        (legacy with { Admission = FeedbackAdmission.OwnerApproved }).AdmittedAs.MayBeRead.Should().BeTrue();
    }
}
