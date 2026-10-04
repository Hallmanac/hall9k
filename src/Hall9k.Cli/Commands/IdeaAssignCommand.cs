using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx.Events;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// A member lays hold of an idea, visibly to the fleet or the team, before any task exists
/// (<see cref="IdeaAssigneeSet"/>), or hands it to another member. The idea's assignee, or its creator
/// when it has none, may name any member, itself included; every other member is refused, including
/// one naming itself on a teammate's unassigned idea (decision b8aa9007), unless an Owner-role member
/// overrides with <c>--holder</c> and <c>--reason</c>. Naming a person is all this verb does: moving an
/// idea to a project is <c>h9k idea move</c> (decision ca1f0313), and a project named here is refused
/// with that pointer.
/// </summary>
public sealed class IdeaAssignCommand : Hall9kAsyncCommand<IdeaAssignCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Idea id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandArgument(1, "[MEMBER]")]
        [Description(
            "The member who holds the idea: their name, an unambiguous fragment of it or of their "
            + "email, or their id. Omit it to lay hold of the idea yourself. Naming another member needs "
            + "the idea shared with the team first (h9k idea share)")]
        public string? Member { get; init; }

        [CommandOption("--project <PROJECT>")]
        [Description(
            "Refused: this verb names a person. Moving an idea to a project is h9k idea move <id> <project>")]
        public string? Project { get; init; }

        [CommandOption("--reason <REASON>")]
        [Description(
            "Why an Owner-role member is assigning another owner's idea. Required with --holder, where it "
            + "is the override's reason and is recorded on the assignee event")]
        public string? Reason { get; init; }

        [CommandOption("--holder <NAME>")]
        [Description(
            "An idea somebody holds is theirs to hand on, so this refuses unless this node's owner may "
            + "act on it, which is its assignee, or with none its creator, who may assign it to any member "
            + "(a member who is neither may not, and may not take a teammate's unassigned idea for "
            + "themselves). An Owner-role member may assign it on that owner's behalf by naming the holder "
            + "here (their label, which the refusal names, or at least 8 hex characters of their root "
            + "fingerprint; the word 'unknown' when the idea's owner cannot be resolved on this node) and "
            + "giving --reason, both required together")]
        public string? Holder { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        RefuseProject(settings);

        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        return await RunAsync(
            session, settings, new GitLedgerChainReader(), new NodeKeyStore(), DateTimeOffset.UtcNow, cancellationToken);
    }

    /// <summary>
    /// <c>--project</c> is declared only so it can be refused with the verb that does the job: assignment
    /// names a person, and a stale habit of moving an idea this way must learn <c>h9k idea move</c>
    /// from the refusal itself (decision ca1f0313).
    /// </summary>
    internal static void RefuseProject(Settings settings)
    {
        if (settings.Project.IsNotBlank())
        {
            throw new DomainValidationException(
                "h9k idea assign names the person who holds an idea. Moving it to a project is "
                + $"h9k idea move {settings.Id} {settings.Project}.");
        }
    }

    /// <summary>Testable core: the chain read and the key store are the seams the Owner-role check of an override needs.</summary>
    internal static async Task<int> RunAsync(
        IDocumentSession session, Settings settings, ILedgerChainReader chainReader, NodeKeyStore keyStore,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Fenced like the other idea acts: a conclude racing this assign must not both land.
        Guid ideaId = await IdeaIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        StreamState fence = await session.Events.FetchStreamStateAsync(ideaId, cancellationToken)
            ?? throw new DomainNotFoundException($"No idea {ideaId}.");
        IdeaAggregate idea = await session.Events.AggregateStreamAsync<IdeaAggregate>(
                ideaId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No idea {ideaId}.");
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        OwnerDetails assignee = await ResolveMemberAsync(session, idea, settings.Member, context, cancellationToken);

        // The idea's own terminal state is the one refusal every path shares, so it comes before the
        // ownership question: an ended idea has nothing left to hand on, whoever asks.
        IdeaDecider.RequireCaptured(idea, "assign");
        TaskOwnerOverrideDecision ownerDecision = await IdeaOwnerGuard.AuthorizeAsync(
            session, idea, context, "assign", settings.Holder, settings.Reason, chainReader, keyStore, cancellationToken);

        IdeaAssigneeSet? set = AppendAssignee(session, idea, assignee, context.OwnerId, ownerDecision, now, fence.Version + 1);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Idea {idea.Id} changed while it was being assigned, so nothing was recorded. Read it back with "
                + $"h9k idea show {settings.Id}, then re-run this command if it is still open.");
        }

        string shortId = TaskListCommand.ShortId(idea.Id);
        string name = assignee.Name.EscapeMarkup();
        AnsiConsole.MarkupLine(set is null
            ? $"[yellow]Idea {shortId} is already assigned to {name}[/]. Nothing changed."
            : $"[green]Idea {shortId} assigned to {name}[/]: only they, or an Owner-role member with "
                + "--holder and --reason, may conclude, archive or promote it now.");
        AnsiConsole.MarkupLine(assignee.Id == context.OwnerId
            ? $"[dim]To let go of it:[/] h9k idea unassign {shortId} [dim]· to cut a task from it:[/] "
                + $"h9k task add --from-idea {shortId} --objective \"…\""
            : $"[dim]It is {name}'s now: they can cut tasks from it, conclude or archive it, or let go with[/] "
                + $"h9k idea unassign {shortId}");
        TaskOwnerGuard.AnnounceOverride(ownerDecision, "assigned");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The member the verb names. Omitted means this node's own owner. A name that is no member but
    /// is a project is refused with the verb that moves an idea there (decision ca1f0313); a name that
    /// is both resolves as the member, because assignment is this verb's whole meaning.
    /// </summary>
    internal static async Task<OwnerDetails> ResolveMemberAsync(
        IQuerySession session, IdeaAggregate idea, string? member, BootstrapContext context,
        CancellationToken cancellationToken)
    {
        if (member.IsBlank())
        {
            return await session.LoadAsync<OwnerDetails>(context.OwnerId, cancellationToken)
                ?? throw new DomainNotFoundException($"No owner {context.OwnerId}.");
        }

        try
        {
            return await OwnerResolver.ResolveAsync(session, member, cancellationToken);
        }
        catch (DomainNotFoundException)
        {
            ProjectDetails? project = await TryResolveProjectAsync(session, member, cancellationToken);
            if (project is not null)
            {
                throw new DomainValidationException(
                    $"'{member}' is a project, not a member: h9k idea assign names the person who holds an "
                    + $"idea. Moving idea {TaskListCommand.ShortId(idea.Id)} to a project is "
                    + $"h9k idea move {TaskListCommand.ShortId(idea.Id)} {project.Name}.");
            }

            throw;
        }
    }

    /// <summary>
    /// Appends <see cref="IdeaAssigneeSet"/> onto an open session (the caller saves), or nothing when
    /// <paramref name="assignee"/> already holds the idea. Naming another member needs a root
    /// fingerprint to compare: every peer judges the hold by root, never by this node's local owner id.
    /// </summary>
    internal static IdeaAssigneeSet? AppendAssignee(
        IDocumentSession session, IdeaAggregate idea, OwnerDetails assignee, Guid setByOwnerId,
        TaskOwnerOverrideDecision ownerDecision, DateTimeOffset now, long? expectedVersion = null)
    {
        bool assigneeIsActor = assignee.Id == setByOwnerId;
        if (!assigneeIsActor && assignee.RootFingerprint.IsBlank())
        {
            throw new DomainValidationException(
                $"This node has no root fingerprint for {assignee.Name}, so a teammate's node could not tell whose "
                + "idea this is. Hand the idea to a member this node can resolve to a root.");
        }

        bool overridden = ownerDecision.Outcome == TaskOwnerOverrideOutcome.Override;
        IdeaAssigneeSet? set = IdeaDecider.SetAssignee(
            idea, assignee.Id, assignee.RootFingerprint, assigneeIsActor, now, setByOwnerId,
            overridden ? ownerDecision.OnBehalfOfRootFingerprint : null,
            overridden ? ownerDecision.Reason : null);
        if (set is not null && expectedVersion is { } version)
        {
            session.Events.Append(idea.Id, version, set);
        }
        else if (set is not null)
        {
            session.Events.Append(idea.Id, set);
        }

        return set;
    }

    private static async Task<ProjectDetails?> TryResolveProjectAsync(
        IQuerySession session, string nameOrFragment, CancellationToken cancellationToken)
    {
        try
        {
            return await ProjectResolver.ResolveAsync(session, nameOrFragment, cancellationToken);
        }
        catch (DomainException)
        {
            return null;
        }
    }
}
