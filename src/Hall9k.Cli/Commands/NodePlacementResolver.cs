using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Resolves <c>h9k task assign --node</c>'s own argument against the owner's known fleet for a
/// project (idea 202383dc: an owner can place a task on one of their own nodes) — this node's own
/// id always counts, and every other candidate has to be a node the project's own ledger currently
/// vouches for that owner (<c>TaskAssignCommand</c> builds the candidate list from
/// <c>TrustedOwner.Nodes</c> plus this node's own id before calling here). Kept pure and
/// I/O-free, mirroring <see cref="TaskIdResolver"/>'s own shape, so the refusal for an unknown node
/// is a plain unit test against a fixed candidate list rather than one that needs a ledger.
/// </summary>
internal static class NodePlacementResolver
{
    /// <summary>Full guid, or an unambiguous fragment matched against either end (mirrors <see cref="TaskIdResolver.ResolveAsync"/>).</summary>
    public static Guid Resolve(string idOrFragment, IReadOnlyCollection<Guid> fleet)
    {
        if (Guid.TryParse(idOrFragment, out Guid id))
        {
            return fleet.Contains(id)
                ? id
                : throw new DomainValidationException(
                    $"Node {id} is not vouched into this owner's fleet for this project — h9k node vouch {id} "
                    + "first, or omit --node to leave the task unplaced.");
        }

        string fragment = idOrFragment.Replace("-", "");
        if (fragment.Length == 0)
        {
            throw new DomainValidationException(
                $"'{idOrFragment}' has no characters to match a node by — pass a full id or a "
                + "non-empty fragment of one.");
        }

        Guid[] matches = [.. fleet
            .Where(candidate => candidate.ToString("N").StartsWith(fragment, StringComparison.OrdinalIgnoreCase)
                || candidate.ToString("N").EndsWith(fragment, StringComparison.OrdinalIgnoreCase))];

        return matches switch
        {
            [Guid single] => single,
            [] => throw new DomainNotFoundException(
                $"No node vouched into this owner's fleet for this project matches '{idOrFragment}' — "
                + "h9k node vouch <id> first, or check h9k status for the id."),
            _ => throw new DomainConflictException(
                $"'{idOrFragment}' is ambiguous ({matches.Length} matches) — use more characters."),
        };
    }
}
