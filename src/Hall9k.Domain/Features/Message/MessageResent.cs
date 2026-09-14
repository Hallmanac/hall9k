namespace Hall9k.Domain.Features.Message;

/// <summary>A previously failed send landed on retry, at the same seq it always held.</summary>
public sealed record MessageResent(Guid FromNodeId, long Seq, DateTimeOffset At);
