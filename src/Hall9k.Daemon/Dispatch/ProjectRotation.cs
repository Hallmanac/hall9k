using Hall9k.Domain.Features.Project;

namespace Hall9k.Daemon.Dispatch;

/// <summary>
/// One queued task as the claim loop needs it: the task, the project whose cap and tier it
/// answers to, and whether a human marked it queue-first (Decisions Log #127).
/// </summary>
/// <param name="QueueFirst">
/// The human's own per-task override, which is why it outranks the rotation entirely: it is more
/// specific than a tier (one task, not a project) and it clears itself the moment this claim
/// commits, so it buys exactly one slot and cannot silently persist as a policy.
/// </param>
public sealed record QueuedCandidate(Guid TaskId, Guid ProjectId, bool QueueFirst);

/// <summary>
/// Why a project won the free slot, in the one sentence the claim logs (Decisions Log #141). An
/// in-process decision outcome, never persisted (AGENTS.md: enums only for unpersisted in-process
/// outcomes) — what is persisted is the tier on the project's own stream and the claim itself.
/// </summary>
public enum SlotReason
{
    /// <summary>The only project with eligible work this sweep — which is every sweep on a single-project node.</summary>
    OnlyEligibleProject,

    /// <summary>Longest unserved of the eligible projects in its tier: the rotation's own rule.</summary>
    LongestUnserved,

    /// <summary>Its tier outranks another eligible project's, so the rotation did not decide this slot.</summary>
    PriorityTier,

    /// <summary>A human marked this task queue-first, which takes the next free slot outright.</summary>
    QueueFirstMarker,
}

/// <summary>
/// Which project receives the next free slot, and the reason that rides the claim.
/// </summary>
/// <param name="Candidate">The winning project's own oldest eligible task — within a project the queue's order decides.</param>
/// <param name="Reason">Why this project won, for the claim's own log line.</param>
/// <param name="EligibleProjects">How many projects had eligible work when this slot was decided, so the line can say what the winner was chosen out of.</param>
/// <param name="LastServedAt">
/// When this node last dispatched for the winning project, or null when it has not since this
/// daemon started — the honest reading of a cold start's own rotation memory, never a
/// manufactured "served at process start" timestamp.
/// </param>
/// <param name="Priority">The winner's tier, named in the line when the tier is what decided the slot.</param>
public sealed record RotationSlot(
    QueuedCandidate Candidate,
    SlotReason Reason,
    int EligibleProjects,
    DateTimeOffset? LastServedAt,
    ProjectPriority Priority);

