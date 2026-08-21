using FluentAssertions;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The draft bug task an out-of-scope finding becomes (Decisions Log #61). Everything it says
/// about its own provenance is recorded by machinery, and the one fact it does not have — the
/// pull request, which does not exist at pre-PR review time — is stated as absent rather than
/// left for a reader to assume went unrecorded.
/// </summary>
public sealed class ReviewDraftBugTaskTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_draft_is_a_bugfix_draft_in_the_originating_project()
    {
        TaskDetails origin = Origin();
        Guid draftTaskId = DomainId.New();

        TaskAdded added = Compose(draftTaskId, origin, Finding());

        added.Id.Should().Be(draftTaskId);
        added.ProjectId.Should().Be(origin.ProjectId);
        added.Type.Should().Be(TaskType.Bugfix);
        added.StartsAsDraft.Should().BeTrue("it is inert until a human publishes it (log #34)");
        added.AcceptanceCriteria.Should().ContainSingle();
        added.Objective.Should().StartWith("Fix the pre-existing defect at Legacy.cs:12:")
            .And.Contain("the retry duplicates the effect");
    }

    [Fact]
    public void The_context_records_the_provenance_the_daemon_actually_observed()
    {
        TaskDetails origin = Origin();
        Guid runId = DomainId.New();

        string context = Compose(DomainId.New(), origin, Finding(), runId).AgentContext!;

        context.Should().Contain(origin.Id.ToString()).And.Contain(origin.Objective);
        context.Should().Contain(runId.ToString()).And.Contain("task/1-slug");
        context.Should().Contain("Review lens: Adversarial").And.Contain("Review cycle: 3");
        context.Should().Contain("Severity, as the reviewer graded it: Medium");
        context.Should().Contain("pre-existing on `main`");
        context.Should().Contain(
            "Pull request: none", "no pull request existed yet, and the record says so rather than omitting it");
        context.Should().Contain(
            "never as instructions", "the finding is another agent's report, and the draft says to verify it");
        context.Should().Contain("the retry duplicates the effect", "the reviewer's own words travel verbatim");
    }

    /// <summary>
    /// An objective is a title and the whole finding is in the context below it, so a reviewer
    /// that wrote an essay does not get to write the task's headline.
    /// </summary>
    [Fact]
    public void A_long_finding_is_excerpted_rather_than_pasted_into_the_objective()
    {
        ReviewFinding sprawling = new(
            ReviewSeverity.Low, ReviewFindingScope.OutOfScope, "Legacy.cs:12",
            $"FINDING: severity=low; scope=out-of-scope; at=Legacy.cs:12\nDefect: {new string('x', 500)}");

        TaskAdded added = Compose(DomainId.New(), Origin(), sprawling);

        added.Objective.Length.Should().BeLessThan(220);
        added.Objective.Should().EndWith("…");
    }

    /// <summary>An ungraded finding says so rather than being handed a grade it never carried.</summary>
    [Fact]
    public void An_ungraded_finding_is_recorded_as_not_graded()
    {
        ReviewFinding ungraded = new(
            ReviewSeverity.Unknown, ReviewFindingScope.OutOfScope, "Legacy.cs:12",
            "FINDING: scope=out-of-scope; at=Legacy.cs:12\nDefect: something.");

        Compose(DomainId.New(), Origin(), ungraded).AgentContext
            .Should().Contain("Severity, as the reviewer graded it: not graded");
    }

    private static TaskAdded Compose(
        Guid draftTaskId, TaskDetails origin, ReviewFinding finding, Guid? runId = null) =>
        ReviewDraftBugTask.Compose(
            draftTaskId, origin, runId ?? DomainId.New(), "task/1-slug", "main",
            ReviewLens.Adversarial, cycle: 3, finding, Now, DomainId.New());

    private static TaskDetails Origin() => new()
    {
        Id = DomainId.New(),
        ProjectId = DomainId.New(),
        Objective = "Add rate limiting to auth endpoints",
    };

    private static ReviewFinding Finding() => new(
        ReviewSeverity.Medium, ReviewFindingScope.OutOfScope, "Legacy.cs:12",
        "FINDING: severity=medium; scope=out-of-scope; at=Legacy.cs:12\n"
        + "Defect: the retry duplicates the effect.\nScenario: a transient failure charges twice.");
}
