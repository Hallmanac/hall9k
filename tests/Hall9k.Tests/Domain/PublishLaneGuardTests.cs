using FluentAssertions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Every test class that shells out to a real <c>dotnet publish</c> — i.e. every caller of
/// <c>PublishTestSupport.RunPublishAsync</c> — has to carry two attributes, and both are load-
/// bearing rather than decorative:
/// <list type="bullet">
/// <item><c>[Trait("Category", "PublishesBinary")]</c>, so a cycle gate can drop the whole family
/// by filter (<c>--filter "Category!=PublishesBinary"</c>) while the mandatory final full pass
/// still runs it — the same role <c>Category=RequiresDocker</c> plays for the container tier.</item>
/// <item><c>[Collection("PublishesBinary")]</c>, so no two of them run at once. They publish
/// against the repository's own <c>obj/Release</c> (that is what lets them run <c>--no-restore</c>
/// against the build that precedes <c>dotnet test</c>), so two concurrent callers would be two
/// MSBuild processes writing one set of intermediate directories.</item>
/// </list>
/// <para>
/// A source scan rather than a reflection check, because reflection can see a class's attributes
/// but not that it calls a publish helper — and the failure being guarded is precisely a third
/// publish test added later with the attributes forgotten, which then silently rejoins the
/// parallel lane and the un-excludable set. Same shape as
/// <see cref="ContainerRoutingGuardTests"/>, whose scan this one is modelled on, and same tree:
/// the whole <c>tests/</c> directory, since a second test project (this repository has one,
/// <c>Hall9k.Tests.LockHolder</c>) could call the helper exactly as this project's classes can.
/// </para>
/// <para>
/// The call site is matched against comment/string-stripped source
/// (<see cref="TestSourceTree.StripCommentsAndStrings"/>) so prose naming the helper — this
/// file's own marker among it — is never mistaken for a real call, which is also what lets this
/// file scan itself with no name-based exemption. The two attributes, in contrast, are matched
/// against the raw text: their arguments <em>are</em> string literals, so the stripped text no
/// longer contains them (<see cref="StackedBaseBranchGuardTests"/> matches raw for its own
/// reasons). The consequence is that only the exact spelling below satisfies this guard; a
/// differently-spaced but equivalent attribute would have to be taught here. The stripper's
/// brace-balance flag is deliberately ignored: this is a flat marker search with no structural
/// matching, which is the one case <see cref="TestSourceTree.StripCommentsAndStrings"/>'s own doc
/// comment names as not needing it (<see cref="ContainerRoutingGuardTests"/> does check it,
/// because it matches class boundaries).
/// </para>
/// </summary>
public sealed class PublishLaneGuardTests
{
    private const string PublishCallMarker = "RunPublishAsync";
    private const string RequiredTrait = """[Trait("Category", "PublishesBinary")]""";
    private const string RequiredCollection = """[Collection("PublishesBinary")]""";

    [Fact]
    public void Every_test_that_runs_a_real_publish_is_traited_and_serialised()
    {
        string repositoryRoot = Path.GetDirectoryName(TestSourceTree.SourceDirectory())
            ?? throw new InvalidOperationException("the resolved src directory has no parent directory");
        string testsDirectory = Path.Combine(repositoryRoot, "tests");

        // The helper that declares the method, not a caller of it. Relative to the tests root
        // rather than a bare filename, the same care ContainerRoutingGuardTests takes, so a
        // future same-named file elsewhere is not silently exempted along with the real one.
        string helperRelativePath = Path.Combine("Hall9k.Tests", "Cli", "PublishTestSupport.cs");

        string[] files =
        [
            .. Directory.EnumerateFiles(testsDirectory, "*.cs", SearchOption.AllDirectories)
               .Where(file => !string.Equals(
                   Path.GetRelativePath(testsDirectory, file), helperRelativePath, StringComparison.Ordinal))
               .Where(file => !TestSourceTree.IsBuildOutput(testsDirectory, file)),
        ];

        List<string> offenders = [];
        int callers = 0;

        foreach (string file in files)
        {
            string source = File.ReadAllText(file);
            (string code, _, _) = TestSourceTree.StripCommentsAndStrings(source);
            if (!code.Contains(PublishCallMarker, StringComparison.Ordinal))
            {
                continue;
            }

            callers++;
            string relativePath = Path.GetRelativePath(testsDirectory, file);
            if (!source.Contains(RequiredTrait, StringComparison.Ordinal))
            {
                offenders.Add($"{relativePath} runs a real publish without {RequiredTrait}");
            }

            if (!source.Contains(RequiredCollection, StringComparison.Ordinal))
            {
                offenders.Add($"{relativePath} runs a real publish without {RequiredCollection}");
            }
        }

        offenders.Should().BeEmpty(
            "a test that shells out to a real dotnet publish must be excludable by filter and must "
            + "never run beside another one, since both build against the repository's own "
            + "obj/Release (see tests/Hall9k.Tests/README.md)");

        // The scan finding nothing to check at all would pass vacuously, which is how a renamed
        // helper method silently retires this guard rather than failing it.
        callers.Should().BeGreaterThanOrEqualTo(
            2, $"the two known publish tests both call {PublishCallMarker}; if this scan sees fewer, "
            + "the helper was renamed and this guard is no longer matching anything");
    }
}
