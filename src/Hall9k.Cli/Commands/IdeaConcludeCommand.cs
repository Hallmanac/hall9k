using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Tasks.Handlers;
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

        [CommandOption("--holder <NAME>")]
        [Description(
            "An idea somebody holds is theirs to conclude, so this refuses unless this node's owner is its "
            + "assignee, or with none its creator. An Owner-role member may conclude it on that owner's "
            + "behalf by naming the holder here (their label, which the refusal names, or at least 8 hex "
            + "characters of their root fingerprint; the word 'unknown' when the idea's owner cannot be "
            + "resolved on this node) and giving --reason, which is then also the override's reason")]
        public string? Holder { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        return await RunAsync(
            session, settings, new GitLedgerChainReader(), new NodeKeyStore(), DateTimeOffset.UtcNow, cancellationToken);
    }

    /// <summary>Testable core: the chain read and the key store are the seams the Owner-role check of an override needs.</summary>
    internal static async Task<int> RunAsync(
        IDocumentSession session, Settings settings, ILedgerChainReader chainReader, NodeKeyStore keyStore,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
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

        // Only the idea's assignee, or its creator when it has none, decides its fate, or an
        // Owner-role member by the deliberate override. An ended idea earns its own refusal first.
        IdeaDecider.RequireCaptured(idea, "conclude");
        TaskOwnerOverrideDecision ownerDecision = await IdeaOwnerGuard.AuthorizeAsync(
            session, idea, context, "conclude", settings.Holder, settings.Reason, chainReader, keyStore, cancellationToken);
        IdeaConcluded concluded = IdeaDecider.Conclude(idea, settings.Reason ?? string.Empty, now, context.OwnerId);
        if (ownerDecision.Outcome == TaskOwnerOverrideOutcome.Override)
        {
            concluded = concluded with
            {
                OnBehalfOfOwnerRootFingerprint = ownerDecision.OnBehalfOfRootFingerprint,
                OverrideReason = ownerDecision.Reason,
            };
        }

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
        TaskOwnerGuard.AnnounceOverride(ownerDecision, "concluded");
        return ExitCodes.Ok;
    }
}
