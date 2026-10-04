using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The assignee lets go of an idea (<see cref="IdeaAssigneeCleared"/>): nobody holds it afterwards,
/// so it falls back to its creator. Allowed only to the current assignee, since a creator who handed
/// the idea away no longer holds it, or to an Owner-role member with <c>--holder</c> and
/// <c>--reason</c>.
/// </summary>
public sealed class IdeaUnassignCommand : Hall9kAsyncCommand<IdeaUnassignCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Idea id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--reason <REASON>")]
        [Description(
            "Why the idea is being let go of; recorded on IdeaAssigneeCleared and left unknown when "
            + "omitted, never inferred. Required with --holder, where it is also recorded as the "
            + "override's reason")]
        public string? Reason { get; init; }

        [CommandOption("--holder <NAME>")]
        [Description(
            "Another member's idea is theirs to let go of, so this refuses unless this node's owner is its "
            + "assignee. An Owner-role member may let go of it on that owner's behalf by naming the "
            + "holder here (their label, which the refusal names, or at least 8 hex characters of their "
            + "root fingerprint) and giving --reason, both required together")]
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
        // Fenced like the other idea acts: a conclude racing this unassign must not both land.
        Guid ideaId = await IdeaIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        StreamState fence = await session.Events.FetchStreamStateAsync(ideaId, cancellationToken)
            ?? throw new DomainNotFoundException($"No idea {ideaId}.");
        IdeaAggregate idea = await session.Events.AggregateStreamAsync<IdeaAggregate>(
                ideaId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No idea {ideaId}.");
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        // Decided first for the refusals it owns (an ended idea, an idea nobody holds); the override
        // stamp is added once the guard has answered.
        IdeaAssigneeCleared cleared = IdeaDecider.ClearAssignee(idea, settings.Reason, now, context.OwnerId);
        TaskOwnerOverrideDecision ownerDecision = await IdeaOwnerGuard.AuthorizeAsync(
            session, idea, context, "unassign", settings.Holder, settings.Reason, chainReader, keyStore, cancellationToken);
        if (ownerDecision.Outcome == TaskOwnerOverrideOutcome.Override)
        {
            cleared = cleared with
            {
                OnBehalfOfOwnerRootFingerprint = ownerDecision.OnBehalfOfRootFingerprint,
                OverrideReason = ownerDecision.Reason,
            };
        }

        session.Events.Append(idea.Id, expectedVersion: fence.Version + 1, cleared);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Idea {idea.Id} changed while letting go of it, so nothing was released. Read it back with "
                + $"h9k idea show {settings.Id}, then re-run this command if it still has an assignee.");
        }

        AnsiConsole.MarkupLine(
            $"[blue]Idea {TaskListCommand.ShortId(idea.Id)} let go[/] - nobody holds it now, so it falls back to its creator.");
        TaskOwnerGuard.AnnounceOverride(ownerDecision, "unassigned");
        return ExitCodes.Ok;
    }
}
