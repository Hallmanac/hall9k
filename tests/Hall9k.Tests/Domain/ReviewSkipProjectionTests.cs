using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The read side of <c>h9k task skip-to-pr</c>'s event, plus the two registries that decide who may
/// replicate it and where it travels.
/// </summary>
public sealed class ReviewSkipProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static (RunDetailsProjection Projection, RunDetails View, Guid Id) ParkedRun()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = projection.Create(new FakeEvent<RunDispatched>(new RunDispatched(
            id, DomainId.New(), Guid.Empty, DomainId.New(), 1, DomainId.New(), "/wt/x", "task/x",
            ExecutorMode.Subscription, Now)));
        projection.Apply(new FakeEvent<AgentSessionCompleted>(new AgentSessionCompleted(id, Now)), view);
        projection.Apply(new FakeEvent<ReviewParked>(
            new ReviewParked(id, "Interactive mode is on for this task.", Now, IsInteractiveGate: true)), view);
        return (projection, view, id);
    }

    [Fact]
    public void Run_details_settles_as_overridden_at_under_review_with_the_park_cleared_and_the_record_kept()
    {
        (RunDetailsProjection projection, RunDetails view, Guid id) = ParkedRun();
        Guid owner = DomainId.New();
        view.State.Should().Be(RunState.ReviewParked);

        projection.Apply(new FakeEvent<ReviewSkippedToPullRequest>(new ReviewSkippedToPullRequest(
            id, owner, Now, "docs only", ReasonByAgent: true,
            [ReviewSkipPlan.Gates, ReviewSkipPlan.SettlingGate], "fingerprint-1")), view);

        view.State.Should().Be(RunState.UnderReview);
        view.ReviewSettlement.Should().Be(ReviewSettlement.Overridden);
        view.LastReviewVerdict.Should().Be(ReviewVerdict.MergeReady);
        view.ParkedIsInteractiveGate.Should().BeFalse("the interactive gate is cleared");
        view.ParkedReason.Should().BeNull();
        view.ReviewSkip.Should().BeEquivalentTo(new ReviewSkipRecord(
            owner, "fingerprint-1", Now, "docs only", true, [ReviewSkipPlan.Gates, ReviewSkipPlan.SettlingGate]));
    }

    [Fact]
    public void Run_details_without_the_event_carries_no_override()
    {
        (_, RunDetails view, _) = ParkedRun();

        view.ReviewSkip.Should().BeNull();
        view.ReviewSettlement.Should().Be(ReviewSettlement.Unknown);
    }

    [Fact]
    public void The_lean_run_row_reads_under_review_after_a_skip_from_the_build_or_a_park()
    {
        RunListItemProjection projection = new();
        Guid id = DomainId.New();
        RunListItem view = projection.Create(new FakeEvent<RunDispatched>(new RunDispatched(
            id, DomainId.New(), Guid.Empty, DomainId.New(), 1, DomainId.New(), "/wt/x", "task/x",
            ExecutorMode.Subscription, Now)));
        projection.Apply(new FakeEvent<ReviewParked>(new ReviewParked(id, "park", Now)), view);
        view.State.Should().Be(RunState.ReviewParked);

        projection.Apply(new FakeEvent<ReviewSkippedToPullRequest>(
            new ReviewSkippedToPullRequest(id, DomainId.New(), Now, null, false, [])), view);

        view.State.Should().Be(RunState.UnderReview);
    }

    [Fact]
    public void The_event_is_owner_only_so_a_members_node_can_never_replicate_one_onto_an_owners_run()
    {
        TaskActClassificationRegistry.TryClassificationOf(typeof(ReviewSkippedToPullRequest))
            .Should().Be(TaskActClassification.OwnerOnly);
        TaskActClassificationRegistry.TryClassificationOf(typeof(ReviewParkResolved))
            .Should().Be(TaskActClassification.OwnerOnly, "a skip is the same call a park resolution is");
    }

    [Fact]
    public void The_event_is_project_scoped()
    {
        EventScopeRegistry.ClassificationOf(typeof(ReviewSkippedToPullRequest)).Should().Be(EventScope.ProjectScoped);
    }
}
