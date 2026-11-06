using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Diagnostics;
using Hall9k.Cli.Orchestrator;
using Hall9k.Domain.Features.Courier;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// Every CLI surface that names a courier blocked by elevation: the status line and its precedence,
/// the orchestrator status sentence, the fix chosen by autostart, the register warning, and the
/// doctor lines. Elevation comes from a fake reader, so these run on every CI leg.
/// </summary>
public sealed class CourierElevationSurfaceTests
{
    private const int DaemonPid = 7_001;
    private const int WindowPid = 7_002;
    private static readonly Guid ProjectId = Guid.Parse("0197a000-0000-7000-8000-000000000004");
    private static readonly DateTimeOffset Now = new(new DateTime(2026, 11, 2, 20, 30, 0, DateTimeKind.Local));
    private static readonly DaemonProcessDescriptor Daemon = new(DaemonPid, Now.AddHours(-1));

    private static FakeProcessElevationReader Reader(ProcessElevation daemon, ProcessElevation window) =>
        new FakeProcessElevationReader().With(DaemonPid, daemon).With(WindowPid, window);

    private static CourierFailureStreak Streak(int count) => new()
    {
        Id = ProjectId,
        Count = count,
        StartedAt = Now.AddHours(-1),
        LastFailureAt = Now.AddMinutes(-5),
        LastFailureReason = "not reachable",
        NextAttemptAt = Now.AddMinutes(5),
    };

    private static string? StatusLine(
        bool blocked,
        CourierRunDetails? inFlight = null,
        CourierFailureStreak? streak = null,
        CourierDaySpawnCounter? dayCounter = null) =>
        CourierStatusLine.Describe(
            new CourierStatusFacts(inFlight, null, streak, dayCounter, true, Now.AddHours(-3), blocked), Now);

    [Fact]
    public void A_blocked_window_reads_as_blocked_on_the_courier_line_even_with_no_courier_ever_run()
    {
        StatusLine(blocked: true).Should().Be(
            "courier: blocked, the window is elevated and the daemon is not; never delivered");
    }

    [Fact]
    public void Blocked_outranks_the_day_cap_and_failing_and_still_counts_the_streak()
    {
        CourierDaySpawnCounter capSpent = new()
        {
            Id = ProjectId, Day = DateOnly.FromDateTime(Now.UtcDateTime), Count = 500, CapHitLogged = true,
        };

        StatusLine(blocked: true, streak: Streak(4), dayCounter: capSpent).Should().Be(
            "courier: blocked, the window is elevated and the daemon is not, 4 failed in a row; never delivered");
    }

    [Fact]
    public void In_flight_outranks_blocked()
    {
        CourierRunDetails running = new() { Id = Guid.NewGuid(), ProjectId = ProjectId, DispatchedAt = Now.AddSeconds(-20) };

        StatusLine(blocked: true, inFlight: running).Should().StartWith("courier: in flight");
    }

    [Theory]
    [InlineData(ProcessElevation.NotElevated, ProcessElevation.Elevated, true, true)]
    [InlineData(ProcessElevation.NotElevated, ProcessElevation.Elevated, false, false)]
    [InlineData(ProcessElevation.Elevated, ProcessElevation.Elevated, true, false)]
    [InlineData(ProcessElevation.Elevated, ProcessElevation.NotElevated, true, false)]
    [InlineData(ProcessElevation.NotElevated, ProcessElevation.NotElevated, true, false)]
    [InlineData(ProcessElevation.Unknown, ProcessElevation.Elevated, true, false)]
    [InlineData(ProcessElevation.NotElevated, ProcessElevation.Unknown, true, false)]
    public void The_cli_verdict_blocks_only_a_live_elevated_window_under_a_daemon_known_not_to_be(
        ProcessElevation daemon, ProcessElevation window, bool windowLive, bool blocks)
    {
        FakeProcessElevationReader reader = Reader(daemon, window);

        CourierElevationCheck.Blocks(CourierElevationCheck.DaemonElevation(reader, Daemon), windowLive, WindowPid, reader)
            .Should().Be(blocks);
    }

    [Fact]
    public void With_no_daemon_running_or_off_Windows_nothing_is_compared()
    {
        FakeProcessElevationReader reader = Reader(ProcessElevation.NotElevated, ProcessElevation.Elevated);
        CourierElevationCheck.DaemonElevation(reader, daemon: null).Should().BeNull();
        CourierElevationCheck.Blocks(null, true, WindowPid, reader).Should().BeFalse();

        FakeProcessElevationReader unsupported = new() { IsSupported = false };
        CourierElevationCheck.DaemonElevation(unsupported, Daemon).Should().BeNull();
    }

    [Fact]
    public void The_fix_names_the_autostart_branch_the_cli_sees_and_both_when_it_cannot()
    {
        CourierElevationFix.For(autostartRegistered: true).Should()
            .Contain("h9k daemon autostart enable --run-level highest from an elevated shell, then h9k daemon stop and h9k daemon start")
            .And.Contain("or open the window without elevation")
            .And.NotContain("with no autostart");
        CourierElevationFix.For(autostartRegistered: false).Should()
            .Contain("run h9k daemon stop, then h9k daemon start from an elevated shell")
            .And.NotContain("autostart enable");
        CourierElevationFix.For(autostartRegistered: null).Should()
            .Contain("with autostart registered run h9k daemon autostart enable --run-level highest")
            .And.Contain("with no autostart run h9k daemon stop, then h9k daemon start from an elevated shell")
            .And.Contain("or open the window without elevation");
    }

