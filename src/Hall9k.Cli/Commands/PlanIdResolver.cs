using Hall9k.Domain.Features.Plan.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>Names a plan the way <see cref="TaskIdResolver"/> names a task: a full id, or an unambiguous fragment of either end of it. Plans are node-local, so only this node's are candidates.</summary>
internal static class PlanIdResolver
{
    public static async Task<PlanDetails> ResolveAsync(IQuerySession session, string idOrFragment, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(idOrFragment, out Guid id))
        {
            return await session.LoadAsync<PlanDetails>(id, cancellationToken)
                ?? throw new DomainNotFoundException($"No plan {id} on this node. Plans never leave the node they were started on; h9k plan list shows this node's.");
        }

        string fragment = idOrFragment.Replace("-", string.Empty);
        if (fragment.Length == 0)
        {
            throw new DomainValidationException(
                $"'{idOrFragment}' has no characters to match a plan by: pass a full id or a non-empty fragment of one.");
        }

        IReadOnlyList<PlanDetails> all = await session.Query<PlanDetails>().ToListAsync(cancellationToken);
        PlanDetails[] matches = [.. all.Where(plan =>
            plan.Id.ToString("N").StartsWith(fragment, StringComparison.OrdinalIgnoreCase)
            || plan.Id.ToString("N").EndsWith(fragment, StringComparison.OrdinalIgnoreCase))];
        return matches switch
        {
            [PlanDetails single] => single,
            [] => throw new DomainNotFoundException(
                $"No plan on this node matches '{idOrFragment}'. Plans never leave the node they were started on; h9k plan list shows this node's."),
            _ => throw new DomainConflictException($"'{idOrFragment}' is ambiguous ({matches.Length} matches): use more characters."),
        };
    }
}
