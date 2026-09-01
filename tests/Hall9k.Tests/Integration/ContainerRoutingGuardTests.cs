using FluentAssertions;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <see cref="PostgresFixture"/> is the only place in this test project allowed to construct a
/// Testcontainers Postgres instance, because it is the only place that gates container lifetime
/// through <see cref="PostgresFixture"/>'s own concurrency semaphore (Decisions Log #108). A test
/// class that instead builds a <c>PostgreSqlBuilder</c>/<c>PostgreSqlContainer</c> of its own —
/// directly, or through any helper other than <see cref="PostgresFixture"/> — starts a container
/// the bound never sees, silently reopening the unbounded-concurrency problem the semaphore
/// exists to close. This is a source scan rather than a runtime check because the failure mode is
/// "a container exists outside the gate", which by construction never runs through anything this
/// process could intercept at test time.
/// </summary>
public sealed class ContainerRoutingGuardTests
{
    private const string AllowedFileName = "PostgresFixture.cs";

    // This file's own source is exempt too: it names the markers below as data, not code, so a
    // plain substring scan of the tree would otherwise flag itself as the one offender.
    private const string GuardFileName = "ContainerRoutingGuardTests.cs";

    private static readonly string[] ContainerConstructionMarkers =
    [
        "PostgreSqlBuilder",
        "PostgreSqlContainer",
    ];

    [Fact]
    public void Every_postgres_container_in_the_test_tree_is_built_by_the_bounded_fixture()
    {
        string testsDirectory = TestsDirectory();

        string[] files =
        [
            .. Directory.EnumerateFiles(testsDirectory, "*.cs", SearchOption.AllDirectories)
               .Where(file => !string.Equals(Path.GetFileName(file), AllowedFileName, StringComparison.Ordinal))
               .Where(file => !string.Equals(Path.GetFileName(file), GuardFileName, StringComparison.Ordinal))
               .Where(file => !IsBuildOutput(testsDirectory, file)),
        ];

        List<string> offenders = [];

        foreach (string file in files)
        {
            string source = File.ReadAllText(file);

            if (ContainerConstructionMarkers.Any(marker => source.Contains(marker, StringComparison.Ordinal)))
            {
                offenders.Add(Path.GetRelativePath(testsDirectory, file));
            }
        }

        offenders.Should().BeEmpty(
            $"only {AllowedFileName} may construct a Testcontainers Postgres instance — every " +
            "other container-backed test class must depend on PostgresFixture via " +
            "IClassFixture<PostgresFixture> so its container lifetime is bounded by that " +
            "fixture's concurrency gate; add IClassFixture<PostgresFixture> instead of building a " +
            "container directly");

        files.Length.Should().BeGreaterThan(
            100,
            "this is far fewer .cs files than the test tree actually holds — TestsDirectory() is " +
            "probably no longer resolving to tests/Hall9k.Tests");
    }

    private static bool IsBuildOutput(string testsDirectory, string file)
    {
        string relative = Path.GetRelativePath(testsDirectory, file);
        string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return segments.Any(segment =>
            string.Equals(segment, "bin", StringComparison.Ordinal) ||
            string.Equals(segment, "obj", StringComparison.Ordinal));
    }

    private static string TestsDirectory([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        // .../tests/Hall9k.Tests/Integration/ContainerRoutingGuardTests.cs -> .../tests/Hall9k.Tests
        Path.GetDirectoryName(Path.GetDirectoryName(here))!;
}
