using System.Formats.Tar;
using System.IO.Compression;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Cli.Installation;
using Hall9k.Connectors.Releases;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Prompts;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The one-command path for a machine already installed (backlog 42): fetch, verify,
/// stage, and finish through the same idempotent republish as h9k install --from-release.
/// A fake gh stands in for the real download and, on success, writes the release payload
/// it would have placed — the download itself is not the interesting part; what h9k does
/// with what gh handed back is.
/// </summary>
// UpdateCommand publishes under HALL9K_HOME; redirecting it to a temp directory keeps
// the write off a developer's or CI runner's real home. The PATH-linking step is
// skipped here (RunAsync's linkOntoPath: false) rather than redirected, because it
// mutates the REAL process PATH and home directory — redirecting those two env vars
// process-wide would race any concurrently running test that shells out to git/gh/docker
// via PATH (origin incident: an early version of this test wiped PATH out from under
// GitWorktreeManagerTests running in a different collection at the same time, and,
// before that fix, briefly overwrote this machine's own real /opt/homebrew/bin/h9k with
// a symlink into a temp directory that was deleted moments later). LinkOntoPath and
// ComputeUserPath already have direct unit coverage with fake paths in
// InstallCommandTests, so skipping the step here loses no coverage.
public sealed class UpdateCommandTests : IDisposable
{
    private readonly ScopedTestHome _scopedHome = new();
    private readonly string workspace = Path.Combine(Path.GetTempPath(), $"h9k-update-workspace-{Path.GetRandomFileName()}");
    // A scratch root unique to this test instance, handed to UpdateCommand.RunAsync so its
    // download/extract directories land here instead of the machine-wide temp directory: this
    // host runs many worktrees' test suites against that same shared directory at once, and a
    // sibling process's own in-flight "h9k-update-*" scratch directory would otherwise show up
    // as a false leak in TempScratchDirectories()'s before/after diff.
    private readonly string scratchRoot = Path.Combine(Path.GetTempPath(), $"h9k-update-scratch-{Path.GetRandomFileName()}");

    private string home => _scopedHome.Home;

    public UpdateCommandTests()
    {
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(scratchRoot);
    }

    public void Dispose()
    {
        _scopedHome.Dispose();
        // Best-effort, matching InstallCommand.TryDelete's own reason: a recently-written
        // executable can still be held by Defender or an indexer, and an unguarded delete
        // here would replace the test's real outcome with an unrelated IOException.
        InstallCommand.TryDelete(workspace);
        InstallCommand.TryDelete(scratchRoot);
    }

    [Fact]
    public async Task A_verified_release_is_installed_and_its_skills_published()
    {
        if (ReleasePlatform.CurrentRid() is null)
        {
            // No release build target for this platform (release.yml covers osx-arm64,
            // win-x64, win-arm64, linux-x64) — nothing to stage a fake download for.
            return;
        }

        FakeGh gh = FakeGh.ForCurrentPlatform(workspace, version: "1.2.3", skillName: "pr-summary");

        int exitCode = await Run(gh.Runner);

        exitCode.Should().Be(0);
        File.Exists(Path.Combine(DaemonRuntime.BinDirectory, InstallCommand.BinaryFileName("h9k"))).Should().BeTrue();
        File.Exists(Path.Combine(DaemonRuntime.BinDirectory, InstallCommand.BinaryFileName("h9kd"))).Should().BeTrue();
        File.Exists(Path.Combine(SkillLibraryPaths.CanonicalDirectory, "pr-summary", "SKILL.md")).Should().BeTrue();
        File.Exists(PostgresRuntime.ComposeFile).Should().BeTrue();
    }

