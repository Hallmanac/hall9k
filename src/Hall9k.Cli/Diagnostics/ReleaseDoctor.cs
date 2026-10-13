using Hall9k.Cli.Commands;
using Hall9k.Cli.DaemonControl;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Releases;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Infrastructure;
using Spectre.Console;

namespace Hall9k.Cli.Diagnostics;

/// <summary>What <see cref="ReleaseDoctor"/> concluded about this CLI against the newest release.</summary>
public enum ReleaseVerdict
{
    /// <summary>Nothing newer exists, or the newer tag is still uploading its assets.</summary>
    Current,

    /// <summary>A newer release is published and complete.</summary>
    UpdateAvailable,

    /// <summary>The lookup could not answer, so nothing is known either way.</summary>
    CouldNotCheck,
}

/// <summary>The doctor's verdict and the one line of markup it prints.</summary>
internal sealed record ReleaseDoctorReport(ReleaseVerdict Verdict, string Line);

/// <summary>
/// <c>h9k doctor</c>'s release check (idea 93c1d24d, card 2): runs the shared lookup live, which
/// the daemon's own notice does on a timer, and compares the answer against this CLI's own version.
/// The one surface that tells the truth when the daemon has not noticed yet, is stopped, or noticed a
/// release a changed channel no longer offers. It only informs: nothing here installs or restarts.
/// </summary>
public static class ReleaseDoctor
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        ReleaseCheck check = LiveReleaseCheck.Create(
            new ProjectGitHubClient().AmbientProcessRunner, Directory.GetCurrentDirectory());
        await RunAsync(check, CliVersion.Current, DaemonProcess.ProbeBootStatus().State, cancellationToken);
    }

    internal static async Task RunAsync(
        ReleaseCheck check, string runningVersion, DaemonBootState daemon, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[bold]Release[/]");
        LatestReleaseResult result = await check(cancellationToken);
        AnsiConsole.MarkupLine(Evaluate(result, runningVersion, daemon).Line);
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// The three results the doctor can give, from one lookup answer, this CLI's version and
    /// whether a daemon is running here (which decides whether the update command says
    /// <c>--restart</c>). Pure, so every outcome and both commands are unit-testable.
    /// </summary>
    internal static ReleaseDoctorReport Evaluate(LatestReleaseResult result, string runningVersion, DaemonBootState daemon)
    {
        string running = ExternalText.OneLineMarkup(runningVersion);
        return result.Outcome switch
        {
            LatestReleaseOutcome.Found when result.Tag is { } tag && ReleaseTagVersion.IsNewerThan(tag, runningVersion) =>
                new ReleaseDoctorReport(
                    ReleaseVerdict.UpdateAvailable,
                    $"[yellow]  update available:[/] hall9k {ExternalText.OneLineMarkup(ReleaseTagVersion.Bare(tag))} is available "
                    + $"(this node runs {running}); {ReleaseNoticeStatus.UpdateCommandFor(daemon)} installs it"),
            LatestReleaseOutcome.Found =>
                new ReleaseDoctorReport(ReleaseVerdict.Current, $"[dim]  hall9k {running} is current.[/]"),
            LatestReleaseOutcome.NotYetComplete when result.CandidateTag is { } publishing
                && ReleaseTagVersion.IsNewerThan(publishing, runningVersion) =>
                new ReleaseDoctorReport(
                    ReleaseVerdict.Current,
                    $"[dim]  hall9k {running} is current. The newer tag {ExternalText.OneLineMarkup(publishing)} is still "
                    + "publishing (its assets are still uploading); check again in a few minutes.[/]"),
            LatestReleaseOutcome.NotYetComplete =>
                new ReleaseDoctorReport(ReleaseVerdict.Current, $"[dim]  hall9k {running} is current.[/]"),
            LatestReleaseOutcome.NoRelease =>
                new ReleaseDoctorReport(
                    ReleaseVerdict.Current,
                    $"[dim]  hall9k {running} is current: no published release was found to compare against.[/]"),
            _ => new ReleaseDoctorReport(
                ReleaseVerdict.CouldNotCheck, $"[yellow]  could not check for a newer release:[/] {Reason(result)}"),
        };
    }

    private static string Reason(LatestReleaseResult result) => result.Outcome switch
    {
        LatestReleaseOutcome.GhMissing =>
            "gh is not installed or not on the PATH. Install it from https://cli.github.com and run gh auth login.",
        LatestReleaseOutcome.GhFailed =>
            $"{ExternalText.OneLineMarkup(result.Command)} failed: {ExternalText.OneLineMarkup(result.Detail)} (check gh auth status).",
        LatestReleaseOutcome.TimedOut =>
            $"{ExternalText.OneLineMarkup(result.Command)} timed out: {ExternalText.OneLineMarkup(result.Detail)}.",
        LatestReleaseOutcome.Unparseable =>
            $"{ExternalText.OneLineMarkup(result.Command)} returned output that could not be read: {ExternalText.OneLineMarkup(result.Detail)}.",
        LatestReleaseOutcome.UnsupportedPlatform =>
            $"this platform has no release build ({ExternalText.OneLineMarkup(result.Detail)}).",
        _ => $"the lookup ended {result.Outcome}.",
    };
}
