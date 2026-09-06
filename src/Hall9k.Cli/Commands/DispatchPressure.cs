using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Project;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// What the local node's dispatch sweep last reported about its own capacity (Decisions Log
/// #64), read rather than re-derived: the daemon publishes the count it actually claimed
/// against, so the board and the dispatcher cannot disagree about how full the machine is.
/// Absent when no measurement is current, which reads as "nothing known about capacity" —
/// the pane then says nothing about slots rather than inventing a number.
/// <para>
/// Runs, because a slot is what a queued task is waiting for and, as of Decisions Log #111,
/// the setting behind the number (<c>DaemonOptions.MaxConcurrentTaskRuns</c>) is denominated
/// directly in runs too — the section heading names the lever in its own unit with no
/// conversion in between. A run's own review occupancy (one process per lens on discovery and
/// final-full-pass cycles, one total on the verify cycles between them, fewer still under a
/// lowered session cap) is <c>DaemonOptions.SessionCapPerRun</c>'s own concern, bounded per run
/// rather than reserved against this node-wide ceiling; no CLI surface has to know how many
/// processes a run tree is worth.
/// </para>
/// </summary>
/// <param name="LiveRuns">The runs this node was carrying when its last sweep looked.</param>
/// <param name="MaxConcurrentRuns">The node ceiling that sweep admitted against.</param>
/// <param name="Projects">
/// The same sweep's per-project measurement, keyed by project id (Decisions Log #140), or null on
/// a measurement written before per-project ceilings existed. A project absent from it is one
/// that sweep measured nothing about, which reads as "no cap observed" rather than "no cap set" —
/// the never-guess rule again: the cap is enforced from the dispatcher's own reading, so a
/// surface must not claim a cap the dispatcher was not admitting against.
/// </param>
internal sealed record DispatchPressure(
    int LiveRuns,
    int MaxConcurrentRuns,
    IReadOnlyDictionary<Guid, ProjectRunCeiling>? Projects = null)
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
    /// Slots this node had free when it last swept. The number that makes a paused project's hold
    /// worth shouting about (Decisions Log #140): work held while the machine sits idle is the
    /// forgotten-cap footgun, and work held on a full node is the ceiling doing its job.
    /// </summary>
    public int IdleRuns => Math.Max(0, MaxConcurrentRuns - LiveRuns);

    /// <summary>
    /// This project's own ceiling as that same sweep measured it, or null when it measured
    /// nothing about this project.
    /// </summary>
    public ProjectRunCeiling? ForProject(Guid projectId) =>
        Projects is null ? null : Projects.GetValueOrDefault(projectId);

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
    /// <para>
    /// It names the node out loud, because a per-project cap holds rows back in the same column
    /// with the same numbers (Decisions Log #140): "2 of 2 running" alone left a reader unable to
    /// tell which of the two levers to reach for.
    /// </para>
    /// </summary>
    public string ReasonLine => LiveRuns > MaxConcurrentRuns
        ? $"waiting for a slot — node {LiveRuns} running, over a ceiling of {MaxConcurrentRuns}"
        : $"waiting for a slot — node {LiveRuns} of {MaxConcurrentRuns} running";

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
        NodeDispatchLoad? load = await ReadFreshMeasurementAsync(session, now, cancellationToken);
        return load is null
            ? null
            : new DispatchPressure(
                load.LiveRuns,
                load.MaxConcurrentRuns,
                load.ProjectLoads.ToDictionary(
                    project => project.ProjectId,
                    project => new ProjectRunCeiling(project.LiveRuns, project.Cap)));
    }

    /// <summary>
    /// The raw published measurement this machine's own daemon last swept, or null when there is
    /// none current — shared with <see cref="SpendPressure"/>, which reads the same row's spend
    /// fields rather than its concurrency fields, so both surfaces apply the identical freshness
    /// gate and never disagree about whether a daemon is confirmed alive here.
    /// </summary>
    internal static async Task<NodeDispatchLoad?> ReadFreshMeasurementAsync(
        IQuerySession session, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string machineName = Environment.MachineName;
        NodeDispatchLoad? load = (await session.Query<NodeDispatchLoad>()
            .Where(record => record.MachineName == machineName)
            .OrderByDescending(record => record.ObservedAt)
            .Take(1)
            .ToListAsync(cancellationToken)).FirstOrDefault();

        return load is not null && now - load.ObservedAt <= Freshness ? load : null;
    }
}
