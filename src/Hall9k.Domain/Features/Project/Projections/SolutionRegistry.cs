using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Project.Projections;

/// <summary>
/// One solution as this node knows it: the freshest membership any of its listed projects carries,
/// and the projects carrying it. <see cref="Inconsistent"/> names the carriers the record cannot
/// vouch for: one whose copy differs from <see cref="Membership"/> (a rewrite still replicating, or
/// a teammate's copy that disagrees) and one <see cref="Membership"/> does not list.
/// </summary>
public sealed record SolutionView(
    SolutionMembership Membership, IReadOnlyList<ProjectDetails> HeldProjects, IReadOnlyList<ProjectDetails> Inconsistent)
{
    /// <summary>
    /// Whether <paramref name="project"/> may be read as a member of this solution: it does not
    /// carry another one. A project that carries no solution yet still counts (a node that has not
    /// received the record), but one that has since joined another solution, which a closed solution
    /// allows, no longer belongs on this solution's board or under its filter.
    /// </summary>
    public bool NotInAnotherSolution(ProjectDetails project) =>
        project.Solution is not { } carried || carried.Key.Equals(Membership.Key, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads the solutions on this node. There is no Solution document: a solution is the membership
/// its member projects carry, so this groups the registered projects by membership key. A node that
/// holds any one member knows the whole solution from the record on it. Two members can briefly
/// carry different copies (a rewrite replicates member by member); the copy stamped latest wins, the
/// same last-writer rule every team setting follows. Only a copy carried by a project it lists
/// can win, so a project the solution does not name never speaks for it, and the carriers that
/// disagree with the winner are reported (<see cref="SolutionView.Inconsistent"/>) so a command
/// that would copy the winner onto every member can refuse instead of spreading one teammate's copy.
/// </summary>
public static class SolutionRegistry
{
    public static IReadOnlyList<SolutionView> Read(IEnumerable<ProjectDetails> projects)
    {
        List<(ProjectDetails Project, SolutionMembership Membership)> carriers = [];
        foreach (ProjectDetails project in projects)
        {
            if (project.Solution is { } membership)
            {
                carriers.Add((project, membership));
            }
        }

        List<SolutionView> views = [];
        foreach (IGrouping<string, (ProjectDetails Project, SolutionMembership Membership)> group in
                 carriers.GroupBy(carrier => carrier.Membership.Key, StringComparer.OrdinalIgnoreCase))
        {
            (ProjectDetails Project, SolutionMembership Membership)[] speakers =
                [.. group.Where(carrier => Lists(carrier.Membership, carrier.Project))];
            if (speakers.Length == 0)
            {
                continue;
            }

            SolutionMembership winner = speakers
                .OrderByDescending(carrier => carrier.Project.TeamSettingStamps.GetValueOrDefault(nameof(ProjectDetails.Solution)))
                .ThenByDescending(carrier => carrier.Membership.Members.Count)
                .First().Membership;
            views.Add(new SolutionView(
                winner,
                [.. group.Select(carrier => carrier.Project).OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)],
                [.. group
                    .Where(carrier => !Lists(winner, carrier.Project) || !carrier.Membership.Agrees(winner))
                    .Select(carrier => carrier.Project)
                    .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)]));
        }

        return [.. views.OrderBy(view => view.Membership.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static bool Lists(SolutionMembership membership, ProjectDetails project) =>
        project.ProjectKey is { } key
        && membership.Members.Any(member => member.ProjectKey.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Names a solution the way <c>ProjectResolver</c> names a project: the exact name
    /// (case-insensitive), else a key prefix. Failing is self-correcting: no match lists what exists,
    /// ambiguity names the candidates.
    /// </summary>
    public static SolutionView Match(IReadOnlyList<SolutionView> solutions, string nameOrKeyPrefix)
    {
        if (solutions.Count == 0)
        {
            throw new DomainNotFoundException(
                "No solutions exist on this node yet. Group projects: h9k solution add <name> --project <project> --project <project>");
        }

        string wanted = nameOrKeyPrefix.Trim();
        SolutionView[] byName = [.. solutions.Where(view => view.Membership.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))];
        SolutionView[] matches = byName.Length > 0
            ? byName
            : wanted.Length == 0
                ? []
                : [.. solutions.Where(view => view.Membership.Key.StartsWith(wanted, StringComparison.OrdinalIgnoreCase))];

        return matches switch
        {
            [SolutionView single] => single,
            [] => throw new DomainNotFoundException(
                $"No solution matches '{nameOrKeyPrefix}': a solution is named by its name or a prefix of its key. "
                + $"Known: {string.Join(", ", solutions.Select(view => view.Membership.Name))}. See them with h9k solution list."),
            _ => throw new DomainConflictException(
                $"'{nameOrKeyPrefix}' is ambiguous ({matches.Length} solutions): "
                + $"{string.Join(", ", matches.Select(view => $"{view.Membership.Name} ({view.Membership.Key})"))}. "
                + "Use the exact name or more of the key."),
        };
    }

    /// <summary>
    /// Refuses a solution name another solution already carries (case-insensitive, closed ones
    /// included), the rule a project name follows: <paramref name="ownKey"/> exempts the solution being renamed.
    /// </summary>
    public static void RequireUniqueName(IReadOnlyList<SolutionView> solutions, string name, string? ownKey)
    {
        if (name.IsBlank())
        {
            throw new DomainValidationException("A solution requires a name, the way a project does.");
        }

        if (solutions.Any(view =>
                view.Membership.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)
                && !view.Membership.Key.Equals(ownKey, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DomainConflictException($"A solution named '{name.Trim()}' already exists on this node.");
        }
    }
}
