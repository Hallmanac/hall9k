namespace Hall9k.Domain.Features.Message;

/// <summary>
/// The version-1 envelope shape every outbox file holds (idea 202383dc, M1a): one node's own
/// signed statement to another node, an owner, or the whole project. <see cref="Version"/> is
/// mandatory on the wire (<see cref="MessageEnvelopeCodec"/> checks it before ever attempting to
/// read the rest) but is not a field here — this type <em>is</em> version 1, and a later version
/// gets its own type rather than an optional field on this one.
/// <para>
/// <see cref="ProjectKey"/> (idea 202383dc, M2; Brian's ruling 2026-09-17) is the project's own
/// generated key — the 26-character ULID <c>h9k project join</c> mints once at genesis, never a
/// local project id (which differs per install for the identical shared project) and never the
/// genesis owner's own root fingerprint (which two projects sharing one genesis owner would
/// share too) — stamped in fresh at flush time from that project's own
/// <c>Hall9k.Connectors.Trust.TrustChain.ProjectKey</c>. Optional, defaulting to null, so every
/// envelope <see cref="MessageEnvelopeCodec"/> already encoded before M2 shipped still round-trips:
/// an outbox ref written before this change carries no such field at all, and one written before
/// this ruling may still carry the retired owner-fingerprint value — a reader on either must keep
/// reading it rather than refuse it as malformed (<c>MessageInbox</c>/<c>EventReplicationInbox</c>
/// resolve the local project by this key, and read null or an unrecognized value as "no opinion",
/// never as a mismatch to refuse).
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
