using FluentAssertions;
using Hall9k.Domain.Features.Courier;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Which orchestrator registrations reset a failure streak: the ones that append a launch, because
/// the delivery address changed, and not the unchanged same-process register, which appends nothing.
/// Driven through <see cref="OrchestratorPresenceDecider"/> itself so the two cannot drift apart.
/// </summary>
public sealed class CourierStreakRegistrationResetTests
{
    private static readonly Guid NodeId = DomainId.New();
    private static readonly Guid ProjectId = DomainId.New();
    private static readonly DateTimeOffset WindowOpenedAt = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LastFailureAt = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RegisterAt = new(2026, 11, 2, 10, 5, 0, TimeSpan.Zero);

    private static CourierFailureStreak StreakFailingSince9() => new()
    {
        Id = ProjectId,
        Count = 12,
        StartedAt = WindowOpenedAt,
        LastFailureAt = LastFailureAt,
        LastFailureReason = "not reachable",
        NextAttemptAt = LastFailureAt.AddMinutes(30),
    };

    private static (FakeOrchestratorProcessProbe Probe, OrchestratorPresenceAggregate Presence) LiveWindow()
    {
        FakeOrchestratorProcessProbe probe = new FakeOrchestratorProcessProbe()
            .Running(48213, WindowOpenedAt)
            .Running(51999, WindowOpenedAt.AddMinutes(30));
        OrchestratorPresenceAggregate presence = new();
        presence.Apply(OrchestratorPresenceDecider.Register(
            null, NodeId, ProjectId, "hall9k-orchestrator", 48213, "claude-code", replace: false, probe, WindowOpenedAt).Launched!);
        return (probe, presence);
    }

    /// <summary>The launch time <c>h9k status</c> would then read: the new launch's, or the old one when nothing was appended.</summary>
    private static DateTimeOffset LaunchedAtAfter(OrchestratorRegistrationDecision decision) =>
        decision.Launched?.LaunchedAt ?? WindowOpenedAt;

    [Fact]
    public void An_unchanged_register_from_the_same_process_appends_nothing_and_does_not_reset()
    {
        (FakeOrchestratorProcessProbe probe, OrchestratorPresenceAggregate presence) = LiveWindow();

        OrchestratorRegistrationDecision decision = OrchestratorPresenceDecider.Register(
            presence, NodeId, ProjectId, "hall9k-orchestrator", 48213, "claude-code", replace: false, probe, RegisterAt);

        decision.Outcome.Should().Be(OrchestratorRegistrationOutcome.AlreadyRegistered);
        CourierFailureStreak.WasResetByRegistration(StreakFailingSince9(), LaunchedAtAfter(decision)).Should().BeFalse();
    }

    [Fact]
    public void A_register_that_changes_the_session_name_appends_a_launch_and_resets()
    {
        (FakeOrchestratorProcessProbe probe, OrchestratorPresenceAggregate presence) = LiveWindow();

        OrchestratorRegistrationDecision decision = OrchestratorPresenceDecider.Register(
            presence, NodeId, ProjectId, "hall9k-board", 48213, "claude-code", replace: false, probe, RegisterAt);

        CourierFailureStreak.WasResetByRegistration(StreakFailingSince9(), LaunchedAtAfter(decision)).Should().BeTrue();
    }

    [Fact]
    public void A_register_that_changes_the_cli_appends_a_launch_and_resets()
    {
        (FakeOrchestratorProcessProbe probe, OrchestratorPresenceAggregate presence) = LiveWindow();

        OrchestratorRegistrationDecision decision = OrchestratorPresenceDecider.Register(
            presence, NodeId, ProjectId, "hall9k-orchestrator", 48213, "codex", replace: false, probe, RegisterAt);

        CourierFailureStreak.WasResetByRegistration(StreakFailingSince9(), LaunchedAtAfter(decision)).Should().BeTrue();
    }

    [Fact]
    public void A_new_window_taking_over_with_replace_resets()
    {
        (FakeOrchestratorProcessProbe probe, OrchestratorPresenceAggregate presence) = LiveWindow();

        OrchestratorRegistrationDecision decision = OrchestratorPresenceDecider.Register(
            presence, NodeId, ProjectId, "hall9k-second", 51999, "claude-code", replace: true, probe, RegisterAt);

        CourierFailureStreak.WasResetByRegistration(StreakFailingSince9(), LaunchedAtAfter(decision)).Should().BeTrue();
    }
}
