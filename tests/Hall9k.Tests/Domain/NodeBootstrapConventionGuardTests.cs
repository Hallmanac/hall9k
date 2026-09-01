using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <see cref="Hall9k.Tests.Fakes.NodeBootstrapSeed"/> exists so an integration test never
/// constructs a bare <c>NodeContext</c> and calls <c>InitializeAsync</c> against a fresh
/// <see cref="Hall9k.Tests.Integration.PostgresFixture"/> database, which carries no
/// <c>ConnectionDetails</c> row yet — <c>NodeBootstrap.EnsureAsync</c> falls through to
/// <c>NodeBootstrap.GhLogin</c> in exactly that case and shells to the real <c>gh</c> (PLAN.md
/// §16 #110). Until this guard, that rule was a convention: forty-five call sites were migrated
/// to <see cref="Hall9k.Tests.Fakes.NodeBootstrapSeed.NewNodeAsync"/> by hand, and nothing stopped
/// a test class added next month from writing the same two lines all of them used before —
/// <c>NodeContext node = new(); await node.InitializeAsync(store, ct);</c> — and silently
/// reopening the gap. This is the same enforcement shape
/// <see cref="HomeEnvironmentIsolationTests"/> already uses for a different convention: fail the
/// build for any file that breaks the rule, rather than trusting every future test author to
/// remember it.
/// <para>
/// The scan matches against comment/string-stripped source
/// (<see cref="TestSourceTree.StripCommentsAndStrings"/>), so a doc comment that merely names the
/// pattern — this file's own included — is never mistaken for a real construction.
/// </para>
/// <para>
/// The scan covers the whole <c>tests/</c> directory rather than this project alone, the same
/// scope <see cref="ProcessTerminationGuardTests"/> settled on: a fixture in a second test project
/// added later reaches <c>NodeBootstrap.EnsureAsync</c> exactly as this project's do, so a scan
/// bounded to <c>tests/Hall9k.Tests</c> would claim a coverage its own test name does not qualify.
/// </para>
/// <para>
/// Two files are exempt, by repository-relative path exactly like
/// <see cref="ContainerRoutingGuardTests"/>'s single exemption:
/// <see cref="Hall9k.Tests.Fakes.NodeBootstrapSeed"/> itself, which is where the one legitimate
/// direct construction lives, and
/// <c>tests/Hall9k.Tests/Integration/CardPublicationEngineTests.cs</c>, whose
/// <c>The_loop_waits_for_this_node_to_have_an_identity_before_its_first_sweep</c> case deliberately
/// keeps its own direct construction and a deferred <c>InitializeAsync</c> call — the test exists
/// to exercise the loop's pre-bootstrap window, so the initialization has to happen on its own
/// schedule rather than bundled inside <c>NewNodeAsync</c> — made gh-safe instead by calling
/// <see cref="Hall9k.Tests.Fakes.NodeBootstrapSeed.SeedGitHubConnectionAsync"/> explicitly,
/// immediately before it (PLAN.md §16 #110). Both exemptions are file-wide rather than
/// case-specific: a second direct construction added anywhere else in
/// <c>CardPublicationEngineTests.cs</c> is not caught.
/// </para>
/// <para>
/// Its own blind spot, stated the way both sibling guards state theirs: the marker names two
/// spellings of a direct construction, <c>new NodeContext()</c> and
/// <c>NodeContext name = new()</c>, so a nullable-annotated declaration
/// (<c>NodeContext? node = new();</c>, whose <c>?</c> defeats the second alternative) and a
/// return-position construction (<c>private static NodeContext Node() =&gt; new();</c>, which
/// names no variable at all) both escape it. Neither shape appears in the tree today, and both
/// would still have to call <c>InitializeAsync</c> against an unseeded database to reopen the
/// gap, but the guard does not catch them, so it is recorded here rather than left to be
/// discovered as coverage that was assumed and never held (PLAN.md §16 #110 scarred on exactly
/// that: a guard doc claiming a reach the marker did not have).
/// </para>
/// </summary>
public sealed class NodeBootstrapConventionGuardTests
{
    private static readonly Regex DirectConstructionMarker = new(
        @"\bnew\s+NodeContext\s*\(\)|\bNodeContext\s+\w+\s*=\s*new\s*\(\)",
        RegexOptions.Compiled);

