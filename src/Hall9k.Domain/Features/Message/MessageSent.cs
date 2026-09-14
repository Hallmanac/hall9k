namespace Hall9k.Domain.Features.Message;

/// <summary>This node sent an envelope through the outbox transport (A1), landing signed at
/// <c>messages/&lt;seq&gt;.json</c> in its own outbox ref.</summary>
public sealed record MessageSent(Guid FromNodeId, long Seq, DateTimeOffset At);
