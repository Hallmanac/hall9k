using FluentAssertions;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The QA persona's own earned exception to a pr-review session's fixed permission file (security
/// review idea 6be68ee2, process-injection finding 1) is gated on <see cref="GateSetAcceptance"/>,
/// never a bare fallback to a project's current, possibly-unaccepted gate list (independent pre-PR
/// review, cycle 1, adversarial lens).
/// </summary>
public sealed class QaGateCommandsResolverTests
{
    [Fact]
    public void An_accepted_gate_set_is_resolved_verbatim()
    {
        VerifyCommand[] gates = [new VerifyCommand("test", "dotnet test")];
        ProjectDetails project = new() { VerifyCommands = [.. gates], AcceptedVerifyCommands = [.. gates] };

        QaGateCommandsResolver.Resolve(project).Should().Equal(gates);
    }

    /// <summary>
    /// A project whose gate set was replicated from a teammate's node, or changed there, and
    /// which this node's own operator has never vetted through <c>h9k project accept-gates</c> —
    /// the original defect fell back to this same unaccepted list rather than refusing it.
    /// </summary>
    [Fact]
    public void A_never_accepted_gate_set_resolves_to_none()
    {
        ProjectDetails project = new() { VerifyCommands = [new VerifyCommand("test", "dotnet test")] };

        QaGateCommandsResolver.Resolve(project).Should().BeNull();
    }

    [Fact]
    public void A_gate_set_changed_since_the_last_acceptance_resolves_to_none()
    {
        ProjectDetails project = new()
        {
            VerifyCommands = [new VerifyCommand("test", "dotnet test"), new VerifyCommand("build", "dotnet build")],
            AcceptedVerifyCommands = [new VerifyCommand("test", "dotnet test")],
        };

        QaGateCommandsResolver.Resolve(project).Should().BeNull();
    }

    [Fact]
    public void An_empty_gate_set_resolves_to_empty_rather_than_holding_for_acceptance()
    {
        ProjectDetails project = new() { VerifyCommands = [] };

        QaGateCommandsResolver.Resolve(project).Should().BeEmpty();
    }
}
