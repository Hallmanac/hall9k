using Hall9k.Domain.Features.Project;

namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A reviewer said yes to a review report's offer and the orchestrator stood this branch up
/// (<c>h9k task run-local</c>, idea b9b09779 piece 5). Opens the launch and records the plan it is
/// going to follow, before a single step has run — so a launch that dies part-way through still
/// leaves a record of what it set out to do, and a <c>--continue</c> arriving in a different
/// process has the same plan to resume against.
/// <para>
/// On the run's own stream rather than the task's, because the worktree is the run's
/// (<c>RunDispatched.WorktreePath</c>) and so is everything else recorded about processes on this
/// machine. <see cref="TaskId"/> rides along so the record still names the card.
/// </para>
/// </summary>
/// <param name="NodeId">
/// The node whose machine these processes are on. These events are node-scoped
/// (<c>EventScopeRegistry</c>) so they never travel, which makes this redundant as a filter and
/// keeps it as the audit fact it is: a pid means nothing without the machine it is on, and the
/// record would otherwise not say. The teardown sweep reads it rather than assuming every launch
/// it can see is its own — a second fence behind the scoping, not the only one.
/// </param>
/// <param name="Walker">
/// The <c>h9k</c> process about to walk the plan, as a process identity of its own (Decisions Log
/// #2's pid plus start time, the same as every other process this launch records). It is what
/// tells a launch whose first launch step has not been reached yet from one whose processes have
/// all gone: both records carry no processes, and only this says whether anybody is still walking.
/// Null on an event written before the walker was recorded, which reads as nobody walking — the
/// same answer that record gave then.
/// </param>
public sealed record LocalLaunchStarted(
    Guid Id,
    Guid LaunchId,
    Guid TaskId,
    Guid NodeId,
    string WorktreePath,
    IReadOnlyList<RunSkillStep> Steps,
    LocalLaunchProcess? Walker,
    DateTimeOffset StartedAt,
    Guid StartedByOwnerId);
