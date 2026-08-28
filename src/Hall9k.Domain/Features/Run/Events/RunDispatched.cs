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
/// 49). This is the dispatch-time record, not a live pointer: a task's directory can move
/// under <c>tasks/_archive/</c> and back after dispatch (backlog 51, PLAN.md §16 #84), so every
/// consumer resolves the run's current location through
/// <see cref="Hall9k.Domain.Infrastructure.Storage.RunPaths.ResolveCurrentDirectory"/> rather
/// than trusting this value verbatim; old runs (an empty string, meaning "before this field
/// existed") and new ones resolve through <c>RunPaths.GlobalDirectory</c> and the recorded path
/// respectively, with no special case at the read site.
/// PrReviewBaseRefName is the pull request's base branch as RunLauncher read it at dispatch, for
/// a pr-review task only (cycle-3 conformance finding): the adversarial lens's diff and the
/// conformance lens's own re-diff must compare against the identical base, and a second live
/// `gh pr view` minutes later can disagree with the first — the base moved, or the read itself
/// failed — with nothing on the stream to say which base either lens actually used. Recording it
/// once here is what lets the conformance lens reuse this run's own dispatch-time read instead of
/// taking a second one. Null for a pr-review run dispatched before this field existed, or for any
/// non-pr-review run, which never carries one.
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
    string RunDirectory = "",
    string? PrReviewBaseRefName = null);
