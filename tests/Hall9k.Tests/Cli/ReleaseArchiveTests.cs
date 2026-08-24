using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using FluentAssertions;
using Hall9k.Cli.Installation;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// What h9k update does with a downloaded release asset before trusting it: checksum
/// verification against the release's own checksums.txt, then unpacking. Both archive
/// kinds (.tar.gz for macOS/Linux, .zip for Windows) are exercised on whatever OS runs
/// the test — the extraction is pure managed code (System.Formats.Tar, ZipFile), not a
/// shell-out, so there is nothing platform-specific to skip.
/// </summary>
public sealed class ReleaseArchiveTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"h9k-archive-{Path.GetRandomFileName()}");

    public ReleaseArchiveTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public async Task A_matching_checksum_verifies()
    {
        string archive = WriteZip("hall9k-win-x64.zip", "payload");
        string checksums = WriteChecksums(("hall9k-win-x64.zip", Sha256Of(archive)));

        string? problem = await ReleaseArchive.VerifyAsync(archive, checksums, CancellationToken.None);

        problem.Should().BeNull();
    }

    [Fact]
    public async Task A_tampered_archive_is_refused_by_name()
    {
        string archive = WriteZip("hall9k-osx-arm64.tar.gz", "payload");
        string checksums = WriteChecksums(("hall9k-osx-arm64.tar.gz", new string('0', 64)));

        string? problem = await ReleaseArchive.VerifyAsync(archive, checksums, CancellationToken.None);

        problem.Should().Contain("checksum mismatch").And.Contain("hall9k-osx-arm64.tar.gz");
    }

    [Fact]
    public async Task An_asset_missing_from_checksums_is_refused_by_name()
    {
        string archive = WriteZip("hall9k-linux-x64.tar.gz", "payload");
        string checksums = WriteChecksums(("hall9k-win-x64.zip", "irrelevant"));

        string? problem = await ReleaseArchive.VerifyAsync(archive, checksums, CancellationToken.None);

        problem.Should().Contain("no entry").And.Contain("hall9k-linux-x64.tar.gz");
    }

    [Fact]
    public async Task A_missing_checksums_file_is_named_rather_than_thrown()
    {
        string archive = WriteZip("hall9k-win-x64.zip", "payload");

        string? problem = await ReleaseArchive.VerifyAsync(
            archive, Path.Combine(directory, "checksums.txt"), CancellationToken.None);

        problem.Should().Contain("No checksums file");
    }

    [Fact]
    public async Task A_zip_archive_extracts_its_files()
    {
        string archive = Path.Combine(directory, "hall9k-win-x64.zip");
        using (ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(WriteFile("h9k.exe", "cli"), "h9k.exe");
            ZipArchiveEntry skill = zip.CreateEntry("skills/pr-summary/SKILL.md");
            await using (Stream entryStream = skill.Open())
            await using (StreamWriter writer = new(entryStream))
            {
                await writer.WriteAsync("# pr-summary");
            }
        }

        string destination = Path.Combine(directory, "extracted");
        await ReleaseArchive.ExtractAsync(archive, destination, CancellationToken.None);

        File.ReadAllText(Path.Combine(destination, "h9k.exe")).Should().Be("cli");
        File.ReadAllText(Path.Combine(destination, "skills", "pr-summary", "SKILL.md")).Should().Be("# pr-summary");
    }

    [Fact]
    public async Task A_tar_gz_archive_extracts_its_files()
    {
        string archive = Path.Combine(directory, "hall9k-osx-arm64.tar.gz");
        string staged = Path.Combine(directory, "staged");
        Directory.CreateDirectory(Path.Combine(staged, "skills", "pr-summary"));
        File.WriteAllText(Path.Combine(staged, "h9k"), "cli");
        File.WriteAllText(Path.Combine(staged, "skills", "pr-summary", "SKILL.md"), "# pr-summary");

        await using (FileStream fileStream = File.Create(archive))
        await using (GZipStream gzip = new(fileStream, CompressionMode.Compress))
        {
            await TarFile.CreateFromDirectoryAsync(staged, gzip, includeBaseDirectory: false, CancellationToken.None);
        }

        string destination = Path.Combine(directory, "extracted");
        await ReleaseArchive.ExtractAsync(archive, destination, CancellationToken.None);

        File.ReadAllText(Path.Combine(destination, "h9k")).Should().Be("cli");
        File.ReadAllText(Path.Combine(destination, "skills", "pr-summary", "SKILL.md")).Should().Be("# pr-summary");
    }

    private string WriteZip(string fileName, string content)
    {
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private string WriteFile(string fileName, string content)
    {
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private string WriteChecksums(params (string FileName, string Hash)[] entries)
    {
        string path = Path.Combine(directory, "checksums.txt");
        File.WriteAllLines(path, entries.Select(entry => $"{entry.Hash}  {entry.FileName}"));
        return path;
    }

    private static string Sha256Of(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
