using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Shared.Exceptions;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Names an owner the way <see cref="ProjectResolver"/> names a project: the full id, the
/// exact name, or an unambiguous fragment of the name or email. Assignment is the one place
/// a human says whose nodes may run a task (Decisions Log #34), so guessing at an ambiguous
/// name is exactly the wrong thing to do — every failure names the candidates instead.
/// </summary>
internal static class OwnerResolver
{
    public static async Task<OwnerDetails> ResolveAsync(
        IQuerySession session, string nameOrFragment, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(nameOrFragment, out Guid id))
        {
            return await session.LoadAsync<OwnerDetails>(id, cancellationToken)
                ?? throw new DomainNotFoundException($"No owner with id {id}.");
        }

        IReadOnlyList<OwnerDetails> owners = await session.Query<OwnerDetails>().ToListAsync(cancellationToken);
        return Match(owners, nameOrFragment);
    }

    /// <summary>
    /// The platform's sole owner, or null when more than one is registered. Single-owner
    /// convenience is offered only where it cannot be wrong (Decisions Log #34).
    /// </summary>
    public static async Task<OwnerDetails?> SoleOwnerAsync(IQuerySession session, CancellationToken cancellationToken)
    {
        IReadOnlyList<OwnerDetails> owners = await session.Query<OwnerDetails>()
            .Take(2)
            .ToListAsync(cancellationToken);

        return owners is [OwnerDetails single] ? single : null;
    }

    public static OwnerDetails Match(IReadOnlyList<OwnerDetails> owners, string nameOrFragment)
    {
        if (owners.Count == 0)
        {
            throw new DomainNotFoundException(
                "No owners are registered yet — run any h9k command that touches the database first "
                + "(the first one registers this machine's owner and node).");
        }

        OwnerDetails[] exact = [.. owners.Where(o => o.Name.Equals(nameOrFragment, StringComparison.OrdinalIgnoreCase))];
        OwnerDetails[] matches = exact.Length > 0
            ? exact
            : [.. owners.Where(o =>
                o.Name.Contains(nameOrFragment, StringComparison.OrdinalIgnoreCase)
                || (o.Email?.Contains(nameOrFragment, StringComparison.OrdinalIgnoreCase) ?? false))];

        return matches switch
        {
            [OwnerDetails single] => single,
            [] => throw new DomainNotFoundException(
                $"No owner matches '{nameOrFragment}'. Registered: {Names(owners)}."),
            _ => throw new DomainConflictException(
                $"'{nameOrFragment}' is ambiguous ({matches.Length} matches): {Names(matches)} — "
                + "use more characters, the exact name, or the owner's id."),
        };
    }

    private static string Names(IReadOnlyList<OwnerDetails> owners) =>
        string.Join(", ", owners.Select(o => o.Name).Order(StringComparer.OrdinalIgnoreCase));
}
