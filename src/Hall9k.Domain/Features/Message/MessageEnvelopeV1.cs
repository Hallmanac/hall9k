namespace Hall9k.Domain.Features.Message;

/// <summary>
/// The version-1 envelope shape every outbox file holds (idea 202383dc, M1a): one node's own
/// signed statement to another node, an owner, or the whole project. <see cref="Version"/> is
/// mandatory on the wire (<see cref="MessageEnvelopeCodec"/> checks it before ever attempting to
/// read the rest) but is not a field here — this type <em>is</em> version 1, and a later version
/// gets its own type rather than an optional field on this one.
/// </summary>
public sealed record MessageEnvelopeV1(
    long Seq,
    DateTimeOffset At,
    Guid FromNode,
    string FromOwner,
    MessageAudience To,
    string? About,
    MessageKind Kind,
    string Body)
{
    public const int Version = 1;
}
