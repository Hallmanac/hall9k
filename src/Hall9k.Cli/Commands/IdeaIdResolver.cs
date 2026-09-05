using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Shared.Exceptions;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Names an idea the way <see cref="TaskIdResolver"/> names a task: the full id, or an
/// unambiguous fragment matched against either end of it (UUIDv7 front-loads the timestamp,
/// so same-batch ids are told apart by their tails).
/// </summary>
internal static class IdeaIdResolver
{
    public static async Task<Guid> ResolveAsync(
        IQuerySession session, string idOrFragment, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(idOrFragment, out Guid id))
        {
            return id;
        }

        string fragment = idOrFragment.Replace("-", "");
        if (fragment.Length == 0)
        {
            throw new DomainValidationException(
                $"'{idOrFragment}' has no characters to match an idea by — pass a full id or a "
                + "non-empty fragment of one.");
        }

        IReadOnlyList<IdeaDetails> all = await session.Query<IdeaDetails>().ToListAsync(cancellationToken);
        Guid[] matches = [.. all
            .Where(idea => idea.Id.ToString("N").StartsWith(fragment, StringComparison.OrdinalIgnoreCase)
                        || idea.Id.ToString("N").EndsWith(fragment, StringComparison.OrdinalIgnoreCase))
            .Select(idea => idea.Id)];

        return matches switch
        {
            [Guid single] => single,
            [] => throw new DomainNotFoundException(
                $"No idea matches '{idOrFragment}'. See what has been captured: h9k idea list"),
            _ => throw new DomainConflictException(
                $"'{idOrFragment}' is ambiguous ({matches.Length} matches) — use more characters."),
        };
    }

    /// <summary>Loads the aggregate behind a reference, for the commands that decide on state.</summary>
    public static async Task<IdeaAggregate> LoadAsync(
        IDocumentSession session, string idOrFragment, CancellationToken cancellationToken)
    {
        Guid ideaId = await ResolveAsync(session, idOrFragment, cancellationToken);
        return await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No idea {ideaId}.");
    }
}
