using FluentAssertions;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The phase-class shares on <see cref="ThroughputSummary"/> (idea 6e6d0200, Card 6), folded straight
/// off hand-built classifications the way <c>ThroughputQueryTests</c> folds its passages: period
/// totals rather than averages, the five-task floor on the period and on each owner's slice, and one
/// person's several nodes counting as one owner by root fingerprint.
/// </summary>
public sealed class ThroughputPhaseSharesTests
{
    private static readonly Guid RunId = DomainId.New();

    private static ClassifiedPhase Phase(PhaseActor actor, ClaimOwner? owner = null, PhaseKind kind = PhaseKind.Build) =>
        new(kind, actor, RunId, owner ?? new ClaimOwner(DomainId.New(), "SHA256:solo"));

    private static TaskPassage Passage(params ClassifiedPhase[] phases) =>
        new(
            Queued: PassagePhase.Closed(TimeSpan.FromHours(1)),
            Building: PassagePhase.NotApplicable,
            Gates: PassagePhase.NotApplicable,
            Review: new ReviewCyclePassage(1, TimeSpan.Zero, false, 0, TimeSpan.Zero, false),
            Delivery: PassagePhase.NotApplicable,
            MergeWait: PassagePhase.NotApplicable,
            HumanWaits: [],
            ClaimToMerge: PassagePhase.Closed(TimeSpan.FromHours(3)),
            Laps: [],
            Sessions: 1,
            MergedAt: DateTimeOffset.UtcNow,
            QueuedBeforeFirstClaim: PassagePhase.Closed(TimeSpan.FromHours(1)),
            Classification: new PhaseClassification(phases));

    private static List<TaskPassage> Five(Func<int, TaskPassage> make) => [.. Enumerable.Range(0, 5).Select(make)];

    [Fact]
    public void Shares_are_the_periods_total_phase_count_not_an_average_of_each_tasks_own_percentage()
    {
        // One task of six automatic phases and four tasks of one human phase each: 4 of 10 phases are
        // human, where the average of the five tasks' own percentages would read 80 percent.
        List<TaskPassage> merged =
        [
            Passage([.. Enumerable.Repeat(Phase(PhaseActor.Automatic), 6)]),
            .. Enumerable.Range(0, 4).Select(_ => Passage(Phase(PhaseActor.Human))),
        ];

        PhaseClassShares shares = ThroughputQuery.Compute(merged).PhaseShares!;

        shares.Human.Should().BeApproximately(0.4, 1e-9);
        shares.Automatic.Should().BeApproximately(0.6, 1e-9);
        shares.Delegated.Should().Be(0);
        shares.Skipped.Should().Be(0);
    }

    [Fact]
    public void Every_class_gets_its_own_share_and_they_sum_to_one()
    {
        List<TaskPassage> merged = Five(_ => Passage(
            Phase(PhaseActor.Human), Phase(PhaseActor.Delegated), Phase(PhaseActor.Automatic), Phase(PhaseActor.Automatic),
            Phase(PhaseActor.Skipped)));

        PhaseClassShares shares = ThroughputQuery.Compute(merged).PhaseShares!;

        (shares.Human, shares.Delegated, shares.Automatic, shares.Skipped).Should().Be((0.2, 0.2, 0.4, 0.2));
    }

    [Fact]
    public void Under_the_floor_the_shares_and_every_owner_slice_are_null()
    {
        List<TaskPassage> merged = [.. Five(_ => Passage(Phase(PhaseActor.Human))).Take(4)];

        ThroughputSummary summary = ThroughputQuery.Compute(merged);

        summary.HasEnoughData.Should().BeFalse();
        summary.PhaseShares.Should().BeNull();
        summary.OwnerShares.Should().BeNull();
    }

