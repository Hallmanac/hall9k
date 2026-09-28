namespace Hall9k.Domain.Features.Project.Events;

/// <summary>
/// This node's own operator vetted exactly this gate set (security review idea 6be68ee2,
/// process-injection finding 1, the local half; <see cref="GateSetAcceptance"/> is the decision
/// this event feeds). Carries the accepted <see cref="VerifyCommand"/> list itself, not merely its
/// fingerprint — <see cref="VerifyCommand.Fingerprint"/> is derived from it wherever a comparison
/// is needed, so this event stays the one record of what was actually accepted, readable on its
/// own terms (h9k project show, an audit) rather than only as an opaque hash.
/// <para>
/// Node-scoped, deliberately: acceptance is a fact about this install alone. A set that arrived by
/// replication, or one changed on this owner's own other node, holds here regardless of what any
/// other node of this owner has accepted — the whole point of the feature (Brian's own accepted
/// cost: a gate change on one of his nodes holds his other node until he runs accept-gates there
/// too). Recorded by three sources: <c>h9k project accept-gates</c> (an explicit review); the same
/// <c>SaveChangesAsync</c> as <c>ProjectSettingsChanged</c>/<c>ProjectTeamSettingsChanged</c> when
/// an operator changes gates locally with <c>h9k project set --verify</c> or
/// <c>--verify-gate-filter</c> (so a node can never hold itself on its own local change); and the
/// daemon's own one-time baseline at its first start after this feature ships, for a project that
/// already exists on this node with nothing accepted yet.
/// </para>
/// </summary>
public sealed record ProjectGateSetAccepted(
    Guid Id,
    IReadOnlyList<VerifyCommand> VerifyCommands,
    Guid AcceptedByOwnerId,
    DateTimeOffset AcceptedAt);
