using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Trust;
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
        return await RunAsync(session, settings, new GitLedgerChainReader(), cancellationToken);
    }

    internal static async Task<int> RunAsync(
        IDocumentSession session, Settings settings, ILedgerChainReader chainReader, CancellationToken cancellationToken)
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

        OwnerRoleGateVerdict ownerRoleVerdict = await PromptAddendumOwnerRoleGate.ResolveAsync(
            session, project, context.OwnerId, chainReader, cancellationToken);
        if (ownerRoleVerdict != OwnerRoleGateVerdict.OwnerRole)
        {
            // Never "will never reach the ledger from here": PushAsync leaves this event's own
            // sync position untouched on skip rather than advancing past it, so a later promotion
            // of this node's own owner to Owner-role picks this exact removal back up and pushes
            // it then — telling the operator "never" was simply false (independent pre-PR review,
            // cycle 3, adversarial lens, medium).
            string reason = ownerRoleVerdict == OwnerRoleGateVerdict.NeverJoined
                ? $"this node has never joined '{project.Name.EscapeMarkup()}' (no claimed owner root yet) — "
                    + $"run h9k project join {project.Name.EscapeMarkup()} first"
                : $"this node's own owner is not currently an Owner-role member of '{project.Name.EscapeMarkup()}'";
            AnsiConsole.MarkupLine(
                $"[yellow]Recorded, but {reason}, so this removal is not pushed to the ledger from here yet — "
                + "only an Owner-role member's own node pushes a prompt addendum change to the ledger, and this "
                + "one will be pushed the moment that changes. Until then, every node keeps materializing "
                + "whatever the ledger's own owner-authorized state already is, which will restore this "
                + "locally.[/]");
            return ExitCodes.Ok;
        }

        AnsiConsole.MarkupLine(
            $"[green]Removed[/] the {builder.Value} addendum from '{project.Name.EscapeMarkup()}'. "
            + "The daemon clears it from the ledger on its next sweep.");
        return ExitCodes.Ok;
    }
}
