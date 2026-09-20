using FluentAssertions;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The read view both presence surfaces load (idea 89471598, piece 1), built without a database.
/// What it has to carry beyond "registered or not" is why not: a window that said goodbye and a
/// window that was found gone are different facts, and "none live" names which one it was.
/// </summary>
public sealed class OrchestratorPresenceDetailsProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_launch_then_a_shutdown_then_a_relaunch_leaves_the_current_window_and_the_last_exit()
    {
        OrchestratorPresenceDetailsProjection projection = new();
        Guid nodeId = DomainId.New();
        Guid projectId = DomainId.New();

        OrchestratorPresenceDetails view = projection.Create(new FakeEvent<OrchestratorLaunched>(
            new OrchestratorLaunched(nodeId, projectId, "hall9k-orchestrator", 48213, "claude-code", Now, Now)));

        view.Registered.Should().BeTrue();
        view.SessionName.Should().Be("hall9k-orchestrator");
        view.ProcessId.Should().Be(48213);
        view.Cli.Should().Be("claude-code");
        view.LaunchedAt.Should().Be(Now);
        view.ShutDownAt.Should().BeNull();
        view.LostAt.Should().BeNull();

        projection.Apply(new FakeEvent<OrchestratorShutDown>(
            new OrchestratorShutDown(nodeId, projectId, "hall9k-orchestrator", 48213, Now.AddHours(2))), view);

        view.Registered.Should().BeFalse();
        view.ShutDownAt.Should().Be(Now.AddHours(2));

        projection.Apply(new FakeEvent<OrchestratorLaunched>(
            new OrchestratorLaunched(
                nodeId, projectId, "hall9k-evening", 52000, "codex", Now.AddHours(3), Now.AddHours(3))), view);

        view.Registered.Should().BeTrue();
        view.SessionName.Should().Be("hall9k-evening");
        view.ProcessId.Should().Be(52000);
        view.Cli.Should().Be("codex");
        view.ShutDownAt.Should().Be(Now.AddHours(2), "the earlier exit stays on the record; the relaunch does not erase it");
    }

    [Fact]
    public void A_loss_is_recorded_as_its_own_fact_rather_than_as_a_shutdown()
    {
        OrchestratorPresenceDetailsProjection projection = new();
        Guid nodeId = DomainId.New();
        Guid projectId = DomainId.New();

        OrchestratorPresenceDetails view = projection.Create(new FakeEvent<OrchestratorLaunched>(
            new OrchestratorLaunched(nodeId, projectId, "hall9k-orchestrator", 48213, "claude-code", Now, Now)));
        projection.Apply(new FakeEvent<OrchestratorLost>(
            new OrchestratorLost(nodeId, projectId, "hall9k-orchestrator", 48213, Now.AddMinutes(40))), view);

        view.Registered.Should().BeFalse();
        view.LostAt.Should().Be(Now.AddMinutes(40));
        view.ShutDownAt.Should().BeNull("the window never said goodbye, and the record must not claim it did");
    }

    [Fact]
    public void An_ending_that_names_a_superseded_window_leaves_the_current_one_registered()
    {
        // The projection has to mirror OrchestratorPresenceAggregate's own guard exactly: a
        // disagreement between the two is an h9k status line that contradicts what the register
        // refusal just decided off the aggregate.
        OrchestratorPresenceDetailsProjection projection = new();
        Guid nodeId = DomainId.New();
        Guid projectId = DomainId.New();

        OrchestratorPresenceDetails view = projection.Create(new FakeEvent<OrchestratorLaunched>(
            new OrchestratorLaunched(nodeId, projectId, "hall9k-orchestrator", 48213, "claude-code", Now, Now)));
        projection.Apply(new FakeEvent<OrchestratorLaunched>(
            new OrchestratorLaunched(
                nodeId, projectId, "hall9k-next-window", 51999, "claude-code",
                Now.AddMinutes(19), Now.AddMinutes(19))), view);

        projection.Apply(new FakeEvent<OrchestratorLost>(
            new OrchestratorLost(nodeId, projectId, "hall9k-orchestrator", 48213, Now.AddMinutes(20))), view);
        projection.Apply(new FakeEvent<OrchestratorShutDown>(
            new OrchestratorShutDown(nodeId, projectId, "hall9k-orchestrator", 48213, Now.AddMinutes(20))), view);

        view.Registered.Should().BeTrue();
        view.ProcessId.Should().Be(51999);
        view.LostAt.Should().BeNull();
        view.ShutDownAt.Should().BeNull();
    }

    [Fact]
    public void An_unreadable_process_start_time_is_carried_as_unknown()
    {
        OrchestratorPresenceDetailsProjection projection = new();

        OrchestratorPresenceDetails view = projection.Create(new FakeEvent<OrchestratorLaunched>(
            new OrchestratorLaunched(
                DomainId.New(), DomainId.New(), "hall9k-orchestrator", 48213, "claude-code", null, Now)));

        view.ProcessStartedAt.Should().BeNull();
    }
}
