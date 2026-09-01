using System.Text;
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
/// <para>
/// The scan matches against comment/string-stripped source (<see cref="StripCommentsAndStrings"/>),
/// not the raw text, so a file that merely names <c>PostgreSqlBuilder</c>/<c>PostgreSqlContainer</c>
/// in a doc comment or a quoted string — this file's own marker list among them — is never mistaken
/// for a real construction call; that is also what lets this file scan itself rather than needing
/// a name-based exemption the way an earlier draft did.
/// </para>
/// <para>
/// The marker list only names the two Postgres-specific Testcontainers types, so it does not catch
/// a container built through the generic <c>ContainerBuilder</c>/<c>IContainer</c> API instead —
/// the same class of blind spot <see cref="Hall9k.Tests.Domain.HomeEnvironmentIsolationTests"/>
/// documents for its own scan. This guard cannot follow a construction there.
/// </para>
/// </summary>
public sealed class ContainerRoutingGuardTests
{
    private const string AllowedFileName = "PostgresFixture.cs";

    private static readonly string[] ContainerConstructionMarkers =
    [
        "PostgreSqlBuilder",
        "PostgreSqlContainer",
    ];

    [Fact]
    public void Every_postgres_container_in_the_test_tree_is_built_by_the_bounded_fixture()
    {
        string testsDirectory = TestSourceTree.RootDirectory();

        string[] files =
        [
            .. Directory.EnumerateFiles(testsDirectory, "*.cs", SearchOption.AllDirectories)
               .Where(file => !string.Equals(Path.GetFileName(file), AllowedFileName, StringComparison.Ordinal))
               .Where(file => !TestSourceTree.IsBuildOutput(testsDirectory, file)),
        ];

        List<string> offenders = [];

        foreach (string file in files)
        {
            string code = StripCommentsAndStrings(File.ReadAllText(file));

            if (ContainerConstructionMarkers.Any(marker => code.Contains(marker, StringComparison.Ordinal)))
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
            "this is far fewer .cs files than the test tree actually holds — TestSourceTree.RootDirectory() is " +
            "probably no longer resolving to tests/Hall9k.Tests");
    }

    /// <summary>
    /// A smaller-scope cousin of
    /// <see cref="Hall9k.Tests.Domain.HomeEnvironmentIsolationTests.StripCommentsAndStrings"/>:
    /// this guard only needs to know whether a marker survives into real code somewhere in the
    /// file, not which class or line it lands in, so it returns stripped text alone rather than
    /// also tracking a position map back to the source.
    /// </summary>
    private static string StripCommentsAndStrings(string source)
    {
        StringBuilder result = new(source.Length);
        int i = 0;

        while (i < source.Length)
        {
            char c = source[i];

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                int end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? source.Length : end + 2;
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                int end = source.IndexOf('\n', i);
                i = end < 0 ? source.Length : end;
                continue;
            }

            if (c == '"' && i + 2 < source.Length && source[i + 1] == '"' && source[i + 2] == '"')
            {
                i = SkipRawString(source, i);
                continue;
            }

            // Not raw ("""), so an interpolated string ($") is treated as an ordinary string:
            // skipping just its '$' here hands the opening quote to the regular-string case
            // below, which is enough to find the closing quote correctly. A real construction
            // call is never written inside an interpolation hole, so this never hides a genuine
            // offender — it can only ever suppress a false one.
            if (c == '$' && i + 1 < source.Length && source[i + 1] == '"')
            {
                i++;
                continue;
            }

            if (c == '@' && i + 1 < source.Length && source[i + 1] == '"')
            {
                i = SkipVerbatimString(source, i);
                continue;
            }

            if (c == '\'')
            {
                i = SkipCharLiteral(source, i);
                continue;
            }

            if (c == '"')
            {
                i = SkipRegularString(source, i);
                continue;
            }

            result.Append(c);
            i++;
        }

        return result.ToString();
    }

    private static int SkipRawString(string source, int i)
    {
        int quoteRun = 0;
        while (i + quoteRun < source.Length && source[i + quoteRun] == '"')
        {
            quoteRun++;
        }

        string delimiter = new('"', quoteRun);
        int end = source.IndexOf(delimiter, i + quoteRun, StringComparison.Ordinal);
        return end < 0 ? source.Length : end + quoteRun;
    }

    private static int SkipVerbatimString(string source, int i)
    {
        int j = i + 2;
        while (j < source.Length)
        {
            if (source[j] == '"')
            {
                if (j + 1 < source.Length && source[j + 1] == '"')
                {
                    j += 2;
                    continue;
                }

                return j + 1;
            }

            j++;
        }

        return j;
    }

    private static int SkipCharLiteral(string source, int i)
    {
        int j = i + 1;
        while (j < source.Length && source[j] != '\'' && source[j] != '\n')
        {
            j += source[j] == '\\' && j + 1 < source.Length ? 2 : 1;
        }

        return j < source.Length && source[j] == '\'' ? j + 1 : j;
    }

    private static int SkipRegularString(string source, int i)
    {
        int j = i + 1;
        while (j < source.Length && source[j] != '"' && source[j] != '\n')
        {
            j += source[j] == '\\' && j + 1 < source.Length ? 2 : 1;
        }

        return j < source.Length && source[j] == '"' ? j + 1 : j;
    }
}
