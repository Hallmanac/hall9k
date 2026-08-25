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
/// </summary>
public static class AtomicFileWrite
{
    public static async Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken)
    {
        string tempPath = $"{path}.tmp-{Path.GetRandomFileName()}";
        try
        {
            bool targetExists = File.Exists(path);

            // The target's mode is applied to the temp file before any content is written, not
            // after: applying it afterwards (as a follow-up File.SetUnixFileMode once the write
            // finished) would leave the file world-readable at the process umask's default mode
            // for the whole write, and for config.json — which carries the Postgres connection
            // string under an operator's chmod 600 — that is a real exposure window rather than
            // a cosmetic one. The file is empty when the mode is narrowed, so there is nothing to
            // leak during the gap.
            if (targetExists && !OperatingSystem.IsWindows())
            {
                using (File.Create(tempPath))
                {
                }

                File.SetUnixFileMode(tempPath, File.GetUnixFileMode(path));
            }

            await File.WriteAllTextAsync(tempPath, contents, cancellationToken);

            if (!targetExists)
            {
                File.Move(tempPath, path);
                return;
            }

            File.Replace(tempPath, path, destinationBackupFileName: null);
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
}
