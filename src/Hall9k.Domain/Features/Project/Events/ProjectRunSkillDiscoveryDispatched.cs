using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Project.Events;

/// <summary>
/// The daemon spawned this project's run-skill discovery session (idea b9b09779, piece 4).
/// Recorded before the spawn, not after, the same "save the decision before the wait" discipline
/// <c>StackAssessmentDispatched</c> follows: a daemon restart mid-wait must find this fact on the
/// stream even though the session's own outcome is still unknown, or the next sweep tick would
/// read the request as still outstanding and dispatch a second session onto the same repository.
/// </summary>
/// <param name="HeadCommit">
/// The commit the session is composing against, read by the daemon with <c>git rev-parse HEAD</c>
/// in the worktree it is about to hand the session — never asked of the session itself, and blank
/// only when the read genuinely failed.
/// </param>
public sealed record ProjectRunSkillDiscoveryDispatched(
    Guid ProjectId,
    Guid SessionId,
    AgentModel? Model,
    string SessionName,
    string HeadCommit,
    DateTimeOffset DispatchedAt);
