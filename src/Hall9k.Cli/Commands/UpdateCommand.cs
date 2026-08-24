using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Cli.Installation;
using Hall9k.Connectors.Processes;
using Hall9k.Domain.Infrastructure.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The one-command path for a machine that is already installed (backlog 42): fetch the
/// latest release for this platform through <c>gh</c> (the machine's already-authenticated
/// GitHub credential, the same seam <see cref="Hall9k.Connectors.WorkItems.GitHubWorkItemProvider"/>
/// uses), verify its checksum, republish through the same idempotent path as
/// <c>h9k install --from-release</c>, republish the canonical skill set, and offer the daemon
/// restart — everything a second machine needs to stay current without a repo checkout or the
/// .NET SDK. The bootstrap scripts cover the machine that does not have <c>h9k</c> yet; this
/// covers the one that does.
/// </summary>
public sealed class UpdateCommand(ProcessRunner? gh = null) : Hall9kAsyncCommand<UpdateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--repo <OWNER/REPO>")]
        [Description("The GitHub repository releases are fetched from")]
        public string? Repository { get; init; }

        [CommandOption("--restart")]
        [Description("Restart a running daemon onto the fresh binaries without asking")]
        public bool Restart { get; init; }

        [CommandOption("--no-restart")]
        [Description("Leave a running daemon on its current binaries (it picks up the new ones at its next start)")]
        public bool NoRestart { get; init; }
    }

    private readonly ProcessRunner gh = gh ?? ExternalProcess.Runner;

    protected override Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken) =>
        RunAsync(gh, settings.Repository ?? ReleasePlatform.DefaultRepository, settings.Restart, settings.NoRestart, cancellationToken);

    /// <summary>The whole command, independent of Spectre: the thin wrapper above unpacks
    /// settings and calls this, and tests call it directly with a fake <paramref name="gh"/>.
    /// <paramref name="linkOntoPath"/> defaults true for the real command; a test passes
    /// false to keep <see cref="InstallCommand.FinishAsync"/> from touching this
    /// machine's actual PATH and home directory (see the comment at its call site).</summary>
    internal static async Task<int> RunAsync(
        ProcessRunner gh,
        string repository,
        bool restart,
        bool noRestart,
        CancellationToken cancellationToken,
        bool linkOntoPath = true)
    {
        string? rid = ReleasePlatform.CurrentRid();
        if (rid is null)
        {
            await Console.Error.WriteLineAsync(
                "h9k update has no release for this platform — release.yml builds osx-arm64, win-x64, "
                + "and linux-x64 only. Build from source instead: h9k install --repo <path>.");
            return ExitCodes.Error;
        }

        string workingDirectory = Directory.GetCurrentDirectory();
        string archiveName = ReleasePlatform.ArchiveFileName(rid);

        string downloadDirectory = Path.Combine(Path.GetTempPath(), $"h9k-update-{Path.GetRandomFileName()}");
        Directory.CreateDirectory(downloadDirectory);

        AnsiConsole.MarkupLineInterpolated($"[dim]Fetching the latest release for {rid} from {repository}…[/]");
        ProcessResult download = await gh(
            "gh",
            [
                "release", "download",
                "--repo", repository,
                "--pattern", archiveName,
                "--pattern", "checksums.txt",
                "--dir", downloadDirectory,
                "--clobber",
            ],
            workingDirectory,
            cancellationToken);
        if (download.ExitCode != 0)
        {
            await Console.Error.WriteLineAsync(
                $"gh release download failed for {repository} ({archiveName}):");
            await Console.Error.WriteLineAsync(download.StandardError);
            await Console.Error.WriteLineAsync(
                "Check gh auth status — a private repository's releases need an authenticated gh (gh auth login).");
            return ExitCodes.Error;
        }

        string archivePath = Path.Combine(downloadDirectory, archiveName);
        string checksumsPath = Path.Combine(downloadDirectory, "checksums.txt");
        if (!File.Exists(archivePath))
        {
            await Console.Error.WriteLineAsync(
                $"gh release download reported success but {archiveName} is not in {downloadDirectory} — "
                + "the release may not carry a build for this platform yet.");
            return ExitCodes.Error;
        }

        string? checksumProblem = await ReleaseArchive.VerifyAsync(archivePath, checksumsPath, cancellationToken);
        if (checksumProblem is not null)
        {
            await Console.Error.WriteLineAsync(checksumProblem);
            return ExitCodes.Error;
        }

        AnsiConsole.MarkupLine("[dim]Checksum verified.[/]");

        string extractDirectory = Path.Combine(Path.GetTempPath(), $"h9k-update-extract-{Path.GetRandomFileName()}");
        await ReleaseArchive.ExtractAsync(archivePath, extractDirectory, cancellationToken);

        string? payloadProblem = InstallCommand.ValidateReleasePayload(extractDirectory);
        if (payloadProblem is not null)
        {
            await Console.Error.WriteLineAsync(payloadProblem);
            return ExitCodes.Error;
        }

        string version = File.Exists(Path.Combine(extractDirectory, "VERSION"))
            ? (await File.ReadAllTextAsync(Path.Combine(extractDirectory, "VERSION"), cancellationToken)).Trim()
            : "unknown";
        string skillsSource = Path.Combine(extractDirectory, "skills");

        string staging = DaemonRuntime.StagingBinDirectory;
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        InstallCommand.StageFromRelease(extractDirectory, staging);

        return await InstallCommand.FinishAsync(
            staging,
            Directory.Exists(skillsSource) ? skillsSource : null,
            version,
            restart,
            noRestart,
            cancellationToken,
            linkOntoPath);
    }
}
