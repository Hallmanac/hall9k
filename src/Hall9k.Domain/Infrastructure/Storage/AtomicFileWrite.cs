namespace Hall9k.Domain.Infrastructure.Storage;

/// <summary>
/// Writes a file's full contents by staging into a sibling temp file and renaming it into place,
/// so a cancelled or crashed write can never leave the target truncated or empty — unlike
/// <see cref="File.WriteAllTextAsync(string, string?, CancellationToken)"/>, which opens the
/// target with truncate-on-create and honours cancellation mid-write. Used for durable config
/// files (<c>config.json</c>) that other code reads on every startup and cannot tolerate landing
/// half-written.
/// </summary>
public static class AtomicFileWrite
{
    public static async Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken)
    {
        string tempPath = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(tempPath, contents, cancellationToken);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
