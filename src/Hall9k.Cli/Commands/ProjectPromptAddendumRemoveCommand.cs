using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Clears a project's own addendum for one prompt builder (idea b9b09779, piece 6), mirroring
/// <see cref="ProjectPromptAddendumSetCommand"/>: appends only <see cref="ProjectPromptAddendumRemoved"/>
/// here — the daemon's own sweep is what deletes the ledger file.
/// </summary>
public sealed class ProjectPromptAddendumRemoveCommand : Hall9kAsyncCommand<ProjectPromptAddendumRemoveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PROJECT>")]
        [Description("Project name, an unambiguous fragment of it, or its id.")]
        public string Project { get; init; } = string.Empty;

        [CommandArgument(1, "<BUILDER>")]
        [Description("Which shipped prompt builder to clear the addendum from: work, review-lap, agent, or mention-follow-up.")]
        public string Builder { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        return await RunAsync(session, settings, cancellationToken);
    }

    internal static async Task<int> RunAsync(IDocumentSession session, Settings settings, CancellationToken cancellationToken)
    {
        ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
        PromptBuilderKey builder = PromptBuilderKey.Parse(settings.Builder);

        if (!project.PromptAddenda.ContainsKey(builder.Value))
        {
            throw new DomainValidationException($"'{project.Name}' has no {builder.Value} addendum to remove.");
        }

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        ProjectPromptAddendumRemoved removed = ProjectDecider.RemovePromptAddendum(
            project.Id, builder, context.OwnerId, DateTimeOffset.UtcNow);
        session.Events.Append(project.Id, removed);
        await session.SaveChangesAsync(cancellationToken);

        AnsiConsole.MarkupLine(
            $"[green]Removed[/] the {builder.Value} addendum from '{project.Name.EscapeMarkup()}'. "
            + "The daemon clears it from the ledger on its next sweep.");
        return ExitCodes.Ok;
    }
}
