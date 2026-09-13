using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Tasks;

namespace Hall9k.Daemon.Dispatch;

/// <summary>
/// One queued task as the claim loop needs it: the task, the project whose cap and tier it
/// answers to, whether a human marked it queue-first (Decisions Log #127), and its own
/// <see cref="TaskRank"/> (Decisions Log #187).
/// </summary>
/// <param name="QueueFirst">
/// The human's own per-task override, which is why it outranks the rotation entirely: it is more
/// specific than a tier (one task, not a project) and it clears itself the moment this claim
/// commits, so it buys exactly one slot and cannot silently persist as a policy.
/// </param>
/// <param name="Rank">
/// How far along this task is toward merging — a follow-up lap past its first pull request, a
/// retry or hand-back before any pull request, or a plain first claim — resolved off
/// <see cref="Hall9k.Domain.Features.Tasks.Projections.TaskListItem.Rank"/> once for the whole
/// sweep, in the same read that already carries this candidate's project and marker.
/// </param>
public sealed record QueuedCandidate(Guid TaskId, Guid ProjectId, bool QueueFirst, TaskRank Rank);

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
/// <param name="Candidate">
/// The winning project's own task that takes this slot — rank decides among that project's
/// eligible tasks before assignment age (Decisions Log #187), so this is not
/// always the project's own oldest one; see <see cref="RankBeatenTaskId"/>.
/// </param>
/// <param name="Reason">Why this project won, for the claim's own log line.</param>
/// <param name="EligibleProjects">How many projects had eligible work when this slot was decided, so the line can say what the winner was chosen out of.</param>
/// <param name="LastServedAt">
/// When this node last dispatched for the winning project, or null when it has not since this
/// daemon started — the honest reading of a cold start's own rotation memory, never a
/// manufactured "served at process start" timestamp.
/// </param>
/// <param name="Priority">The winner's tier, named in the line when the tier is what decided the slot.</param>
/// <param name="RankBeatenTaskId">
/// The oldest other eligible task of the winning project that this candidate's own rank passed
/// over, or null when nothing was beaten — either only one candidate was eligible in the project,
/// or the oldest one already carried the best rank, so age alone would have picked the same
/// winner. Named so the claim's log can state the rank decision as its own sentence, beside the
/// rotation's, exactly when the rank actually decided something (Decisions Log
/// #187).
/// </param>
public sealed record RotationSlot(
    QueuedCandidate Candidate,
    SlotReason Reason,
    int EligibleProjects,
    DateTimeOffset? LastServedAt,
    ProjectPriority Priority,
    Guid? RankBeatenTaskId = null);

/// <summary>
/// How free run slots are shared across projects (Decisions Log #141): round-robin by default —
/// the eligible project longest unserved since its last dispatch wins the next slot, its own rank
/// then oldest-assignment order deciding which of its tasks takes it (Decisions Log
/// #187) — with an optional priority tier that outranks the rotation while it has
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
    /// <item><b>Queue order, between projects.</b> Two projects equally unserved — every project
    /// on a cold start, which is the first-dispatch case — are separated by their own oldest
    /// queued task, in the same order the queue always had (the queue-first marker, then oldest
    /// assignment, then oldest added). That decides which project wins a cold start's first
    /// slot exactly as it always did; which of that project's own tasks is claimed is the next
    /// step, rank, below — so even a single-project node no longer dispatches plain
    /// oldest-first once one of its tasks outranks another.</item>
    /// <item><b>Rank, within the winning project.</b> Once a project has won the slot, which of
    /// its own eligible tasks actually takes it is decided by <see cref="TaskRank"/> before
    /// assignment age (Decisions Log #187): a follow-up lap on a task past its
    /// first pull request outranks a retry or hand-back before any pull request, which outranks a
    /// first claim — with the queue's own order (already assignment-age order within one project)
    /// breaking a tie inside one rank, exactly as it always has. This is the only step rank
    /// touches; it never reaches across projects; the queue-first marker above still bypasses it
    /// entirely, and a first claim marked queue-first still takes the slot ahead of a pending lap
    /// of any rank.</item>
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
        // The head candidate of each eligible project, in queue order (oldest assignment first).
        // One entry per project — the rotation decides between projects on this list, and the
        // head is what breaks a tie between two equally unserved ones — but the head is no longer
        // necessarily the task that ends up claimed once a project wins: rank decides that, below.
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
        QueuedCandidate projectHead = heads
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

        // The project is decided; which of its own tasks takes the slot is rank's turn. `remaining`
        // filtered to this one project is still in queue order (assignment-age ascending, since
        // that sub-order survives a stable global sort), so OrderBy(Rank) alone — a stable sort —
        // reproduces "oldest first" for two tasks of the same rank and only reorders across ranks.
        QueuedCandidate winner = remaining
            .Where(candidate => candidate.ProjectId == projectHead.ProjectId)
            .OrderBy(candidate => candidate.Rank)
            .First();

        // Beaten only when rank actually changed the outcome: the project's own oldest task
        // (projectHead) is not who won. Equal ranks never reach here — the stable sort above keeps
        // the oldest task first among equals, so it IS the winner, and nothing was beaten.
        Guid? beaten = winner.TaskId == projectHead.TaskId ? null : projectHead.TaskId;

        return Slot(winner, reason, heads.Count, load, lastServed, beaten);
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
        IReadOnlyDictionary<Guid, DateTimeOffset> lastServed,
        Guid? rankBeatenTaskId = null) =>
        new(
            candidate,
            reason,
            eligibleProjects,
            lastServed.TryGetValue(candidate.ProjectId, out DateTimeOffset servedAt) ? servedAt : null,
            load.Project(candidate.ProjectId).Priority,
            rankBeatenTaskId);
}
