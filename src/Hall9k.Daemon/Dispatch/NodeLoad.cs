using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Documents;

namespace Hall9k.Daemon.Dispatch;

/// <summary>
/// One agent session tree this node is holding, and the thing that identifies it: the run it
/// belongs to, or — inside the dispatch handoff, where the claim has committed and
/// <c>RunDispatched</c> is seconds away — the task whose lease has no run recorded yet.
/// <see cref="RunId"/> is null in that window alone, which is the one case where the node knows
/// a session is coming without being able to name the run it will belong to.
/// </summary>
public sealed record LiveSlot(Guid TaskId, Guid? RunId);

/// <summary>
/// What this node is carrying right now against what it may carry (Decisions Log #64).
/// The ceiling exists because the machine, not the platform, was enforcing one: the origin
/// incident (2026-08-21) was an OOM that killed three of four concurrently dispatched agent
/// sessions the first time the queue went four wide.
/// <para>
/// The configured ceiling is denominated in <em>agent sessions</em>, because sessions are what
/// the machine runs out of memory for, and a run tree is not one session. This record is the
/// conversion: it counts run trees, since a run is the only thing the dispatcher can decline to
/// start, and charges each one the peak number of sessions its tree can hold at once.
/// </para>
/// </summary>
public sealed record NodeLoad(int LiveRuns, int MaxConcurrentAgentSessions)
{
    /// <summary>
    /// The most agent sessions one run tree can have resident at the same time, derived from
    /// the lens list rather than written down, so appending a third lens tightens the ceiling
    /// instead of quietly doubling what the machine is asked to hold.
    /// <para>
    /// A review cycle spawns every active lens together and waits on them together
    /// (<c>ReviewEngine.DispatchReviewPassesAsync</c>, log #59), so a run under review is
    /// <see cref="ReviewLens.CycleLenses"/> processes, not one. Every other stage of a run tree
    /// is strictly sequential and costs one session: the blocker-synthesis pass is awaited
    /// before the build session spawns, the build session has exited by the time the
    /// verification gates run, and a fix session is the only thing resident in its phase.
    /// </para>
    /// </summary>
    public static readonly int PeakAgentSessionsPerRun = Math.Max(1, ReviewLens.CycleLenses.Count);

    /// <summary>
    /// What this node is holding in the unit the ceiling is set in: every live run charged its
    /// peak, not its current, session count. Charged at the peak on purpose — a run that is
    /// building today reaches its review cycle on this same machine, and a ceiling that only
    /// counted what was resident this second would claim three build sessions and then watch
    /// them become six. Reserving is what makes "cannot start more agents than the machine can
    /// hold" a guarantee about what the dispatcher <em>starts</em>, rather than an average.
    /// <para>
    /// It is deliberately not a guarantee about what is resident, and the gap is one recorded
    /// exception rather than an oversight (Decisions Log #64). A run whose review park a human
    /// resolves re-enters the pipeline through <c>RunSupervisor.ResumeResolvedReviewsAsync</c>,
    /// and a stranded one re-enters through startup adoption; neither asks this record anything,
    /// and both hand a worktree back to a session tree the node had already released. So
    /// <see cref="LiveRuns"/> can sit above <see cref="MaxConcurrentRuns"/>. That overshoot is
    /// accepted, because refusing a human's explicit resume to protect a number is the worse
    /// trade, and it is bounded rather than compounding: <see cref="Capacity"/> reads zero, so
    /// the dispatcher claims nothing further until the node is back under. Accepted is not the
    /// same as invisible, so the sweep that measures it says so
    /// (<c>DispatchEngine.ReportOverCeiling</c>) instead of leaving the machine's memory
    /// pressure with a symptom and no recorded cause.
    /// </para>
    /// </summary>
    public int LiveAgentSessions => LiveRuns * PeakAgentSessionsPerRun;

    /// <summary>
    /// The session ceiling as a number of runs, which is the unit the dispatcher claims in and
    /// the one a human reads on the pane. Never zero: a session budget smaller than one run's
    /// peak would dispatch nothing at all, and a node that silently does no work is a worse
    /// answer than a node that runs one task over a budget it cannot honour anyway.
    /// </summary>
    public int MaxConcurrentRuns => Math.Max(1, MaxConcurrentAgentSessions / PeakAgentSessionsPerRun);

    /// <summary>
    /// How many more runs may start right now; never negative, because a node can be over its
    /// ceiling without the dispatcher having put it there. A resolved review park and startup
    /// adoption both re-enter a run whose slot the node had released (Decisions Log #64), and
    /// clamping is what turns that into "claim nothing until something finishes" rather than a
    /// negative budget that would owe the queue slots as runs end.
    /// </summary>
    public int Capacity => Math.Max(0, MaxConcurrentRuns - LiveRuns);

    public bool AtCeiling => Capacity == 0;

    /// <summary>
    /// The counting rule, pure so it can be read in one sitting and tested without a database.
    /// A live slot is an agent session tree this node is supervising right now, counted per
    /// <em>run</em> rather than per task:
    /// <list type="bullet">
    /// <item>a run in a live state (dispatched, running, verifying, under review) — the review
    /// and fix sessions of a review cycle are that run's own sessions, so they cost its slot
    /// and not another one, which is the whole reason the ceiling can be trusted;</item>
    /// <item>a lease with no run recorded at its generation yet — the dispatch handoff, where
    /// the claim has committed and <c>RunDispatched</c> is seconds away. A session about to
    /// exist holds a slot, or a sweep that runs inside that window claims over the ceiling.</item>
    /// </list>
    /// One task can hold two slots, because it can have two session trees: a run whose review
    /// cycle startup adoption has just resumed, plus a fresh claim of the same task made after
    /// the expiry sweep requeued it. Counting those as one was this rule's first cut, and it
    /// let the node run a session tree over its ceiling (pre-PR review of this branch,
    /// 2026-08-22) — the ceiling counts what the machine has to hold, and the machine holds
    /// both.
    /// <para>
    /// A slot is a session <em>tree</em>, and how many processes a tree is worth is
    /// <see cref="PeakAgentSessionsPerRun"/>'s question, not this one's.
    /// </para>
    /// <para>
    /// Everything else has released its slot: a parked run is waiting on a human with no
    /// process resident, a completed or failed run is over, and a task waiting on a merge
    /// observation holds no memory at all. A follow-up gets its slot back the ordinary way,
    /// by being claimed as its own run.
    /// </para>
    /// </summary>
    public static IReadOnlyCollection<LiveSlot> LiveSlots(
        Guid nodeId, IEnumerable<TaskLease> leases, IReadOnlyCollection<RunListItem> runs)
    {
        HashSet<LiveSlot> live =
        [
            .. runs.Where(run => run.NodeId == nodeId && run.State.IsLive)
                .Select(run => new LiveSlot(run.TaskId, run.Id)),
        ];

        foreach (TaskLease lease in leases.Where(lease => lease.NodeId == nodeId))
        {
            bool runRecorded = runs.Any(run => run.TaskId == lease.Id && run.LeaseGeneration == lease.LeaseGeneration);
            if (!runRecorded)
            {
                live.Add(new LiveSlot(lease.Id, null));
            }
        }

        return live;
    }
}
