using FluentAssertions;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The one place two live auto-created reviews of one pull request are ordered against each
/// other. Every assertion here is about the id, never a clock: two nodes whose wall clocks
/// disagree must still name the same survivor.
/// </summary>
public sealed class PullRequestReviewDuplicateRuleTests
{
    private static readonly Guid OwnerId = DomainId.New();
    private static readonly Guid TeammateId = DomainId.New();

    // UUIDv7 leads with a millisecond timestamp, so the smaller of these is the earlier mint.
    private static readonly Guid Older = Guid.Parse("01997a00-0000-7000-8000-000000000001");
    private static readonly Guid Younger = Guid.Parse("01997a00-0001-7000-8000-000000000001");

    [Fact]
    public void The_smallest_id_survives_whatever_order_the_ids_arrive_in()
    {
        PullRequestReviewDuplicateRule.SurvivorOf([Younger, Older]).Should().Be(Older);
        PullRequestReviewDuplicateRule.SurvivorOf([Older, Younger]).Should().Be(Older);
    }

    [Fact]
    public void The_survivor_is_chosen_by_id_and_never_by_the_wall_clock_the_task_recorded()
    {
        // The older id carries the LATER AddedAt: a node whose clock ran ahead minted it. A rule
        // reading AddedAt would pick the other task on this node and this one on a node whose
        // clock ran true, which is exactly the disagreement an id comparison cannot have.
        TaskListItem olderIdLaterClock = Live(Older, addedAt: new DateTimeOffset(2026, 9, 24, 15, 40, 0, TimeSpan.Zero));
        TaskListItem youngerIdEarlierClock = Live(Younger, addedAt: new DateTimeOffset(2026, 9, 24, 15, 36, 0, TimeSpan.Zero));

        Guid survivor = PullRequestReviewDuplicateRule.SurvivorOf(
            new[] { youngerIdEarlierClock, olderIdLaterClock }.Select(task => task.Id));

        survivor.Should().Be(Older);
        PullRequestReviewDuplicateRule.IsYoungerThan(youngerIdEarlierClock.Id, olderIdLaterClock.Id).Should().BeTrue();
        PullRequestReviewDuplicateRule.IsYoungerThan(olderIdLaterClock.Id, youngerIdEarlierClock.Id).Should().BeFalse();
    }

    [Fact]
    public void Guid_ordering_agrees_with_the_bytewise_order_postgres_sorts_a_uuid_column_by()
    {
        // The claim the rule rests on: comparing Guid values is the same order as the canonical
        // string (RFC byte order) a uuid column sorts by, so a node's SQL and its C# never
        // disagree about which of two ids is smaller.
        Guid[] ids = [Younger, Older, DomainId.New(), DomainId.New(), DomainId.New()];

        Guid[] byGuid = [.. ids.Order()];
        Guid[] byCanonicalString = [.. ids.OrderBy(id => id.ToString("D"), StringComparer.Ordinal)];

        byGuid.Should().Equal(byCanonicalString);
        PullRequestReviewDuplicateRule.SurvivorOf(ids).Should().Be(byGuid[0]);
    }

    [Fact]
    public void A_task_is_never_its_own_younger_twin()
    {
        PullRequestReviewDuplicateRule.IsYoungerThan(Older, Older).Should().BeFalse();
    }

    [Fact]
    public void A_live_auto_created_review_of_this_owner_is_a_rival()
    {
        PullRequestReviewDuplicateRule.IsRival(Live(Older), OwnerId, ownerRootFingerprint: null).Should().BeTrue();
    }

    [Fact]
    public void A_teammates_review_of_the_same_pull_request_is_never_a_rival()
    {
        TaskListItem teammates = Live(Older);
        teammates.AssignedOwnerId = TeammateId;

        PullRequestReviewDuplicateRule.IsRival(teammates, OwnerId, ownerRootFingerprint: null).Should().BeFalse();
    }

    [Fact]
    public void A_recorded_owner_fingerprint_decides_ownership_over_the_self_declared_owner_id()
    {
        TaskListItem task = Live(Older);
        task.AssignedOwnerId = TeammateId;
        task.AssignedOwnerFingerprint = "fingerprint-of-this-owner";

        PullRequestReviewDuplicateRule.IsRival(task, OwnerId, "fingerprint-of-this-owner").Should().BeTrue();
        PullRequestReviewDuplicateRule.IsRival(task, OwnerId, "someone-elses-fingerprint").Should().BeFalse();
    }

    [Fact]
    public void A_task_a_person_adopted_by_hand_is_never_a_rival()
    {
        TaskListItem adopted = Live(Older);
        adopted.WasAutoPrReviewCreated = false;

        PullRequestReviewDuplicateRule.IsRival(adopted, OwnerId, ownerRootFingerprint: null).Should().BeFalse();
    }

    [Fact]
    public void A_closed_review_is_never_a_rival()
    {
        foreach (TaskState closedState in new[] { TaskState.Done, TaskState.Abandoned })
        {
            TaskListItem closed = Live(Older);
            closed.State = closedState;

            PullRequestReviewDuplicateRule.IsRival(closed, OwnerId, ownerRootFingerprint: null)
                .Should().BeFalse($"a {closedState.Value} review is over");
        }
    }

    [Fact]
    public void The_abandon_reason_names_the_survivor_and_survives_the_feeds_clip_whole()
    {
        string reason = PullRequestReviewDuplicateRule.AbandonReason(Older);
        TaskAbandoned abandoned = new(Younger, reason, DateTimeOffset.UnixEpoch, OwnerId);

        reason.Should().Contain(Older.ToString());
        OrchestratorFeedDescription.Of(abandoned).Should().Be($"abandoned: {reason}",
            "a reason the feed clips would cut the survivor's id, the one part a reader needs");
    }

    private static TaskListItem Live(Guid id, DateTimeOffset? addedAt = null) => new()
    {
        Id = id,
        Type = TaskType.PrReview,
        State = TaskState.Queued,
        WasAutoPrReviewCreated = true,
        AssignedOwnerId = OwnerId,
        ExternalReference = "github-pr:acme/widgets#42",
        AddedAt = addedAt ?? DateTimeOffset.UnixEpoch,
    };
}
