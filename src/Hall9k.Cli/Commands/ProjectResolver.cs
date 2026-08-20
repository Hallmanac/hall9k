using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Names a project the way <see cref="TaskIdResolver"/> names a task: the full id, the
/// exact name, or an unambiguous fragment of it. Both failure modes are self-correcting —
/// no match names what is registered, ambiguity names the candidates.
/// </summary>
internal static class ProjectResolver
{
    public static async Task<ProjectDetails> ResolveAsync(
        IQuerySession session, string nameOrFragment, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(nameOrFragment, out Guid id))
        {
            return await session.LoadAsync<ProjectDetails>(id, cancellationToken)
                ?? throw new DomainNotFoundException($"No project with id {id}.");
        }

        IReadOnlyList<ProjectDetails> projects = await session.Query<ProjectDetails>().ToListAsync(cancellationToken);
        return Match(projects, nameOrFragment);
    }

    /// <summary>Exact name first (a project named "hall9k" is never ambiguous with "hall9k-docs"), then fragment.</summary>
    public static ProjectDetails Match(IReadOnlyList<ProjectDetails> projects, string nameOrFragment)
    {
        if (projects.Count == 0)
        {
            throw new DomainNotFoundException(
                "No projects are registered yet. Register one: "
                + "h9k project add --name <name> --repo <path> [--base-branch <branch>]");
        }

        ProjectDetails[] exact = [.. projects.Where(p => p.Name.Equals(nameOrFragment, StringComparison.OrdinalIgnoreCase))];
        ProjectDetails[] matches = exact.Length > 0
            ? exact
            : [.. projects.Where(p => p.Name.Contains(nameOrFragment, StringComparison.OrdinalIgnoreCase))];

        return matches switch
        {
            [ProjectDetails single] => single,
            [] => throw new DomainNotFoundException(
                $"No project matches '{nameOrFragment}'. Registered: {Names(projects)}. "
                + $"See them with h9k project list, or register this one: h9k project add --name {nameOrFragment} --repo <path>"),
            _ => throw new DomainConflictException(
                $"'{nameOrFragment}' is ambiguous ({matches.Length} matches): {Names(matches)} — "
                + "use more characters, or the exact project name."),
        };
    }

    /// <summary>Candidate names, alphabetical and bounded: a wall of names teaches nothing.</summary>
    private static string Names(IReadOnlyList<ProjectDetails> projects)
    {
        const int Shown = 10;
        string[] names = [.. projects.Select(p => p.Name).Order(StringComparer.OrdinalIgnoreCase)];
        return names.Length <= Shown
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(Shown)) + $", and {names.Length - Shown} more";
    }
}
