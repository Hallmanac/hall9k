using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Rewriting the note as discovery sharpens it. Unlike a task revision, this needs no
/// ceremony: nothing dispatches from an idea, so there is no promise an edit could break.
/// Every version stays on the stream.
/// </summary>
public sealed class IdeaReviseCommand : Hall9kAsyncCommand<IdeaReviseCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Idea id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandArgument(1, "<TEXT>")]
        [Description(
            "The note as it reads now — this replaces the whole text. What it said before stays "
            + "on the stream and in h9k idea show, so the thinking is never overwritten, only moved on from")]
        public string Text { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        IdeaAggregate idea = await IdeaIdResolver.LoadAsync(session, settings.Id, cancellationToken);
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        IdeaRevised revised = IdeaDecider.Revise(idea, settings.Text, DateTimeOffset.UtcNow, context.OwnerId);
        session.Events.Append(idea.Id, revised);
        await session.SaveChangesAsync(cancellationToken);

        string shortId = TaskListCommand.ShortId(idea.Id);
        AnsiConsole.MarkupLine($"[blue]Idea {shortId} revised[/] {revised.Text.EscapeMarkup()}");
        AnsiConsole.MarkupLine($"[dim]What it said before is still there:[/] h9k idea show {shortId}");
        return ExitCodes.Ok;
    }
}