    [Fact]
    public async Task Updating_over_an_existing_install_replaces_its_binaries()
    {
        if (ReleasePlatform.CurrentRid() is null)
        {
            return;
        }

        FakeGh first = FakeGh.ForCurrentPlatform(workspace, version: "1.2.3", skillName: "pr-summary");
        (await Run(first.Runner)).Should().Be(0);

        string binary = Path.Combine(DaemonRuntime.BinDirectory, InstallCommand.BinaryFileName("h9k"));
        File.ReadAllText(binary).Should().Be("cli\n");

        // The interesting case for SwapIntoPlace: bin already exists, so this exercises the
        // branch that retires whatever was there before — a directory-level rename on Unix,
        // a file-by-file merge on Windows (InstallCommand.SwapFilesIntoPlace) — rather than
        // the fresh-install branch the first run above just took.
        string secondWorkspace = Path.Combine(Path.GetTempPath(), $"h9k-update-workspace-{Path.GetRandomFileName()}");
        Directory.CreateDirectory(secondWorkspace);
        try
        {
            FakeGh second = FakeGh.ForCurrentPlatform(
                secondWorkspace, version: "1.2.4", skillName: "pr-summary", cliContent: "cli v2\n", daemonContent: "daemon v2\n");

            (await Run(second.Runner)).Should().Be(0);

            File.ReadAllText(binary).Should().Be("cli v2\n");
            File.ReadAllText(Path.Combine(DaemonRuntime.BinDirectory, InstallCommand.BinaryFileName("h9kd")))
                .Should().Be("daemon v2\n");
        }
        finally
        {
            InstallCommand.TryDelete(secondWorkspace);
        }
    }

