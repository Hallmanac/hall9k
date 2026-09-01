using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks.Projections;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The strict task &gt; project &gt; node &gt; compiled default hierarchy (Brian's ruling,
/// 2026-08-29, task: the review cycle caps become settable at three levels), resolved
/// independently per cap.
/// </summary>
public sealed class ReviewCapResolverTests
{
    [Fact]
    public void Nothing_set_anywhere_resolves_to_the_compiled_defaults()
    {
        ResolvedReviewCaps caps = ReviewCapResolver.Resolve(task: null, project: null, new DaemonOptions());

        caps.MaxComplianceReviewCycles.Value.Should().Be(3);
        caps.MaxComplianceReviewCycles.Level.Should().Be(ReviewCapLevel.Default);
        caps.MaxAdversarialReviewCycles.Value.Should().Be(10);
        caps.MaxAdversarialReviewCycles.Level.Should().Be(ReviewCapLevel.Default);
        caps.MaxFinalFullPassRounds.Value.Should().Be(3);
        caps.MaxFinalFullPassRounds.Level.Should().Be(ReviewCapLevel.Default);
        caps.LifetimeReviewCycleBudget.Value.Should().Be(25);
        caps.LifetimeReviewCycleBudget.Level.Should().Be(ReviewCapLevel.Default);
    }

    [Fact]
    public void A_node_value_that_differs_from_the_compiled_default_resolves_as_the_node_level()
    {
        DaemonOptions node = new() { MaxComplianceReviewCycles = 7 };

        ResolvedReviewCaps caps = ReviewCapResolver.Resolve(task: null, project: null, node);

        caps.MaxComplianceReviewCycles.Value.Should().Be(7);
        caps.MaxComplianceReviewCycles.Level.Should().Be(ReviewCapLevel.Node);
    }

    [Fact]
    public void A_project_override_outranks_the_node_even_when_the_node_also_set_a_value()
    {
        DaemonOptions node = new() { MaxComplianceReviewCycles = 7 };
        ProjectDetails project = new() { MaxComplianceReviewCycles = 2 };

        ResolvedReviewCaps caps = ReviewCapResolver.Resolve(task: null, project, node);

        caps.MaxComplianceReviewCycles.Value.Should().Be(2);
        caps.MaxComplianceReviewCycles.Level.Should().Be(ReviewCapLevel.Project);
    }

    [Fact]
    public void A_task_override_outranks_both_the_project_and_the_node()
    {
        DaemonOptions node = new() { MaxComplianceReviewCycles = 7 };
        ProjectDetails project = new() { MaxComplianceReviewCycles = 2 };
        TaskDetails task = new() { MaxComplianceReviewCycles = 1 };

        ResolvedReviewCaps caps = ReviewCapResolver.Resolve(task, project, node);

        caps.MaxComplianceReviewCycles.Value.Should().Be(1);
        caps.MaxComplianceReviewCycles.Level.Should().Be(ReviewCapLevel.Task);
    }

    /// <summary>Each of the four caps resolves on its own — a task naming only one inherits the rest from the levels above.</summary>
    [Fact]
    public void A_task_setting_only_one_cap_inherits_the_other_three_from_the_project()
    {
        ProjectDetails project = new() { MaxAdversarialReviewCycles = 6, MaxFinalFullPassRounds = 2 };
        TaskDetails task = new() { MaxComplianceReviewCycles = 1 };

        ResolvedReviewCaps caps = ReviewCapResolver.Resolve(task, project, new DaemonOptions());

        caps.MaxComplianceReviewCycles.Value.Should().Be(1);
        caps.MaxComplianceReviewCycles.Level.Should().Be(ReviewCapLevel.Task);
        caps.MaxAdversarialReviewCycles.Value.Should().Be(6);
        caps.MaxAdversarialReviewCycles.Level.Should().Be(ReviewCapLevel.Project);
        caps.MaxFinalFullPassRounds.Value.Should().Be(2);
        caps.MaxFinalFullPassRounds.Level.Should().Be(ReviewCapLevel.Project);
        caps.LifetimeReviewCycleBudget.Value.Should().Be(25, "nothing set this one anywhere");
        caps.LifetimeReviewCycleBudget.Level.Should().Be(ReviewCapLevel.Default);
    }

    [Fact]
    public void CapFor_reads_the_adversarial_cap_only_for_the_adversarial_lens()
    {
        ProjectDetails project = new() { MaxComplianceReviewCycles = 1, MaxAdversarialReviewCycles = 9 };
        ResolvedReviewCaps caps = ReviewCapResolver.Resolve(task: null, project, new DaemonOptions());

        caps.CapFor(ReviewLens.Conformance).Value.Should().Be(1);
        caps.CapFor(ReviewLens.Adversarial).Value.Should().Be(9);
    }
}
