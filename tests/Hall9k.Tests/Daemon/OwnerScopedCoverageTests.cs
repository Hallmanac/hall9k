using FluentAssertions;
using Hall9k.Daemon.AutoPrReview;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The covering-task decision both auto-pr-review triggers share, with no database, no branch and no
/// GitHub: which of the live pr-review tasks on one pull request this install may treat as its own
/// (<see cref="OwnerScopedCoverage"/>). The origin case is AgelessRx/arx-platform#2166, where a
/// teammate's install found Brian's replicated task by external reference alone and claimed it.
/// </summary>
public sealed class OwnerScopedCoverageTests
{
    private const string Mine = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string Theirs = "2222222222222222222222222222222222222222222222222222222222222222";

    private static readonly DateTimeOffset Now = new(2026, 10, 1, 17, 21, 0, TimeSpan.Zero);

    private static CoveringTaskCandidate Covering(TaskOwnerFacts facts, TimeSpan age) =>
        new(new TaskListItem { Id = DomainId.New(), AddedAt = Now - age }, facts);

    private static TaskOwnerFacts HeldBy(string root) =>
        new(OwnerRootFact.Known(root), OwnerRootFact.Absent, OwnerRootFact.Absent);

    private static TaskOwnerFacts AssignedTo(string root) =>
        new(OwnerRootFact.Absent, OwnerRootFact.Known(root), OwnerRootFact.Known(Mine));