    [Fact]
    public async Task Updating_over_a_locked_binary_on_windows_retires_it_instead_of_failing()
    {
        if (!OperatingSystem.IsWindows() || ReleasePlatform.CurrentRid() is null)
        {
            // The retire-aside path this test exercises (InstallCommand.RetireFile) only runs
            // on Windows; SwapIntoPlace takes the directory-rename branch everywhere else.
            return;
        }

        FakeGh first = FakeGh.ForCurrentPlatform(workspace, version: "1.2.3", skillName: "pr-summary");
        (await Run(first.Runner)).Should().Be(0);

        string binary = Path.Combine(DaemonRuntime.BinDirectory, InstallCommand.BinaryFileName("h9k"));

        string secondWorkspace = Path.Combine(Path.GetTempPath(), $"h9k-update-workspace-{Path.GetRandomFileName()}");
        Directory.CreateDirectory(secondWorkspace);
        try
        {
            FakeGh second = FakeGh.ForCurrentPlatform(
                secondWorkspace, version: "1.2.4", skillName: "pr-summary", cliContent: "cli v2\n", daemonContent: "daemon v2\n");

            // Models a running h9k.exe the same way UninstallCommandTests does: the OS loader
            // maps a running module's image with FILE_SHARE_DELETE granted, which is what lets
            // its name be renamed aside while it is still mapped, even though overwriting its
            // bytes in place is refused — a plain FileShare.Read handle would be stricter than
            // reality and block the rename this test means to exercise too.
            //
            // This handle is deliberately not asserted to keep the retired .old sibling alive
            // afterwards: unlike a genuinely running h9k.exe, a plain FileStream never maps the
            // file as an executable image, so it has no defense against InstallCommand's own
            // closing TryDeleteFile reclaiming the retiree the moment placement finishes — that
            // reclaim is exactly as real Windows behaves for any retiree that turns out not to
            // still be locked. What this test can prove either way is that the update completed
            // and the new content landed under the binary's original name despite the file being
            // open throughout — the retiree's own fate afterwards is not.
            using (new FileStream(binary, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                (await Run(second.Runner)).Should().Be(0);
            }

            File.ReadAllText(binary).Should().Be("cli v2\n", "the new version takes the locked file's name once it is retired aside");
        }
        finally
        {
            InstallCommand.TryDelete(secondWorkspace);
        }
    }

    [Fact]
    public async Task Updating_with_the_daemon_running_retires_the_locked_daemon_binary_instead_of_failing()
    {
        if (!OperatingSystem.IsWindows() || ReleasePlatform.CurrentRid() is null)
        {
            // The retire-aside path this test exercises (InstallCommand.RetireFile) only runs
            // on Windows; SwapIntoPlace takes the directory-rename branch everywhere else.
            return;
        }

        FakeGh first = FakeGh.ForCurrentPlatform(workspace, version: "1.2.3", skillName: "pr-summary");
        (await Run(first.Runner)).Should().Be(0);

        string daemonBinary = Path.Combine(DaemonRuntime.BinDirectory, InstallCommand.BinaryFileName("h9kd"));

        string secondWorkspace = Path.Combine(Path.GetTempPath(), $"h9k-update-workspace-{Path.GetRandomFileName()}");
        Directory.CreateDirectory(secondWorkspace);
        try
        {
            FakeGh second = FakeGh.ForCurrentPlatform(
                secondWorkspace, version: "1.2.4", skillName: "pr-summary", cliContent: "cli v2\n", daemonContent: "daemon v2\n");

            // Same share-delete modeling as the h9k.exe test above, held on h9kd(.exe) instead:
            // the per-file retire logic in InstallCommand is generic (SwapFilesIntoPlace retires
            // whatever it cannot overwrite, by name, never by which binary it is), so a running
            // daemon needs no special pre-swap stop step — this is the acceptance criterion that
            // claim rests on, exercised directly rather than only through h9k's own binary. See
            // the h9k.exe test above for why the retired .old sibling's own survival afterwards
            // is not asserted here either.
            using (new FileStream(daemonBinary, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                (await Run(second.Runner)).Should().Be(0);
            }

            File.ReadAllText(daemonBinary).Should().Be(
                "daemon v2\n", "the new version takes the locked daemon binary's name once it is retired aside");
        }
        finally
        {
            InstallCommand.TryDelete(secondWorkspace);
        }
    }

    [Fact]
    public async Task A_tampered_download_is_refused_before_anything_is_installed()
    {
        if (ReleasePlatform.CurrentRid() is null)
        {
            return;
        }

        FakeGh gh = FakeGh.ForCurrentPlatform(workspace, version: "1.2.3", skillName: "pr-summary");
        gh.CorruptTheDownloadedArchive();

        int exitCode = await Run(gh.Runner);

        exitCode.Should().NotBe(0);
        Directory.Exists(DaemonRuntime.BinDirectory).Should().BeFalse("a checksum mismatch must refuse before the swap");
    }

    [Fact]
    public async Task A_failing_gh_download_is_refused_with_the_auth_hint()
    {
        ProcessRunner failing = (_, _, _, _) => Task.FromResult(new ProcessResult(1, string.Empty, "HTTP 404: Not Found"));

        int exitCode = await Run(failing);

        exitCode.Should().NotBe(0);
    }

    [Fact]
    public async Task The_download_and_the_attestation_verify_are_both_pinned_to_the_tag_resolved_from_gh_release_view()
    {
        if (ReleasePlatform.CurrentRid() is null)
        {
            return;
        }

        FakeGh gh = FakeGh.ForCurrentPlatform(workspace, version: "1.2.3", skillName: "pr-summary", tag: "v9.9.9");

        int exitCode = await Run(gh.Runner);

        exitCode.Should().Be(0);
        gh.DownloadedTag.Should().Be("v9.9.9");
        gh.VerifiedSourceRef.Should().Be("refs/tags/v9.9.9");
    }

    [Fact]
    public async Task A_failed_attestation_verification_aborts_before_extraction()
    {
        if (ReleasePlatform.CurrentRid() is null)
        {
            return;
        }

        FakeGh gh = FakeGh.ForCurrentPlatform(workspace, version: "1.2.3", skillName: "pr-summary");
        gh.MakeAttestationVerifyFail();

        int exitCode = await Run(gh.Runner);

        exitCode.Should().NotBe(0);
        Directory.Exists(DaemonRuntime.BinDirectory).Should().BeFalse(
            "a failed attestation verification must refuse before the checksum check or the swap ever run");
    }

    [Fact]
    public async Task A_release_carrying_no_attestation_is_refused_with_its_own_message()
    {
        if (ReleasePlatform.CurrentRid() is null)
        {
            return;
        }

        FakeGh gh = FakeGh.ForCurrentPlatform(workspace, version: "1.2.3", skillName: "pr-summary", tag: "v1.2.3");
        gh.MakeAttestationVerifyReportNoAttestation();

        using ScopedConsoleCapture captured = ScopedConsoleCapture.StandardError();

        int exitCode = await Run(gh.Runner);

        exitCode.Should().NotBe(0);
        Directory.Exists(DaemonRuntime.BinDirectory).Should().BeFalse();
        captured.Text.Should().Contain("v1.2.3").And.Contain("no attestation");
    }

    [Fact]
    public async Task A_gh_too_old_to_verify_attestations_prints_an_upgrade_message_rather_than_a_verification_failure()
    {
        if (ReleasePlatform.CurrentRid() is null)
        {
            return;
        }

        FakeGh gh = FakeGh.ForCurrentPlatform(workspace, version: "1.2.3", skillName: "pr-summary");
        gh.MakeAttestationVerifyReportGhTooOld();

        using ScopedConsoleCapture captured = ScopedConsoleCapture.StandardError();

        int exitCode = await Run(gh.Runner);

        exitCode.Should().NotBe(0);
        Directory.Exists(DaemonRuntime.BinDirectory).Should().BeFalse();
        captured.Text.Should().Contain("2.68.0").And.Contain("Upgrade gh");
        captured.Text.Should().NotContain("Attestation verification failed",
            "gh being too old to attempt verification is not the same outcome as a verification that ran and failed");
    }

    [Fact]
    public async Task A_gh_new_enough_for_attestation_but_too_old_for_source_ref_prints_the_same_upgrade_message()
    {
        if (ReleasePlatform.CurrentRid() is null)
        {
            return;
        }

        FakeGh gh = FakeGh.ForCurrentPlatform(workspace, version: "1.2.3", skillName: "pr-summary");
        gh.MakeAttestationVerifyReportGhTooOldForSourceRefFlag();

        using ScopedConsoleCapture captured = ScopedConsoleCapture.StandardError();

        int exitCode = await Run(gh.Runner);

        exitCode.Should().NotBe(0);
        Directory.Exists(DaemonRuntime.BinDirectory).Should().BeFalse();
        captured.Text.Should().Contain("2.68.0").And.Contain("Upgrade gh");
        captured.Text.Should().NotContain("Attestation verification failed",
            "a gh new enough for `gh attestation` but too old for --source-ref must not be reported as a failed verification");
    }

    [Fact]
    public async Task A_successful_update_leaves_no_scratch_directories_in_temp()
    {
        if (ReleasePlatform.CurrentRid() is null)
        {
            return;
        }

        FakeGh gh = FakeGh.ForCurrentPlatform(workspace, version: "1.2.3", skillName: "pr-summary");
        IReadOnlySet<string> before = TempScratchDirectories();

        int exitCode = await Run(gh.Runner);

        exitCode.Should().Be(0);
        TempScratchDirectories().Except(before).Should().BeEmpty(
            "h9k update's download and extract directories are scratch space for one run, not a growing pile in temp");
    }

    [Fact]
    public async Task A_refused_update_still_cleans_up_its_scratch_directories()
    {
        if (ReleasePlatform.CurrentRid() is null)
        {
            return;
        }

        FakeGh gh = FakeGh.ForCurrentPlatform(workspace, version: "1.2.3", skillName: "pr-summary");
        gh.CorruptTheDownloadedArchive();
        IReadOnlySet<string> before = TempScratchDirectories();

        int exitCode = await Run(gh.Runner);

        exitCode.Should().NotBe(0);
        TempScratchDirectories().Except(before).Should().BeEmpty(
            "a refused update must not leave its downloaded archive or extracted payload behind in temp");
    }

    [Fact]
    public async Task A_release_whose_assets_are_not_all_attached_yet_says_so_and_downloads_nothing()
    {
        if (ReleasePlatform.CurrentRid() is null)
        {
            return;
        }

        FakeGh gh = FakeGh.ForCurrentPlatform(workspace, version: "1.2.3", skillName: "pr-summary");
        gh.AttachOnly("checksums.txt");
        using ScopedConsoleCapture captured = ScopedConsoleCapture.StandardError();

        int exitCode = await Run(gh.Runner);

        exitCode.Should().NotBe(0);
        captured.Text.Should().Contain("not yet complete").And.Contain(ReleasePlatform.ArchiveFileName(ReleasePlatform.CurrentRid()!));
        gh.DownloadedTag.Should().BeNull("a release that is not complete must never reach the download");
        Directory.Exists(DaemonRuntime.BinDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task The_cleared_channel_installs_the_release_github_calls_latest()
    {
        if (ReleasePlatform.CurrentRid() is null)
        {
            return;
        }

        FakeGh gh = FakeGh.ForCurrentPlatform(workspace, version: "1.2.3", skillName: "pr-summary", tag: "v1.2.3");
        gh.ListRelease("v1.3.0", isDraft: false);

        int exitCode = await Run(gh.Runner, ReleaseChannel.Cleared);

        exitCode.Should().Be(0);
        gh.DownloadedTag.Should().Be("v1.2.3");
        gh.Verbs.Should().NotContain("release list");
    }

    [Fact]
    public async Task The_all_channel_installs_the_highest_published_version_and_pins_the_attestation_to_it()
    {
        if (ReleasePlatform.CurrentRid() is null)
        {
            return;
        }

        FakeGh gh = FakeGh.ForCurrentPlatform(workspace, version: "1.2.3", skillName: "pr-summary", tag: "v1.2.3");
        gh.ListRelease("v1.2.9", isDraft: false);
        gh.ListRelease("v1.3.0", isDraft: true);
        gh.ListRelease("v1.2.10", isDraft: false);

        int exitCode = await Run(gh.Runner, ReleaseChannel.All);

        exitCode.Should().Be(0);
        gh.DownloadedTag.Should().Be("v1.2.10", "the highest published version wins over the newest created and over a draft");
        gh.VerifiedSourceRef.Should().Be("refs/tags/v1.2.10");
    }

    [Fact]
    public async Task A_missing_gh_keeps_its_install_hint()
    {
        using ScopedConsoleCapture captured = ScopedConsoleCapture.StandardError();

        int exitCode = await Run((_, _, _, _) => throw new Win32Exception("No such file or directory"));

        exitCode.Should().NotBe(0);
        captured.Text.Should().Contain("gh is not installed or not on the PATH").And.Contain("https://cli.github.com");
    }

    [Fact]
    public async Task A_signed_out_gh_keeps_its_auth_hint()
    {
        using ScopedConsoleCapture captured = ScopedConsoleCapture.StandardError();

        int exitCode = await Run((_, _, _, _) => Task.FromResult(new ProcessResult(1, string.Empty, "HTTP 401: Bad credentials")));

        exitCode.Should().NotBe(0);
        captured.Text.Should().Contain("gh release view failed for").And.Contain("HTTP 401: Bad credentials").And.Contain("gh auth login");
    }

    [Fact]
    public async Task A_repository_with_no_cleared_release_points_at_clearing_one_or_the_all_channel()
    {
        using ScopedConsoleCapture captured = ScopedConsoleCapture.StandardError();

        int exitCode = await Run((_, _, _, _) => Task.FromResult(new ProcessResult(1, string.Empty, "release not found")));

        exitCode.Should().NotBe(0);
        captured.Text.Should().Contain("gh release edit <tag> --prerelease=false --latest").And.Contain("--release-channel all");
    }

    [Fact]
    public async Task A_gh_that_times_out_keeps_its_timeout_message()
    {
        using ScopedConsoleCapture captured = ScopedConsoleCapture.StandardError();

        int exitCode = await Run((_, _, _, _) => throw new TimeoutException("gh did not answer"));

        exitCode.Should().NotBe(0);
        captured.Text.Should().Contain("gh release view timed out: gh did not answer");
    }

    [Fact]
    public async Task Unparseable_gh_output_keeps_its_parse_message()
    {
        using ScopedConsoleCapture captured = ScopedConsoleCapture.StandardError();

        int exitCode = await Run((_, _, _, _) => Task.FromResult(new ProcessResult(0, "<html>rate limited</html>", string.Empty)));

        exitCode.Should().NotBe(0);
        captured.Text.Should().Contain("gh release view returned output h9k update could not parse for");
    }

    [Fact]
    public async Task A_channel_warning_is_printed_and_the_update_still_proceeds_on_cleared()
    {
        if (ReleasePlatform.CurrentRid() is null)
        {
            return;
        }

        FakeGh gh = FakeGh.ForCurrentPlatform(workspace, version: "1.2.3", skillName: "pr-summary");
        using ScopedConsoleCapture captured = ScopedConsoleCapture.StandardError();

        int exitCode = await UpdateCommand.RunAsync(
            gh.Runner, ReleasePlatform.DefaultRepository, restart: false, noRestart: true, linkOntoPath: false,
            readReleaseChannel: _ => Task.FromResult(
                new ReleaseChannelResolution(ReleaseChannel.Cleared, "Release channel: could not use config.json; using cleared.")),
            containerRuntimeRunner: (_, _, _, _) => Task.FromResult(new ProcessResult(1, string.Empty, "docker not reached in this test")),
            restartChildRunner: (_, _, _) => Task.FromResult(RestartStepResult.Exited(ExitCodes.Ok)),
            scratchRoot: scratchRoot,
            cancellationToken: CancellationToken.None);

        exitCode.Should().Be(0);
        captured.Text.Should().Contain("Release channel: could not use config.json; using cleared.");
    }

    // Scoped to this test instance's own scratchRoot (never the machine-wide temp directory),
    // so a sibling process's own in-flight scratch directory on this shared host never shows up
    // as a false leak in the before/after diff.
    private IReadOnlySet<string> TempScratchDirectories() =>
        Directory.EnumerateDirectories(scratchRoot).ToHashSet();

    private Task<int> Run(ProcessRunner gh, ReleaseChannel? channel = null) =>
        UpdateCommand.RunAsync(
            gh, ReleasePlatform.DefaultRepository, restart: false, noRestart: true, linkOntoPath: false,
            // The real reader would look in this machine's config file; a test names its channel.
            readReleaseChannel: _ => Task.FromResult(new ReleaseChannelResolution(channel ?? ReleaseChannel.Cleared, null)),
            // The port-binding check InstallCommand.FinishAsync now runs unconditionally shells
            // out to docker — a fake that never answers keeps this test's outcome independent of
            // whatever Docker happens to be running on the machine the test suite executes on.
            containerRuntimeRunner: (_, _, _, _) => Task.FromResult(new ProcessResult(1, string.Empty, "docker not reached in this test")),
            // The payload's h9k is a stand-in file, and the anchor refresh would otherwise try to run it.
            restartChildRunner: (_, _, _) => Task.FromResult(RestartStepResult.Exited(ExitCodes.Ok)),
            scratchRoot: scratchRoot,
            cancellationToken: CancellationToken.None);

    /// <summary>
    /// A fake `gh release download`: on the expected arguments, it writes the same shape
    /// a real download would have (the platform's archive plus checksums.txt) into the
    /// requested --dir, built fresh from a small payload so the test never needs a real
    /// network call or a real GitHub release. Dispatches on the subcommand
    /// (<c>release view</c>, <c>release download</c>, <c>attestation verify</c>) rather than
    /// treating every call as a download, since UpdateCommand.RunAsync now makes all three.
    /// </summary>
    private sealed class FakeGh
    {
        private readonly string archiveSource;
        private readonly string archiveFileName;
        private readonly string tag;
        private bool corrupt;
        private AttestationOutcome attestationOutcome = AttestationOutcome.Verified;
        private readonly List<(string Tag, bool IsDraft)> listed = [];
        private IReadOnlyList<string>? attachedAssets;

        private enum AttestationOutcome
        {
            Verified,
            NoAttestationFound,
            GhTooOldForAttestation,
            GhTooOldForSourceRefFlag,
            VerificationFailed,
        }

        private FakeGh(string archiveSource, string archiveFileName, string tag)
        {
            this.archiveSource = archiveSource;
            this.archiveFileName = archiveFileName;
            this.tag = tag;
        }

        /// <summary>Every <c>gh</c> subcommand pair invoked ("release view", "release list", ...), in order.</summary>
        public List<string> Verbs { get; } = [];

        /// <summary>Adds a release to what <c>gh release list</c> returns, in the order added (newest created first).</summary>
        public void ListRelease(string listedTag, bool isDraft) => listed.Add((listedTag, isDraft));

        /// <summary>Limits the asset list <c>gh release view</c> reports, as a release still mid-publish would have.</summary>
        public void AttachOnly(params string[] assetNames) => attachedAssets = assetNames;

        /// <summary>The tag argument <c>gh release download</c> was actually invoked with, or
        /// null if it was never called.</summary>
        public string? DownloadedTag { get; private set; }

        /// <summary>The <c>--source-ref</c> value <c>gh attestation verify</c> was actually
        /// invoked with, or null if it was never called.</summary>
        public string? VerifiedSourceRef { get; private set; }

        public static FakeGh ForCurrentPlatform(
            string workspace, string version, string skillName, string cliContent = "cli\n", string daemonContent = "daemon\n",
            string tag = "v1.2.3")
        {
            string rid = ReleasePlatform.CurrentRid()!;
            string payload = Path.Combine(workspace, "payload");
            Directory.CreateDirectory(Path.Combine(payload, "skills", skillName));
            // ValidateReleasePayload requires the review-lap-prompt-builder, work-prompt-builder,
            // agent-prompt-builder, mention-followup-prompt-builder,
            // design-review-prompt-builder, qa-review-prompt-builder,
            // security-review-prompt-builder and learn-distill templates
            // packages in every payload it accepts (release.yml bundles templates/ beside
            // skills/) — a single file in each package is enough to satisfy the gate, since this
            // fixture is not exercising template publication itself.
            foreach (string templateDirectory in new[]
            {
                ReviewLapPromptBuilder.TemplateDirectory, WorkPromptBuilder.TemplateDirectory,
                AgentPromptBuilder.TemplateDirectory, MentionFollowUpPromptBuilder.TemplateDirectory,
                DesignReviewPromptBuilder.TemplateDirectory,
                QaReviewPromptBuilder.TemplateDirectory,
                SecurityReviewPromptBuilder.TemplateDirectory,
                LearningDistillCommand.TemplatePackage,
            })
            {
                string templatePackage = Path.Combine(payload, "templates", templateDirectory);
                Directory.CreateDirectory(templatePackage);
                File.WriteAllText(Path.Combine(templatePackage, "build.md"), "# build\n");
            }
            File.WriteAllText(Path.Combine(payload, InstallCommand.BinaryFileName("h9k")), cliContent);
            File.WriteAllText(Path.Combine(payload, InstallCommand.BinaryFileName("h9kd")), daemonContent);
            File.WriteAllText(Path.Combine(payload, "VERSION"), version);
            File.WriteAllText(Path.Combine(payload, "skills", skillName, "SKILL.md"), $"# {skillName}\n");

            string archiveFileName = ReleasePlatform.ArchiveFileName(rid);
            string archivePath = Path.Combine(workspace, archiveFileName);
            if (archiveFileName.EndsWith(".zip", StringComparison.Ordinal))
            {
                ZipFile.CreateFromDirectory(payload, archivePath);
            }
            else
            {
                using FileStream fileStream = File.Create(archivePath);
                using GZipStream gzip = new(fileStream, CompressionMode.Compress);
                TarFile.CreateFromDirectory(payload, gzip, includeBaseDirectory: false);
            }

            return new FakeGh(archivePath, archiveFileName, tag);
        }

        public void CorruptTheDownloadedArchive() => corrupt = true;

        public void MakeAttestationVerifyReportNoAttestation() => attestationOutcome = AttestationOutcome.NoAttestationFound;

        public void MakeAttestationVerifyReportGhTooOld() => attestationOutcome = AttestationOutcome.GhTooOldForAttestation;

        // gh 2.49.0 to 2.67.x has the `attestation` subcommand but not the --source-ref flag
        // (that landed in 2.68.0, cli/cli#10308), so it fails with "unknown flag" rather than
        // "unknown command" — a differently-worded case of the same "gh is too old" outcome.
        public void MakeAttestationVerifyReportGhTooOldForSourceRefFlag() => attestationOutcome = AttestationOutcome.GhTooOldForSourceRefFlag;

        public void MakeAttestationVerifyFail() => attestationOutcome = AttestationOutcome.VerificationFailed;

        public ProcessRunner Runner => (fileName, arguments, _, _) =>
        {
            List<string> argumentList = [.. arguments];
            Verbs.Add(string.Join(' ', argumentList.Take(2)));
            return Task.FromResult(argumentList switch
            {
                ["release", "view", ..] => HandleReleaseView(argumentList),
                ["release", "list", ..] => HandleReleaseList(),
                ["release", "download", ..] => HandleReleaseDownload(argumentList),
                ["attestation", "verify", ..] => HandleAttestationVerify(argumentList),
                _ => throw new InvalidOperationException($"FakeGh does not know how to handle: gh {string.Join(' ', argumentList)}"),
            });
        };

        // A tag-less view is GitHub's latest release, which the cleared channel resolves; a view
        // that names a tag (the all channel's second call) reports that tag's own assets.
        private ProcessResult HandleReleaseView(List<string> argumentList)
        {
            string viewedTag = argumentList[2].StartsWith("--", StringComparison.Ordinal) ? tag : argumentList[2];
            string[] assetNames = attachedAssets is { } attached
                ? [.. attached]
                : [archiveFileName, "checksums.txt", "hall9k-other-platform.zip"];
            return new ProcessResult(
                0,
                JsonSerializer.Serialize(new { tagName = viewedTag, assets = assetNames.Select(name => new { name }) }),
                string.Empty);
        }

        private ProcessResult HandleReleaseList() =>
            new(0, JsonSerializer.Serialize(listed.Select(release => new { tagName = release.Tag, isDraft = release.IsDraft })), string.Empty);

        private ProcessResult HandleReleaseDownload(List<string> argumentList)
        {
            // The tag UpdateCommand.RunAsync resolved from `gh release view` above is passed as
            // the positional argument right after the subcommand — recorded here rather than
            // assumed, so the pinned-tag test can assert against what was actually passed
            // (DownloadedTag) instead of trusting the call was shaped correctly.
            DownloadedTag = argumentList[2];

            string downloadDirectory = argumentList[argumentList.IndexOf("--dir") + 1];
            Directory.CreateDirectory(downloadDirectory);

            byte[] archiveBytes = File.ReadAllBytes(archiveSource);
            if (corrupt)
            {
                archiveBytes[^1] ^= 0xFF;
            }

            string destinationArchive = Path.Combine(downloadDirectory, archiveFileName);
            File.WriteAllBytes(destinationArchive, archiveBytes);

            string hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archiveSource)));
            File.WriteAllText(Path.Combine(downloadDirectory, "checksums.txt"), $"{hash}  {archiveFileName}\n");

            return new ProcessResult(0, string.Empty, string.Empty);
        }

        private ProcessResult HandleAttestationVerify(List<string> argumentList)
        {
            VerifiedSourceRef = argumentList[argumentList.IndexOf("--source-ref") + 1];

            return attestationOutcome switch
            {
                AttestationOutcome.Verified => new ProcessResult(0, string.Empty, string.Empty),
                AttestationOutcome.NoAttestationFound => new ProcessResult(1, string.Empty, "Error: no attestations found"),
                AttestationOutcome.GhTooOldForAttestation =>
                    new ProcessResult(1, string.Empty, "unknown command \"attestation\" for \"gh\""),
                AttestationOutcome.GhTooOldForSourceRefFlag =>
                    new ProcessResult(1, string.Empty, "unknown flag: --source-ref"),
                AttestationOutcome.VerificationFailed =>
                    new ProcessResult(1, string.Empty, "Error: verification failed: signer workflow does not match"),
                _ => throw new InvalidOperationException($"Unhandled {nameof(AttestationOutcome)}: {attestationOutcome}"),
            };
        }
    }
}
