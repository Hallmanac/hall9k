using FluentAssertions;
using Hall9k.Cli.Orchestrator;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The one sentence <c>h9k orchestrator status</c> and the <c>h9k status</c> header both print
/// (idea 89471598, piece 1). Its whole job is to be honest between sweeps: a window closed a
/// moment ago is still registered on the document, and this line says "none live" anyway,
/// because this node can read its own process table.
/// </summary>
public sealed class OrchestratorPresenceLineTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_live_window_is_named_with_its_session_cli_process_and_age()
    {
        FakeOrchestratorProcessProbe probe = new FakeOrchestratorProcessProbe().Running(48213, Now.AddHours(-2));

        string line = OrchestratorPresenceLine.Describe(
            Registered("hall9k-orchestrator", 48213, Now.AddHours(-2)), probe, Now);

        line.Should().StartWith("orchestrator: live");
        line.Should().Contain("hall9k-orchestrator").And.Contain("claude-code").And.Contain("48213");
        line.Should().Contain("2h00m ago");
    }

    [Fact]
    public void A_registered_window_whose_process_is_gone_reads_as_none_live_before_the_sweep_catches_up()
    {
        FakeOrchestratorProcessProbe probe = new();

        string line = OrchestratorPresenceLine.Describe(
            Registered("hall9k-orchestrator", 48213, Now.AddHours(-2)), probe, Now);

        line.Should().StartWith("orchestrator: none live");
        line.Should().Contain("its process is").And.Contain("gone");
        line.Should().Contain("presence sweep", "the line says who will make the record catch up");
    }

    [Fact]
    public void A_deregistered_window_reports_when_it_shut_down()
    {
        OrchestratorPresenceDetails presence = Registered("hall9k-orchestrator", 48213, Now.AddHours(-3));
        presence.Registered = false;
        presence.ShutDownAt = Now.AddHours(-1);

        string line = OrchestratorPresenceLine.Describe(presence, new FakeOrchestratorProcessProbe(), Now);

        line.Should().StartWith("orchestrator: none live").And.Contain("last shut down");
    }

    [Fact]
    public void A_lost_window_reports_the_loss_and_a_later_loss_wins_over_an_earlier_shutdown()
    {
        OrchestratorPresenceDetails presence = Registered("hall9k-orchestrator", 48213, Now.AddHours(-3));
        presence.Registered = false;
        presence.ShutDownAt = Now.AddHours(-3);
        presence.LostAt = Now.AddHours(-1);

        string line = OrchestratorPresenceLine.Describe(presence, new FakeOrchestratorProcessProbe(), Now);

        line.Should().Contain("last lost").And.NotContain("last shut down");
    }

    [Fact]
    public void Nothing_ever_registered_here_says_exactly_that()
    {
        string line = OrchestratorPresenceLine.Describe(presence: null, new FakeOrchestratorProcessProbe(), Now);

        line.Should().Be("orchestrator: none live — none has ever registered on this machine");
    }

    private static OrchestratorPresenceDetails Registered(string sessionName, int processId, DateTimeOffset launchedAt) =>
        new()
        {
            Id = DomainId.New(),
            NodeId = DomainId.New(),
            ProjectId = DomainId.New(),
            Registered = true,
            SessionName = sessionName,
            ProcessId = processId,
            Cli = "claude-code",
            ProcessStartedAt = launchedAt,
            LaunchedAt = launchedAt,
        };
}
