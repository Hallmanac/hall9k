using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The classification row on <c>h9k task show</c>'s passage section (idea 6e6d0200, Card 6): one row in
/// plain words, checked against a golden, and absent for a task that is never classified.
/// </summary>
public sealed class TaskPassageClassificationRowTests
{
    private static readonly Guid RunId = DomainId.New();
    private static readonly ClaimOwner Owner = new(DomainId.New(), "SHA256:me");

    private static ClassifiedPhase Phase(
        PhaseKind kind, PhaseActor actor, int number = 0, bool fixedByHand = false, PhaseHandOff? handOff = null,
        bool skipOverride = false) =>
        new(kind, actor, RunId, Owner, number, fixedByHand, handOff, skipOverride);

    private static TaskPassage Passage(PhaseClassification? classification) => new(
        PassagePhase.NotApplicable, PassagePhase.Closed(TimeSpan.FromMinutes(5)), PassagePhase.NotApplicable,
        new ReviewCyclePassage(0, TimeSpan.Zero, false, 0, TimeSpan.Zero, false),
        PassagePhase.NotApplicable, PassagePhase.NotApplicable, [], PassagePhase.NotApplicable, [], 1, null,
        PassagePhase.NotApplicable, Classification: classification);

    [Fact]
    public void The_row_matches_its_golden()
    {
        PhaseClassification classification = new(
        [
            Phase(PhaseKind.Build, PhaseActor.Human),
            Phase(PhaseKind.Gates, PhaseActor.Human),
            Phase(PhaseKind.ReviewCycle, PhaseActor.Automatic, 1),
            Phase(PhaseKind.ReviewCycle, PhaseActor.Automatic, 2, fixedByHand: true),
            Phase(PhaseKind.PullRequestOpen, PhaseActor.Human, skipOverride: true),
            Phase(PhaseKind.CloseoutLap, PhaseActor.Automatic, 1, handOff: PhaseHandOff.PullRequestResolve),
        ]);

        List<string> lines = TaskShowCommand.ComposePassageLines(Passage(classification));

        lines.Should().Contain(
            "Build and gates by hand; review cycles 1 and 2 automatic, cycle 2 fixed by hand; "
            + "PR opened by hand (skip to PR); closeout automatic after pr resolve");
    }

    [Fact]
    public void A_task_that_is_never_classified_shows_no_classification_row()
    {
        List<string> lines = TaskShowCommand.ComposePassageLines(Passage(classification: null));

        lines.Should().NotContain(line => line.Contains("automatic") || line.Contains("by hand"));
    }

    [Fact]
    public void A_pr_review_task_shows_no_classification_row()
    {
        TaskPassage passage = TaskPassageQuery.Compute([], [], TaskType.PrReview, taskConcluded: false, DateTimeOffset.UtcNow);

        passage.Classification.Should().BeNull();
        TaskShowCommand.ComposePassageLines(passage).Should().NotContain(line => line.Contains("automatic"));
    }
}
