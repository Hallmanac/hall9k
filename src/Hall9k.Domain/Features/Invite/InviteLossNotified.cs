namespace Hall9k.Domain.Features.Invite;

/// <summary>
/// The minting node's own sweep found a candidate whose proof genuinely matches this invite's
/// secret after it was already spent by a different node (2026-09-26/27 security review, idea
/// 6be68ee2: "a second node that proves a spent secret is told so") — this node's own local record
/// that <see cref="LosingNodeId"/> was already notified, so a still-outstanding, unexpired invite is
/// never re-queued a second note on a later sweep tick for a loser it has already told.
/// </summary>
public sealed record InviteLossNotified(Guid InviteId, Guid LosingNodeId, DateTimeOffset NotifiedAt);
