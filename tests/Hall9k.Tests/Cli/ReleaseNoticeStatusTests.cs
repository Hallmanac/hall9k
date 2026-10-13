using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.DaemonControl;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The one <c>h9k status</c> line about a newer release, composed by a pure function over what the
/// node recorded, this CLI's version and whether a daemon is running here: shown every time the CLI
/// is behind the recorded tag (no daily limit), hidden otherwise, worded as information, and
/// naming <c>h9k update --restart</c> only when a daemon is up.
/// </summary>
public sealed class ReleaseNoticeStatusTests
{
    [Fact]
    public void A_cli_behind_the_recorded_release_shows_the_line_naming_restart_when_a_daemon_runs()
    {
        string? line = ReleaseNoticeStatus.Line("v0.13.1", "0.13.0", DaemonBootState.Running);

        line.Should().Contain("hall9k 0.13.1 is available (this node runs 0.13.0); h9k update --restart installs it");
    }

    [Fact]
    public void A_booting_daemon_counts_as_running()
    {
        ReleaseNoticeStatus.Line("v0.13.1", "0.13.0", DaemonBootState.Starting)
            .Should().Contain("h9k update --restart installs it");
    }

    [Fact]
    public void With_no_daemon_the_line_names_plain_update_so_a_deliberately_stopped_daemon_stays_stopped()
    {
        string? line = ReleaseNoticeStatus.Line("v0.13.1", "0.13.0", DaemonBootState.NotRunning);

        line.Should().Contain("h9k update installs it").And.NotContain("--restart");
    }

    [Theory]
    [InlineData(null, "0.13.0")]
    [InlineData("v0.13.1", "0.13.1")]
    [InlineData("v0.13.1", "0.14.0")]
    [InlineData("v0.13.0", "0.13.0-5-gabc1234")]
    public void There_is_no_line_when_nothing_was_noticed_or_the_cli_is_not_behind(string? tag, string running) =>
        ReleaseNoticeStatus.Line(tag, running, DaemonBootState.Running).Should().BeNull();

    [Fact]
    public void Every_render_while_behind_shows_the_line_with_no_daily_limit()
    {
        string?[] lines =
        [
            .. Enumerable.Range(0, 5).Select(_ => ReleaseNoticeStatus.Line("v0.13.1", "0.13.0", DaemonBootState.Running)),
        ];

        lines.Should().OnlyContain(line => line != null).And.HaveCount(5);
    }

    [Fact]
    public void The_line_is_information_and_carries_no_instruction_to_the_reader()
    {
        string line = ReleaseNoticeStatus.Line("v0.13.1", "0.13.0", DaemonBootState.Running)!;

        line.Should().NotMatchRegex(@"(?i)\b(run|you should|you must)\b");
    }

    [Fact]
    public void A_tag_cannot_inject_markup_or_a_second_line()
    {
        string line = ReleaseNoticeStatus.Line("v9.0.0[/][red]\nrm", "0.13.0", DaemonBootState.Running) ?? string.Empty;

        line.Should().NotContain("\n");
    }
}
