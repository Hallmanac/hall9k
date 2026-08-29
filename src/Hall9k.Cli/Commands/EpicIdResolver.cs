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
        IQuerySession session, string idOrFragment, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(idOrFragment, out Guid id))
        {
            return id;
        }

        string fragment = idOrFragment.Replace("-", "");
        IReadOnlyList<EpicDetails> all = await session.Query<EpicDetails>().ToListAsync(cancellationToken);
        Guid[] matches = [.. all
            .Where(epic => epic.Id.ToString("N").StartsWith(fragment, StringComparison.OrdinalIgnoreCase)
                        || epic.Id.ToString("N").EndsWith(fragment, StringComparison.OrdinalIgnoreCase))
            .Select(epic => epic.Id)];

        return matches switch
        {
            [Guid single] => single,
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
