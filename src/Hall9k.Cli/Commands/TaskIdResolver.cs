using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Marten;

namespace Hall9k.Cli.Commands;

internal static class TaskIdResolver
{
    /// <summary>Full guid, or an unambiguous fragment matched against either end (UUIDv7 tails differ).</summary>
    public static async Task<Guid> ResolveAsync(IQuerySession session, string idOrFragment, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(idOrFragment, out Guid id))
        {
            return id;
        }

        string fragment = idOrFragment.Replace("-", "");
        if (fragment.Length == 0)
        {
            throw new DomainValidationException(
                $"'{idOrFragment}' has no characters to match a task by — pass a full id or a "
                + "non-empty fragment of one.");
        }

        IReadOnlyList<TaskListItem> all = await session.Query<TaskListItem>().ToListAsync(cancellationToken);
        Guid[] matches = [.. all
            .Where(t => t.Id.ToString("N").StartsWith(fragment, StringComparison.OrdinalIgnoreCase)
                     || t.Id.ToString("N").EndsWith(fragment, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Id)];

        return matches switch
        {
            [Guid single] => single,
            [] => throw new DomainNotFoundException($"No task matches '{idOrFragment}'."),
            _ => throw new DomainConflictException($"'{idOrFragment}' is ambiguous ({matches.Length} matches) — use more characters."),
        };
    }
}
