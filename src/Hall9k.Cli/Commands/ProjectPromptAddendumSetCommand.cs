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
/// Replaces the whole addendum a project states for one prompt builder (idea b9b09779, piece 6).
/// Appends only <see cref="ProjectPromptAddendumSet"/> here — never touches the ledger itself: the
/// daemon's own <c>PromptAddendaSweepEngine</c> is the only thing that ever writes
/// <c>prompt-addenda/&lt;builder&gt;.md</c>, so a dispatched agent session running this command can
/// add project guidance to a future prompt without ever holding write access to the ledger.
/// </summary>
public sealed class ProjectPromptAddendumSetCommand : Hall9kAsyncCommand<ProjectPromptAddendumSetCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PROJECT>")]
        [Description("Project name, an unambiguous fragment of it, or its id.")]
        public string Project { get; init; } = string.Empty;

        [CommandArgument(1, "<BUILDER>")]
        [Description("Which shipped prompt builder this addendum is for: work, review-lap, agent, or mention-follow-up.")]
        public string Builder { get; init; } = string.Empty;

        [CommandOption("--file <PATH>")]
        [Description("A markdown file whose whole content replaces this builder's addendum.")]
        public string File { get; init; } = string.Empty;

        [CommandOption("--over-cap <REASON>")]
        [Description(
            "Accept an addendum past the length cap, with this reason recorded on the event — the cap is a "
            + "judgment call, never a blocker, but it needs a stated why.")]
        public string? OverCapReason { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        return await RunAsync(session, settings, cancellationToken);
    }

    internal static async Task<int> RunAsync(IDocumentSession session, Settings settings, CancellationToken cancellationToken)
    {
        if (settings.File.IsBlank())
        {
            throw new DomainValidationException("--file is required: the path to the whole addendum text to set.");
        }

        if (!System.IO.File.Exists(settings.File))
        {
            throw new DomainValidationException($"'{settings.File}' does not exist.");
        }

        ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
        if (!project.HomeDirectory.HasValue)
        {
            // MaterializeAsync (PromptAddendaSweepEngine) only ever writes the local copy every
            // prompt builder's loader reads under a project's own home directory, so a home-less
            // project (h9k project add --no-home) can push this event forever and never have it
            // reach a single prompt — refused here rather than a silent no-op nobody would ever
            // trace back to this (independent pre-PR review, cycle 1, conformance lens, medium).
            throw new DomainValidationException(
                $"'{project.Name}' has no home directory yet, so an addendum set here can never reach a "
                + $"prompt — the daemon only ever materializes one under a project's own home. Give it one "
                + $"first: h9k project init {project.Name}");
        }

        PromptBuilderKey builder = PromptBuilderKey.Parse(settings.Builder);
        string content = await System.IO.File.ReadAllTextAsync(settings.File, cancellationToken);
        bool overCap = settings.OverCapReason.IsNotBlank();

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        ProjectPromptAddendumSet set = ProjectDecider.SetPromptAddendum(
            project.Id, builder, content, overCap, settings.OverCapReason, context.OwnerId, DateTimeOffset.UtcNow);
        session.Events.Append(project.Id, set);
        await session.SaveChangesAsync(cancellationToken);

        AnsiConsole.MarkupLine(
            $"[green]Set[/] the {builder.Value} addendum for '{project.Name.EscapeMarkup()}'"
            + (overCap ? " [yellow](over this project's usual length cap)[/]" : string.Empty)
            + ". The daemon writes it to the ledger on its next sweep.");
        return ExitCodes.Ok;
    }
}
