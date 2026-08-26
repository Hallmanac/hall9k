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

        // On Windows there is no Unix mode to set or preserve, so only the permissions assertion
        // is skipped there — the write itself runs unconditionally on both legs of CI, the same
        // convention CredentialVaultTests uses. Origin: the cycle-4 pre-PR review found the write
        // call itself inside the Windows guard, so the Windows leg exercised nothing at all while
        // this comment claimed otherwise.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        await AtomicFileWrite.WriteAllTextAsync(path, "updated", CancellationToken.None);

        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(path).Should().Be(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                "the rename replaces the inode, so the swap must copy the target's existing mode onto the " +
                "replacement rather than leaving it at whatever a freshly-created temp file gets");
        }

        (await File.ReadAllTextAsync(path)).Should().Be("updated");
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

    /// <summary>
    /// There is no target mode to copy on a first write, so the temp file must already be
    /// narrowed before the content write rather than left at the process umask's default —
    /// otherwise <c>config.json</c>'s first write (no prior <c>chmod 600</c> to preserve) would be
    /// world-readable until an operator noticed and locked it down by hand. Origin: the cycle-4
    /// pre-PR review found the narrowing gated on the target already existing, so this path
    /// exercised nothing at all.
    /// </summary>
    [Fact]
    public async Task Writing_a_file_that_does_not_exist_yet_is_created_with_a_private_mode()
    {
        await AtomicFileWrite.WriteAllTextAsync(path, "new", CancellationToken.None);

        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(path).Should().Be(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                "a first write has no prior mode to preserve, so it must land at a private mode rather than " +
                "the process umask's default");
        }
    }

    /// <summary>
    /// An operator who keeps <c>config.json</c> as a symlink into a separately version-controlled
    /// dotfiles repo needs the link itself to survive a write: <see cref="File.Replace(string, string, string?)"/>
    /// unlinks whatever inode sits at its destination path, so swapping in at the symlink's own
    /// path would replace the link with an ordinary file and orphan whatever it pointed at.
    /// Origin: the cycle-4 pre-PR review found exactly that regression — the write must land on
    /// the real file the link resolves to instead.
    /// </summary>
    [Fact]
    public async Task Writing_through_a_symlink_updates_the_real_file_and_leaves_the_link_intact()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string realTarget = Path.Combine(Path.GetTempPath(), $"h9k-atomic-target-{Path.GetRandomFileName()}.json");
        await File.WriteAllTextAsync(realTarget, "original");
        File.CreateSymbolicLink(path, realTarget);

        try
        {
            await AtomicFileWrite.WriteAllTextAsync(path, "updated", CancellationToken.None);

            new FileInfo(path).LinkTarget.Should().Be(
                realTarget, "the swap must land on the real file rather than replacing the symlink with one");
            (await File.ReadAllTextAsync(realTarget)).Should().Be(
                "updated", "the write must reach the file the symlink points at");
        }
        finally
        {
            File.Delete(path);
            File.Delete(realTarget);
        }
    }

    [Fact]
    public async Task Writing_leaves_no_temp_file_behind()
    {
        await File.WriteAllTextAsync(path, "original");

        await AtomicFileWrite.WriteAllTextAsync(path, "updated", CancellationToken.None);

        Directory.GetFiles(Path.GetTempPath(), $"{Path.GetFileName(path)}.tmp-*").Should().BeEmpty();
    }
}
