namespace Hall9k.Tests.Integration;

/// <summary>
/// Shared by every source-scanning guard test (<see cref="ContainerRoutingGuardTests"/>,
/// <see cref="Hall9k.Tests.Domain.HomeEnvironmentIsolationTests"/>): both walk the whole test
/// tree from their own file's location and both need to tell a real source file from build
/// output.
/// </summary>
internal static class TestSourceTree
{
    /// <summary>
    /// The <c>tests/Hall9k.Tests</c> root, resolved from the caller's own file path rather than
    /// a hardcoded relative segment count, so it stays correct regardless of which guard's file
    /// calls it or how deep that file sits under <c>tests/Hall9k.Tests</c>.
    /// </summary>
    public static string RootDirectory([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        // .../tests/Hall9k.Tests/<Feature>/<File>.cs -> .../tests/Hall9k.Tests
        Path.GetDirectoryName(Path.GetDirectoryName(here))!;

    /// <summary>
    /// True for a file under a <c>bin</c> or <c>obj</c> build-output directory, which
    /// <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/>'s recursive search
    /// otherwise walks right along with the real sources — generated files there
    /// (<c>*.GlobalUsings.g.cs</c>, <c>*.AssemblyInfo.cs</c>, and the like) would make a scan's
    /// file and hit counts depend on which configurations happen to be built locally.
    /// </summary>
    public static bool IsBuildOutput(string testsDirectory, string file)
    {
        string relative = Path.GetRelativePath(testsDirectory, file);
        string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return segments.Any(segment =>
            string.Equals(segment, "bin", StringComparison.Ordinal) ||
            string.Equals(segment, "obj", StringComparison.Ordinal));
    }
}
