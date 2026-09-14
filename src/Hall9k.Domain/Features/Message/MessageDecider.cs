using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Message;

public static class MessageDecider
{
    public static MessageQueued Queue(
        Guid fromNodeId, long seq, string fromOwner, MessageAudience to, string? about, MessageKind kind,
        string body, DateTimeOffset at)
    {
        if (seq < 1)
        {
            throw new DomainValidationException("A message's seq starts at 1 and only ever grows.");
        }

        return new MessageQueued(fromNodeId, seq, fromOwner, to.Value, about, kind.Value, body, at);
    }

    public static MessageSent Send(Guid fromNodeId, long seq, DateTimeOffset at)
    {
        if (seq < 1)
        {
            throw new DomainValidationException("A message's seq starts at 1 and only ever grows.");
        }

        return new MessageSent(fromNodeId, seq, at);
    }

    public static MessageSendFailed FailSend(MessageEnvelopeV1 envelope, string reason, DateTimeOffset at)
    {
        if (reason.IsBlank())
        {
            throw new DomainValidationException("A failed send needs the reason it failed.");
        }

        return new MessageSendFailed(
            envelope.FromNode, envelope.Seq, envelope.FromOwner, envelope.To.Value, envelope.About,
            envelope.Kind.Value, envelope.Body, reason, at);
    }

    public static MessageResent Resend(MessageAggregate message, DateTimeOffset at)
    {
        if (!message.SendFailed)
        {
            throw new DomainValidationException(
                $"Message {message.FromNodeId}/{message.Seq} has not failed to send — only a failed send is resent.");
        }

        return new MessageResent(message.FromNodeId, message.Seq, at);
    }

    /// <summary>
    /// Never checks for a duplicate itself — a caller (<c>Hall9k.Connectors.Messaging.MessageInbox</c>)
    /// decides that first, by reading the stream: a decider answers "what does receiving this mean",
    /// not "should this be recorded at all" (AGENTS.md: no business logic on the aggregate, and the
    /// same discipline applies to the decider's own idempotency questions here).
    /// </summary>
    public static MessageReceived Receive(Guid fromNodeId, MessageEnvelopeV1 envelope, DateTimeOffset receivedAt) =>
        new(
            fromNodeId, envelope.Seq, envelope.At, envelope.FromOwner, envelope.To.Value,
            envelope.About, envelope.Kind.Value, envelope.Body, receivedAt);

    public static MessageHandled Handle(MessageAggregate message, DateTimeOffset at)
    {
        if (message.ReceivedAt is null)
        {
            throw new DomainValidationException(
                $"Message {message.FromNodeId}/{message.Seq} has not been received yet — only a received message is handled.");
        }

        return new MessageHandled(message.FromNodeId, message.Seq, at);
    }
}
