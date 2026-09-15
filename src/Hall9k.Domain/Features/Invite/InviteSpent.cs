namespace Hall9k.Domain.Features.Invite;

/// <summary>
/// The minting node's own sweep matched this invite's outstanding secret against a candidate node
/// file's proof and vouched it in (idea 202383dc, T2) — this invite's single use is now spent, on
/// this node's own local record; the corresponding
/// <c>owners/&lt;root&gt;/invites/&lt;id&gt;.yaml</c> ledger file is updated with <c>spent: true</c>
/// in the same sweep tick, so any other reader sees the identical fact.
/// </summary>
public sealed record InviteSpent(Guid InviteId, Guid ClaimedByNodeId, string ClaimedByRootFingerprint, DateTimeOffset SpentAt);
