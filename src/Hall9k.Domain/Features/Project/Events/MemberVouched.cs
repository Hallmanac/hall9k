using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Project.Events;

/// <summary>
/// A root fingerprint was added or confirmed as this project's member (idea 202383dc, T1) — the
/// local record of the write behind it: this project's genesis member (self-written, the folder
/// empty), or a later addition an owner-role member's own node vouched. The ledger file
/// (<c>members/&lt;root&gt;.yaml</c> on <c>refs/hall9k/ledger/members</c>) is what every node's
/// chain read actually honors; this event is team-facing (project-scoped, travels with the
/// project's other streams once M2 replicates them), never the ledger read's own source of truth.
/// </summary>
public sealed record MemberVouched(Guid ProjectId, string RootFingerprint, ProjectMemberRole Role, DateTimeOffset IssuedAt);
