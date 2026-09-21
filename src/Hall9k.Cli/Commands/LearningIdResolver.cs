using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Shared.Exceptions;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Names a lesson the way <see cref="DecisionIdResolver"/> names a decision: the full id, or an
/// unambiguous fragment matched against either end of it. The short id is what a prompt-injected
/// lesson will carry once backlog 55's injection lands, so a fragment of it is what an agent
/// citing or retiring one types.
/// </summary>
internal static class LearningIdResolver
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
                $"'{idOrFragment}' has no characters to match a lesson by — pass a full id or a "
                + "non-empty fragment of one.");
        }

        IReadOnlyList<LearningDetails> all = await session.Query<LearningDetails>().ToListAsync(cancellationToken);
        Guid[] matches = [.. all
            .Where(learning => learning.Id.ToString("N").StartsWith(fragment, StringComparison.OrdinalIgnoreCase)
                            || learning.Id.ToString("N").EndsWith(fragment, StringComparison.OrdinalIgnoreCase))
            .Select(learning => learning.Id)];

        return matches switch
        {
            [Guid single] => single,
            [] => throw new DomainNotFoundException(
                $"No lesson matches '{idOrFragment}'. See what has been recorded: h9k learn list"),
            _ => throw new DomainConflictException(
                $"'{idOrFragment}' is ambiguous ({matches.Length} matches) — use more characters."),
        };
    }

    /// <summary>Loads the aggregate behind a reference, for the commands that decide on state.</summary>
    public static async Task<LearningAggregate> LoadAsync(
        IDocumentSession session, string idOrFragment, CancellationToken cancellationToken)
    {
        Guid learningId = await ResolveAsync(session, idOrFragment, cancellationToken);
        return await session.Events.AggregateStreamAsync<LearningAggregate>(learningId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No lesson {learningId}.");
    }
}
