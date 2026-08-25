using FluentAssertions;
using Hall9k.Domain.Infrastructure.Storage;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <see cref="AtomicFileWrite"/> stages into a sibling temp file and swaps it into place, which
/// replaces the target's inode — a plain overwrite would hand the file the temp file's
/// freshly-created permissions instead of whatever the target already had. That matters for
/// <c>config.json</c>: it carries the Postgres connection string, and an operator who
/// <c>chmod 600</c>s it needs that to survive the next <c>h9k config set</c> (cycle-3 pre-PR
/// review finding).
/// </summary>
public sealed class AtomicFileWriteTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"h9k-atomic-{Path.GetRandomFileName()}.json");

    public void Dispose()
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Overwriting_an_existing_file_preserves_its_unix_permissions()
    {
        await File.WriteAllTextAsync(path, "original");

        // On Windows there is no Unix mode to set or preserve; the assertion below is skipped
        // there, the same convention CredentialVaultTests uses, so the write itself is still
        // exercised on both legs of CI.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            await AtomicFileWrite.WriteAllTextAsync(path, "updated", CancellationToken.None);

            File.GetUnixFileMode(path).Should().Be(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                "the rename replaces the inode, so the swap must copy the target's existing mode onto the " +
                "replacement rather than leaving it at whatever a freshly-created temp file gets");
            (await File.ReadAllTextAsync(path)).Should().Be("updated");
        }
    }

    /// <summary>
    /// A target locked down to <c>chmod 400</c> (the operator hardening scenario this type exists
    /// to preserve, and the shape <see cref="PlatformConfigFileTests"/> itself stages) has no
    /// owner-write bit at all. Origin: the cycle-2 pre-PR review found the target's mode applied
    /// to the temp file *before* the content write, so the write itself failed with
    /// <see cref="UnauthorizedAccessException"/> on its own staged file — a write that succeeded
    /// before that mode-ordering change.
    /// </summary>
    [Fact]
    public async Task Overwriting_a_read_only_file_still_succeeds()
    {
        await File.WriteAllTextAsync(path, "original");

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead);

        await AtomicFileWrite.WriteAllTextAsync(path, "updated", CancellationToken.None);

        File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead, "the target's own restrictive mode must survive the write");
        (await File.ReadAllTextAsync(path)).Should().Be("updated");
    }

    [Fact]
    public async Task Writing_a_file_that_does_not_exist_yet_just_creates_it()
    {
        await AtomicFileWrite.WriteAllTextAsync(path, "new", CancellationToken.None);

        (await File.ReadAllTextAsync(path)).Should().Be("new");
    }

    [Fact]
    public async Task Writing_leaves_no_temp_file_behind()
    {
        await File.WriteAllTextAsync(path, "original");

        await AtomicFileWrite.WriteAllTextAsync(path, "updated", CancellationToken.None);

        Directory.GetFiles(Path.GetTempPath(), $"{Path.GetFileName(path)}.tmp-*").Should().BeEmpty();
    }
}
