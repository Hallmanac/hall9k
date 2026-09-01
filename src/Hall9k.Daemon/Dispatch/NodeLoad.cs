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
/// What this node is carrying right now against what it may carry (Decisions Log #64, #108).
/// The ceiling exists because the machine, not the platform, was enforcing one: the origin
/// incident (2026-08-21) was an OOM that killed three of four concurrently dispatched agent
/// sessions the first time the queue went four wide.
/// <para>
/// The configured ceiling is denominated directly in <em>task runs</em> as of Decisions Log #108
/// (<c>DaemonOptions.MaxConcurrentTaskRuns</c>) — the thing an operator actually reasons about,
/// and the only thing the dispatcher can decline to start. The whole-life reservation this record
/// used to compute by dividing a session budget by a run's peak session cost dissolves along with
/// the old unit: a run's own peak concurrent sessions is now bounded per-phase by
/// <c>DaemonOptions.SessionCapPerRun</c> (<c>ReviewEngine</c>'s own concern), never reserved for
/// the run's whole life against this node's ceiling.
/// </para>
/// </summary>
public sealed record NodeLoad(int LiveRuns, int ConfiguredMaxConcurrentRuns)
{
    /// <summary>
    /// The ceiling actually enforced: never below 1, so a misconfigured
    /// <c>max-concurrent-task-runs</c> of zero or less dispatches one run at a time rather than
    /// nothing at all — the same floor the retired session-denominated ceiling's own
    /// <c>Math.Max(1, …)</c> derivation guaranteed, and the floor
    /// <see cref="Hall9k.Domain.Infrastructure.Persistence.OperatingSettingsResolver.WarnIfBelowRunFloor"/>'s
    /// own operator-facing warning already promises. Every admission decision this record makes —
    /// <see cref="Capacity"/> included — goes through this property rather than
    /// <see cref="ConfiguredMaxConcurrentRuns"/> directly. The two operator-facing surfaces that
    /// report the configured ceiling by name (the daemon's own startup log, and
    /// <c>h9k config show</c>/<c>h9k daemon status</c> via <c>OperatingSettingsReport</c>) read
    /// the unfloored value on purpose, so the floor stays a named, separate line rather than a
    /// silently substituted number —
    /// <see cref="Hall9k.Domain.Infrastructure.Persistence.OperatingSettingsResolver.WarnIfBelowRunFloor"/>
    /// is what tells the operator dispatch has floored to one run when it shows.
    /// </summary>
    public int MaxConcurrentRuns => Math.Max(1, ConfiguredMaxConcurrentRuns);

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
    /// A slot is a session <em>tree</em>, and how many processes are resident inside it at once
    /// is <c>DaemonOptions.SessionCapPerRun</c>'s question (<c>ReviewEngine</c>'s own concern),
    /// not this one's.
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
