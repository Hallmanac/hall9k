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
        string tempPath = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(tempPath, contents, cancellationToken);

            if (!File.Exists(path))
            {
                File.Move(tempPath, path);
                return;
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tempPath, File.GetUnixFileMode(path));
            }

            File.Replace(tempPath, path, destinationBackupFileName: null);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
