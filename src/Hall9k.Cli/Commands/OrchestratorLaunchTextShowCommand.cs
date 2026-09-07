using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Cli.Orchestrator;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Persistence;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Prints one CLI's launch text (task: an operator starts a lean node or project orchestrator
/// window): the node's own setting by default, or a project's with <c>--project</c>. Nothing has
/// to be set first — before any <c>launch-text set</c>, this prints the platform's own computed
/// default for <see cref="LaunchText.DefaultCli"/> so there is always something to paste.
/// </summary>
public sealed class OrchestratorLaunchTextShowCommand : Hall9kAsyncCommand<OrchestratorLaunchTextShowCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--cli <NAME>")]
        [Description("Which agent CLI's launch text to show. Defaults to claude-code, the only CLI with a computed default today.")]
        public string Cli { get; init; } = LaunchText.DefaultCli;

        [CommandOption("--project <NAME>")]
        [Description(
            "Show this project's own launch text instead of the node's (name, an unambiguous fragment, "
            + "or the full id). Omit for the node's setting.")]
        public string? Project { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Project is { } projectArg)
        {
            using var store = CliStore.Open();
            await using IQuerySession session = store.QuerySession();
            ProjectDetails project = await ProjectResolver.ResolveAsync(session, projectArg, cancellationToken);
            Print(
                settings.Cli,
                OrchestratorLaunchTextResolution.Resolve(
                    project.LaunchTexts,
                    settings.Cli,
                    OrchestratorRecipeContext.ProjectWorkingDirectory(project),
                    OrchestratorRecipeContext.ProjectOpeningMessage(project.Name)));
            return ExitCodes.Ok;
        }

        OperatingSettings operatingSettings = await PlatformConfigFile.ReadOperatingSettingsAsync(cancellationToken);
        Print(
            settings.Cli,
            OrchestratorLaunchTextResolution.Resolve(
                operatingSettings.LaunchTexts ?? [],
                settings.Cli,
                OrchestratorRecipeContext.NodeWorkingDirectory,
                OrchestratorRecipeContext.NodeOpeningMessage));
        return ExitCodes.Ok;
    }

    private static void Print(string cli, LaunchText? resolved)
    {
        string escapedCli = cli.EscapeMarkup();
        if (resolved is null)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]No launch text recorded for '{escapedCli}', and nothing computes a default for it.[/] "
                + $"Set one: h9k orchestrator launch-text set --cli {escapedCli} \"<command>\"");
            return;
        }

        AnsiConsole.WriteLine(resolved.Text);
        AnsiConsole.MarkupLine(resolved is { MeasuredTurnOneTokens: { } tokens, MeasuredAt: { } at }
            ? $"[dim]Last measured: {tokens} tokens on {at:yyyy-MM-dd}.[/]"
            : $"[dim]Last measured: not measured. Measure it: h9k orchestrator measure --cli {escapedCli}[/]");
    }
}
