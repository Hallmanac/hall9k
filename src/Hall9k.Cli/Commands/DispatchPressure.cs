using Hall9k.Domain.Features.Node;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// What the local node's dispatch sweep last reported about its own capacity (Decisions Log
/// #64), read rather than re-derived: the daemon publishes the count it actually claimed
/// against, so the board and the dispatcher cannot disagree about how full the machine is.
/// Absent when no measurement is current, which reads as "nothing known about capacity" —
/// the pane then says nothing about slots rather than inventing a number.
/// <para>
/// Runs, because a slot is what a queued task is waiting for. The setting behind the number is
/// denominated in agent sessions and a run's review occupancy varies by cycle mode (one per
/// review lens on discovery and final-full-pass cycles, one total on the verify cycles between
/// them), so the conversion is the daemon's and is published already done: the section heading
/// names the lever in its own unit, and no CLI surface has to know how many processes a run
/// tree is worth.
/// </para>
/// </summary>
internal sealed record DispatchPressure(int LiveRuns, int MaxConcurrentRuns)
{
    /// <summary>
    /// How old a measurement may be and still describe now. Generous against a dispatch cycle,
    /// which can block for minutes on a blocker-synthesis session before it sweeps again, and
    /// short enough that a daemon stopped hours ago cannot have its last count repeated as
    /// current.
    /// </summary>
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(10);

    /// <summary>Nothing more can start here until something finishes.</summary>
    public bool AtCeiling => LiveRuns >= MaxConcurrentRuns;

    /// <summary>
    /// The one-line reason a queued task is not moving: the cause, in the numbers that caused
    /// it. The lever belongs to the section that carries these rows, since it is one setting
    /// for the whole node rather than a per-task action.
    /// <para>
    /// A node can read <em>over</em> its ceiling rather than at it: resolving a review park hands
    /// a worktree back to a session tree the node had released, and the dispatcher accepts that
    /// overshoot rather than refusing a human's explicit resume (Decisions Log #64). "4 of 3
    /// running" would read as an arithmetic fault on a pane whose whole job here is to say the
    /// board is throttled rather than broken, so the over case says what it is instead.
    /// </para>
    /// </summary>
    public string ReasonLine => LiveRuns > MaxConcurrentRuns
        ? $"waiting for a slot — {LiveRuns} running, over a ceiling of {MaxConcurrentRuns}"
        : $"waiting for a slot — {LiveRuns} of {MaxConcurrentRuns} running";

    /// <summary>
    /// This machine's current measurement, or null when there is none: no daemon has swept
    /// here since the ceiling was built, or the last sweep is too old to describe now. The node
    /// is found by machine name, which is how <c>NodeBootstrap</c> identifies nodes.
    /// <para>
    /// Newest measurement first, because machine name is not the document key — the row is keyed
    /// by node id, and one machine re-registering as a new node leaves the old row behind. Taking
    /// whichever the database returned first could read the retired node's last sweep, and the
    /// freshness gate below would then report nothing at all about a node that is sweeping right
    /// now.
    /// </para>
    /// </summary>
    public static async Task<DispatchPressure?> ReadAsync(
        IQuerySession session, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string machineName = Environment.MachineName;
        NodeDispatchLoad? load = (await session.Query<NodeDispatchLoad>()
            .Where(record => record.MachineName == machineName)
            .OrderByDescending(record => record.ObservedAt)
            .Take(1)
            .ToListAsync(cancellationToken)).FirstOrDefault();

        return load is not null && now - load.ObservedAt <= Freshness
            ? new DispatchPressure(load.LiveRuns, load.MaxConcurrentRuns)
            : null;
    }
}
