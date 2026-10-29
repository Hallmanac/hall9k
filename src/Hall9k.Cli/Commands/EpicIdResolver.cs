using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Names an epic the way <see cref="TaskIdResolver"/> names a task: the full id, or an
/// unambiguous fragment matched against either end of it (UUIDv7 front-loads the timestamp,
/// so same-batch ids are told apart by their tails). A solution epic is one epic per member project
/// and is named by its shared solution epic key, a fragment of it, or the id of any copy.
/// </summary>
internal static class EpicIdResolver
{
    /// <summary>
    /// The epic a person named, as a group of the copies this node holds: one for a project epic, every
    /// held copy for a solution epic, whichever copy's id or the shared key was typed.
    /// </summary>
    public static async Task<EpicGroup> ResolveGroupAsync(
        IQuerySession session, string idOrFragment, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(idOrFragment, out Guid id))
        {
            EpicDetails epic = await session.LoadAsync<EpicDetails>(id, cancellationToken)
                ?? throw new DomainNotFoundException(
                    $"No epic {id}. See what exists: h9k epic list");
            return epic.SolutionEpicKey is { } key
                ? new EpicGroup([.. await session.Query<EpicDetails>()
                    .Where(candidate => candidate.SolutionEpicKey == key)
                    .ToListAsync(cancellationToken)])
                : new EpicGroup([epic]);
        }

        string fragment = idOrFragment.Replace("-", "");
        if (fragment.Length == 0)
        {
            throw new DomainValidationException(
                $"'{idOrFragment}' has no characters to match an epic by — pass a full id or a "
                + "non-empty fragment of one.");
        }

        IReadOnlyList<EpicDetails> all = await session.Query<EpicDetails>().ToListAsync(cancellationToken);
        EpicGroup[] matches = [.. EpicGroup.Of(all).Where(group => Names(group, fragment))];

        return matches switch
        {
            [EpicGroup single] => single,
            [] => throw new DomainNotFoundException(
                $"No epic matches '{idOrFragment}'. See what exists: h9k epic list"),
            _ => throw new DomainConflictException(
                $"'{idOrFragment}' is ambiguous ({matches.Length} matches) — use more characters."),
        };
    }

    /// <summary>What a person types for an epic: a project epic's short id, a solution epic's shared key shortened to its tail.</summary>
    public static string ReferenceOf(Guid epicId, string? solutionEpicKey) =>
        solutionEpicKey is { } key ? key[^8..] : TaskListCommand.ShortId(epicId);

    private static bool Names(EpicGroup group, string fragment) =>
        group.Copies.Any(copy => copy.Id.ToString("N").StartsWith(fragment, StringComparison.OrdinalIgnoreCase)
                              || copy.Id.ToString("N").EndsWith(fragment, StringComparison.OrdinalIgnoreCase))
        || (group.SolutionEpicKey is { } key
            && (key.StartsWith(fragment, StringComparison.OrdinalIgnoreCase)
                || key.EndsWith(fragment, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The ids of every copy this node holds of the epic a person named, for a filter that has to
    /// match a task in any member project's copy.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> ResolveIdsAsync(
        IQuerySession session, string idOrFragment, CancellationToken cancellationToken) =>
        [.. (await ResolveGroupAsync(session, idOrFragment, cancellationToken)).Copies.Select(copy => copy.Id)];

    /// <summary>
    /// Resolves and validates an epic a task is about to join: it must exist, belong to the
    /// same project as the task, and be Open — "the only state a task can join"
    /// (<see cref="EpicState.Open"/>). Every join point (task add, task revise) calls this
    /// rather than <see cref="ResolveGroupAsync"/> so a mistyped id, a cross-project id, or a closed
    /// epic is refused instead of recorded as a silent dangling reference.
    /// <para>
    /// A solution epic is open to tasks from any member project: the task joins its own project's copy,
    /// whichever copy's id or the shared key was named. A task from a project that is not a member of
    /// the epic's solution is refused, and so is a member with no copy on this node.
    /// </para>
    /// </summary>
    public static async Task<Guid> ResolveForMembershipAsync(
        IQuerySession session, string idOrFragment, Guid projectId, CancellationToken cancellationToken)
    {
        EpicGroup group = await ResolveGroupAsync(session, idOrFragment, cancellationToken);

        if (!group.IsSolutionEpic)
        {
            EpicDetails epic = group.First;
            if (epic.ProjectId != projectId)
            {
                throw new DomainConflictException(
                    $"Epic '{epic.Title}' belongs to a different project; a task can only join an epic "
                    + "in its own project.");
            }

            return OpenOrRefuse(epic.Title, epic.State, epic.Id);
        }

        string solutionKey = group.SolutionKey ?? string.Empty;
        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(projectId, cancellationToken);
        if (project?.Solution is not { Holds: true } membership
            || !membership.Key.Equals(solutionKey, StringComparison.OrdinalIgnoreCase))
        {
            SolutionContext context = await SolutionContext.LoadAsync(session, cancellationToken);
            string solution = context.ViewOf(solutionKey)?.Membership.Name is { } name ? $"'{name}'" : "a solution";
            throw new DomainConflictException(
                $"Epic '{group.Title}' is a solution epic of {solution}, and a task joins it only from a project "
                + $"that is a member of that solution; {(project is null ? "this task's project" : $"'{project.Name}'")} is not. "
                + "Join an epic of the project's own, or bring the project in with h9k solution link.");
        }

        EpicDetails copy = group.CopyIn(projectId)
            ?? throw new DomainConflictException(
                $"Project '{project.Name}' holds no copy of the solution epic '{group.Title}' on this node: a copy is "
                + "made in each member project the node that created the epic holds, or that a link later gave it. "
                + "Join an epic of the project's own, or ask the node that created it.");

        return OpenOrRefuse(group.Title, group.State == EpicState.Open ? copy.State : group.State, copy.Id);
    }

    private static Guid OpenOrRefuse(string title, EpicState state, Guid id) =>
        state == EpicState.Open
            ? id
            : throw new DomainConflictException(
                $"Epic '{title}' is {state.Value} — Open is the only state a task can join.");

    /// <summary>Loads the aggregate of every copy behind a reference, for the commands that decide on state.</summary>
    public static async Task<IReadOnlyList<EpicAggregate>> LoadAsync(
        IDocumentSession session, string idOrFragment, CancellationToken cancellationToken)
    {
        EpicGroup group = await ResolveGroupAsync(session, idOrFragment, cancellationToken);
        List<EpicAggregate> copies = [];
        foreach (EpicDetails copy in group.Copies.OrderBy(copy => copy.AddedAt))
        {
            copies.Add(await session.Events.AggregateStreamAsync<EpicAggregate>(copy.Id, token: cancellationToken)
                ?? throw new DomainNotFoundException($"No epic {copy.Id}."));
        }

        return copies;
    }
}
