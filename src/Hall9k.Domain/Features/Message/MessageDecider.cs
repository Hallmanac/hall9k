using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Message;

public static class MessageDecider
{
    public static MessageQueued Queue(
        Guid fromNodeId, long seq, Guid projectId, string fromOwner, MessageAudience to, string? about,
        MessageKind kind, string body, DateTimeOffset at)
    {
        if (seq < 1)
        {
            throw new DomainValidationException("A message's seq starts at 1 and only ever grows.");
        }

        if (projectId == Guid.Empty)
        {
            throw new DomainValidationException("A message needs the project it belongs to.");
        }

        return new MessageQueued(fromNodeId, seq, fromOwner, to.Value, about, kind.Value, body, at, projectId);
    }

    public static MessageSent Send(Guid fromNodeId, long seq, Guid projectId, DateTimeOffset at)
    {
        if (seq < 1)
        {
            throw new DomainValidationException("A message's seq starts at 1 and only ever grows.");
        }

        if (projectId == Guid.Empty)
        {
            throw new DomainValidationException("A sent message needs the project it belongs to.");
        }

        return new MessageSent(fromNodeId, seq, at, projectId);
    }

    public static MessageSendFailed FailSend(MessageEnvelopeV1 envelope, Guid projectId, string reason, DateTimeOffset at)
    {
        if (reason.IsBlank())
        {
            throw new DomainValidationException("A failed send needs the reason it failed.");
        }

        if (projectId == Guid.Empty)
        {
            throw new DomainValidationException("A failed send needs the project it belongs to.");
        }

        return new MessageSendFailed(
            envelope.FromNode, envelope.Seq, envelope.FromOwner, envelope.To.Value, envelope.About,
            envelope.Kind.Value, envelope.Body, reason, at, projectId);
    }

    public static MessageResent Resend(MessageAggregate message, Guid projectId, DateTimeOffset at)
    {
        if (!message.SendFailed)
        {
            throw new DomainValidationException(
                $"Message {message.FromNodeId}/{message.Seq} has not failed to send — only a failed send is resent.");
        }

        if (projectId == Guid.Empty)
        {
            throw new DomainValidationException("A resent message needs the project it belongs to.");
        }

        return new MessageResent(message.FromNodeId, message.Seq, at, projectId);
    }

    /// <summary>
    /// Never checks for a duplicate itself — a caller (<c>Hall9k.Connectors.Messaging.MessageInbox</c>)
    /// decides that first, by reading the stream: a decider answers "what does receiving this mean",
    /// not "should this be recorded at all" (AGENTS.md: no business logic on the aggregate, and the
    /// same discipline applies to the decider's own idempotency questions here).
    /// </summary>
    public static MessageReceived Receive(
        Guid fromNodeId, Guid projectId, MessageEnvelopeV1 envelope, DateTimeOffset receivedAt)
    {
        if (projectId == Guid.Empty)
        {
            throw new DomainValidationException("A received message needs the local project it was read into.");
        }

        return new(
            fromNodeId, envelope.Seq, envelope.At, envelope.FromOwner, envelope.To.Value,
            envelope.About, envelope.Kind.Value, envelope.Body, receivedAt, projectId);
    }

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
