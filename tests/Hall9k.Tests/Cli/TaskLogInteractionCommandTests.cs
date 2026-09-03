using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="TaskLogInteractionCommand.Validate"/> and <see cref="TaskLogInteractionCommand.BuildEvent"/>
/// are the escape-hatch invariant's own gate (task: every outside interaction a dispatched agent
/// has is logged unconditionally, and a logged human directive must say so plainly rather than
/// reporting a human's own call as the agent's independent decision). These are the DB-free
/// claims: what is required, and what the event ends up carrying — the store round trip itself is
/// TaskLogInteractionCommand's own integration-tier concern.
/// </summary>
public sealed class TaskLogInteractionCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private static TaskLogInteractionCommand.Settings Settings(
        string party = "another agent session", string summary = "Shared the worktree path", bool humanDirected = false,
        string? reason = null) => new()
    {
        Task = "28b19893",
        Party = party,
        Summary = summary,
        HumanDirected = humanDirected,
        Reason = reason,
    };

    [Fact]
    public void Refuses_a_blank_party()
    {
        Action act = () => TaskLogInteractionCommand.Validate(Settings(party: "  "));

        act.Should().Throw<DomainValidationException>().WithMessage("*--party*");
    }

    [Fact]
    public void Refuses_a_blank_summary()
    {
        Action act = () => TaskLogInteractionCommand.Validate(Settings(summary: ""));

        act.Should().Throw<DomainValidationException>().WithMessage("*--summary*");
    }

    [Fact]
    public void Refuses_human_directed_with_no_reason()
    {
        Action act = () => TaskLogInteractionCommand.Validate(Settings(humanDirected: true));

        act.Should().Throw<DomainValidationException>().WithMessage(
            "*--human-directed*", "an assertion of human involvement without saying what it was is not a real record");
    }

    [Fact]
    public void Validation_passes_a_well_formed_agent_initiated_settings()
    {
        Action act = () => TaskLogInteractionCommand.Validate(Settings());

        act.Should().NotThrow();
    }

    [Fact]
    public void Validation_passes_a_well_formed_human_directed_settings()
    {
        Action act = () => TaskLogInteractionCommand.Validate(Settings(humanDirected: true, reason: "Real bug"));

        act.Should().NotThrow();
    }

    [Fact]
    public void Allows_an_agent_initiated_interaction_with_no_reason_at_all()
    {
        Guid runId = DomainId.New();

        ExternalInteractionLogged logged = TaskLogInteractionCommand.BuildEvent(Settings(), runId, DomainId.New(), Now);

        logged.HumanDirected.Should().BeFalse();
        logged.Reason.Should().BeNull("nothing here required a reason for an agent's own interaction");
    }

    /// <summary>
    /// The one fact this whole command exists to keep honest: a human-directed entry carries the
    /// human's own reason, on the run this session is actually attached to, never left to be
    /// confused with the agent's own report.
    /// </summary>
    [Fact]
    public void Builds_a_human_directed_event_carrying_the_reason_and_run()
    {
        Guid runId = DomainId.New();
        Guid ownerId = DomainId.New();

        ExternalInteractionLogged logged = TaskLogInteractionCommand.BuildEvent(
            Settings(party: "the operator", summary: "Skip the workaround", humanDirected: true, reason: "Real bug, ordered fixed"),
            runId, ownerId, Now);

        logged.RunId.Should().Be(runId);
        logged.LoggedByOwnerId.Should().Be(ownerId);
        logged.LoggedAt.Should().Be(Now);
        logged.Party.Should().Be("the operator");
        logged.Summary.Should().Be("Skip the workaround");
        logged.HumanDirected.Should().BeTrue();
        logged.Reason.Should().Be("Real bug, ordered fixed");
    }

    [Fact]
    public void Trims_party_summary_and_reason()
    {
        ExternalInteractionLogged logged = TaskLogInteractionCommand.BuildEvent(
            Settings(party: "  the operator  ", summary: "  Skip it  ", humanDirected: true, reason: "  Real bug  "),
            DomainId.New(), DomainId.New(), Now);

        logged.Party.Should().Be("the operator");
        logged.Summary.Should().Be("Skip it");
        logged.Reason.Should().Be("Real bug");
    }
}
