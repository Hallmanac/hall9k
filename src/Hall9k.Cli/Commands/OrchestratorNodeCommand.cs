using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Cli.Orchestrator;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The node orchestrator's own view (task: an operator starts a lean node or project orchestrator
/// window): daemon liveness, the launch text, and where its recipe and journal are. Never
/// launches a session — an operator copies the printed line themselves (the design's own ruling:
/// Hall9k prints instructions and never spawns an interactive session).
/// </summary>
public sealed class OrchestratorNodeCommand : Hall9kAsyncCommand<OrchestratorNodeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--cli <NAME>")]
        [Description("Which agent CLI's launch text to print. Defaults to claude-code.")]
        public string Cli { get; init; } = LaunchText.DefaultCli;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLineInterpolated($"[dim]{OrchestratorDaemonLiveness.Describe()}[/]");

        OperatingSettings operatingSettings = await PlatformConfigFile.ReadOperatingSettingsAsync(cancellationToken);
        LaunchText? resolved = OrchestratorLaunchTextResolution.Resolve(
            operatingSettings.LaunchTexts ?? [],
            settings.Cli,
            OrchestratorRecipeContext.NodeWorkingDirectory,
            OrchestratorRecipeContext.NodeOpeningMessage);

        OrchestratorReport.Print(settings.Cli, resolved, RecipeLibraryPaths.OrchestratorRecipeFile, RecipeLibraryPaths.JournalFile);
        return ExitCodes.Ok;
    }
}
