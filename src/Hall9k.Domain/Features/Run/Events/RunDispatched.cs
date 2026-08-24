using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// OwnerId is the node's owner AT dispatch time — frozen so the accountability chain
/// (PLAN.md §6.2) survives any future node ownership change.
/// Model is the resolved model this run's build session was spawned on, recorded as an
/// observed fact (Decisions Log #33), appended with a default so streams written before
/// the chain existed replay as Unknown rather than as a reconstruction (the log #30
/// discipline: the unobserved is admitted, never guessed).
/// RunDirectory is resolved once here, exactly as WorktreePath is: where the run's prompt,
/// stream, verify logs and review files live, under the owning task's directory when the
/// project has a home and the platform-global location otherwise (ruled 2026-08-23, backlog
/// 49). Every consumer reads this recorded value rather than rederiving it, so old runs (an
/// empty string, meaning "before this field existed") and new ones resolve through
/// <c>RunPaths.GlobalDirectory</c> and the recorded path respectively, with no special case at
/// the read site.
/// </summary>
public sealed record RunDispatched(
    Guid Id,
    Guid TaskId,
    Guid NodeId,
    Guid OwnerId,
    int LeaseGeneration,
    Guid SessionId,
    string WorktreePath,
    string Branch,
    ExecutorMode ExecutorMode,
    DateTimeOffset DispatchedAt,
    bool IsFollowUp = false,
    AgentModel? Model = null,
    string RunDirectory = "");
