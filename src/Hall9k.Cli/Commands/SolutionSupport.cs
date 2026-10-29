using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Spectre.Console;

namespace Hall9k.Cli.Commands;

/// <summary>One project's membership change: a membership to write onto it, or null to take it out of its solution.</summary>
internal sealed record SolutionWrite(ProjectDetails Project, SolutionMembership? Membership);

internal static class SolutionWriter
{
    /// <summary>
    /// Rewrites the membership on every project in <paramref name="writes"/> in one commit, the way
    /// <c>h9k project set</c> writes a team setting: <see cref="ProjectDecider.ChangeSettings"/>
    /// builds the node event (and refuses a second open solution), and its team companion is
    /// appended beside it so the change replicates with no path of its own. One failed refusal
    /// aborts before anything is saved, so a solution is never half rewritten.
    /// </summary>
    public static async Task WriteAsync(
        IDocumentSession session, IReadOnlyList<SolutionWrite> writes, CancellationToken cancellationToken)
    {
        await AppendAsync(session, writes, cancellationToken);

        foreach (SolutionWrite write in writes)
        {
            await Doorbell.RingAsync($"project-changed:{write.Project.Id}", cancellationToken);
        }
    }

    /// <summary>The append and the single commit of <see cref="WriteAsync"/>, without ringing the daemon's doorbell.</summary>
    internal static async Task AppendAsync(
        IDocumentSession session, IReadOnlyList<SolutionWrite> writes, CancellationToken cancellationToken)
    {
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        DateTimeOffset changedAt = DateTimeOffset.UtcNow;

        foreach (SolutionWrite write in writes)
        {
            StreamState fence = await session.Events.FetchStreamStateAsync(write.Project.Id, cancellationToken)
                ?? throw new DomainNotFoundException($"No project {write.Project.Id}.");
            ProjectAggregate aggregate = await session.Events.AggregateStreamAsync<ProjectAggregate>(
                    write.Project.Id, version: fence.Version, token: cancellationToken)
                ?? throw new DomainNotFoundException($"No project {write.Project.Id}.");

            // The command decided from the project as it loaded it; if the stream moved the membership
            // since (a replicated change lands between the load and this read), that decision is stale.
            if (!SameMembership(aggregate.Solution, write.Project.Solution))
            {
                throw StaleWrite();
            }

            ProjectSettingsChanged changed = ProjectDecider.ChangeSettings(
                aggregate,
                verifyCommands: default,
                skipPermissions: default,
                contextLinks: default,
                changedAt,
                context.OwnerId,
                solution: Optional<SolutionMembership?>.Of(write.Membership));

            List<object> events = [changed];
            if (ProjectTeamSettingsChanged.From(changed) is { } teamChanged)
            {
                events.Add(teamChanged);
            }

            session.Events.Append(write.Project.Id, fence.Version + events.Count, events);
        }

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw StaleWrite();
        }
    }

    private static bool SameMembership(SolutionMembership? current, SolutionMembership? loaded) =>
        current is null
            ? loaded is null
            : loaded is not null && current.Agrees(loaded);

    private static DomainConflictException StaleWrite() =>
        new("A member project changed while the solution was being rewritten, so nothing was written. "
            + "Check h9k solution show and run this command again.");

    /// <summary>
    /// The writes that give <paramref name="membership"/> to the project on this node that holds each of
    /// its members (resolved through <paramref name="context"/>), plus a clear for each project in
    /// <paramref name="removed"/>. Callers check <see cref="SolutionDecider.RequireEveryMemberHeld"/> first,
    /// which is what makes the resolution here total, and <see cref="SolutionDecider.RequireConsistent"/>,
    /// which is what makes every project written here one that carries the solution (or is joining it).
    /// </summary>
    public static IReadOnlyList<SolutionWrite> Rewrite(
        SolutionMembership membership, SolutionContext context, IReadOnlyList<ProjectDetails> removed) =>
    [
        .. membership.Members.Select(member => new SolutionWrite(
            SolutionDecider.HeldProject(member, context.Projects, context.RootFingerprintOf)
                ?? throw new DomainConflictException($"The member '{member.Name}' is not registered and joined on this node."),
            membership)),
        .. removed.Select(project => new SolutionWrite(project, null)),
    ];
}

/// <summary>
/// Gives projects joining a solution the solution epics they missed. A solution epic is one ordinary epic
/// per member project, so a project linked later has no copy of the epics made before it came, and a
/// task there could not join them; this makes the copies, in the same commit as the membership write.
/// </summary>
internal static class SolutionEpicCopies
{
    /// <summary>
    /// Stages (does not save) a copy in each of <paramref name="joining"/> of every open solution epic this node
    /// holds for <paramref name="solutionKey"/>, skipping a project that already has one and any scheduled
    /// for permanent deletion. Returns how many copies it staged.
    /// </summary>
    public static async Task<int> StageAsync(
        IDocumentSession session, string solutionKey, IReadOnlyList<ProjectDetails> joining, CancellationToken cancellationToken)
    {
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        IReadOnlyList<EpicDetails> epics = await session.Query<EpicDetails>().ToListAsync(cancellationToken);
        IReadOnlyList<ProjectDetails> projects = await session.Query<ProjectDetails>().ToListAsync(cancellationToken);
        IReadOnlyList<EpicAdded> copies = SolutionEpicDecider.CopiesForNewMembers(
            epics,
            projects,
            solutionKey,
            [.. joining.Where(project => project.PurgeAt is null).Select(project => project.Id)],
            DomainId.New,
            DateTimeOffset.UtcNow,
            context.OwnerId);

        foreach (EpicAdded copy in copies)
        {
            session.Events.StartStream<EpicAggregate>(copy.Id, copy);
        }

        return copies.Count;
    }

    public static string Describe(int copied) =>
        copied == 0
            ? string.Empty
            : $" {copied} open solution epic cop{(copied == 1 ? "y was" : "ies were")} made for it.";
}

/// <summary>The warn-and-ask step <c>h9k solution add</c> and <c>link</c> share when a new member's repository owner is new to the solution.</summary>
internal static class SolutionOwnerConfirmation
{
    /// <summary>
    /// True when the grouping may proceed: nothing to warn about, <c>--yes</c>, or an interactive
    /// yes. False when the person answered no. Refuses outright, quoting the rule, in a session that
    /// cannot prompt and was not given <c>--yes</c>.
    /// </summary>
    public static bool Confirm(
        IReadOnlyList<SolutionOwnerMismatch> mismatches, string solutionName, bool yes, IInteractiveConfirmation confirmation)
    {
        if (mismatches.Count == 0)
        {
            return true;
        }

        string detail = string.Join(
            "; ",
            mismatches.Select(mismatch =>
                $"'{mismatch.Candidate.Name}' belongs to {mismatch.CandidateOwner ?? "an owner this URL does not name"} "
                + $"({mismatch.Candidate.RepositoryUrl}), while the solution's repositories belong to "
                + string.Join(", ", mismatch.ExistingOwners)));
        string warning =
            $"Grouping into the solution '{solutionName}' puts a repository from a different owner or organization "
            + $"in front of every teammate of every member: each member's name and repository URL travels with the "
            + $"solution. {detail}.";

        if (yes)
        {
            return true;
        }

        if (!confirmation.IsInteractive)
        {
            throw new DomainValidationException(
                warning + " This session cannot prompt, so confirm it explicitly: pass --yes.");
        }

        AnsiConsole.MarkupLineInterpolated($"[yellow]{warning}[/]");
        return confirmation.Confirm("Group them anyway?", defaultValue: false);
    }
}
