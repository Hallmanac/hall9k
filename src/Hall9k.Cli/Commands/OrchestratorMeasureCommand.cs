using System.ComponentModel;
using System.Text.RegularExpressions;
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

            LaunchText resolved = OrchestratorLaunchTextResolution.ResolveStored(project.LaunchTexts, settings.Cli)
                ?? throw new DomainValidationException(
                    $"No launch text for '{settings.Cli}' to measure. Set one first: h9k orchestrator launch-text set --cli {settings.Cli} \"<command>\" --project {details.Name}");

            int tokens = await OrchestratorMeasureProbe.RunAsync(
                workingDirectory, AnchorPathIn(resolved.Text), SettingsPathIn(resolved.Text), cancellationToken);

            // Re-loaded fresh rather than reused from the aggregate loaded above: the probe just
            // blocked for up to OrchestratorMeasureProbe's own timeout, and building the write
            // from the stale, pre-probe project.LaunchTexts would silently revert any
            // launch-text set that landed on this project while the probe was running
            // (independent pre-PR review, cycle 3, adversarial lens).
            ProjectAggregate current = (await session.Events.AggregateStreamAsync<ProjectAggregate>(details.Id, token: cancellationToken))!;
            IReadOnlyList<LaunchText> updated = OrchestratorLaunchTextResolution.WithMeasurement(current.LaunchTexts, resolved, tokens, measuredAt);
            BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
            ProjectSettingsChanged changed = ProjectDecider.ChangeSettings(
                current,
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
        LaunchText nodeResolved = OrchestratorLaunchTextResolution.ResolveStored(operatingSettings.LaunchTexts ?? [], settings.Cli)
            ?? throw new DomainValidationException(
                $"No launch text for '{settings.Cli}' to measure. Set one first: h9k orchestrator launch-text set --cli {settings.Cli} \"<command>\"");

        int nodeTokens = await OrchestratorMeasureProbe.RunAsync(
            OrchestratorRecipeContext.NodeWorkingDirectory, AnchorPathIn(nodeResolved.Text), SettingsPathIn(nodeResolved.Text), cancellationToken);

        await PlatformConfigFile.WriteOperatingSettingsAsync(
            operating => operating.LaunchTexts =
                [.. OrchestratorLaunchTextResolution.WithMeasurement(operating.LaunchTexts ?? [], nodeResolved, nodeTokens, measuredAt)],
            cancellationToken);

        AnsiConsole.MarkupLineInterpolated(
            $"[green]Measured '{settings.Cli}' on this node: {nodeTokens} tokens ({measuredAt:yyyy-MM-dd}).[/]");
        return ExitCodes.Ok;
    }

    /// <summary>The <c>--append-system-prompt-file</c> path this exact launch text runs with, or the platform default when the text does not carry that flag at all (independent pre-PR review, cycle 3, both lenses: the probe used to always measure the default flag set regardless of what the record it stamped actually said).</summary>
    internal static string AnchorPathIn(string launchText) =>
        FlagValue(launchText, "--append-system-prompt-file") ?? LaunchTextDefaults.AnchorRelativePath;

    /// <summary>The <c>--settings</c> path this exact launch text runs with, or the platform default when the text does not carry that flag at all.</summary>
    internal static string SettingsPathIn(string launchText) =>
        FlagValue(launchText, "--settings") ?? LaunchTextDefaults.SettingsRelativePath;

    private static string? FlagValue(string launchText, string flagName)
    {
        Match match = Regex.Match(launchText, $@"{Regex.Escape(flagName)}\s+(\S+)");
        return match.Success ? match.Groups[1].Value : null;
    }
}
