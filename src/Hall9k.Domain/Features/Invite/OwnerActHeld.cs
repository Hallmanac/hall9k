using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Invite;

/// <summary>
/// An owner-role member write, held on the root for its own human to approve (idea 6be68ee2,
/// companion 1bb803e1: "an owner-role write waits for approval on the root") - <see cref="Id"/> is
/// the <c>owner-act-request</c> message's own stream id, so one hold ties back to exactly the
/// request that produced it, and a resend of that same request (byte-identical, same message id)
/// never starts a second hold for it.
/// </summary>
public sealed record OwnerActHeld(
    Guid Id, Guid ProjectId, string ProjectRepositoryPath, Guid InviteId, Guid RequesterNodeId,
    string CandidateOwnerFingerprint, ProjectMemberRole Role, DateTimeOffset IssuedAt, DateTimeOffset HeldAt);
