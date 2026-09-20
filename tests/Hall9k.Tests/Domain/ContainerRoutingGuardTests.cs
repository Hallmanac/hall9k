using System.Reflection;
using FluentAssertions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <see cref="Hall9k.Tests.Integration.PostgresFixture"/> is the only place in the test tree
/// allowed to construct a Testcontainers Postgres instance, because it is the only place that
/// gates container lifetime through <see cref="Hall9k.Tests.Integration.CrossProcessContainerGate"/>
/// (Decisions Log #132, following up #108). A test class that instead builds a
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

    /// <summary>
    /// The other half of the bound this guard's sibling fact protects: not only must
    /// <see cref="Hall9k.Tests.Integration.PostgresFixture"/> be the only place a container gets
    /// built, it must actually acquire its permit from
    /// <see cref="Hall9k.Tests.Integration.CrossProcessContainerGate"/> before starting that
    /// container, and the cap it acquires against must still be four (Decisions Log #108,
    /// #132, PLACEHOLDER-98484f36) — the number every wall-clock estimate for the flow-scoped
    /// seam assumes. Both are read straight from <c>PostgresFixture.cs</c> rather than proven by
    /// actually starting containers: this class needs no Docker, the same reasoning
    /// <see cref="Every_postgres_container_in_the_test_tree_is_built_by_the_bounded_fixture"/>
    /// already rests on.
    /// </summary>
    [Fact]
    public void PostgresFixture_acquires_a_gate_permit_before_starting_its_container_and_the_cap_is_still_four()
    {
        FieldInfo? maxConcurrentContainers = typeof(Hall9k.Tests.Integration.PostgresFixture).GetField(
            "MaxConcurrentContainers", BindingFlags.NonPublic | BindingFlags.Static);

        maxConcurrentContainers.Should().NotBeNull(
            "PostgresFixture.MaxConcurrentContainers has apparently been renamed or removed — this " +
            "reflection lookup needs to follow it so the cap below is still checked against the " +
            "real field, not a stale copy");
        maxConcurrentContainers!.GetRawConstantValue().Should().Be(
            4, "the container-gate cap this whole seam's wall-clock estimate assumes (Decisions Log " +
            "#108) must still be four, machine-wide across every concurrent dotnet test invocation");

        string repositoryRoot = Path.GetDirectoryName(TestSourceTree.SourceDirectory())
            ?? throw new InvalidOperationException("the resolved src directory has no parent directory");
        string fixturePath = Path.Combine(repositoryRoot, "tests", "Hall9k.Tests", "Integration", "PostgresFixture.cs");
        (string code, _, bool balanced) = TestSourceTree.StripCommentsAndStrings(File.ReadAllText(fixturePath));

        balanced.Should().BeTrue(
            "StripCommentsAndStrings desynced on PostgresFixture.cs, so the ordering check below " +
            "cannot be trusted");

        int acquireIndex = code.IndexOf("CrossProcessContainerGate.AcquireAsync(", StringComparison.Ordinal);
        int startIndex = code.IndexOf("_container.StartAsync(", StringComparison.Ordinal);

        acquireIndex.Should().BeGreaterThan(
            -1, "PostgresFixture no longer calls CrossProcessContainerGate.AcquireAsync at all — the " +
            "gate this whole bound depends on has apparently been removed or renamed");
        startIndex.Should().BeGreaterThan(
            -1, "PostgresFixture no longer calls _container.StartAsync — Testcontainers' own start " +
            "API has apparently changed shape, and this check needs to follow it");
        acquireIndex.Should().BeLessThan(
            startIndex, "the gate permit must be acquired before the container starts, not after — " +
            "starting first and acquiring afterward would let more than the cap's own containers run " +
            "concurrently for the window between them");
    }
}
