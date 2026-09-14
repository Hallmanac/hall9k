namespace Hall9k.Domain.Features.Message;

/// <summary>This node queued an envelope to send, recorded in its own store before any push
/// (idea 202383dc, M1b) — the daemon's own flush sweep is what actually lands it in the outbox
/// transport, batched with whatever else is queued by the time that sweep runs. Carries the
/// envelope's own content because nothing else remembers it until a flush succeeds: a restart
/// between this event and the next flush must still find every field needed to rebuild the exact
/// same wire bytes.</summary>
public sealed record MessageQueued(
    Guid FromNodeId, long Seq, string FromOwner, string To, string? About, string Kind, string Body,
    DateTimeOffset At);
