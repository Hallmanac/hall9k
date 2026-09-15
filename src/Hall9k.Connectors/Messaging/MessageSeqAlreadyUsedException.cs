namespace Hall9k.Connectors.Messaging;

/// <summary>
/// <paramref name="seq"/> already has an envelope in <paramref name="fromNodeId"/>'s own outbox —
/// normally impossible, since <c>MessageOutbox</c> allocates each seq once from this node's own
/// local store before ever calling <see cref="IMessageTransport.SendAsync"/>. Reachable when an
/// earlier send's own push landed but the local record of it was never durably saved (a crash or a
/// cancellation between the two): the next send then computes the same seq again. Its own distinct
/// type, rather than a bare <see cref="InvalidOperationException"/>, exists so
/// <c>MessageOutbox</c> can catch exactly this case and move the new message to the next seq
/// instead of recording it as a failure against content it never actually held.
/// </summary>
public sealed class MessageSeqAlreadyUsedException(Guid fromNodeId, long seq, string message)
    : InvalidOperationException(message)
{
    public Guid FromNodeId { get; } = fromNodeId;

    public long Seq { get; } = seq;
}
