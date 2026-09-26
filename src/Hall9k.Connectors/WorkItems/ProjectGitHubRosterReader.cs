using System.ComponentModel;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Marten;

namespace Hall9k.Connectors.WorkItems;

/// <summary>How a declared GitHub account stands against a project's collaborator roster.</summary>
public enum DeclaredAccountStanding
{
    /// <summary>The account is a collaborator with push.</summary>
    PushConfirmed,

    /// <summary>The account is a collaborator without push.</summary>
    ReadOnly,

    /// <summary>A roster exists and the account is not in it.</summary>
    NotACollaborator,

    /// <summary>This node holds no roster (it lacks push itself), so nothing can be checked here.</summary>
    UncheckedHere,
}

/// <summary>
/// The collaborator roster as this node can best read it right now. <see cref="Held"/> is false
/// when this node has no roster at all (its own account lacks push, which GitHub answers by
/// refusing to list collaborators). <see cref="Live"/> is false when gh could not answer and the
/// stored mirror was read instead; <see cref="AsOf"/> then dates a held roster.
/// </summary>
public sealed record ProjectGitHubRoster(
    IReadOnlyList<ProjectGitHubMemberView> Collaborators, bool Held, bool Live, DateTimeOffset? AsOf)
{

    /// <summary>
    /// Matches on the numeric account id first, since a rename keeps the id, and on the login
    /// (case-insensitively, as GitHub logins are) only when no collaborator has that id.
    /// </summary>
    public DeclaredAccountStanding Check(DeclaredGitHubAccount account)
    {
        if (!Held)
        {
            return DeclaredAccountStanding.UncheckedHere;
        }

        ProjectGitHubMemberView? match =
            Collaborators.FirstOrDefault(collaborator => collaborator.AccountId == account.AccountId)
            ?? Collaborators.FirstOrDefault(
                collaborator => string.Equals(collaborator.Login, account.Login, StringComparison.OrdinalIgnoreCase));

        return match switch
        {
            null => DeclaredAccountStanding.NotACollaborator,
            { Role.HasPush: true } => DeclaredAccountStanding.PushConfirmed,
            _ => DeclaredAccountStanding.ReadOnly,
        };
    }
}

/// <summary>
/// Reads a project's collaborator roster: the list <see cref="ProjectGitHubAccessMirror.ObserveAsync"/>
/// just read from gh when it answers, and the stored <see cref="ProjectGitHubMembers"/> mirror, dated,
/// when it cannot (or answers without a collaborator list). The mirror is never the fresh answer: it
/// keeps a collaborator GitHub has since removed. The one place the roster is read for
/// checking a declared account, so <c>h9k project members</c> and any later verify command share it.
/// </summary>
public sealed class ProjectGitHubRosterReader(ProjectGitHubAccessMirror? mirror = null)
{
    private readonly ProjectGitHubAccessMirror mirror = mirror ?? new ProjectGitHubAccessMirror();

    public async Task<ProjectGitHubRoster> ReadAsync(
        IDocumentSession session, ProjectDetails project, DateTimeOffset now, CancellationToken cancellationToken)
    {
        bool refreshed;
        bool holdsRoster = false;
        IReadOnlyList<GitHubCollaboratorRole>? observed = null;
        try
        {
            ProjectGitHubAccessResult access = await mirror.ObserveAsync(session, project, now, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
            refreshed = true;
            holdsRoster = access.OwnRole.HasPush;
            observed = access.Collaborators;
        }
        // gh not installed, not authenticated, wedged, or answering with something unreadable: every
        // one is "gh cannot answer", which falls back to whatever was last observed.
        catch (Exception exception) when (exception is DomainValidationException or InvalidOperationException
            or TimeoutException or Win32Exception)
        {
            refreshed = false;
        }

        // The collaborator list gh just returned is the roster, not the stored mirror: the mirror
        // never drops a collaborator GitHub removed, so checking against it would keep confirming
        // an account that no longer has access.
        if (observed is not null)
        {
            return new ProjectGitHubRoster(
                [.. observed.Select(collaborator => new ProjectGitHubMemberView(
                    collaborator.AccountId, collaborator.Login, collaborator.Role, now))],
                true,
                true,
                null);
        }

        ProjectGitHubMembers? stored = await session.LoadAsync<ProjectGitHubMembers>(project.Id, cancellationToken);
        IReadOnlyList<ProjectGitHubMemberView> collaborators = stored is null ? [] : [.. stored.Members.Values];

        if (!refreshed)
        {
            // Without a live read, this node holds a roster only if its own account had push when
            // it last looked: below push the mirror holds nothing but this node's own row.
            ProjectGitHubAccount? own = await ProjectGitHubClient.TryResolveAccountAsync(session, project, cancellationToken);
            holdsRoster = own is not null
                && stored is not null
                && stored.Members.TryGetValue(own.Id, out ProjectGitHubMemberView? ownView)
                && ownView.Role.HasPush;
        }

        // Reaching here with a successful refresh means gh answered the repository but returned no
        // collaborator list (a failed or unreadable collaborator call, or no push): that is not a
        // live roster, so it is reported as the stored mirror with its date, never as a fresh read.
        return holdsRoster && collaborators.Count > 0
            ? new ProjectGitHubRoster(collaborators, true, false, collaborators.Max(member => member.LastObservedAt))
            : new ProjectGitHubRoster([], false, refreshed, null);
    }
}
