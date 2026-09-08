namespace Hall9k.Tests.TestSupport;

/// <summary>
/// Deletes a throwaway directory tree that may contain a real git repository. Git writes its
/// loose objects and packfiles read-only on purpose (they are content-addressed and must never
/// be rewritten in place), and on Windows <see cref="Directory.Delete(string, bool)"/> refuses a
/// read-only file outright with <see cref="UnauthorizedAccessException"/> rather than clearing
/// the attribute the way Unix's permission model makes unnecessary. So the attribute is cleared
/// across the tree first, then the delete runs.
/// <para>
/// Origin: the Windows full-suite baseline of 2026-09-08 — ten
/// <c>Hall9k.Tests.Integration.PullRequestOpenerTests</c> failures, every assertion in them
/// passing, all of them thrown out of <c>Dispose</c> on <c>.git/objects</c>. The pre-existing
/// answer elsewhere in this project was to widen a <c>catch</c> to swallow the exception
/// (<c>ReviewEngineTests.Dispose</c>), which keeps the suite green while leaving a temp
/// repository on disk after every run; this one actually deletes.
/// </para>
/// </summary>
internal static class TemporaryTree
{
    /// <summary>
    /// Clears read-only across the tree and deletes it, letting a genuine failure throw — for a
    /// test whose own arrangement depends on the directory being gone (a push guard's
    /// <c>ls-remote</c> against a deliberately removed origin, say).
    /// </summary>
    public static void Delete(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        ClearReadOnly(path);
        Directory.Delete(path, recursive: true);
    }

    /// <summary>
    /// The <c>Dispose</c> form: best-effort, swallowing the file-system exceptions a temp
    /// directory can still legitimately raise once the attributes are out of the way — a handle
    /// another process (an agent stand-in, a Testcontainers volume) has not closed yet.
    /// </summary>
    public static void TryDelete(string path)
    {
        try
        {
            Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void ClearReadOnly(string directory)
    {
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort per file: the delete below is what reports the real problem, and it
                // names the offending path where a failure here would only name this sweep.
            }
        }
    }
}
