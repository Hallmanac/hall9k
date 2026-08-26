namespace Hall9k.Domain.Infrastructure.Storage;

/// <summary>
/// Writes a file's full contents by staging into a sibling temp file and renaming it into place,
/// so a cancelled or crashed write can never leave the target truncated or empty — unlike
/// <see cref="File.WriteAllTextAsync(string, string?, CancellationToken)"/>, which opens the
/// target with truncate-on-create and honours cancellation mid-write. Used for durable config
/// files (<c>config.json</c>) that other code reads on every startup and cannot tolerate landing
/// half-written.
/// <para>
/// The rename replaces the target's inode, so a plain overwrite would hand the file the temp
/// file's freshly-created permissions rather than whatever the target already had — for
/// <c>config.json</c>, which carries the Postgres connection string, that would silently undo an
/// operator's <c>chmod 600</c> on every write. When the target already exists, its Unix file mode
/// is copied onto the temp file before the swap, and the swap itself uses
/// <see cref="File.Replace(string, string, string?)"/> rather than <see cref="File.Move"/>: on
/// Windows this is the Win32 <c>ReplaceFile</c> function, which preserves the replaced file's ACL
/// by design (unlike <c>MoveFileEx</c>, which <see cref="File.Move"/> uses, and which takes the
/// source file's ACL instead). A target that does not exist yet has no prior permissions to
/// preserve, so it is just moved into place.
/// </para>
/// <para>
/// <paramref name="path"/> is resolved through a symbolic link before any of that happens: the
/// rename swaps out whatever inode currently sits at the resolved path, so swapping at the
/// symlink's own path instead would unlink the symlink itself and leave a plain file in its
/// place. An operator who keeps <c>config.json</c> as a symlink into a separately
/// version-controlled dotfiles repo needs the link to survive a write the same way the
/// permissions above do. Origin: the cycle-4 pre-PR review found the first write after such a
/// symlink replaced it outright, orphaning the file it pointed at.
/// </para>
/// </summary>
public static class AtomicFileWrite
{
    public static async Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken)
    {
        string resolvedPath = ResolveSymbolicLinkTarget(path);
        string tempPath = $"{resolvedPath}.tmp-{Path.GetRandomFileName()}";
        try
        {
            bool targetExists = File.Exists(resolvedPath);

            // Narrowed to a private, owner-writable mode before any content is written, not the
            // target's own mode: applying nothing here (as a follow-up File.SetUnixFileMode once
            // the write finished) would leave the file world-readable at the process umask's
            // default mode for the whole write, and for config.json — which carries the Postgres
            // connection string under an operator's chmod 600 — that is a real exposure window
            // rather than a cosmetic one. Applying the target's own mode instead can be stricter
            // than this: a target chmod'd to 400 or 444 (no owner-write bit at all) would make the
            // content write below fail outright with UnauthorizedAccessException, a write that
            // succeeded before this file carried Unix modes at all. UserRead|UserWrite is always
            // at least as private as the process umask's default and never blocks the write that
            // follows; the target's exact mode — whatever it narrows down to, including no
            // owner-write bit — is applied once there is content to protect and before the swap
            // makes it visible under the real name.
            if (targetExists && !OperatingSystem.IsWindows())
            {
                using (File.Create(tempPath))
                {
                }

                File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            await File.WriteAllTextAsync(tempPath, contents, cancellationToken);

            if (targetExists && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tempPath, File.GetUnixFileMode(resolvedPath));
            }

            if (!targetExists)
            {
                try
                {
                    File.Move(tempPath, resolvedPath);
                }
                catch (IOException) when (File.Exists(resolvedPath))
                {
                    // The existence check above and the move below are not one atomic step: a
                    // concurrent writer that claims the target in between (for example, two
                    // `h9k daemon stop` invocations racing to write the same file) leaves a
                    // target here that File.Move refuses to overwrite. That target now exists,
                    // which is exactly the targetExists case, so finishing the write is a
                    // Replace instead.
                    File.Replace(tempPath, resolvedPath, destinationBackupFileName: null);
                }

                return;
            }

            try
            {
                File.Replace(tempPath, resolvedPath, destinationBackupFileName: null);
            }
            catch (FileNotFoundException)
            {
                // The existence check above and the replace below are not one atomic step: a
                // concurrent reader that claims the target by renaming it away in between (for
                // example, WindowsStopRequestWatcher's claim-by-rename racing a second
                // h9k daemon stop write) leaves nothing for File.Replace to swap onto, and it
                // throws rather than treating "gone" as "never existed". A target that vanished
                // is exactly the !targetExists case, so finishing the write is a plain move.
                File.Move(tempPath, resolvedPath);
            }
        }
        finally
        {
            // Best-effort: the temp file is already gone on the success path (Move/Replace
            // consumed it), so this only ever has real work to do when the try block threw, and a
            // delete failure here (permissions, a concurrent scanner) must not shadow that
            // original exception with one about a harmless orphaned temp file.
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// The real file a symlink at <paramref name="path"/> ultimately points to, or
    /// <paramref name="path"/> itself when it is not a symlink — including when nothing exists
    /// there yet (the ordinary first-write case), which <see cref="FileSystemInfo.ResolveLinkTarget"/>
    /// itself refuses with <see cref="FileNotFoundException"/> rather than answering "not a link".
    /// <see cref="FileSystemInfo.ResolveLinkTarget"/> walks the whole chain
    /// (<c>returnFinalTarget: true</c>), so a symlink pointing at another symlink still resolves
    /// to the one real file the swap must land on.
    /// </summary>
    private static string ResolveSymbolicLinkTarget(string path) =>
        File.Exists(path) || Directory.Exists(path)
            ? new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path
            : path;
}
