using FluentAssertions;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The whole presence lifecycle of a project's orchestrator window on one node (idea 89471598,
/// piece 1), driven through the decider and the aggregate with a hand-written process table:
/// register, the same session registering again, a second live window refused and then replaced,
/// a deliberate deregister, and the loss the daemon's sweep records for a window that simply
/// stopped existing. Nothing here touches a database, a branch, or GitHub.
/// </summary>
public sealed class OrchestratorPresenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 18, 0, 0, TimeSpan.Zero);
    private static readonly Guid NodeId = DomainId.New();
    private static readonly Guid ProjectId = DomainId.New();

    [Fact]
    public void Register_records_the_session_the_process_the_cli_and_the_time()
    {
        FakeOrchestratorProcessProbe probe = new FakeOrchestratorProcessProbe().Running(48213, Now.AddMinutes(-1));

        OrchestratorRegistrationDecision decision = Register(presence: null, probe, "hall9k-orchestrator", 48213);

        decision.Outcome.Should().Be(OrchestratorRegistrationOutcome.Registered);
        decision.Replaced.Should().BeNull();
        decision.Launched.Should().NotBeNull();
        decision.Launched!.NodeId.Should().Be(NodeId);
        decision.Launched.ProjectId.Should().Be(ProjectId);
        decision.Launched.SessionName.Should().Be("hall9k-orchestrator");
        decision.Launched.ProcessId.Should().Be(48213);
        decision.Launched.Cli.Should().Be("claude-code");
        decision.Launched.LaunchedAt.Should().Be(Now);
        decision.Launched.ProcessStartedAt.Should().Be(Now.AddMinutes(-1),
            "the start time is read off the process table so a recycled pid can never read as this window");
    }

    [Fact]
    public void Register_normalizes_the_cli_name_the_way_the_launch_text_setting_does()
    {
        FakeOrchestratorProcessProbe probe = new FakeOrchestratorProcessProbe().Running(48213, Now);

        OrchestratorRegistrationDecision decision = OrchestratorPresenceDecider.Register(
            presence: null, NodeId, ProjectId, "hall9k-orchestrator", 48213, " Claude-Code ",
            replace: false, probe, Now);

        decision.Launched!.Cli.Should().Be("claude-code");
    }

    [Fact]
    public void Register_refuses_a_process_this_machine_cannot_find()
    {
        FakeOrchestratorProcessProbe probe = new();

        Action act = () => Register(presence: null, probe, "hall9k-orchestrator", 48213);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*No process 48213*", "a registration nothing can ever check is not worth recording");
    }

    [Fact]
    public void Register_refuses_a_blank_session_name_and_a_nonsense_process_id()
    {
        FakeOrchestratorProcessProbe probe = new FakeOrchestratorProcessProbe().Running(48213, Now);

        Action blankSession = () => Register(presence: null, probe, "  ", 48213);
        Action noProcess = () => Register(presence: null, probe, "hall9k-orchestrator", 0);

        blankSession.Should().Throw<DomainValidationException>();
        noProcess.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Registering_the_same_session_again_is_a_no_op()
    {
        FakeOrchestratorProcessProbe probe = new FakeOrchestratorProcessProbe().Running(48213, Now);
        OrchestratorPresenceAggregate presence = Live(probe, "hall9k-orchestrator", 48213);

        OrchestratorRegistrationDecision decision = Register(presence, probe, "hall9k-orchestrator", 48213);

        decision.Outcome.Should().Be(OrchestratorRegistrationOutcome.AlreadyRegistered);
        decision.Launched.Should().BeNull("an anchor read twice in one window must not litter the stream");
        decision.Replaced.Should().BeNull();
    }

    [Fact]
    public void The_same_process_under_a_new_session_name_re_announces_itself_rather_than_being_refused()
    {
        FakeOrchestratorProcessProbe probe = new FakeOrchestratorProcessProbe().Running(48213, Now);
        OrchestratorPresenceAggregate presence = Live(probe, "hall9k-orchestrator", 48213);

        OrchestratorRegistrationDecision decision = Register(presence, probe, "hall9k-board", 48213);

        decision.Outcome.Should().Be(OrchestratorRegistrationOutcome.Registered,
            "a session name is Claude Code's own mutable state, so a moved name is this window, not a second one");
        decision.Launched!.SessionName.Should().Be("hall9k-board");
        decision.Replaced.Should().BeNull();
    }

    [Fact]
    public void A_second_live_orchestrator_is_refused_naming_the_live_one()
    {
        FakeOrchestratorProcessProbe probe = new FakeOrchestratorProcessProbe()
            .Running(48213, Now)
            .Running(51999, Now.AddMinutes(10));
        OrchestratorPresenceAggregate presence = Live(probe, "hall9k-orchestrator", 48213);

        Action act = () => Register(presence, probe, "hall9k-second-window", 51999);

        act.Should().Throw<DomainConflictException>()
            .WithMessage("*hall9k-orchestrator*").And.Message.Should().Contain("48213").And.Contain("--replace");
    }

    [Fact]
    public void Replace_records_the_live_one_as_shut_down_before_the_new_one_launches()
    {
        FakeOrchestratorProcessProbe probe = new FakeOrchestratorProcessProbe()
            .Running(48213, Now)
            .Running(51999, Now.AddMinutes(10));
        OrchestratorPresenceAggregate presence = Live(probe, "hall9k-orchestrator", 48213);

        OrchestratorRegistrationDecision decision = OrchestratorPresenceDecider.Register(
            presence, NodeId, ProjectId, "hall9k-second-window", 51999, LaunchText.DefaultCli,
            replace: true, probe, Now.AddMinutes(11));

        decision.Outcome.Should().Be(OrchestratorRegistrationOutcome.Replaced);
        decision.Replaced!.SessionName.Should().Be("hall9k-orchestrator");
        decision.Replaced.ProcessId.Should().Be(48213);
        decision.Replaced.ShutDownAt.Should().Be(Now.AddMinutes(11));
        decision.Launched!.SessionName.Should().Be("hall9k-second-window");

        presence.Apply(decision.Replaced);
        presence.Apply(decision.Launched);
        presence.Registered.Should().BeTrue();
        presence.ProcessId.Should().Be(51999);
        presence.ShutDownAt.Should().Be(Now.AddMinutes(11), "the replaced window's exit is on the record too");
    }

    [Fact]
    public void A_registration_whose_process_is_already_gone_takes_over_without_asking_and_records_the_loss()
    {
        FakeOrchestratorProcessProbe probe = new FakeOrchestratorProcessProbe().Running(48213, Now);
        OrchestratorPresenceAggregate presence = Live(probe, "hall9k-orchestrator", 48213);
        probe.Gone(48213).Running(51999, Now.AddMinutes(10));

        OrchestratorRegistrationDecision decision = Register(presence, probe, "hall9k-next-window", 51999);

        decision.Outcome.Should().Be(OrchestratorRegistrationOutcome.Registered,
            "the refusal protects a LIVE window; a pid that is gone is not one, sweep or no sweep");
        decision.Replaced.Should().BeNull("nothing live was displaced, so this is no takeover");
        decision.Lost.Should().NotBeNull(
            "this register is the read that observed the window gone, and no later sweep can record it: "
            + "the sweep only ever rules on the currently registered pid, which is the new window from here on");
        decision.Lost!.SessionName.Should().Be("hall9k-orchestrator");
        decision.Lost.ProcessId.Should().Be(48213);
        decision.Lost.LostAt.Should().Be(Now, "the time is when it was noticed, which is this call");

        presence.Apply(decision.Lost);
        presence.Apply(decision.Launched!);
        presence.Registered.Should().BeTrue();
        presence.ProcessId.Should().Be(51999);
        presence.LostAt.Should().Be(Now, "the window that simply vanished has an ending on the record too");
    }

    [Fact]
    public void A_registration_after_an_ending_is_already_recorded_adds_no_second_one()
    {
        FakeOrchestratorProcessProbe probe = new FakeOrchestratorProcessProbe().Running(48213, Now);
        OrchestratorPresenceAggregate presence = Live(probe, "hall9k-orchestrator", 48213);
        presence.Apply(OrchestratorPresenceDecider.Deregister(presence, 48213, Now.AddHours(1)).ShutDown!);
        probe.Gone(48213).Running(51999, Now.AddHours(2));

        OrchestratorRegistrationDecision decision = Register(presence, probe, "hall9k-next-window", 51999);

        decision.Lost.Should().BeNull("the window already said goodbye; ending it twice would be a fiction");
        decision.Replaced.Should().BeNull();
        decision.Launched.Should().NotBeNull();
    }

    [Fact]
    public void A_recycled_pid_does_not_keep_a_dead_window_live()
    {
        FakeOrchestratorProcessProbe probe = new FakeOrchestratorProcessProbe().Running(48213, Now);
        OrchestratorPresenceAggregate presence = Live(probe, "hall9k-orchestrator", 48213);

        // Same pid, an entirely unrelated process the operating system handed it to an hour later.
        probe.Running(48213, Now.AddHours(1));

        OrchestratorLiveness.IsLive(presence, probe).Should().BeFalse();
    }

    [Fact]
    public void A_running_process_whose_start_time_cannot_be_read_stays_live()
    {
        FakeOrchestratorProcessProbe probe = new FakeOrchestratorProcessProbe().Running(48213, Now);
        OrchestratorPresenceAggregate presence = Live(probe, "hall9k-orchestrator", 48213);

        probe.RunningWithUnreadableStartTime(48213);

        OrchestratorLiveness.IsLive(presence, probe).Should().BeTrue(
            "an unreadable start time is an unknown, not evidence the window died");
    }

    [Fact]
    public void Deregister_records_a_shutdown_and_is_a_no_op_once_nothing_is_registered()
    {
        FakeOrchestratorProcessProbe probe = new FakeOrchestratorProcessProbe().Running(48213, Now);
        OrchestratorPresenceAggregate presence = Live(probe, "hall9k-orchestrator", 48213);

        OrchestratorDeregistrationDecision decision =
            OrchestratorPresenceDecider.Deregister(presence, 48213, Now.AddHours(2));

        decision.Outcome.Should().Be(OrchestratorDeregistrationOutcome.Deregistered);
        OrchestratorShutDown shutDown = decision.ShutDown!;
        shutDown.SessionName.Should().Be("hall9k-orchestrator");
        shutDown.ProcessId.Should().Be(48213);
        shutDown.ShutDownAt.Should().Be(Now.AddHours(2));

        presence.Apply(shutDown);
        presence.Registered.Should().BeFalse();
        presence.ShutDownAt.Should().Be(Now.AddHours(2));
        OrchestratorLiveness.IsLive(presence, probe).Should().BeFalse("the window said it was leaving");

        OrchestratorPresenceDecider.Deregister(presence, 48213, Now.AddHours(3)).Outcome.Should().Be(
            OrchestratorDeregistrationOutcome.NothingRegistered,
            "a close step that can fail is a close step an operator learns to skip");
        OrchestratorPresenceDecider.Deregister(presence: null, 48213, Now).ShutDown.Should().BeNull();
    }

    [Fact]
    public void Deregister_refuses_a_nonsense_process_id_rather_than_ending_whatever_is_registered()
    {
        FakeOrchestratorProcessProbe probe = new FakeOrchestratorProcessProbe().Running(48213, Now);
        OrchestratorPresenceAggregate presence = Live(probe, "hall9k-orchestrator", 48213);

        Action act = () => OrchestratorPresenceDecider.Deregister(presence, 0, Now);

        act.Should().Throw<DomainValidationException>().WithMessage("*not a process id*");
    }

    [Fact]
    public void A_stale_window_s_close_step_leaves_the_replacement_that_took_over_registered()
    {
        // The recipe's own restart contract, run in full: window A deregisters, the operator
        // starts window B, B's anchor registers it, and A — still an open Claude Code session —
        // is told later that it is done and runs its close step a second time. Deregistering
        // whatever is registered would end B here, and the superseded-ending guard could not
        // catch it, because that shutdown names B's own live pid.
        FakeOrchestratorProcessProbe probe = new FakeOrchestratorProcessProbe()
            .Running(48213, Now)
            .Running(51999, Now.AddMinutes(5));
        OrchestratorPresenceAggregate presence = Live(probe, "hall9k-orchestrator", 48213);

        presence.Apply(OrchestratorPresenceDecider.Deregister(presence, 48213, Now.AddMinutes(4)).ShutDown!);
        presence.Apply(OrchestratorPresenceDecider.Register(
            presence, NodeId, ProjectId, "hall9k-next-window", 51999, LaunchText.DefaultCli,
            replace: false, probe, Now.AddMinutes(5)).Launched!);

        OrchestratorDeregistrationDecision closingAgain =
            OrchestratorPresenceDecider.Deregister(presence, 48213, Now.AddMinutes(40));

        closingAgain.Outcome.Should().Be(OrchestratorDeregistrationOutcome.HeldByAnotherWindow);
        closingAgain.ShutDown.Should().BeNull("a window drops its own claim and only its own");
        presence.Registered.Should().BeTrue();
        presence.ProcessId.Should().Be(51999);
    }

    [Fact]
    public void The_sweep_records_a_registered_window_whose_process_is_gone_as_lost_with_the_time()
    {
        FakeOrchestratorProcessProbe probe = new FakeOrchestratorProcessProbe().Running(48213, Now);
        OrchestratorPresenceAggregate presence = Live(probe, "hall9k-orchestrator", 48213);

        OrchestratorPresenceDecider.Lose(presence, probe, Now.AddMinutes(5)).Should().BeNull(
            "a live window is nothing for the sweep to record");

        probe.Gone(48213);
        OrchestratorLost? lost = OrchestratorPresenceDecider.Lose(presence, probe, Now.AddMinutes(30));

        lost.Should().NotBeNull();
        lost!.SessionName.Should().Be("hall9k-orchestrator");
        lost.ProcessId.Should().Be(48213);
        lost.LostAt.Should().Be(Now.AddMinutes(30), "the time is when the sweep noticed, never a guess at the death");

        presence.Apply(lost);
        presence.Registered.Should().BeFalse();
        presence.LostAt.Should().Be(Now.AddMinutes(30));

        OrchestratorPresenceDecider.Lose(presence, probe, Now.AddMinutes(45)).Should().BeNull(
            "the transition is recorded once, not on every tick that follows");
    }

    [Fact]
    public void A_loss_that_lands_behind_a_newer_window_s_launch_does_not_unregister_it()
    {
        // The close-one-window-start-another race: the sweep re-aggregates, finds pid 48213
        // gone, and decides a loss; the operator's replacement registers and commits first; the
        // sweep's loss lands afterwards. Applied blind it would read as "none live" for a window
        // that is running, which is the answer the courier and h9k status both act on.
        FakeOrchestratorProcessProbe probe = new FakeOrchestratorProcessProbe().Running(48213, Now);
        OrchestratorPresenceAggregate presence = Live(probe, "hall9k-orchestrator", 48213);

        probe.Gone(48213);
        OrchestratorLost lost = OrchestratorPresenceDecider.Lose(presence, probe, Now.AddMinutes(20))!;

        probe.Running(51999, Now.AddMinutes(19));
        OrchestratorRegistrationDecision replacement = Register(presence, probe, "hall9k-next-window", 51999);
        presence.Apply(replacement.Lost!);
        presence.Apply(replacement.Launched!);

        presence.Apply(lost);

        presence.Registered.Should().BeTrue();
        presence.ProcessId.Should().Be(51999);
        presence.LostAt.Should().Be(Now,
            "the register's own loss stands, stamped when it observed the window gone; the sweep's later "
            + "one names a window this stream has already moved past and changes nothing");
    }

    [Fact]
    public void A_deregister_that_lands_behind_a_replacement_does_not_unregister_it_either()
    {
        FakeOrchestratorProcessProbe probe = new FakeOrchestratorProcessProbe()
            .Running(48213, Now)
            .Running(51999, Now.AddMinutes(5));
        OrchestratorPresenceAggregate presence = Live(probe, "hall9k-orchestrator", 48213);

        // The leaving window's own deregister, decided before the replacement took over.
        OrchestratorShutDown slowGoodbye =
            OrchestratorPresenceDecider.Deregister(presence, 48213, Now.AddMinutes(6)).ShutDown!;

        OrchestratorRegistrationDecision replacement = OrchestratorPresenceDecider.Register(
            presence, NodeId, ProjectId, "hall9k-next-window", 51999, LaunchText.DefaultCli,
            replace: true, probe, Now.AddMinutes(6));
        presence.Apply(replacement.Replaced!);
        presence.Apply(replacement.Launched!);

        presence.Apply(slowGoodbye);

        presence.Registered.Should().BeTrue();
        presence.ProcessId.Should().Be(51999);
    }

    [Fact]
    public void The_stream_id_is_the_same_for_one_node_and_project_and_different_for_another_node()
    {
        Guid otherNode = DomainId.New();

        OrchestratorPresenceStreamId.For(NodeId, ProjectId)
            .Should().Be(OrchestratorPresenceStreamId.For(NodeId, ProjectId));
        OrchestratorPresenceStreamId.For(otherNode, ProjectId)
            .Should().NotBe(OrchestratorPresenceStreamId.For(NodeId, ProjectId),
                "presence is a fact about one machine's own process table");
    }

    private static OrchestratorRegistrationDecision Register(
        OrchestratorPresenceAggregate? presence, IOrchestratorProcessProbe probe, string sessionName, int processId) =>
        OrchestratorPresenceDecider.Register(
            presence, NodeId, ProjectId, sessionName, processId, LaunchText.DefaultCli, replace: false, probe, Now);

    private static OrchestratorPresenceAggregate Live(
        IOrchestratorProcessProbe probe, string sessionName, int processId)
    {
        OrchestratorPresenceAggregate presence = new();
        presence.Apply(Register(presence: null, probe, sessionName, processId).Launched!);
        return presence;
    }
}
