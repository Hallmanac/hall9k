using FluentAssertions;
using Hall9k.Cli.Infrastructure;
using Hall9k.Cli.Orchestrator;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// What the anchor refresh <c>h9k install</c> and <c>h9k update</c> launch through the installed CLI
/// does once it runs: rewrite a stale anchor wherever one already exists, leave a home with none
/// without one, and report a registry or write failure as one line while still returning success.
/// The registry read and the write are the seams, so no database is needed.
/// </summary>
public sealed class LaunchAnchorRefreshTests : IDisposable
{
    private const string Stale = "an anchor rendered by an older build\n";

    private readonly ScopedTestHome _scopedHome = new();

    public void Dispose() => _scopedHome.Dispose();

    private string NewProjectHome(string name, string? existingAnchor)
    {
        string home = Path.Combine(_scopedHome.Home, "projects", name);
        Directory.CreateDirectory(home);
        if (existingAnchor is not null)
        {
            Directory.CreateDirectory(ProjectHomePaths.RecipesDirectory(home));
            File.WriteAllText(ProjectHomePaths.LaunchAnchorFile(home), existingAnchor);
        }

        return home;
    }

    private static Func<CancellationToken, Task<IReadOnlyList<RegisteredProjectHome>>> Registry(
        params (string Name, string Home)[] homes) =>
        _ => Task.FromResult<IReadOnlyList<RegisteredProjectHome>>(
            [.. homes.Select(entry => new RegisteredProjectHome(entry.Name, entry.Home))]);

    [Fact]
    public async Task A_home_whose_anchor_predates_the_change_is_rewritten_and_so_is_the_node_s()
    {
        string home = NewProjectHome("alpha", Stale);
        Directory.CreateDirectory(RecipeLibraryPaths.CanonicalDirectory);
        File.WriteAllText(RecipeLibraryPaths.LaunchAnchorFile, Stale);

        int exitCode = await LaunchAnchorRefresh.RunAsync(Registry(("alpha", home)), CancellationToken.None);

        exitCode.Should().Be(ExitCodes.Ok);
        File.ReadAllText(ProjectHomePaths.LaunchAnchorFile(home)).Should().Be(LaunchAnchorDocument.Render());
        File.ReadAllText(RecipeLibraryPaths.LaunchAnchorFile).Should().Be(LaunchAnchorDocument.Render());
    }

    [Fact]
    public async Task A_home_with_no_anchor_stays_without_one()
    {
        string home = NewProjectHome("bare", existingAnchor: null);

        int exitCode = await LaunchAnchorRefresh.RunAsync(Registry(("bare", home)), CancellationToken.None);

        exitCode.Should().Be(ExitCodes.Ok);
        File.Exists(ProjectHomePaths.LaunchAnchorFile(home)).Should().BeFalse();
        Directory.Exists(ProjectHomePaths.RecipesDirectory(home)).Should().BeFalse(
            "a home that never rendered its recipe files is not given a lone anchor");
    }

    [Fact]
    public async Task A_registry_that_cannot_be_read_prints_one_line_naming_project_init_and_succeeds()
    {
        string output = await ScopedAnsiConsoleCapture.CaptureAsync(async () =>
        {
            int exitCode = await LaunchAnchorRefresh.RunAsync(
                _ => throw new InvalidOperationException("connection refused\nsecond line of the driver's message"),
                CancellationToken.None);

            exitCode.Should().Be(ExitCodes.Ok);
        });

        output.Split('\n').Where(line => line.Contains("No project home's launch anchor was refreshed"))
            .Should().ContainSingle().Which.Should().Contain("h9k project init <project>")
            .And.Contain("connection refused second line of the driver's message");
        File.Exists(RecipeLibraryPaths.LaunchAnchorFile).Should().BeTrue(
            "the node's own anchor needs no registry, so an unreadable one does not hold it back");
    }

    [Fact]
    public async Task A_home_that_cannot_be_written_is_named_and_the_rest_are_still_refreshed()
    {
        string blocked = NewProjectHome("blocked", Stale);
        string fine = NewProjectHome("fine", Stale);
        string blockedAnchor = ProjectHomePaths.LaunchAnchorFile(blocked);

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(async () =>
        {
            int exitCode = await LaunchAnchorRefresh.RunAsync(
                Registry(("blocked", blocked), ("fine", fine)),
                CancellationToken.None,
                writeAnchor: path =>
                {
                    if (path == blockedAnchor)
                    {
                        // Not an IOException: install is the repair path, so any failure is skipped.
                        throw new NotSupportedException("access denied");
                    }

                    LaunchAnchorDocument.Write(path);
                });

            exitCode.Should().Be(ExitCodes.Ok);
        });

        output.Should().Contain(blocked).And.Contain("access denied").And.Contain("h9k project init blocked");
        File.ReadAllText(blockedAnchor).Should().Be(Stale);
        File.ReadAllText(ProjectHomePaths.LaunchAnchorFile(fine)).Should().Be(LaunchAnchorDocument.Render());
    }
}