/// <summary>
/// How free run slots are shared across projects (Decisions Log #141): round-robin by default —
/// the eligible project longest unserved since its last dispatch wins the next slot, oldest task
/// first within it — with an optional priority tier that outranks the rotation while it has
/// eligible work and releases itself the moment its queue drains.
/// <para>
/// Pure and one decision at a time: it answers "who gets THIS slot" and is asked again for the
/// next one, because a sweep's own claims change who is eligible as it goes (a project reaching
/// its cap, a project that has now been served). That shape is also what makes the whole rule
/// testable without a database, the discipline <see cref="NodeLoad.LiveSlots"/> already follows.
/// </para>
/// <para>
/// Nothing here preempts anything. Ordering decides only who receives the next <em>free</em>
/// slot; a live run always completes, whatever tier or rotation state changes underneath it
/// (AGENTS.md's never-auto-kill restraint, the same rule <see cref="ProjectRunCeiling"/> holds
/// to). Nor does it defer anything on its own: a project that loses a slot is turned away by the
/// node ceiling or by its own cap, which is what the deferral log names, so the rotation adds no
/// third cause an operator would have to learn.
/// </para>
/// </summary>
public static class ProjectRotation
{
    /// <summary>
    /// The next free slot's winner, or null when no queued candidate's project can admit a claim.
    /// <para>
    /// The order of decisions, which is also the order of tie-breaks and the reason two operators
    /// reading the same state predict the same next claim:
    /// </para>
    /// <list type="number">
    /// <item><b>Eligibility.</b> A candidate counts only while its project admits another run
    /// (<see cref="ProjectRunCeiling.Admits"/>, counting what this sweep has already claimed for
    /// it). A project at its cap, paused at 0, or with nothing queued is simply not a member this
    /// time round — it never consumes a turn, so the rotation cannot idle on a project that
    /// cannot start work.</item>
    /// <item><b>The queue-first marker.</b> A human's explicit "this one next" outranks the
    /// rotation, and clears itself as the claim commits (Decisions Log #127), so it buys one slot
    /// rather than standing policy.</item>
    /// <item><b>Tier.</b> The highest tier with an eligible project takes the slot
    /// (<see cref="ProjectPriority"/>); the rotation runs within that tier. When the tier's queue
    /// drains it stops being eligible, which is the whole of what makes focus self-releasing —
    /// no operator action, in deliberate contrast to a cap of 0.</item>
    /// <item><b>Longest unserved.</b> Within the tier, the project this node dispatched for
    /// longest ago wins; a project it has not dispatched for at all since this daemon started
    /// counts as unserved and so outranks every project that has been.</item>
    /// <item><b>Queue order.</b> Two projects equally unserved — every project on a cold start,
    /// which is the first-dispatch case — are separated by their own oldest queued task, in the
    /// same order the queue always had (the queue-first marker, then oldest assignment, then
    /// oldest added). That makes a single-project node's behaviour identical to plain oldest-first,
    /// and a cold start's first claim identical to what it would have been before any of this
    /// existed.</item>
    /// </list>
    /// </summary>
    /// <param name="remaining">The queue's still-unclaimed candidates, in the order the queue is served.</param>
    /// <param name="load">This sweep's measurement: each project's cap and its tier.</param>
    /// <param name="claimedThisSweep">How many runs this sweep has already claimed per project, so a cap fills as it goes.</param>
    /// <param name="lastServed">When this node last dispatched for each project; a project absent from it is unserved.</param>
    public static RotationSlot? NextSlot(
        IReadOnlyList<QueuedCandidate> remaining,
        DispatchLoad load,
        IReadOnlyDictionary<Guid, int> claimedThisSweep,
        IReadOnlyDictionary<Guid, DateTimeOffset> lastServed)
    {
        // The head candidate of each eligible project, in queue order. One entry per project,
        // because the rotation decides between projects and the queue's own order already decides
        // within one — and because the head's position is what breaks a tie between two equally
        // unserved projects.
        List<QueuedCandidate> heads = [];
        foreach (QueuedCandidate candidate in remaining)
        {
            if (!load.Project(candidate.ProjectId).Ceiling
                    .Admits(claimedThisSweep.GetValueOrDefault(candidate.ProjectId))
                || heads.Any(head => head.ProjectId == candidate.ProjectId))
            {
                continue;
            }

            heads.Add(candidate);
        }

        if (heads.Count == 0)
        {
            return null;
        }

        if (FirstQueueFirstMarked(remaining, heads) is { } marked)
        {
            return Slot(marked, SlotReason.QueueFirstMarker, heads.Count, load, lastServed);
        }

        int topTier = heads.Max(head => load.Project(head.ProjectId).Priority.Tier);

        // OrderBy is a stable sort, so two projects with the same last-served instant — every
        // project on a cold start — keep the queue order `heads` was built in.
        QueuedCandidate winner = heads
            .Where(head => load.Project(head.ProjectId).Priority.Tier == topTier)
            .OrderBy(head => lastServed.TryGetValue(head.ProjectId, out DateTimeOffset servedAt)
                ? servedAt
                : DateTimeOffset.MinValue)
            .First();

        SlotReason reason = heads.Count == 1
            ? SlotReason.OnlyEligibleProject
            : heads.Any(head => load.Project(head.ProjectId).Priority.Tier < topTier)
                ? SlotReason.PriorityTier
                : SlotReason.LongestUnserved;

        return Slot(winner, reason, heads.Count, load, lastServed);
    }

    /// <summary>
    /// The oldest queue-first marked candidate whose project is eligible. Read off the whole
    /// remaining queue rather than off the per-project heads: a project can hold an unmarked older
    /// task ahead of a marked one, and the marker's promise is the next free slot regardless of
    /// age, which the head list would quietly downgrade to "regardless of age, in other projects".
    /// </summary>
    private static QueuedCandidate? FirstQueueFirstMarked(
        IReadOnlyList<QueuedCandidate> remaining, IReadOnlyCollection<QueuedCandidate> heads) =>
        remaining.FirstOrDefault(candidate =>
            candidate.QueueFirst && heads.Any(head => head.ProjectId == candidate.ProjectId));

    private static RotationSlot Slot(
        QueuedCandidate candidate,
        SlotReason reason,
        int eligibleProjects,
        DispatchLoad load,
        IReadOnlyDictionary<Guid, DateTimeOffset> lastServed) =>
        new(
            candidate,
            reason,
            eligibleProjects,
            lastServed.TryGetValue(candidate.ProjectId, out DateTimeOffset servedAt) ? servedAt : null,
            load.Project(candidate.ProjectId).Priority);
}
