namespace Hall9k.Domain.Features.Trust;

/// <summary>
/// This node's own message sweep saw a root-key rotation live in a project's own ledger (idea
/// 6be68ee2, PR B of the succession chain): <paramref name="RootFingerprint"/>'s own ranked key set
/// (<c>TrustedOwner.RootKeys</c>) now includes a key <paramref name="PromotedNodeId"/> introduced by
/// a validated <c>owners/&lt;root&gt;/rotations/&lt;n&gt;.yaml</c>. Persisted so <c>h9k status</c>
/// and the orchestrator feed can name the rotation without ever walking the ledger themselves — the
/// identical "write it, let a standing record carry it" shape
/// <see cref="UnverifiedLedgerWriteObserved"/> already uses for the sibling trust fact. Never a
/// team-visible replicated event: every other node reaches the identical conclusion by reading the
/// ledger itself the next time its own sweep runs.
/// </summary>
public sealed record RootRotationObserved(Guid ProjectId, string RootFingerprint, Guid PromotedNodeId, DateTimeOffset At);