    [Fact]
    public void An_owner_slice_under_five_tasks_keeps_its_count_and_carries_no_shares()
    {
        ClaimOwner busy = new(DomainId.New(), "SHA256:busy");
        ClaimOwner occasional = new(DomainId.New(), "SHA256:occasional");
        List<TaskPassage> merged =
        [
            .. Five(index => index < 2
                ? Passage(Phase(PhaseActor.Automatic, busy), Phase(PhaseActor.Human, occasional))
                : Passage(Phase(PhaseActor.Automatic, busy))),
        ];

        IReadOnlyList<OwnerPhaseShares> owners = ThroughputQuery.Compute(merged).OwnerShares!;

        OwnerPhaseShares busySlice = owners.Single(slice => slice.OwnerKey == "SHA256:busy");
        busySlice.MergedCount.Should().Be(5);
        busySlice.Shares!.Automatic.Should().Be(1);
        OwnerPhaseShares occasionalSlice = owners.Single(slice => slice.OwnerKey == "SHA256:occasional");
        occasionalSlice.MergedCount.Should().Be(2);
        occasionalSlice.Shares.Should().BeNull();
        occasionalSlice.HasEnoughData.Should().BeFalse();
    }

    [Fact]
    public void One_owners_two_nodes_count_as_one_owner_including_a_claim_with_no_recorded_fingerprint()
    {
        Guid firstNodeOwner = DomainId.New();
        Guid secondNodeOwner = DomainId.New();
        Guid legacyOwner = DomainId.New();
        List<TaskPassage> merged =
        [
            Passage(Phase(PhaseActor.Human, new ClaimOwner(firstNodeOwner, "SHA256:alice"))),
            Passage(Phase(PhaseActor.Automatic, new ClaimOwner(secondNodeOwner, "SHA256:alice"))),
            // A claim written before the fingerprint field: resolved from the owner's own Guid.
            Passage(Phase(PhaseActor.Automatic, new ClaimOwner(legacyOwner, null))),
            Passage(Phase(PhaseActor.Human, new ClaimOwner(firstNodeOwner, "SHA256:alice"))),
            Passage(Phase(PhaseActor.Automatic, new ClaimOwner(secondNodeOwner, "SHA256:alice"))),
        ];

        IReadOnlyList<OwnerPhaseShares> owners = ThroughputQuery
            .Compute(merged, new Dictionary<Guid, string> { [legacyOwner] = "SHA256:alice" }).OwnerShares!;

        owners.Should().ContainSingle();
        owners[0].OwnerKey.Should().Be("SHA256:alice");
        owners[0].MergedCount.Should().Be(5);
        owners[0].Shares!.Human.Should().BeApproximately(0.4, 1e-9);
    }

    [Fact]
    public void A_claim_with_no_fingerprint_anywhere_keys_on_the_owner_guid()
    {
        Guid unclaimedRoot = DomainId.New();
        List<TaskPassage> merged = Five(_ => Passage(Phase(PhaseActor.Automatic, new ClaimOwner(unclaimedRoot, null))));

        ThroughputQuery.Compute(merged).OwnerShares!.Single().OwnerKey
            .Should().Be(PhaseOwnerKey.Of(null, unclaimedRoot));
    }

    [Fact]
    public void A_task_counts_once_for_an_owner_however_many_of_its_phases_ran_under_them()
    {
        ClaimOwner owner = new(DomainId.New(), "SHA256:alice");
        List<TaskPassage> merged = Five(_ => Passage(
            Phase(PhaseActor.Human, owner), Phase(PhaseActor.Human, owner, PhaseKind.Gates), Phase(PhaseActor.Automatic, owner)));

        ThroughputQuery.Compute(merged).OwnerShares!.Single().MergedCount.Should().Be(5);
    }

    [Fact]
    public void A_passage_with_no_classification_adds_no_phases()
    {
        List<TaskPassage> merged = Five(_ => Passage(Phase(PhaseActor.Human)) with { Classification = null });

        ThroughputSummary summary = ThroughputQuery.Compute(merged);

        summary.PhaseShares.Should().BeNull();
        summary.OwnerShares.Should().BeEmpty();
    }
}
