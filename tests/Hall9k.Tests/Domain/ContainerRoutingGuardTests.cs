using FluentAssertions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <see cref="Hall9k.Tests.Integration.PostgresFixture"/> is the only place in the test tree
/// allowed to construct a Testcontainers Postgres instance, because it is the only place that
/// gates container lifetime through <see cref="Hall9k.Tests.Integration.CrossProcessContainerGate"/>
/// (Decisions Log #130, following up #108). A test class that instead builds a
/// <c>PostgreSqlBuilder</c>/<c>PostgreSqlContainer</c> of its own — directly, or through any
/// helper other than <see cref="Hall9k.Tests.Integration.PostgresFixture"/> — starts a container
/// the bound never sees, silently reopening the unbounded-concurrency problem the gate
/// exists to close. This is a source scan rather than a runtime check because the failure mode is
/// "a container exists outside the gate", which by construction never runs through anything this
/// process could intercept at test time. This test itself needs no container, so — like its
/// sibling scan <see cref="HomeEnvironmentIsolationTests"/> — it lives in the DB-free unit tier
/// rather than the integration one, even though the fixture it guards lives there.
/// <para>
/// The scan matches against comment/string-stripped source (<see cref="TestSourceTree.StripCommentsAndStrings"/>),
/// not the raw text, so a file that merely names <c>PostgreSqlBuilder</c>/<c>PostgreSqlContainer</c>
/// in a doc comment or a quoted string — this file's own marker list among them — is never mistaken
/// for a real construction call; that is also what lets this file scan itself rather than needing
/// a name-based exemption the way an earlier draft did.
/// </para>
/// <para>
/// The marker list only names the two Postgres-specific Testcontainers types, so it does not catch
/// a container built through the generic <c>ContainerBuilder</c>/<c>IContainer</c> API instead —
/// the same class of blind spot <see cref="HomeEnvironmentIsolationTests"/>
/// documents for its own scan. This guard cannot follow a construction there.
/// </para>
/// <para>
/// The scan covers the whole <c>tests/</c> directory rather than this project alone, the same
/// widening <see cref="ProcessTerminationGuardTests"/> and <see cref="NodeBootstrapConventionGuardTests"/>
/// already settled on (Decisions Log #110): a fixture in a second test project — this repository
/// now has one, <c>Hall9k.Tests.LockHolder</c> — could construct a Postgres container exactly as
/// this project's classes can, and a scan bounded to <c>tests/Hall9k.Tests</c> would claim a
/// coverage its own test name does not qualify.
/// </para>
/// </summary>
public sealed class ContainerRoutingGuardTests
{
    private static readonly string[] ContainerConstructionMarkers =
    [
        "PostgreSqlBuilder",
        "PostgreSqlContainer",
    ];

    [Fact]
    public void Every_postgres_container_in_the_test_tree_is_built_by_the_bounded_fixture()
    {
        // The whole tests/ directory, not just tests/Hall9k.Tests: repositoryRoot is
        // SourceDirectory()'s own parent ("<repositoryRoot>/src"), the same resolution the two
        // sibling guards named above use to reach the same tree.
        string repositoryRoot = Path.GetDirectoryName(TestSourceTree.SourceDirectory())
            ?? throw new InvalidOperationException("the resolved src directory has no parent directory");
        string testsDirectory = Path.Combine(repositoryRoot, "tests");

        // Relative to the tests root rather than a bare filename, so a differently-located file
        // that merely happens to share PostgresFixture.cs's name (e.g. a future
        // Cli/PostgresFixture.cs testing something unrelated) is not silently exempted along with
        // the real one.
        string allowedRelativePath = Path.Combine("Hall9k.Tests", "Integration", "PostgresFixture.cs");

        string[] files =
        [
            .. Directory.EnumerateFiles(testsDirectory, "*.cs", SearchOption.AllDirectories)
               .Where(file => !string.Equals(
                   Path.GetRelativePath(testsDirectory, file), allowedRelativePath, StringComparison.Ordinal))
               .Where(file => !TestSourceTree.IsBuildOutput(testsDirectory, file)),
        ];

        List<string> offenders = [];

        foreach (string file in files)
        {
            (string code, _, bool balanced) = TestSourceTree.StripCommentsAndStrings(File.ReadAllText(file));
            string relativePath = Path.GetRelativePath(testsDirectory, file);

            if (!balanced)
            {
                // Mirrors HomeEnvironmentIsolationTests' own handling of the same signal: a file
                // StripCommentsAndStrings desyncs on (an unmatched brace inside a multi-line
                // @$"..." literal, chiefly) cannot be trusted to have surfaced every real
                // PostgreSqlBuilder/PostgreSqlContainer construction it contains, so it is reported
                // as an offender itself rather than silently dropped from coverage.
                offenders.Add(
                    $"{relativePath} <StripCommentsAndStrings desynced on this file: stripped brace " +
                    "depth never returned to zero, so its container-construction coverage cannot be trusted>");
                continue;
            }

            if (ContainerConstructionMarkers.Any(marker => code.Contains(marker, StringComparison.Ordinal)))
            {
                offenders.Add(relativePath);
            }
        }

        offenders.Should().BeEmpty(
            $"only {allowedRelativePath} may construct a Testcontainers Postgres instance — every " +
            "other container-backed test class must depend on PostgresFixture via " +
            "IClassFixture<PostgresFixture> so its container lifetime is bounded by that " +
            "fixture's concurrency gate; add IClassFixture<PostgresFixture> instead of building a " +
            "container directly");

        files.Length.Should().BeGreaterThan(
            100,
            "this is far fewer .cs files than the test tree actually holds — " +
            "Path.Combine(repositoryRoot, \"tests\"), resolved from TestSourceTree.SourceDirectory(), " +
            "is probably no longer resolving to the repository's tests/ directory");

        // A positive control on the scan itself, the counterpart to the hit-count floor
        // HomeEnvironmentIsolationTests keeps for the same reason: the one file excluded above is
        // a real container construction, so a stripped copy of it that matches no marker means the
        // scan has gone dark — the marker list gone stale against a renamed Testcontainers API, or
        // TestSourceTree.StripCommentsAndStrings regressed into over-stripping — and the offenders
        // assertion above is green because it can no longer see a container anywhere, not because
        // none exists outside the fixture.
        (string allowedCode, _, _) = TestSourceTree.StripCommentsAndStrings(
            File.ReadAllText(Path.Combine(testsDirectory, allowedRelativePath)));

        bool scanStillSeesTheAllowedConstruction = ContainerConstructionMarkers.Any(
            marker => allowedCode.Contains(marker, StringComparison.Ordinal));

        scanStillSeesTheAllowedConstruction.Should().BeTrue(
            $"{allowedRelativePath} does construct a Testcontainers Postgres instance, so this " +
            "scan must be able to detect one there — matching nothing means the marker list no " +
            "longer names the API the fixture actually uses, or comment/string stripping is " +
            "eating real code, and this guard is protecting nothing while reporting success");
    }
}
