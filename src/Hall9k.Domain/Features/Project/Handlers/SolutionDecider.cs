using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Project.Handlers;

/// <summary>
/// One new member whose repository owner or organization matches none of the members already in
/// the solution, so the command asks before it groups them (<see cref="SolutionDecider.OwnerMismatches"/>).
/// </summary>
public sealed record SolutionOwnerMismatch(SolutionMember Candidate, string? CandidateOwner, IReadOnlyList<string> ExistingOwners);

/// <summary>
/// The pure, database-free half of the solution commands: what a membership may hold, how each of
/// <c>add</c>, <c>link</c>, <c>unlink</c>, <c>rename</c> and <c>close</c> rewrites it, which member
/// a node can and cannot rewrite, and the one-solution rule <see cref="ProjectDecider.ChangeSettings"/>
/// enforces. There is no Solution aggregate (the membership on the members is the solution), so
/// these are functions over <see cref="SolutionMembership"/> and the project documents rather than
/// a decider over a stream.
/// </summary>
public static class SolutionDecider
{
    /// <summary>The key of a new solution: a 26-character Crockford-base32 ULID, minted once and never reused.</summary>
    public static string NewKey() => Ulid.NewUlid().ToString();

    /// <summary>
    /// What a membership must be to be written onto a project: a ULID key, a name, a state with the
    /// close reason that state requires, and at least one member, each named by a ULID project key
    /// with a name and an absolute repository URL, none twice.
    /// </summary>
    public static void Validate(SolutionMembership membership)
    {
        if (!Ulid.TryParse(membership.Key, out _))
        {
            throw new DomainValidationException(
                $"'{membership.Key}' is not a solution key: a solution key is the 26-character "
                + "Crockford-base32 ULID h9k solution add mints once.");
        }

        if (membership.Name.IsBlank())
        {
            throw new DomainValidationException("A solution requires a name, the way a project does.");
        }

        if (membership.State != SolutionState.Open && membership.State != SolutionState.Closed)
        {
            throw new DomainValidationException(
                $"A solution is {SolutionState.Open} or {SolutionState.Closed}, not '{membership.State}'.");
        }

        if (membership.State == SolutionState.Closed && membership.CloseReason.IsBlank())
        {
            throw new DomainValidationException(
                "A closed solution carries the reason it was closed: closing is an explicit human act "
                + "with a reason, never automatic. Pass h9k solution close --reason \"<why>\".");
        }

        if (membership.State == SolutionState.Open && membership.CloseReason.IsNotBlank())
        {
            throw new DomainValidationException("An open solution carries no close reason.");
        }

        if (membership.Members.Count == 0)
        {
            throw new DomainValidationException("A solution groups at least one project.");
        }

        foreach (SolutionMember member in membership.Members)
        {
            if (!Ulid.TryParse(member.ProjectKey, out _))
            {
                throw new DomainValidationException(
                    $"'{member.ProjectKey}' is not a project key: a solution names each member by the "
                    + "26-character ULID minted at its ledger genesis, never by a project id.");
            }

            if (member.Name.IsBlank())
            {
                throw new DomainValidationException($"The solution member {member.ProjectKey} has no name.");
            }

            if (member.RepositoryUrl is not { IsAbsoluteUri: true })
            {
                throw new DomainValidationException(
                    $"The solution member '{member.Name}' has no absolute repository URL, which a node "
                    + "that has not joined it shows it by.");
            }
        }

        string? repeated = membership.Members
            .GroupBy(member => member.ProjectKey, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (repeated is not null)
        {
            throw new DomainValidationException($"The project key {repeated} is listed twice in one solution.");
        }
    }

    /// <summary>
    /// The one-solution rule: refuses writing <paramref name="incoming"/> onto a project whose
    /// current membership is open and carries a different key. A project in two solutions would put
    /// each solution's member names and repository URLs in front of the other solution's teammates.
    /// Rewriting the same solution is fine, a closed membership does not hold the project, and a
    /// membership that omits the project it is being written onto is refused (the project key is
    /// known, so the record cannot be about some other project).
    /// </summary>
    public static void RefuseSecondOpenSolution(ProjectAggregate project, SolutionMembership incoming)
    {
        if (project.Solution is { Holds: true } current
            && !current.Key.Equals(incoming.Key, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainConflictException(
                $"Project '{project.Name}' is already in the open solution '{current.Name}', and a project "
                + "belongs to at most one open solution: a project in two would put each solution's member "
                + "names and repository URLs in front of the other solution's teammates. Take it out of "
                + $"'{current.Name}' first: h9k solution unlink \"{current.Name}\" --project {project.Name}");
        }

        if (project.ProjectKey is { } projectKey
            && !incoming.Members.Any(member => member.ProjectKey.Equals(projectKey, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DomainValidationException(
                $"The solution '{incoming.Name}' does not list project '{project.Name}' ({projectKey}) "
                + "among its members, so it cannot be written onto it.");
        }
    }

    /// <summary>
    /// The member a project becomes. Refuses a project with no recorded project key (the key is what
    /// names it on every node) and one with no recorded repository URL (what a node that has not
    /// joined it shows it by).
    /// </summary>
    public static SolutionMember MemberOf(ProjectDetails project)
    {
        if (project.ProjectKey.IsBlank())
        {
            throw new DomainValidationException(
                $"Project '{project.Name}' has no recorded project key, and a solution names each member by "
                + $"it. Run h9k project join {project.Name} to read the key back from the ledger or, as the "
                + $"genesis owner of a ledger that predates keys, h9k project assign-key {project.Name} to mint it.");
        }

        return project.RepositoryUrl is { IsAbsoluteUri: true } url
            ? new SolutionMember(project.ProjectKey, project.Name, url)
            : throw new DomainValidationException(
                $"Project '{project.Name}' has no recorded repository URL, which a solution shows a member "
                + "by on a node that has not joined it. A project registered with only --repo <path> records "
                + "none; register it with h9k project add --repo-url <url>.");
    }

    /// <summary>A new open solution over <paramref name="members"/>.</summary>
    public static SolutionMembership Create(string key, string name, IReadOnlyList<SolutionMember> members)
    {
        SolutionMembership created = new(key, name.Trim(), SolutionState.Open, null, members);
        Validate(created);
        return created;
    }

    /// <summary>The solution with <paramref name="added"/> in it; refused on a closed solution or a project already in it.</summary>
    public static SolutionMembership Link(SolutionMembership current, SolutionMember added)
    {
        RefuseClosed(current, "link a project into");
        if (current.Members.Any(member => member.ProjectKey.Equals(added.ProjectKey, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DomainConflictException($"Project '{added.Name}' is already in the solution '{current.Name}'.");
        }

        SolutionMembership linked = current with { Members = [.. current.Members, added] };
        Validate(linked);
        return linked;
    }

    /// <summary>
    /// The solution without the member named <paramref name="projectKey"/>. Refused when it is not a
    /// member, and when it is the last one: a solution no project carries would be gone without a
    /// word, so closing it is the explicit act.
    /// </summary>
    public static SolutionMembership Unlink(SolutionMembership current, string projectKey)
    {
        SolutionMember removed = current.Members.FirstOrDefault(
                member => member.ProjectKey.Equals(projectKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new DomainValidationException(
                $"The project {projectKey} is not a member of the solution '{current.Name}': "
                + $"{string.Join(", ", current.Members.Select(member => member.Name))}.");
        if (current.Members.Count == 1)
        {
            throw new DomainValidationException(
                $"'{removed.Name}' is the last member of the solution '{current.Name}'; unlinking it would "
                + $"leave a solution no project carries. End it explicitly: h9k solution close \"{current.Name}\" "
                + "--reason \"<why>\"");
        }

        return current with { Members = [.. current.Members.Where(member => member != removed)] };
    }

    /// <summary>The solution under <paramref name="newName"/>, which the caller has already checked is not another solution's.</summary>
    public static SolutionMembership Rename(SolutionMembership current, string newName)
    {
        SolutionMembership renamed = current with { Name = newName.Trim() };
        Validate(renamed);
        return renamed;
    }

    /// <summary>The solution closed with <paramref name="reason"/>: an explicit human act, never automatic, and terminal.</summary>
    public static SolutionMembership Close(SolutionMembership current, string? reason)
    {
        RefuseClosed(current, "close");
        SolutionMembership closed = current with { State = SolutionState.Closed, CloseReason = reason?.Trim() };
        Validate(closed);
        return closed;
    }

    /// <summary>
    /// Which members of <paramref name="membership"/> this node cannot rewrite: no registered project
    /// carries the member's key, or the one that does is not joined to its own ledger here.
    /// <paramref name="rootFingerprintOf"/> answers an owner's claimed root fingerprint, the input
    /// <see cref="ProjectJoinStatus.NotJoined"/> reads.
    /// </summary>
    public static IReadOnlyList<SolutionMember> MembersNotHeldHere(
        SolutionMembership membership, IReadOnlyList<ProjectDetails> projects, Func<Guid, string?> rootFingerprintOf) =>
        [.. membership.Members.Where(member => HeldProject(member, projects, rootFingerprintOf) is null)];

    /// <summary>The registered, joined project on this node that carries <paramref name="member"/>'s key, or null.</summary>
    public static ProjectDetails? HeldProject(
        SolutionMember member, IReadOnlyList<ProjectDetails> projects, Func<Guid, string?> rootFingerprintOf) =>
        projects.FirstOrDefault(project =>
            project.ProjectKey is { } key
            && key.Equals(member.ProjectKey, StringComparison.OrdinalIgnoreCase)
            && !ProjectJoinStatus.NotJoined(project, rootFingerprintOf(project.OwnerId)));

    /// <summary>Refuses, naming every member this node cannot rewrite and how to get each one here.</summary>
    public static void RequireEveryMemberHeld(
        SolutionMembership membership, IReadOnlyList<ProjectDetails> projects, Func<Guid, string?> rootFingerprintOf, string verb)
    {
        IReadOnlyList<SolutionMember> missing = MembersNotHeldHere(membership, projects, rootFingerprintOf);
        if (missing.Count > 0)
        {
            throw new DomainConflictException(
                $"Cannot {verb} the solution '{membership.Name}': {string.Join(", ", missing.Select(member => $"'{member.Name}' ({member.RepositoryUrl})"))} "
                + $"{(missing.Count == 1 ? "is" : "are")} not registered and joined on this node. A solution command "
                + "rewrites the membership on every member in one step, so each has to be here: register it "
                + "(h9k project add --repo-url <url>) and join it (h9k project join <project>), then run this again.");
        }
    }

    /// <summary>
    /// The new members whose repository owner or organization differs from every member that
    /// precedes them (<paramref name="existing"/> first, then <paramref name="candidates"/> in order),
    /// so the command warns and asks before it groups repositories from different owners.
    /// </summary>
    public static IReadOnlyList<SolutionOwnerMismatch> OwnerMismatches(
        IReadOnlyList<SolutionMember> existing, IReadOnlyList<SolutionMember> candidates)
    {
        List<SolutionMember> preceding = [.. existing];
        List<SolutionOwnerMismatch> mismatches = [];
        foreach (SolutionMember candidate in candidates)
        {
            string? candidateOwner = RepositoryOwner(candidate.RepositoryUrl);
            string?[] existingOwners = [.. preceding.Select(member => RepositoryOwner(member.RepositoryUrl))];
            if (existingOwners.Length > 0
                && (candidateOwner is null
                    || existingOwners.All(owner => !string.Equals(owner, candidateOwner, StringComparison.OrdinalIgnoreCase))))
            {
                mismatches.Add(new SolutionOwnerMismatch(
                    candidate, candidateOwner, [.. existingOwners.Select(owner => owner ?? "an unknown owner").Distinct()]));
            }

            preceding.Add(candidate);
        }

        return mismatches;
    }

    /// <summary>
    /// The owner or organization a repository URL names, qualified by host (<c>github.com/acme</c>),
    /// so two hosts' same-named owners are not taken for one; null when the URL has no path to read one from.
    /// </summary>
    public static string? RepositoryOwner(Uri repositoryUrl) =>
        repositoryUrl.IsAbsoluteUri
        && repositoryUrl.Segments.Select(segment => segment.Trim('/')).FirstOrDefault(segment => segment.Length > 0) is { } owner
            ? $"{repositoryUrl.Host}/{owner}"
            : null;

    private static void RefuseClosed(SolutionMembership current, string verb)
    {
        if (current.State == SolutionState.Closed)
        {
            throw new DomainConflictException(
                $"The solution '{current.Name}' is closed ({current.CloseReason}), and a closed solution is "
                + $"final: it cannot {verb}. Start a new one with h9k solution add.");
        }
    }
}
