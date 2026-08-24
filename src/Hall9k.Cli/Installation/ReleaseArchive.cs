using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Hall9k.Cli.Installation;

/// <summary>
/// What <c>h9k update</c> does with a downloaded release asset before it is safe to install
/// from: verify it against the release's own checksums, then unpack it. The bootstrap scripts
/// do the equivalent in shell/PowerShell, since they run before <c>h9k</c> exists on the
/// machine at all — this is the same two steps, for the machine that already has it.
/// </summary>
public static class ReleaseArchive
{
    /// <summary>
    /// Verifies <paramref name="archivePath"/> against the matching line (by file name) in a
    /// <c>sha256sum</c>-format checksums file. Returns the mismatch reason, or null when it
    /// checks out — refusing quietly is not an option here, since a corrupted or substituted
    /// artefact is exactly what the checksum step exists to catch before anything unpacks it.
    /// </summary>
    public static async Task<string?> VerifyAsync(string archivePath, string checksumsPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(checksumsPath))
        {
            return $"No checksums file at {checksumsPath} — the release download did not carry one, so the artefact cannot be verified.";
        }

        string fileName = Path.GetFileName(archivePath);
        string? expected = null;
        foreach (string line in await File.ReadAllLinesAsync(checksumsPath, cancellationToken))
        {
            // sha256sum's own format: "<hex>  <filename>" (two spaces for binary mode, one for text).
            string[] parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*').Equals(fileName, StringComparison.Ordinal))
            {
                expected = parts[0].Trim();
                break;
            }
        }

        if (expected is null)
        {
            return $"{fileName} has no entry in {checksumsPath} — the release did not publish a checksum for this asset.";
        }

        await using FileStream stream = File.OpenRead(archivePath);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        string actual = Convert.ToHexStringLower(hash);

        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"{fileName} checksum mismatch: release.yml recorded {expected}, downloaded file hashes to {actual}. "
              + "Refusing to install a release payload that does not match what was published.";
    }

    /// <summary>
    /// Unpacks a release archive (<c>.tar.gz</c> or <c>.zip</c>, per <see cref="ReleasePlatform.ArchiveExtension"/>)
    /// into <paramref name="destinationDirectory"/>, which is created if absent.
    /// </summary>
    public static async Task ExtractAsync(string archivePath, string destinationDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);

        // Canonicalized, not just Path.GetFullPath: System.Formats.Tar resolves symlinked
        // ancestors of the destination when it guards each entry against escaping the
        // extraction root, but computes each entry's own destination lexically — so a
        // destination reached through a symlinked ancestor (macOS's own temp directory is
        // one; /tmp -> /private/tmp, and Path.GetTempPath() often resolves to a
        // /var/folders/… path that is itself symlinked from /private/var/folders/…) makes
        // the two disagree and throws ArgumentOutOfRangeException deep inside TarEntry.
        // Resolving every ancestor ourselves first keeps both computations looking at the
        // same string.
        string canonicalDestination = Canonicalize(destinationDirectory);
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await ExtractZipAsync(archivePath, canonicalDestination, cancellationToken);
            return;
        }

        await using FileStream fileStream = File.OpenRead(archivePath);
        await using GZipStream gzip = new(fileStream, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gzip, canonicalDestination, overwriteFiles: true, cancellationToken);
    }

    /// <summary>
    /// <see cref="ZipFile.ExtractToDirectory(string, string, bool)"/> is synchronous and
    /// uninterruptible for the whole archive, which on the self-contained payload's ~100 MB
    /// Windows build turns a Ctrl-C during extraction into a silent no-op until the call
    /// finishes on its own. Walking entries one at a time reproduces the same
    /// escape-the-destination guard .NET's own extractor applies, while checking
    /// <paramref name="cancellationToken"/> between entries so it can actually stop.
    /// </summary>
    private static Task ExtractZipAsync(string archivePath, string destinationDirectory, CancellationToken cancellationToken)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        string destinationRoot = destinationDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? destinationDirectory
            : destinationDirectory + Path.DirectorySeparatorChar;
        // Windows (and macOS's default HFS+/APFS configuration) resolve paths
        // case-insensitively, so a crafted entry differing from destinationRoot only in
        // casing would still land inside it at runtime even though an Ordinal comparison
        // here sees it as an escape.
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string entryDestination = Path.GetFullPath(Path.Combine(destinationDirectory, entry.FullName));
            if (!entryDestination.StartsWith(destinationRoot, pathComparison))
            {
                throw new IOException(
                    $"{entry.FullName} in {archivePath} extracts to {entryDestination}, outside {destinationDirectory}.");
            }

            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(entryDestination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(entryDestination)!);
            entry.ExtractToFile(entryDestination, overwrite: true);
        }

        return Task.CompletedTask;
    }

    /// <summary>Resolves every symlinked ancestor of <paramref name="path"/> to its real
    /// target, the way a shell's own <c>realpath</c> would — <see cref="Path.GetFullPath"/>
    /// is purely lexical on its own and never touches the filesystem.</summary>
    private static string Canonicalize(string path)
    {
        string full = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
        {
            return full;
        }

        string current = root;
        foreach (string segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(current, segment);
            FileSystemInfo? entry = Directory.Exists(candidate)
                ? new DirectoryInfo(candidate)
                : File.Exists(candidate) ? new FileInfo(candidate) : null;
            current = entry?.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate;
        }

        return current;
    }
}
