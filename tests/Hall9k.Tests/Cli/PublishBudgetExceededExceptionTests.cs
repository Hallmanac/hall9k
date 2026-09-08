using FluentAssertions;
using Hall9k.Daemon.Execution;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// A budget miss has two readers, and this is the contract with both: the daemon's gate
/// classifier, which must see an infrastructure-class timeout rather than a product assertion and
/// retry the gate once, and the human reading CI, who needs the load the publish saw to tell a
/// busy machine from a wedged one. Neither is served by the bare
/// <see cref="OperationCanceledException"/> this replaced.
/// <para>
/// No publish runs here, so this carries neither the <c>PublishesBinary</c> trait nor its
/// collection: it is an ordinary DB-free unit test over the message shape. Every assertion is
/// positional (<see cref="string.IndexOf(string, StringComparison)"/>) rather than
/// <c>Should().Contain(...)</c>, so a genuine regression fails on an index mismatch instead of
/// echoing a message that carries the marker into this test's own output — where it would get the
/// whole `dotnet test` run misclassified as the infrastructure failure this test is only
/// describing (the same self-reference
/// <see cref="Hall9k.Tests.Daemon.GateInfrastructureFailureClassifierTests"/> already guards).
/// </para>
/// </summary>
public sealed class PublishBudgetExceededExceptionTests
{
    private static readonly PublishBudgetExceededException Miss = PublishBudgetExceededException.ForMiss(
        "Hall9k.Daemon",
        TimeSpan.FromMinutes(5),
        TimeSpan.FromSeconds(300.4),
        "15 dotnet-family processes (MSBuild 2, dotnet 9, testhost 4)",
        "Determining projects to restore...");

    [Fact]
    public void A_missed_budget_classifies_as_an_infrastructure_failure() =>
        GateInfrastructureFailureClassifier.IsInfrastructureFailure(Miss.Message).Should().BeTrue(
            "the daemon must retry a timeout once rather than fail the run on it as a product defect");

    [Fact]
    public void A_missed_budget_leads_with_the_marker_the_classifier_reads() =>
        Miss.Message.IndexOf(GateInfrastructureFailureClassifier.PublishBudgetExceededMarker, StringComparison.Ordinal)
            .Should().Be(0, "the classifier's excerpt window is bounded, so the marker leads the message");

    [Fact]
    public void A_missed_budget_names_the_process_count_it_saw() =>
        Miss.Message.IndexOf("15 dotnet-family processes (MSBuild 2, dotnet 9, testhost 4)", StringComparison.Ordinal)
            .Should().BeGreaterThanOrEqualTo(0, "the load figure is the whole point of the report");

    [Fact]
    public void A_missed_budget_names_the_elapsed_publish_time_and_the_budget_it_missed()
    {
        Miss.Message.IndexOf("300.4s elapsed", StringComparison.Ordinal).Should().BeGreaterThanOrEqualTo(
            0, "a publish killed at 300s under load reads differently from one killed at 8s");
        Miss.Message.IndexOf("300s budget", StringComparison.Ordinal).Should().BeGreaterThanOrEqualTo(
            0, "the budget it missed is what makes the elapsed figure mean anything, and it is "
            + "reported in the same unit so the two can be read against each other");
    }

    [Fact]
    public void A_missed_budget_quotes_the_publish_output_it_managed_to_capture() =>
        Miss.Message.IndexOf("Determining projects to restore...", StringComparison.Ordinal)
            .Should().BeGreaterThanOrEqualTo(0, "how far the publish got is the other half of the diagnosis");
}
