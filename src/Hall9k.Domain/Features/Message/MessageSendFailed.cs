namespace Hall9k.Domain.Features.Message;

/// <summary>This node tried to push an envelope to its own outbox and the transport refused or
/// failed — the seq is still reserved on this stream; a later successful push is a
/// <see cref="MessageResent"/> of the same seq, not a new one.</summary>
public sealed record MessageSendFailed(Guid FromNodeId, long Seq, string Reason, DateTimeOffset At);
