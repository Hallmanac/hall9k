namespace Hall9k.Domain.Features.Message;

/// <summary>
/// The version-1 envelope shape every outbox file holds (idea 202383dc, M1a): one node's own
/// signed statement to another node, an owner, or the whole project. <see cref="Version"/> is
/// mandatory on the wire (<see cref="MessageEnvelopeCodec"/> checks it before ever attempting to
/// read the rest) but is not a field here — this type <em>is</em> version 1, and a later version
/// gets its own type rather than an optional field on this one.
/// <para>
/// <see cref="ProjectKey"/> (idea 202383dc, M2) is the project's own ledger-derived key — never a
/// local project id, which differs per install for the identical shared project — stamped in fresh
/// at flush time from that project's own <c>Hall9k.Connectors.Trust.TrustChain.GenesisRootFingerprint</c>.
/// Optional, defaulting to null, purely so every envelope <see cref="MessageEnvelopeCodec"/> already
/// encoded before M2 shipped still round-trips: an outbox ref written before this change carries no
/// such field at all, and a reader on it must keep reading it rather than refuse it as malformed.
/// </para>
/// </summary>
public sealed record MessageEnvelopeV1(
    long Seq,
    DateTimeOffset At,
    Guid FromNode,
    string FromOwner,
    MessageAudience To,
    string? About,
    MessageKind Kind,
    string Body,
    string? ProjectKey = null)
{
    public const int Version = 1;
}
