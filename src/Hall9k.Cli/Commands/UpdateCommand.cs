using System.ComponentModel;
using System.Text.Json;
using Hall9k.Cli.Infrastructure;
using Hall9k.Cli.Installation;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
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
        [Description("Bring the daemon up onto the fresh binaries without asking, whether or not one was already running — the newly installed h9k then runs h9k daemon stop, h9k doctor --yes --no-configure and h9k daemon start in that order, so an update carrying a schema change ends with the daemon up on a current schema; that doctor step will start a stopped hall9k-postgres container to get there, recreates it first if it is publishing port 5432 on anything but 127.0.0.1 (including a stopped one, never just docker-started with its old binding), and may also migrate the Postgres password off the shipped default onto a generated one, but --no-configure keeps it from recording a connection string on a machine where none resolves")]
        public bool Restart { get; init; }

        [CommandOption("--no-restart")]
        [Description("Leave a running daemon on its current binaries (it picks up the new ones at its next start)")]
        public bool NoRestart { get; init; }

        [CommandOption("--now")]
        [Description("With --restart, skip waiting for a live verification gate on this node to finish and restart at once — h9k daemon stop's own warning still prints, it just no longer holds the restart back")]
        public bool Now { get; init; }
    }

    /// <summary>The release archive is a self-contained, untrimmed publish of two apps —
    /// multiple tens of megabytes — where <see cref="ExternalProcess.Deadline"/>'s two
    /// minutes (sized for a single-issue metadata read) kills a healthy download on an
    /// ordinary connection. Ten minutes is generous for the payload without hanging
    /// indefinitely on a truly wedged <c>gh</c>.</summary>
    private static readonly TimeSpan DownloadDeadline = TimeSpan.FromMinutes(10);

    // No project is involved (the release comes from Hall9k's own repository, never a registered
    // project's), so this still funnels through ProjectGitHubClient — the one place the platform
    // spawns gh — but in its ambient mode: whatever gh's own auth already resolves to, exactly
    // the behaviour this had before the migration (idea 202383dc, A2b item 4).
    private readonly ProcessRunner gh = gh ?? new ProjectGitHubClient(
        ExternalProcess.RunnerWithEnvironmentAndDeadline(DownloadDeadline)).AmbientProcessRunner;

    protected override Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken) =>
        RunAsync(
            gh,
            settings.Repository ?? ReleasePlatform.DefaultRepository,
            settings.Restart,
            settings.NoRestart,
            settings.Now,
            cancellationToken: cancellationToken);

    /// <summary>The whole command, independent of Spectre: the thin wrapper above unpacks
    /// settings and calls this, and tests call it directly with a fake <paramref name="gh"/>.
    /// <paramref name="linkOntoPath"/> defaults true for the real command; a test passes
    /// false to keep <see cref="InstallCommand.FinishAsync"/> from touching this
    /// machine's actual PATH and home directory (see the comment at its call site).
    /// <paramref name="containerRuntimeRunner"/> is the same kind of test seam, for the docker
    /// calls <see cref="InstallCommand.FinishAsync"/>'s own port-binding check makes: it defaults
    /// to the real docker CLI, and a test passes a fake so the check never depends on whatever
    /// Docker happens to be running on the machine the test suite executes on.
    /// <paramref name="scratchRoot"/> is the same kind of seam for where the download/extract
    /// scratch directories below are created: it defaults to the real machine-wide
    /// <see cref="Path.GetTempPath"/>, and a test passes a directory unique to itself so its own
    /// before/after scratch-directory check never observes another process's own in-flight
    /// scratch directories — this host runs many worktrees' test suites against the same shared
    /// OS temp directory at once, and two of them racing there is not a defect in either.</summary>
    internal static async Task<int> RunAsync(
        ProcessRunner gh,
        string repository,
        bool restart,
        bool noRestart,
        bool now = false,
        bool linkOntoPath = true,
        ProcessRunner? containerRuntimeRunner = null,
        string? scratchRoot = null,
        CancellationToken cancellationToken = default)
    {
        string? rid = ReleasePlatform.CurrentRid();
        if (rid is null)
        {
            await Console.Error.WriteLineAsync(
                $"h9k update has no release for this platform — release.yml builds {string.Join(", ", ReleasePlatform.SupportedRids)} "
                + "only. Build from source instead: h9k install --repo <path>.");
            return ExitCodes.Error;
        }

        string workingDirectory = Directory.GetCurrentDirectory();
        string archiveName = ReleasePlatform.ArchiveFileName(rid);

        // Both directories are scratch space for this one run — the archive is copied into
        // ~/.hall9k/bin by way of staging, never read from here again — so they are removed
        // on every exit, success or failure, rather than left for the temp directory to
        // accumulate release-sized payloads across every update.
        string scratchBase = scratchRoot ?? Path.GetTempPath();
        string downloadDirectory = Path.Combine(scratchBase, $"h9k-update-{Path.GetRandomFileName()}");
        string extractDirectory = Path.Combine(scratchBase, $"h9k-update-extract-{Path.GetRandomFileName()}");
        try
        {
            Directory.CreateDirectory(downloadDirectory);

            AnsiConsole.MarkupLineInterpolated($"[dim]Resolving the latest release for {repository}…[/]");
            string? tag = await ResolveLatestTagAsync(gh, repository, workingDirectory, cancellationToken);
            if (tag is null)
            {
                return ExitCodes.Error;
            }

            AnsiConsole.MarkupLineInterpolated($"[dim]Fetching {tag} for {rid} from {repository}…[/]");
            ProcessResult download;
            try
            {
                download = await gh(
                    "gh",
                    [
                        "release", "download", tag,
                        "--repo", repository,
                        "--pattern", archiveName,
                        "--pattern", "checksums.txt",
                        "--dir", downloadDirectory,
                        "--clobber",
                    ],
                    workingDirectory,
                    cancellationToken);
            }
            catch (Win32Exception)
            {
                await Console.Error.WriteLineAsync(
                    "gh is not installed or not on the PATH — h9k update fetches releases through the GitHub "
                    + "CLI. Install it from https://cli.github.com and run gh auth login.");
                return ExitCodes.Error;
            }
            catch (TimeoutException exception)
            {
                await Console.Error.WriteLineAsync($"gh release download timed out: {exception.Message}");
                return ExitCodes.Error;
            }

            if (download.ExitCode != 0)
            {
                await Console.Error.WriteLineAsync(
                    $"gh release download failed for {repository} ({archiveName}) at tag {tag}:");
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

            int? attestationExitCode = await VerifyAttestationAsync(
                gh, repository, tag, archivePath, workingDirectory, cancellationToken);
            if (attestationExitCode is not null)
            {
                return attestationExitCode.Value;
            }

            AnsiConsole.MarkupLine("[dim]Attestation verified.[/]");

            string? checksumProblem = await ReleaseArchive.VerifyAsync(archivePath, checksumsPath, cancellationToken);
            if (checksumProblem is not null)
            {
                await Console.Error.WriteLineAsync(checksumProblem);
                return ExitCodes.Error;
            }

            AnsiConsole.MarkupLine("[dim]Checksum verified.[/]");

            await ReleaseArchive.ExtractAsync(archivePath, extractDirectory, cancellationToken);

            string? payloadProblem = InstallCommand.ValidateReleasePayload(extractDirectory);
            if (payloadProblem is not null)
            {
                await Console.Error.WriteLineAsync(payloadProblem);
                return ExitCodes.Error;
            }

            string version = InstallCommand.ReadVersionFile(extractDirectory) ?? "unknown";
            string skillsSource = Path.Combine(extractDirectory, "skills");

            string staging = DaemonRuntime.StagingBinDirectory;
            InstallCommand.TryDelete(staging);

            InstallCommand.StageFromRelease(extractDirectory, staging, cancellationToken);

            // writeDefaultConnectionStringIfUnconfigured stays off here (FinishAsync's
            // default): an already-installed machine that has never needed config.json's
            // connectionString key has been relying on something update cannot see from
            // wherever it happens to run — an environment variable in a different shell, or a
            // per-project .hall9k-connection override elsewhere on disk — and guessing a
            // default here would permanently outrank that override the moment it is written
            // (cycle-1 review). h9k install is the only place a genuinely fresh, nothing-
            // configured-yet machine gets that guess recorded.
            return await InstallCommand.FinishAsync(
                staging,
                skillsSource,
                version,
                restart,
                noRestart,
                now,
                linkOntoPath,
                commandName: "update",
                containerRuntimeRunner: containerRuntimeRunner,
                cancellationToken: cancellationToken);
        }
        finally
        {
            TryDeleteScratchDirectory(downloadDirectory);
            TryDeleteScratchDirectory(extractDirectory);
        }
    }

    /// <summary>
    /// The tag <c>gh release download</c> pins its download to below, resolved separately rather
    /// than left implicit in a tag-less <c>gh release download</c> (which floats to whatever is
    /// latest at the moment it runs): both the download and the attestation verify that follows
    /// it need to agree on one concrete tag, and <c>--source-ref refs/tags/&lt;tag&gt;</c> only
    /// means something once that tag is in hand. Returns the resolved tag, or null once the
    /// caller should return <see cref="ExitCodes.Error"/> without going any further — the message
    /// has already gone to stderr either way, so there is nothing else for the caller to report.
    /// </summary>
    private static async Task<string?> ResolveLatestTagAsync(
        ProcessRunner gh, string repository, string workingDirectory, CancellationToken cancellationToken)
    {
        ProcessResult view;
        try
        {
            view = await gh(
                "gh",
                ["release", "view", "--repo", repository, "--json", "tagName"],
                workingDirectory,
                cancellationToken);
        }
        catch (Win32Exception)
        {
            await Console.Error.WriteLineAsync(
                "gh is not installed or not on the PATH — h9k update fetches releases through the GitHub "
                + "CLI. Install it from https://cli.github.com and run gh auth login.");
            return null;
        }
        catch (TimeoutException exception)
        {
            await Console.Error.WriteLineAsync($"gh release view timed out: {exception.Message}");
            return null;
        }

        if (view.ExitCode != 0)
        {
            await Console.Error.WriteLineAsync($"gh release view failed for {repository}:");
            await Console.Error.WriteLineAsync(view.StandardError);
            await Console.Error.WriteLineAsync(
                "Check gh auth status — a private repository's releases need an authenticated gh (gh auth login).");
            return null;
        }

        string? tagName;
        try
        {
            using JsonDocument document = JsonDocument.Parse(view.StandardOutput);
            tagName = document.RootElement.GetProperty("tagName").GetString();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            await Console.Error.WriteLineAsync(
                $"gh release view returned output h9k update could not parse for {repository}: {exception.Message}");
            return null;
        }

        if (string.IsNullOrEmpty(tagName))
        {
            await Console.Error.WriteLineAsync(
                $"gh release view returned no tagName for {repository} — cannot resolve which release to download.");
            return null;
        }

        return tagName;
    }

    /// <summary>
    /// Verifies the downloaded archive carries a GitHub artifact attestation pinned to both the
    /// resolved release tag (<c>--source-ref</c>) and the release workflow that is meant to have
    /// produced it (<c>--signer-workflow</c>) — <c>--repo</c> alone would accept an attestation
    /// from any workflow on any ref in the repository, which defeats the point (security review
    /// idea 6be68ee2, finding 4: this closes an asset uploaded or replaced outside release.yml,
    /// a stolen PAT or a <c>--clobber</c> re-upload — not a compromised token that can push the
    /// tag itself and run release.yml legitimately; that is closed by a tag ruleset, a GitHub
    /// Settings change rather than code). Returns null on a successful verification, or the exit
    /// code the caller should return once this has written the refusal to stderr.
    /// <para>
    /// There is deliberately no <c>--skip-attestation</c> escape hatch: every refusal below names
    /// the one manual path that remains (download the release yourself, then
    /// <c>h9k install --from-release &lt;dir&gt;</c>, which verifies nothing of its own) rather than
    /// offering a flag that would make an attacker's job of bypassing verification a one-word one.
    /// </para>
    /// </summary>
    private static async Task<int?> VerifyAttestationAsync(
        ProcessRunner gh, string repository, string tag, string archivePath, string workingDirectory,
        CancellationToken cancellationToken)
    {
        string archiveName = Path.GetFileName(archivePath);
        string manualPath =
            $"To install without verification, download {archiveName} from the {tag} release yourself and run "
            + "h9k install --from-release <dir> instead (that path verifies nothing of its own) — there is no "
            + "--skip-attestation flag.";

        ProcessResult verify;
        try
        {
            verify = await gh(
                "gh",
                [
                    "attestation", "verify", archivePath,
                    "--repo", repository,
                    "--source-ref", $"refs/tags/{tag}",
                    "--signer-workflow", $"{repository}/.github/workflows/release.yml",
                ],
                workingDirectory,
                cancellationToken);
        }
        catch (Win32Exception)
        {
            await Console.Error.WriteLineAsync(
                "gh is not installed or not on the PATH — h9k update fetches releases through the GitHub "
                + "CLI. Install it from https://cli.github.com and run gh auth login.");
            return ExitCodes.Error;
        }
        catch (TimeoutException exception)
        {
            await Console.Error.WriteLineAsync($"gh attestation verify timed out: {exception.Message}");
            return ExitCodes.Error;
        }

        if (verify.ExitCode == 0)
        {
            return null;
        }

        // gh 2.49.0 is where `gh attestation` first shipped, but --source-ref and --signer-workflow
        // (both passed above) need 2.68.0 (cli/cli#10308) — a gh between those two versions has the
        // subcommand but not the flags, and reports "unknown flag" rather than "unknown command" (AC:
        // the Mac this was written on carries 2.100.0; an older gh — Windows unconfirmed at the time
        // of writing — reports one of the two). Either wording means gh is unable to attempt a
        // verification at all, so it gets its own message rather than being reported as an unattested
        // or tampered release.
        if (verify.StandardError.Contains("unknown command", StringComparison.OrdinalIgnoreCase)
            || verify.StandardError.Contains("unknown flag", StringComparison.OrdinalIgnoreCase))
        {
            await Console.Error.WriteLineAsync(
                "gh is too old to verify a release attestation — attestation support needs gh 2.68.0 or newer. "
                + "Upgrade gh from https://cli.github.com and run h9k update again.");
            return ExitCodes.Error;
        }

        if (verify.StandardError.Contains("no attestations found", StringComparison.OrdinalIgnoreCase))
        {
            await Console.Error.WriteLineAsync(
                $"The {tag} release carries no attestation for {archiveName} — refusing to install an unattested "
                + $"archive. {manualPath}");
            return ExitCodes.Error;
        }

        await Console.Error.WriteLineAsync($"Attestation verification failed for {archiveName} at tag {tag}:");
        await Console.Error.WriteLineAsync(verify.StandardError);
        await Console.Error.WriteLineAsync(manualPath);
        return ExitCodes.Error;
    }

    /// <summary>Best-effort, matching <see cref="InstallCommand.TryDelete"/>: a locked file
    /// (Defender or an indexer still holding one of the just-extracted executables open) must
    /// not turn a command that already finished — successfully or not — into an unhandled
    /// exception that discards the real outcome.</summary>
    private static void TryDeleteScratchDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Could not remove scratch directory {directory} yet (still in use) — it will be left for manual cleanup.[/]");
        }
    }
}
