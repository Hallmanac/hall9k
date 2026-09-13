using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// One of an idea's two terminal acts (Brian, 2026-08-21): discovery happened and nothing came
/// of it. Recorded with its reason, never deleted, and its discovery workspace left exactly
/// where it is — an idea that keeps coming back is a signal (PLAN.md §3.1's parking garage), and
/// deleting it would throw that signal away. Replaces <c>h9k idea discard</c> outright (backlog
/// 31): "discovery found nothing to do" was always this case, and there is one door for it now.
/// </summary>
public sealed class IdeaArchiveCommand : Hall9kAsyncCommand<IdeaArchiveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Idea id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--reason <REASON>")]
        [Description(
            "Why this is not worth pursuing. Required: an idea set aside without a why leaves the next "
            + "reader — often you, months later — guessing at what was already decided")]
        public string? Reason { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        // Fenced the same way h9k idea promote fences its own terminal append: a concurrent
        // conclude or archive committing between this read and the append below must not both
        // land as terminal acts on the same idea (independent post-PR review — the unfenced
        // version let a second terminal event win silently instead of being refused).
        Guid ideaId = await IdeaIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        StreamState fence = await session.Events.FetchStreamStateAsync(ideaId, cancellationToken)
            ?? throw new DomainNotFoundException($"No idea {ideaId}.");
        IdeaAggregate idea = await session.Events.AggregateStreamAsync<IdeaAggregate>(
                ideaId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No idea {ideaId}.");
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        IdeaArchived archived = IdeaDecider.Archive(
            idea, settings.Reason ?? string.Empty, DateTimeOffset.UtcNow, context.OwnerId);
        session.Events.Append(idea.Id, expectedVersion: fence.Version + 1, archived);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Idea {idea.Id} changed while archiving — read it back with h9k idea show "
                + $"{settings.Id}, and re-run this command if it is still captured.");
        }

        string shortId = TaskListCommand.ShortId(idea.Id);
        string ideaDirectory = IdeaPaths.ResolveDirectory(
            idea.WorkspaceHome, ProjectHomePaths.EntryDirectoryName(idea.Id, idea.Text), idea.Id);
        AnsiConsole.MarkupLine($"[dim]Idea {shortId} archived:[/] {archived.Reason.EscapeMarkup()}");
        AnsiConsole.MarkupLine(
            "[dim]Kept on the record, workspace and all:[/] "
            + $"{IdeaPaths.WorkspaceDirectory(ideaDirectory).EscapeMarkup()}");
        AnsiConsole.MarkupLine($"[dim]Read it back any time:[/] h9k idea show {shortId}");
        return ExitCodes.Ok;
    }
}
