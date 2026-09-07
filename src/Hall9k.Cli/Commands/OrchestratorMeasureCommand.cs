using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Cli.Orchestrator;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;
using Doorbell = Hall9k.Cli.Infrastructure.Doorbell;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Runs the fixed one-turn probe (task: an operator starts a lean node or project orchestrator
/// window) and stamps its result onto the launch-text record it measured, so
/// <c>launch-text show</c> prints "minimal" as a number rather than a promise. The method never
/// changes between runs — the flags, the fixed probe prompt, and the fixed cheap model are all in
/// <see cref="OrchestratorMeasureProbe"/> — so a number recorded today is comparable to one
/// recorded next month, or on a different project.
/// </summary>
public sealed class OrchestratorMeasureCommand : Hall9kAsyncCommand<OrchestratorMeasureCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--cli <NAME>")]
        [Description("Which agent CLI's launch text to measure. Only claude-code is implemented today.")]
        public string Cli { get; init; } = LaunchText.DefaultCli;

        [CommandOption("--project <NAME>")]
        [Description(
            "Measure this project's own window instead of the node's (name, an unambiguous fragment, "
            + "or the full id). Omit to measure the node's.")]
        public string? Project { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (!string.Equals(LaunchText.NormalizeCli(settings.Cli), LaunchText.DefaultCli, StringComparison.Ordinal))
        {
            throw new DomainValidationException(
                $"Measuring '{settings.Cli}' is not implemented yet — only {LaunchText.DefaultCli} runs the probe today.");
        }

        DateTimeOffset measuredAt = DateTimeOffset.UtcNow;

        if (settings.Project is { } projectArg)
        {
            using var store = CliStore.Open();
            await using IDocumentSession session = store.LightweightSession();

            ProjectDetails details = await ProjectResolver.ResolveAsync(session, projectArg, cancellationToken);
            ProjectAggregate project = (await session.Events.AggregateStreamAsync<ProjectAggregate>(details.Id, token: cancellationToken))!;
            string workingDirectory = OrchestratorRecipeContext.ProjectWorkingDirectory(details);

            LaunchText resolved = OrchestratorLaunchTextResolution.Resolve(
                project.LaunchTexts, settings.Cli, workingDirectory, OrchestratorRecipeContext.ProjectOpeningMessage(details.Name))
                ?? throw new DomainValidationException(
                    $"No launch text for '{settings.Cli}' to measure. Set one first: h9k orchestrator launch-text set --cli {settings.Cli} \"<command>\" --project {details.Name}");

            int tokens = await OrchestratorMeasureProbe.RunAsync(
                workingDirectory, LaunchTextDefaults.AnchorRelativePath, LaunchTextDefaults.SettingsRelativePath, cancellationToken);

            IReadOnlyList<LaunchText> updated = OrchestratorLaunchTextResolution.WithMeasurement(project.LaunchTexts, resolved, tokens, measuredAt);
            BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
            ProjectSettingsChanged changed = ProjectDecider.ChangeSettings(
                project,
                verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
                skipPermissions: Optional<bool>.None,
                contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
                measuredAt,
                context.OwnerId,
                launchTexts: Optional<IReadOnlyList<LaunchText>>.Of(updated));

            session.Events.Append(details.Id, changed);
            await session.SaveChangesAsync(cancellationToken);
            await Doorbell.RingAsync($"project-changed:{details.Id}", cancellationToken);

            AnsiConsole.MarkupLineInterpolated(
                $"[green]Measured '{settings.Cli}' on project '{details.Name}': {tokens} tokens ({measuredAt:yyyy-MM-dd}).[/]");
            return ExitCodes.Ok;
        }

        OperatingSettings operatingSettings = await PlatformConfigFile.ReadOperatingSettingsAsync(cancellationToken);
        string nodeWorkingDirectory = OrchestratorRecipeContext.NodeWorkingDirectory;
        LaunchText nodeResolved = OrchestratorLaunchTextResolution.Resolve(
            operatingSettings.LaunchTexts ?? [], settings.Cli, nodeWorkingDirectory, OrchestratorRecipeContext.NodeOpeningMessage)
            ?? throw new DomainValidationException(
                $"No launch text for '{settings.Cli}' to measure. Set one first: h9k orchestrator launch-text set --cli {settings.Cli} \"<command>\"");

        int nodeTokens = await OrchestratorMeasureProbe.RunAsync(
            nodeWorkingDirectory, LaunchTextDefaults.AnchorRelativePath, LaunchTextDefaults.SettingsRelativePath, cancellationToken);

        await PlatformConfigFile.WriteOperatingSettingsAsync(
            operating => operating.LaunchTexts =
                [.. OrchestratorLaunchTextResolution.WithMeasurement(operating.LaunchTexts ?? [], nodeResolved, nodeTokens, measuredAt)],
            cancellationToken);

        AnsiConsole.MarkupLineInterpolated(
            $"[green]Measured '{settings.Cli}' on this node: {nodeTokens} tokens ({measuredAt:yyyy-MM-dd}).[/]");
        return ExitCodes.Ok;
    }
}
