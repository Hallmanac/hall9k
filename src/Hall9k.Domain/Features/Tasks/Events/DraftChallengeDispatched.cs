using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The daemon is about to spawn the challenge session for a draft. Saved under a version fence before the
/// spawn, so a crash cannot leave a live session with nothing on the stream, and a second sweep cannot start a
/// second one. It holds the session's id and the node that owns it, and nothing machine-local: the session's
/// directory is derived from the id, and a node that does not hold it is told which node does.
/// <para>
/// Project-scoped, so every node can say "a challenge is running on that node" and can end a dispatch that
/// stood past the foreign-session ceiling with no outcome. A dispatch on the stream with nothing running it is
/// recorded as failed on the owning node's next start, never dispatched again.
/// </para>
/// </summary>
public sealed record DraftChallengeDispatched(
    Guid Id,
    Guid SessionId,
    Guid NodeId,
    DateTimeOffset DispatchedAt,
    AgentModel Model,
    AgentEffort Effort);
