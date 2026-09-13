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
/// One of an idea's two terminal acts (Brian, 2026-08-21): discovery happened and something
/// came of it — tasks were cut (<c>h9k task add --from-idea</c>), or an outcome was acted on
/// some other way. Always an explicit human decision: cutting a task never concludes the idea
/// on its own, because discovery may keep producing more of them, and this is the act that says
/// it has stopped.
/// </summary>
public sealed class IdeaConcludeCommand : Hall9kAsyncCommand<IdeaConcludeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Idea id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--reason <REASON>")]
        [Description(
            "What came of it: tasks cut, or an outcome acted on some other way. Required — an idea "
            + "closed without a why leaves the next reader, often you, months later, unable to tell "
            + "this ending apart from an archive without rereading the whole fan-out")]
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

        IdeaConcluded concluded = IdeaDecider.Conclude(
            idea, settings.Reason ?? string.Empty, DateTimeOffset.UtcNow, context.OwnerId);
        session.Events.Append(idea.Id, expectedVersion: fence.Version + 1, concluded);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Idea {idea.Id} changed while concluding — read it back with h9k idea show "
                + $"{settings.Id}, and re-run this command if it is still captured.");
        }

        string shortId = TaskListCommand.ShortId(idea.Id);
        AnsiConsole.MarkupLine($"[green]Idea {shortId} concluded:[/] {concluded.Reason.EscapeMarkup()}");
        AnsiConsole.MarkupLine(idea.CutTaskIds.Count switch
        {
            0 => "[dim]No tasks were cut from it on record here — see h9k idea show for the whole trail.[/]",
            1 => $"[dim]It fanned out into 1 task:[/] h9k idea show {shortId}",
            int n => $"[dim]It fanned out into {n} tasks:[/] h9k idea show {shortId}",
        });
        return ExitCodes.Ok;
    }
}
