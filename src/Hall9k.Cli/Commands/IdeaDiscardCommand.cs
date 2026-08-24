using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Storage;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Closing an idea honestly: recorded with its reason, never deleted, and its discovery
/// workspace left exactly where it is. A discarded idea that keeps coming back is a signal
/// (PLAN.md §3.1's parking garage), and deleting it would throw that signal away.
/// </summary>
public sealed class IdeaDiscardCommand : Hall9kAsyncCommand<IdeaDiscardCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Idea id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--reason <REASON>")]
        [Description(
            "Why this is not worth pursuing. Required: an idea dropped without a why leaves the next "
            + "reader — often you, months later — guessing at what was already decided")]
        public string? Reason { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        IdeaAggregate idea = await IdeaIdResolver.LoadAsync(session, settings.Id, cancellationToken);
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        IdeaDiscarded discarded = IdeaDecider.Discard(
            idea, settings.Reason ?? string.Empty, DateTimeOffset.UtcNow, context.OwnerId);
        session.Events.Append(idea.Id, discarded);
        await session.SaveChangesAsync(cancellationToken);

        string shortId = TaskListCommand.ShortId(idea.Id);
        string ideaDirectory = IdeaPaths.ResolveDirectory(
            idea.WorkspaceHome, ProjectHomePaths.EntryDirectoryName(idea.Id, idea.Text), idea.Id);
        AnsiConsole.MarkupLine($"[dim]Idea {shortId} discarded:[/] {discarded.Reason.EscapeMarkup()}");
        AnsiConsole.MarkupLine(
            "[dim]Kept on the record, workspace and all:[/] "
            + $"{IdeaPaths.WorkspaceDirectory(ideaDirectory).EscapeMarkup()}");
        AnsiConsole.MarkupLine($"[dim]Read it back any time:[/] h9k idea show {shortId}");
        return ExitCodes.Ok;
    }
}
