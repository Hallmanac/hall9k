using Hall9k.Domain.Features.Orchestrator;
using Spectre.Console;

namespace Hall9k.Cli.Orchestrator;

/// <summary>
/// The block <c>h9k orchestrator node</c> and <c>h9k orchestrator project &lt;PROJECT&gt;</c>
/// both print, after the caller's own daemon-liveness line (task: an operator starts a lean node
/// or project orchestrator window): the launch text, the recipe path, the journal path with its
/// last-written time, and the last measured turn-one cost with its date, or 'not measured'.
/// </summary>
public static class OrchestratorReport
{
    public static void Print(string cli, LaunchText? launchText, string recipePath, string journalPath)
    {
        string escapedCli = cli.EscapeMarkup();
        if (launchText is null)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]No launch text recorded for '{escapedCli}', and nothing computes a default for it.[/] "
                + $"Set one: h9k orchestrator launch-text set --cli {escapedCli} \"<command>\"");
        }
        else
        {
            AnsiConsole.WriteLine(launchText.Text);
        }

        string recipeStatus = File.Exists(recipePath) ? string.Empty : " (not created yet - run the orchestrator-recipe-generator skill)";
        AnsiConsole.MarkupLine($"[dim]Recipe: {recipePath.EscapeMarkup()}{recipeStatus}[/]");

        AnsiConsole.MarkupLine(File.Exists(journalPath)
            ? $"[dim]Journal: {journalPath.EscapeMarkup()} (last written {File.GetLastWriteTimeUtc(journalPath):yyyy-MM-dd HH:mm} UTC)[/]"
            : $"[dim]Journal: {journalPath.EscapeMarkup()} (not created yet)[/]");

        AnsiConsole.MarkupLine(launchText is { MeasuredTurnOneTokens: { } tokens, MeasuredAt: { } at }
            ? $"[dim]Last measured: {tokens} tokens on {at:yyyy-MM-dd}.[/]"
            : "[dim]Last measured: not measured.[/]");
    }
}