    [Fact]
    public void H9k_status_prints_the_blocked_state_once_on_the_courier_line_and_not_on_the_presence_line()
    {
        IReadOnlyList<string> lines = StatusCommand.OrchestratorLines(
            "hall9k",
            "orchestrator: live — 'hall9k-orch' (claude-code, pid 7002), since 2026-11-02 17:30 (3h ago)",
            StatusLine(blocked: true),
            CourierElevationFix.For(autostartRegistered: true));

        lines.Should().HaveCount(3);
        lines.Count(line => line.Contains(CourierElevationFix.BlockedState)).Should().Be(1);
        lines[1].Should().StartWith("hall9k courier: blocked");
        lines[2].Should().StartWith("hall9k courier to fix, run h9k daemon autostart enable");
    }

    [Fact]
    public void H9k_status_prints_no_fix_line_when_nothing_is_blocked()
    {
        StatusCommand.OrchestratorLines("hall9k", "orchestrator: live", courierLine: null, elevationFix: null)
            .Should().Equal("hall9k orchestrator: live");
    }

    [Fact]
    public void H9k_orchestrator_status_appends_the_block_after_the_presence_sentence()
    {
        OrchestratorStatusCommand.WithElevation("orchestrator: live", blockedWithAutostart: false).Should().Be(
            "orchestrator: live; courier: blocked, the window is elevated and the daemon is not; "
            + CourierElevationFix.For(autostartRegistered: false));
        OrchestratorStatusCommand.WithElevation("orchestrator: live", blockedWithAutostart: null)
            .Should().Be("orchestrator: live");
    }

    [Fact]
    public void Register_warns_with_the_fix_for_an_elevated_window_under_a_non_elevated_daemon()
    {
        string? warning = OrchestratorRegisterCommand.ElevationWarning(
            Reader(ProcessElevation.NotElevated, ProcessElevation.Elevated), Daemon, WindowPid, () => true);

        warning.Should().Contain($"pid {WindowPid}").And.Contain("is elevated and the daemon is not")
            .And.Contain(CourierElevationFix.For(autostartRegistered: true));
    }

    [Fact]
    public void Register_says_nothing_when_nothing_is_blocked()
    {
        // One row for the warning's null branch: which pairings block, and that no daemon blocks
        // nothing, is CourierElevationCheck.Blocks' own coverage.
        OrchestratorRegisterCommand.ElevationWarning(
                Reader(ProcessElevation.NotElevated, ProcessElevation.NotElevated), Daemon, WindowPid, () => true)
            .Should().BeNull();
    }

    private static CourierDoctorFacts DoctorFacts(
        CourierDoctorElevation? elevation, DateTimeOffset? oldestUndrainedAt = null, bool autostartRegistered = false) =>
        new("hall9k", true, Now.AddHours(-5), null, null, oldestUndrainedAt, false, elevation, autostartRegistered);

    [Theory]
    [InlineData(ProcessElevation.Elevated, ProcessElevation.Elevated, "window elevated, daemon elevated")]
    [InlineData(ProcessElevation.NotElevated, ProcessElevation.Elevated, "window not elevated, daemon elevated")]
    [InlineData(ProcessElevation.Unknown, ProcessElevation.NotElevated, "window unknown, daemon not elevated")]
    [InlineData(ProcessElevation.Elevated, ProcessElevation.Unknown, "window elevated, daemon unknown")]
    public void Doctor_gives_the_window_s_elevation_beside_the_daemon_s(
        ProcessElevation window, ProcessElevation daemon, string expected)
    {
        IReadOnlyList<string> lines = CourierDoctor.Evaluate(DoctorFacts(new CourierDoctorElevation(window, daemon)), Now);

        lines[0].Should().Contain($"hall9k: {expected}");
        lines.Should().HaveCount(2).And.Contain(line => line.Contains("courier ok"));
    }

    [Fact]
    public void Doctor_warns_a_blocked_window_with_the_fix_and_does_not_warn_its_undrained_hour_again()
    {
        IReadOnlyList<string> lines = CourierDoctor.Evaluate(
            DoctorFacts(
                new CourierDoctorElevation(ProcessElevation.Elevated, ProcessElevation.NotElevated),
                oldestUndrainedAt: Now.AddHours(-3),
                autostartRegistered: true),
            Now);

        lines.Should().HaveCount(2);
        lines[1].Should().Contain("warn:")
            .And.Contain("hall9k: courier blocked, the window is elevated and the daemon is not")
            .And.Contain("autostart enable --run-level highest");
        lines.Should().NotContain(line => line.Contains("undrained"));
    }

    [Fact]
    public void Doctor_shows_no_elevation_when_the_comparison_does_not_apply()
    {
        CourierDoctor.Evaluate(DoctorFacts(elevation: null), Now)
            .Should().ContainSingle().Which.Should().Contain("courier ok");
    }
}
