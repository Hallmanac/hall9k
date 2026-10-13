using FluentAssertions;
using Hall9k.Cli.DaemonControl;
using Hall9k.Cli.Diagnostics;
using Hall9k.Connectors.Releases;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// What <c>h9k doctor</c> says after running the release lookup live (here, faked): current,
/// a newer release available (naming the right update command for whether a daemon runs), or could
/// not check with the reason. A release still uploading reads as current, with a note.
/// </summary>
public sealed class ReleaseDoctorTests
{
    private const string Running = "0.13.0";
    private static readonly ReleaseChannelResolution Channel = new(ReleaseChannel.Cleared, null);

    [Fact]
    public void The_newest_release_being_the_running_one_reads_as_current()
    {
        ReleaseDoctorReport report = ReleaseDoctor.Evaluate(
            LatestReleaseResult.Found(Channel, "v0.13.0"), Running, DaemonBootState.Running);

        report.Verdict.Should().Be(ReleaseVerdict.Current);
        report.Line.Should().Contain("0.13.0 is current");
    }

    [Fact]
    public void A_local_build_past_the_latest_tag_reads_as_current()
    {
        ReleaseDoctor.Evaluate(LatestReleaseResult.Found(Channel, "v0.13.0"), "0.13.0-5-gabc1234", DaemonBootState.Running)
            .Verdict.Should().Be(ReleaseVerdict.Current);
    }

    [Fact]
    public void A_newer_release_with_a_daemon_running_names_update_restart()
    {
        ReleaseDoctorReport report = ReleaseDoctor.Evaluate(
            LatestReleaseResult.Found(Channel, "v0.13.1"), Running, DaemonBootState.Running);

        report.Verdict.Should().Be(ReleaseVerdict.UpdateAvailable);
        report.Line.Should().Contain("hall9k 0.13.1 is available (this node runs 0.13.0); h9k update --restart installs it");
    }

    [Fact]
    public void A_newer_release_with_a_booting_daemon_names_update_restart_too()
    {
        ReleaseDoctor.Evaluate(LatestReleaseResult.Found(Channel, "v0.13.1"), Running, DaemonBootState.Starting)
            .Line.Should().Contain("h9k update --restart installs it");
    }

    [Fact]
    public void A_newer_release_with_no_daemon_names_plain_update_because_a_stopped_daemon_is_deliberate_on_some_nodes()
    {
        ReleaseDoctorReport report = ReleaseDoctor.Evaluate(
            LatestReleaseResult.Found(Channel, "v0.13.1"), Running, DaemonBootState.NotRunning);

        report.Verdict.Should().Be(ReleaseVerdict.UpdateAvailable);
        report.Line.Should().Contain("h9k update installs it").And.NotContain("--restart");
    }

    [Fact]
    public void A_newer_tag_still_publishing_reads_as_current_with_a_note_saying_so()
    {
        ReleaseDoctorReport report = ReleaseDoctor.Evaluate(
            LatestReleaseResult.NotYetComplete(Channel, "v0.13.1", ["checksums.txt"]), Running, DaemonBootState.Running);

        report.Verdict.Should().Be(ReleaseVerdict.Current);
        report.Line.Should().Contain("is current").And.Contain("v0.13.1").And.Contain("still publishing");
    }

    [Fact]
    public void An_incomplete_release_that_is_not_newer_is_plain_current_with_no_publishing_note()
    {
        ReleaseDoctorReport report = ReleaseDoctor.Evaluate(
            LatestReleaseResult.NotYetComplete(Channel, "v0.13.0", ["checksums.txt"]), Running, DaemonBootState.Running);

        report.Verdict.Should().Be(ReleaseVerdict.Current);
        report.Line.Should().NotContain("publishing");
    }

    [Fact]
    public void No_published_release_reads_as_current_because_nothing_newer_exists()
    {
        ReleaseDoctor.Evaluate(
                LatestReleaseResult.Failed(LatestReleaseOutcome.NoRelease, Channel, "gh release list", "none"), Running, DaemonBootState.Running)
            .Verdict.Should().Be(ReleaseVerdict.Current);
    }

    [Theory]
    [InlineData(LatestReleaseOutcome.GhMissing, "gh is not installed")]
    [InlineData(LatestReleaseOutcome.GhFailed, "signed out")]
    [InlineData(LatestReleaseOutcome.TimedOut, "timed out")]
    [InlineData(LatestReleaseOutcome.Unparseable, "could not be read")]
    [InlineData(LatestReleaseOutcome.UnsupportedPlatform, "no release build")]
    public void A_lookup_that_could_not_answer_reads_as_could_not_check_with_the_reason(LatestReleaseOutcome outcome, string reason)
    {
        ReleaseDoctorReport report = ReleaseDoctor.Evaluate(
            LatestReleaseResult.Failed(outcome, Channel, "gh release view", "signed out"), Running, DaemonBootState.Running);

        report.Verdict.Should().Be(ReleaseVerdict.CouldNotCheck);
        report.Line.Should().Contain("could not check").And.Contain(reason);
    }

    [Fact]
    public void Output_from_gh_cannot_inject_markup_into_the_line()
    {
        ReleaseDoctorReport report = ReleaseDoctor.Evaluate(
            LatestReleaseResult.Failed(LatestReleaseOutcome.GhFailed, Channel, "gh release view", "[/][red]boom\nsecond"),
            Running, DaemonBootState.Running);

        report.Line.Should().NotContain("\n").And.Contain("[[/]]");
    }
}
