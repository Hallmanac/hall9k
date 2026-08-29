using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Shared.Exceptions;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Names an epic the way <see cref="TaskIdResolver"/> names a task: the full id, or an
/// unambiguous fragment matched against either end of it (UUIDv7 front-loads the timestamp,
/// so same-batch ids are told apart by their tails).
/// </summary>
internal static class EpicIdResolver
{
    public static async Task<Guid> ResolveAsync(
        IQuerySession session, string idOrFragment, CancellationToken cancellationToken) =>
        (await ResolveDetailsAsync(session, idOrFragment, cancellationToken)).Id;

    /// <summary>
    /// Resolves and validates an epic a task is about to join: it must exist, belong to the
    /// same project as the task, and be Open — "the only state a task can join"
    /// (<see cref="EpicState.Open"/>). Every join point (task add, task revise) calls this
    /// rather than <see cref="ResolveAsync"/> so a mistyped id, a cross-project id, or a closed
    /// epic is refused instead of recorded as a silent dangling reference.
    /// </summary>
    public static async Task<Guid> ResolveForMembershipAsync(
        IQuerySession session, string idOrFragment, Guid projectId, CancellationToken cancellationToken)
    {
        EpicDetails epic = await ResolveDetailsAsync(session, idOrFragment, cancellationToken);

        if (epic.ProjectId != projectId)
        {
            throw new DomainConflictException(
                $"Epic '{epic.Title}' belongs to a different project; a task can only join an epic "
                + "in its own project.");
        }

        if (epic.State != EpicState.Open)
        {
            throw new DomainConflictException(
                $"Epic '{epic.Title}' is {epic.State.Value} — Open is the only state a task can join.");
        }

        return epic.Id;
    }

    private static async Task<EpicDetails> ResolveDetailsAsync(
        IQuerySession session, string idOrFragment, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(idOrFragment, out Guid id))
        {
            return await session.LoadAsync<EpicDetails>(id, cancellationToken)
                ?? throw new DomainNotFoundException(
                    $"No epic {id}. See what exists: h9k epic list");
        }

        string fragment = idOrFragment.Replace("-", "");
        if (fragment.Length == 0)
        {
            throw new DomainValidationException(
                $"'{idOrFragment}' has no characters to match an epic by — pass a full id or a "
                + "non-empty fragment of one.");
        }

        IReadOnlyList<EpicDetails> all = await session.Query<EpicDetails>().ToListAsync(cancellationToken);
        EpicDetails[] matches = [.. all
            .Where(epic => epic.Id.ToString("N").StartsWith(fragment, StringComparison.OrdinalIgnoreCase)
                        || epic.Id.ToString("N").EndsWith(fragment, StringComparison.OrdinalIgnoreCase))];

        return matches switch
        {
            [EpicDetails single] => single,
            [] => throw new DomainNotFoundException(
                $"No epic matches '{idOrFragment}'. See what exists: h9k epic list"),
            _ => throw new DomainConflictException(
                $"'{idOrFragment}' is ambiguous ({matches.Length} matches) — use more characters."),
        };
    }

    /// <summary>Loads the aggregate behind a reference, for the commands that decide on state.</summary>
    public static async Task<EpicAggregate> LoadAsync(
        IDocumentSession session, string idOrFragment, CancellationToken cancellationToken)
    {
        Guid epicId = await ResolveAsync(session, idOrFragment, cancellationToken);
        return await session.Events.AggregateStreamAsync<EpicAggregate>(epicId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No epic {epicId}.");
    }
}