    [Fact]
    public void No_test_outside_the_seed_helper_constructs_a_node_context_directly()
    {
        // The whole tests/ directory, not just tests/Hall9k.Tests: TestSourceTree.RootDirectory()
        // resolves to this project's own root, which would leave a second test project's own
        // fixtures outside a guard whose name claims every test. repositoryRoot is
        // SourceDirectory()'s own parent ("<repositoryRoot>/src"), the same resolution
        // ProcessTerminationGuardTests uses to reach the same two trees.
        string repositoryRoot = Path.GetDirectoryName(TestSourceTree.SourceDirectory())
            ?? throw new InvalidOperationException("the resolved src directory has no parent directory");
        string testsDirectory = Path.Combine(repositoryRoot, "tests");

        // Relative to the repository root rather than a bare filename, so a differently-located
        // file that merely happens to share one of these names is not silently exempted along with
        // the real ones.
        string[] exemptRelativePaths =
        [
            Path.Combine("tests", "Hall9k.Tests", "Fakes", "NodeBootstrapSeed.cs"),
            Path.Combine("tests", "Hall9k.Tests", "Integration", "CardPublicationEngineTests.cs"),
        ];

        string[] files =
        [
            .. Directory.EnumerateFiles(testsDirectory, "*.cs", SearchOption.AllDirectories)
               .Where(file => !exemptRelativePaths.Contains(Path.GetRelativePath(repositoryRoot, file)))
               .Where(file => !TestSourceTree.IsBuildOutput(testsDirectory, file)),
        ];

        List<string> offenders = [];

        foreach (string file in files)
        {
            (string code, _, bool balanced) = TestSourceTree.StripCommentsAndStrings(File.ReadAllText(file));
            string relativePath = Path.GetRelativePath(repositoryRoot, file);

            if (!balanced)
            {
                // Mirrors the sibling guards' own handling of the same signal: a file
                // StripCommentsAndStrings desyncs on cannot be trusted to have surfaced every real
                // direct construction it contains, so it is reported as an offender itself rather
                // than silently dropped from coverage.
                offenders.Add(
                    $"{relativePath} <StripCommentsAndStrings desynced on this file: stripped brace " +
                    "depth never returned to zero, so its node-bootstrap coverage cannot be trusted>");
                continue;
            }

            if (DirectConstructionMarker.IsMatch(code))
            {
                offenders.Add(relativePath);
            }
        }

        offenders.Should().BeEmpty(
            "a test that constructs NodeContext directly and calls InitializeAsync against a " +
            "fresh PostgresFixture database shells to the real gh the moment NodeBootstrap.EnsureAsync " +
            "finds no seeded connection (PLAN.md §16 #110) — bootstrap a node through " +
            "NodeBootstrapSeed.NewNodeAsync instead, or seed one explicitly with " +
            "NodeBootstrapSeed.SeedGitHubConnectionAsync before a deliberately deferred InitializeAsync call");

        files.Length.Should().BeGreaterThan(
            100,
            "this is far fewer .cs files than the test tree actually holds — " +
            "Path.Combine(repositoryRoot, \"tests\"), resolved from TestSourceTree.SourceDirectory(), " +
            "is probably no longer resolving to the repository's tests/ directory");

        // A positive control on the scan itself, the same reason ContainerRoutingGuardTests keeps
        // one: NodeBootstrapSeed.cs's own NewNodeAsync does construct a NodeContext directly, so a
        // stripped copy of it that matches no marker means the scan has gone dark — the regex gone
        // stale against a reformatted call site, or StripCommentsAndStrings regressed into
        // over-stripping — and the offenders assertion above would be green because it can no
        // longer see a construction anywhere, not because none exists outside the exempt files.
        string seedRelativePath = Path.Combine("tests", "Hall9k.Tests", "Fakes", "NodeBootstrapSeed.cs");
        (string seedCode, _, _) = TestSourceTree.StripCommentsAndStrings(
            File.ReadAllText(Path.Combine(repositoryRoot, seedRelativePath)));

        DirectConstructionMarker.IsMatch(seedCode).Should().BeTrue(
            $"{seedRelativePath} does construct a NodeContext directly, so this scan must be able " +
            "to detect one there — matching nothing means the marker no longer names the shape the " +
            "seed helper actually uses, or comment/string stripping is eating real code, and this " +
            "guard is protecting nothing while reporting success");
    }
}