    [Fact]
    public void A_mention_on_a_pull_request_only_a_teammates_live_task_covers_decides_the_teammate_row()
    {
        CoveringTaskCandidate teammates = Covering(AssignedTo(Theirs), TimeSpan.FromHours(6));

        ReviewCoverage coverage = OwnerScopedCoverage.Decide(Mine, [teammates]);

        coverage.Kind.Should().Be(ReviewCoverageKind.Teammate);
        coverage.Task.Should().BeSameAs(teammates.Task, "the decision names the task only so a log can say which one stood in the way");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_own_task_wins_over_a_teammates_whichever_is_newer(bool ownIsNewer)
    {
        CoveringTaskCandidate own = Covering(HeldBy(Mine), ownIsNewer ? TimeSpan.FromHours(1) : TimeSpan.FromHours(9));
        CoveringTaskCandidate teammates = Covering(HeldBy(Theirs), ownIsNewer ? TimeSpan.FromHours(9) : TimeSpan.FromHours(1));

        ReviewCoverage coverage = OwnerScopedCoverage.Decide(Mine, [teammates, own]);

        coverage.Kind.Should().Be(ReviewCoverageKind.Own);
        coverage.Task.Should().BeSameAs(own.Task);
    }

    [Fact]
    public void The_newest_of_two_own_tasks_is_the_one_attached_to_whatever_order_they_arrive_in()
    {
        CoveringTaskCandidate older = Covering(HeldBy(Mine), TimeSpan.FromHours(9));
        CoveringTaskCandidate newer = Covering(AssignedTo(Mine), TimeSpan.FromHours(1));

        OwnerScopedCoverage.Decide(Mine, [older, newer]).Task.Should().BeSameAs(newer.Task);
        OwnerScopedCoverage.Decide(Mine, [newer, older]).Task.Should().BeSameAs(newer.Task);
    }

    [Fact]
    public void Nothing_live_is_uncovered()
    {
        OwnerScopedCoverage.Decide(Mine, []).Should().Be(ReviewCoverage.Uncovered);
    }

    [Theory]
    [InlineData(OwnerFactState.Unresolved, OwnerFactState.Absent, OwnerFactState.Known)]
    [InlineData(OwnerFactState.Absent, OwnerFactState.Unresolved, OwnerFactState.Known)]
    [InlineData(OwnerFactState.Absent, OwnerFactState.Absent, OwnerFactState.Unresolved)]
    public void A_task_this_node_cannot_attribute_to_a_known_owner_is_never_this_owners_to_act_on_but_is_not_a_teammates_either(
        OwnerFactState holder, OwnerFactState assignee, OwnerFactState creator)
    {
        // A creator that is this root's own never rescues a holder or assignee that cannot be read.
        TaskOwnerFacts facts = new(Fact(holder), Fact(assignee), Fact(creator));

        OwnerScopedCoverage.IsOwn(Mine, facts).Should().BeFalse();
        OwnerScopedCoverage.Decide(Mine, [Covering(facts, TimeSpan.FromHours(1))]).Kind
            .Should().Be(ReviewCoverageKind.Unattributable, "it may be this owner's own, so a request must not mint over it");
    }

    [Fact]
    public void An_unattributable_task_outranks_a_known_teammates_and_yields_to_an_own_one()
    {
        CoveringTaskCandidate unattributable = Covering(
            new(OwnerRootFact.Absent, OwnerRootFact.Absent, OwnerRootFact.Unresolved), TimeSpan.FromHours(9));
        CoveringTaskCandidate teammates = Covering(HeldBy(Theirs), TimeSpan.FromHours(1));
        CoveringTaskCandidate own = Covering(HeldBy(Mine), TimeSpan.FromHours(20));

        ReviewCoverage withTeammate = OwnerScopedCoverage.Decide(Mine, [teammates, unattributable]);
        ReviewCoverage withOwn = OwnerScopedCoverage.Decide(Mine, [teammates, unattributable, own]);

        withTeammate.Kind.Should().Be(ReviewCoverageKind.Unattributable);
        withTeammate.Task.Should().BeSameAs(unattributable.Task);
        withOwn.Kind.Should().Be(ReviewCoverageKind.Own);
        withOwn.Task.Should().BeSameAs(own.Task);
    }

    [Fact]
    public void An_assignment_to_this_owner_counts_even_when_someone_else_holds_the_task()
    {
        TaskOwnerFacts facts = new(OwnerRootFact.Known(Theirs), OwnerRootFact.Known(Mine), OwnerRootFact.Absent);

        OwnerScopedCoverage.IsOwn(Mine, facts).Should().BeTrue("the rule lets the root act when it is the holder or the assignee");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_node_whose_owner_has_no_root_yet_owns_every_task_it_holds(string? noRoot)
    {
        // The CLI guard lets such a node through for the same reason: no team could own a task instead.
        OwnerScopedCoverage.IsOwn(noRoot, HeldBy(Theirs)).Should().BeTrue();
    }

    [Fact]
    public void Another_owners_closed_task_is_never_this_owners_earlier_review()
    {
        CoveringTaskCandidate teammatesClosed = Covering(HeldBy(Theirs), TimeSpan.FromHours(1));
        CoveringTaskCandidate ownClosed = Covering(HeldBy(Mine), TimeSpan.FromHours(30));

        OwnerScopedCoverage.NewestOwn(Mine, [teammatesClosed]).Should().BeNull();
        OwnerScopedCoverage.NewestOwn(Mine, [teammatesClosed, ownClosed]).Should().BeSameAs(ownClosed.Task);
    }

    [Fact]
    public void A_closed_task_this_node_cannot_attribute_still_counts_as_a_review_that_may_be_this_owners()
    {
        CoveringTaskCandidate unattributable = Covering(
            new(OwnerRootFact.Absent, OwnerRootFact.Absent, OwnerRootFact.Unresolved), TimeSpan.FromHours(2));
        CoveringTaskCandidate teammatesClosed = Covering(HeldBy(Theirs), TimeSpan.FromHours(1));

        OwnerScopedCoverage.NewestMaybeOwn(Mine, [teammatesClosed, unattributable]).Should().BeSameAs(unattributable.Task);
        OwnerScopedCoverage.NewestMaybeOwn(Mine, [teammatesClosed]).Should().BeNull();
    }

    public enum OwnerFactState
    {
        Absent,
        Unresolved,
        Known,
    }

    private static OwnerRootFact Fact(OwnerFactState state) => state switch
    {
        OwnerFactState.Absent => OwnerRootFact.Absent,
        OwnerFactState.Unresolved => OwnerRootFact.Unresolved,
        _ => OwnerRootFact.Known(Mine),
    };
}
