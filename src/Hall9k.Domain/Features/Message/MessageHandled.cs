namespace Hall9k.Domain.Features.Message;

/// <summary>The receiver acted on a received message — an explicit human or CLI act (M1b), never
/// implied by storage alone.</summary>
public sealed record MessageHandled(Guid FromNodeId, long Seq, DateTimeOffset At);
