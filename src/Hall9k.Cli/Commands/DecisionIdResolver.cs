using Hall9k.Domain.Features.Decision;
using Hall9k.Domain.Shared.Exceptions;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Names a decision the way <see cref="IdeaIdResolver"/> names an idea: the full id, or an
/// unambiguous fragment matched against either end of it. The id IS the citation key idea
/// d805fd8b replaced PLAN.md §16's sequential numbers with, so a fragment of it is what a human
/// or an agent actually types.
/// </summary>
internal static class DecisionIdResolver
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
                $"'{idOrFragment}' has no characters to match a decision by — pass a full id or a "
                + "non-empty fragment of one.");
        }

        IReadOnlyList<DecisionDetails> all = await session.Query<DecisionDetails>().ToListAsync(cancellationToken);
        Guid[] matches = [.. all
            .Where(decision => decision.Id.ToString("N").StartsWith(fragment, StringComparison.OrdinalIgnoreCase)
                            || decision.Id.ToString("N").EndsWith(fragment, StringComparison.OrdinalIgnoreCase))
            .Select(decision => decision.Id)];

        return matches switch
        {
            [Guid single] => single,
            [] => throw new DomainNotFoundException(
                $"No decision matches '{idOrFragment}'. See what has been recorded: h9k decide list"),
            _ => throw new DomainConflictException(
                $"'{idOrFragment}' is ambiguous ({matches.Length} matches) — use more characters."),
        };
    }

    /// <summary>Loads the aggregate behind a reference, for the commands that decide on state.</summary>
    public static async Task<DecisionAggregate> LoadAsync(
        IDocumentSession session, string idOrFragment, CancellationToken cancellationToken)
    {
        Guid decisionId = await ResolveAsync(session, idOrFragment, cancellationToken);
        return await session.Events.AggregateStreamAsync<DecisionAggregate>(decisionId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No decision {decisionId}.");
    }
}
