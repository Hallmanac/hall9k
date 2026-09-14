using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Shared.Exceptions;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>Resolves a message's own short id (<see cref="TaskListCommand.ShortId"/> of its stream
/// id) back to the full <see cref="MessageDetails"/> row, scoped to messages this node has actually
/// received — <c>h9k message handle</c> is the only caller, and handling a message this node never
/// received makes no sense. Mirrors <see cref="TaskIdResolver"/>'s own fragment-match shape.</summary>
internal static class MessageIdResolver
{
    public static async Task<MessageDetails> ResolveReceivedAsync(
        IQuerySession session, string idOrFragment, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(idOrFragment, out Guid id))
        {
            MessageDetails? message = await session.LoadAsync<MessageDetails>(id, cancellationToken);
            return message is { ReceivedAt: not null }
                ? message
                : throw new DomainNotFoundException($"No received message '{idOrFragment}'.");
        }

        IReadOnlyList<MessageDetails> received = await session.Query<MessageDetails>()
            .Where(message => message.ReceivedAt != null)
            .ToListAsync(cancellationToken);

        string fragment = idOrFragment.Replace("-", "");
        if (fragment.Length == 0)
        {
            throw new DomainValidationException(
                $"'{idOrFragment}' has no characters to match a message by — pass a full id or a "
                + "non-empty fragment of one.");
        }

        MessageDetails[] matches = [.. received
            .Where(message => message.Id.ToString("N").StartsWith(fragment, StringComparison.OrdinalIgnoreCase)
                            || message.Id.ToString("N").EndsWith(fragment, StringComparison.OrdinalIgnoreCase))];

        return matches switch
        {
            [MessageDetails single] => single,
            [] => throw new DomainNotFoundException($"No received message matches '{idOrFragment}'."),
            _ => throw new DomainConflictException($"'{idOrFragment}' is ambiguous ({matches.Length} matches) — use more characters."),
        };
    }
}
