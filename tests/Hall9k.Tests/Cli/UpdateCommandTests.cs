using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Installation;
using Hall9k.Connectors.Processes;
using Hall9k.Domain.Infrastructure.Storage;
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
// the write off a developer's or CI runner's real home, and sharing the collection
// serializes this against every other HALL9K_HOME redirect. The PATH-linking step is
// skipped here (RunAsync's linkOntoPath: false) rather than redirected, because it
// mutates the REAL process PATH and home directory — redirecting those two env vars
// process-wide would race any concurrently running test that shells out to git/gh/docker
// via PATH (origin incident: an early version of this test wiped PATH out from under
// GitWorktreeManagerTests running in a different collection at the same time, and,
// before that fix, briefly overwrote this machine's own real /opt/homebrew/bin/h9k with
// a symlink into a temp directory that was deleted moments later). LinkOntoPath and
// ComputeUserPath already have direct unit coverage with fake paths in
// InstallCommandTests, so skipping the step here loses no coverage.
[Collection("Hall9kHome")]
public sealed class UpdateCommandTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), $"h9k-update-{Path.GetRandomFileName()}");
    private readonly string? previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");
    private readonly string workspace = Path.Combine(Path.GetTempPath(), $"h9k-update-workspace-{Path.GetRandomFileName()}");

    public UpdateCommandTests()
    {
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(workspace);
        Environment.SetEnvironmentVariable("HALL9K_HOME", home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", previousHome);
        // Best-effort, matching InstallCommand.TryDelete's own reason: a recently-written
        // executable can still be held by Defender or an indexer, and an unguarded delete
        // here would replace the test's real outcome with an unrelated IOException.
        InstallCommand.TryDelete(home);
        InstallCommand.TryDelete(workspace);
    }

    [Fact]
    public async Task A_verified_release_is_installed_and_its_skills_published()
    {
        if (ReleasePlatform.CurrentRid() is null)
        {
            // No release build target for this platform (release.yml covers osx-arm64,
            // win-x64, linux-x64) — nothing to stage a fake download for.
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

    // Pre-existing debris from other processes (or other test runs, before this fix) can
    // already sit in the shared temp directory, so the assertion is a before/after diff
    // rather than an assumption that temp starts clean.
    private static IReadOnlySet<string> TempScratchDirectories() =>
        Directory.EnumerateDirectories(Path.GetTempPath(), "h9k-update-*")
            .Where(entry => !Path.GetFileName(entry).StartsWith("h9k-update-workspace-", StringComparison.Ordinal))
            .ToHashSet();

    private static Task<int> Run(ProcessRunner gh) =>
        UpdateCommand.RunAsync(
            gh, ReleasePlatform.DefaultRepository, restart: false, noRestart: true, CancellationToken.None, linkOntoPath: false);

    /// <summary>
    /// A fake `gh release download`: on the expected arguments, it writes the same shape
    /// a real download would have (the platform's archive plus checksums.txt) into the
    /// requested --dir, built fresh from a small payload so the test never needs a real
    /// network call or a real GitHub release.
    /// </summary>
    private sealed class FakeGh
    {
        private readonly string archiveSource;
        private readonly string archiveFileName;
        private bool corrupt;

        private FakeGh(string archiveSource, string archiveFileName)
        {
            this.archiveSource = archiveSource;
            this.archiveFileName = archiveFileName;
        }

        public static FakeGh ForCurrentPlatform(string workspace, string version, string skillName)
        {
            string rid = ReleasePlatform.CurrentRid()!;
            string payload = Path.Combine(workspace, "payload");
            Directory.CreateDirectory(Path.Combine(payload, "skills", skillName));
            File.WriteAllText(Path.Combine(payload, InstallCommand.BinaryFileName("h9k")), "cli\n");
            File.WriteAllText(Path.Combine(payload, InstallCommand.BinaryFileName("h9kd")), "daemon\n");
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

            return new FakeGh(archivePath, archiveFileName);
        }

        public void CorruptTheDownloadedArchive() => corrupt = true;

        public ProcessRunner Runner => (fileName, arguments, _, _) =>
        {
            List<string> argumentList = [.. arguments];
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

            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        };
    }
}
