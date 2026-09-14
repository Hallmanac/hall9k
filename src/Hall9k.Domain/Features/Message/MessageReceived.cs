namespace Hall9k.Domain.Features.Message;

/// <summary>
/// This node read an envelope addressed to itself, its owner, or the project, from a vouched
/// sender, and it is not a duplicate of one already recorded on this same (sender, seq) stream.
/// Carries the envelope's own fields flattened rather than the envelope type itself, so this event
/// never changes shape when a later envelope version adds a field <see cref="MessageEnvelopeV1"/>
/// never had.
/// </summary>
public sealed record MessageReceived(
    Guid FromNodeId,
    long Seq,
    DateTimeOffset SentAt,
    string FromOwnerFingerprint,
    string To,
    string? About,
    string Kind,
    string Body,
    DateTimeOffset ReceivedAt);
