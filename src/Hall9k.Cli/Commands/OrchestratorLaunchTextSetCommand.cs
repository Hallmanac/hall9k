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
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;
using Doorbell = Hall9k.Cli.Infrastructure.Doorbell;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Replaces one CLI's launch text (task: an operator starts a lean node or project orchestrator
/// window) — on the node by default, or on a project with <c>--project</c>. Any prior measurement
/// for this CLI is cleared: it was observed against the line being replaced, not this one.
/// </summary>
public sealed class OrchestratorLaunchTextSetCommand : Hall9kAsyncCommand<OrchestratorLaunchTextSetCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--cli <NAME>")]
        [Description("Which agent CLI this launch text is for. Defaults to claude-code.")]
        public string Cli { get; init; } = LaunchText.DefaultCli;

        [CommandArgument(0, "<TEXT>")]
        [Description("The exact command line an operator pastes to start this window.")]
        public string Text { get; init; } = string.Empty;

        [CommandOption("--project <NAME>")]
        [Description(
            "Set this project's own launch text instead of the node's (name, an unambiguous "
            + "fragment, or the full id). Omit to set the node's.")]
        public string? Project { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Project is { } projectArg)
        {
            using var store = CliStore.Open();
            await using IDocumentSession session = store.LightweightSession();

            ProjectDetails details = await ProjectResolver.ResolveAsync(session, projectArg, cancellationToken);
            ProjectAggregate project = (await session.Events.AggregateStreamAsync<ProjectAggregate>(details.Id, token: cancellationToken))!;
            BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

            IReadOnlyList<LaunchText> updated = OrchestratorLaunchTextResolution.WithText(project.LaunchTexts, settings.Cli, settings.Text);
            ProjectSettingsChanged changed = ProjectDecider.ChangeSettings(
                project,
                verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
                skipPermissions: Optional<bool>.None,
                maxParallelAgents: Optional<int>.None,
                contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
                DateTimeOffset.UtcNow,
                context.OwnerId,
                launchTexts: Optional<IReadOnlyList<LaunchText>>.Of(updated));

            session.Events.Append(details.Id, changed);
            await session.SaveChangesAsync(cancellationToken);
            await Doorbell.RingAsync($"project-changed:{details.Id}", cancellationToken);

            AnsiConsole.MarkupLineInterpolated(
                $"[green]Launch text for '{settings.Cli}' set on project '{details.Name}'.[/]");
            return ExitCodes.Ok;
        }

        await PlatformConfigFile.WriteOperatingSettingsAsync(
            operating => operating.LaunchTexts =
                [.. OrchestratorLaunchTextResolution.WithText(operating.LaunchTexts ?? [], settings.Cli, settings.Text)],
            cancellationToken);

        AnsiConsole.MarkupLineInterpolated($"[green]Launch text for '{settings.Cli}' set on this node.[/]");
        return ExitCodes.Ok;
    }
}
