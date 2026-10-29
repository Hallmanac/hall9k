using Hall9k.Cli.Infrastructure;
using Hall9k.Cli.Installation;
using Hall9k.Domain.Infrastructure.Storage;
using Spectre.Console;

namespace Hall9k.Cli.Orchestrator;

/// <summary>One registered project and the home directory the registry records for it.</summary>
public sealed record RegisteredProjectHome(string ProjectName, string HomeDirectory);

/// <summary>
/// Re-renders <see cref="LaunchAnchorDocument"/> in the node home, in every registered project
/// home that already has an anchor, and in every solution home on this node that already has one,
/// so a project or solution window never launches on an anchor older than the installed build. A project window reads its own home's <c>recipes/launch-anchor.md</c>, which
/// only <c>h9k project add</c> and <c>h9k project init</c> ever wrote, so without this an anchor
/// change would reach no project window that was registered before it.
/// <para>
/// <c>h9k install</c> and <c>h9k update</c> run this through the newly installed <c>h9k</c> as a
/// child process (<see cref="Step"/>), never in their own process: <c>h9k update</c> runs from the
/// binaries it is about to replace, so rendering in-process would write the OLD build's anchor, and
/// reading the registry after the swap is the Marten load-time crash
/// <see cref="DaemonRestartHandoff"/> describes. Install is the repair path, so every failure here
/// is reported and skipped, and the exit code stays zero.
/// </para>
/// <para>
/// A home with no anchor is left without one: that home never rendered its recipe files, so
/// writing a lone anchor into it would be a half-rendered home. <c>h9k project init</c> is how it
/// gets the whole set. A project home's <c>settings.json</c> is not touched here; it still renders
/// only on <c>h9k project init</c> and <c>h9k project add</c>.
/// </para>
/// <para>
/// Solution homes are found by listing <see cref="SolutionHomePaths.SolutionsRoot"/>, not through
/// the registry: their path is a convention, so the refresh reaches them even when the registry
/// cannot be read, and a home left behind by a renamed solution is refreshed too.
/// </para>
/// </summary>
public static class LaunchAnchorRefresh
{
    /// <summary>The planned invocation of the freshly installed <c>h9k</c> that runs the refresh.</summary>
    public static RestartStep Step { get; } = new(
        "refresh the launch anchor in the node home, every registered project home that has one, and every solution home that has one",
        ["orchestrator", "refresh-anchors"]);

    /// <summary>The one line install prints when no project home's anchor could be refreshed,
    /// whatever the reason: the registry would not read, or the refresh never ran.</summary>
    public static string DescribeNoneRefreshed(string reason) =>
        $"No project home's launch anchor was refreshed ({reason.ReplaceLineEndings(" ")}). "
        + "Run h9k orchestrator refresh-anchors to try again, or h9k project init <project> "
        + "(with --keep-repo-path if the repository path should stay as it is) for a home that never rendered its recipe files.";

    /// <summary>Prints the line <see cref="DescribeNoneRefreshed"/> words, for the process that
    /// launched the refresh and found it did not run to completion.</summary>
    public static void ReportNotRun(string reason) =>
        AnsiConsole.MarkupLineInterpolated($"[yellow]{DescribeNoneRefreshed(reason)}[/]");

    /// <summary>
    /// The refresh itself. <paramref name="readRegistry"/> is the registry read, a seam so a test
    /// can fail it or feed it homes without a database; any exception it throws is the "registry
    /// cannot be read" case. <paramref name="writeAnchor"/> defaults to
    /// <see cref="LaunchAnchorDocument.Write"/>, a seam so a test can fail one home's write
    /// deterministically. <paramref name="listSolutionHomes"/> defaults to
    /// <see cref="SolutionHomePaths.Existing"/>, a seam so a test can name its own solution homes.
    /// Always returns <see cref="ExitCodes.Ok"/>.
    /// </summary>
    public static async Task<int> RunAsync(
        Func<CancellationToken, Task<IReadOnlyList<RegisteredProjectHome>>> readRegistry,
        CancellationToken cancellationToken,
        Action<string>? writeAnchor = null,
        Func<IReadOnlyList<string>>? listSolutionHomes = null)
    {
        Action<string> write = writeAnchor ?? LaunchAnchorDocument.Write;
        RefreshNodeAnchor(write);
        RefreshSolutionAnchors(listSolutionHomes ?? SolutionHomePaths.Existing, write);

        IReadOnlyList<RegisteredProjectHome> homes;
        try
        {
            homes = await readRegistry(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportNotRun($"the project registry could not be read: {exception.Message}");
            return ExitCodes.Ok;
        }

        int refreshed = 0;
        foreach (RegisteredProjectHome registered in homes)
        {
            string anchor = ProjectHomePaths.LaunchAnchorFile(registered.HomeDirectory);
            if (!File.Exists(anchor))
            {
                continue;
            }

            try
            {
                write(anchor);
                refreshed++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[yellow]Could not refresh the launch anchor in project home {registered.HomeDirectory} for {registered.ProjectName} ({exception.Message.ReplaceLineEndings(" ")}). Run h9k orchestrator refresh-anchors to try again, or h9k project init {registered.ProjectName} if that home never rendered its recipe files.[/]");
            }
        }

        AnsiConsole.MarkupLineInterpolated(
            $"[dim]Refreshed the launch anchor in {refreshed} registered project home(s) that already had one.[/]");
        return ExitCodes.Ok;
    }

    private static void RefreshSolutionAnchors(Func<IReadOnlyList<string>> listSolutionHomes, Action<string> write)
    {
        int refreshed = 0;
        try
        {
            foreach (string home in listSolutionHomes())
            {
                string anchor = ProjectHomePaths.LaunchAnchorFile(home);
                if (!File.Exists(anchor))
                {
                    continue;
                }

                try
                {
                    write(anchor);
                    refreshed++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    AnsiConsole.MarkupLineInterpolated(
                        $"[yellow]Could not refresh the launch anchor in solution home {home} ({exception.Message.ReplaceLineEndings(" ")}). Run h9k orchestrator refresh-anchors to try again, or h9k solution init <solution> if that home never rendered its recipe files.[/]");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Could not list the solution homes under {SolutionHomePaths.SolutionsRoot} ({exception.Message.ReplaceLineEndings(" ")}). Run h9k orchestrator refresh-anchors to try again.[/]");
            return;
        }

        AnsiConsole.MarkupLineInterpolated(
            $"[dim]Refreshed the launch anchor in {refreshed} solution home(s) that already had one.[/]");
    }

    private static void RefreshNodeAnchor(Action<string> write)
    {
        try
        {
            write(RecipeLibraryPaths.LaunchAnchorFile);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Could not refresh the node's launch anchor at {RecipeLibraryPaths.LaunchAnchorFile} ({exception.Message.ReplaceLineEndings(" ")}). Run h9k install again to render it.[/]");
        }
    }
}
