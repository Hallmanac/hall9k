using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A plan's daemon is about to spawn the refinement session for a draft. Saved under a version fence before the
/// spawn, so a crash cannot leave a live session with nothing on the stream, and a second sweep cannot start a
/// second one. It holds the session's id and the node that owns it, and nothing machine-local: the session's
/// directory is derived from the id.
/// <para>
/// Project-scoped, the shape of <see cref="DraftChallengeDispatched"/>, so every node can say "a refinement is
/// running on that node" and can end a dispatch that stood past the foreign-session ceiling with no outcome. It
/// names no plan: plan records never leave their node.
/// </para>
/// </summary>
public sealed record DraftRefinementDispatched(
    Guid Id,
    Guid SessionId,
    Guid NodeId,
    DateTimeOffset DispatchedAt,
    AgentModel Model,
    AgentEffort Effort,
    RefinementPurpose Purpose);
