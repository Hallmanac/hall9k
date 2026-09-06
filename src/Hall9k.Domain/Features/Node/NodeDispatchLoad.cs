namespace Hall9k.Domain.Features.Node;

/// <summary>
/// Mutable dispatch telemetry, NOT an event stream and NOT a projection (the RunActivity
/// shape, Decisions Log #11): what one node's dispatch sweep last measured itself carrying,
/// and the ceiling it measured against (Decisions Log #64). Id == NodeId.
/// <para>
/// It exists so the CLI can say why a queue is not moving without re-deriving the daemon's
/// counting rule from documents it would have to join by hand. The number here is the number
/// the sweep actually claimed against, so the board and the dispatcher cannot disagree about
/// how full the node is.
/// </para>
/// </summary>
public sealed class NodeDispatchLoad
{
    public Guid Id { get; set; }

    /// <summary>
    /// The machine this node is, as <c>NodeBootstrap</c> identifies nodes. Carried so a
    /// read-only surface can find its own node's row without resolving identity first.
    /// </summary>
    public string MachineName { get; set; } = string.Empty;

    /// <summary>Task runs this node was supervising when the sweep ran.</summary>
    public int LiveRuns { get; set; }

    /// <summary>
    /// The ceiling the count was actually measured against — <c>Hall9k.Daemon.Dispatch.NodeLoad
    /// .MaxConcurrentRuns</c>, the floored value (never below 1) rather than the raw configured
    /// <c>DaemonOptions.MaxConcurrentTaskRuns</c> a sub-1 setting would otherwise show here
    /// (independent pre-PR review, cycle 1, conformance lens). Published rather than re-derived,
    /// so a reader never has to know how many sessions a run tree is worth, or that the setting
    /// behind this number was ever denominated any other way.
    /// </summary>
    public int MaxConcurrentRuns { get; set; }

    /// <summary>
    /// When the sweep took this measurement. A reader that finds it stale has learned nothing
    /// current and must say nothing rather than repeat a count from a daemon that has since
    /// stopped (AGENTS.md: never guess at unobserved facts).
    /// </summary>
    public DateTimeOffset ObservedAt { get; set; }

    /// <summary>
    /// This node's periodic token-spend budget exactly as the sweep's own <c>DaemonOptions</c>
    /// carries it (Decisions Log #120) — null when this node's daemon is unbudgeted. Frozen at
    /// daemon startup, the same as every value <c>DaemonOptionsBinding.ResolverOwnedKeys</c>
    /// excludes from <c>ConfigurationBinder</c>, so a config-file edit an operator makes without
    /// restarting the daemon shows up here as exactly what the dispatcher is still enforcing —
    /// never what the file now says. <c>h9k status</c> reads this rather than re-resolving the
    /// config file fresh, for the identical reason <see cref="MaxConcurrentRuns"/> is published
    /// rather than re-derived: the board and the dispatcher must not disagree about whether a
    /// budget is even in force (independent pre-PR review, cycle 1, both lenses).
    /// </summary>
    public long? SpendBudgetTokens { get; set; }

    /// <summary>The window <see cref="SpendBudgetTokens"/> resets on ("day" or "week"), carried alongside it for the same reason.</summary>
    public string SpendPeriod { get; set; } = string.Empty;

    /// <summary>
    /// What the same sweep measured per project (Decisions Log #140): one row for every project
    /// this node was carrying a run for, or had a queued candidate from, when it looked. Empty on
    /// a record written before per-project ceilings existed, and on a sweep that saw neither —
    /// which reads as "nothing measured about any project", never as "no project has a cap".
    /// <para>
    /// Published for the identical reason <see cref="MaxConcurrentRuns"/> is (Decisions Log #64):
    /// a reader must not re-derive the dispatcher's own counting rule — which slots count, and
    /// the claim-committed-but-run-not-yet-recorded handoff window a CLI cannot even see — from
    /// documents it would have to join by hand. The cap on each row is the cap that sweep
    /// actually admitted against, so a cap changed a moment ago reads as the old number until the
    /// next sweep, exactly as the board and the dispatcher agreeing requires.
    /// </para>
    /// </summary>
    public List<ProjectRunLoad> ProjectLoads { get; set; } = [];
}

/// <summary>
/// One project's line in a sweep's own measurement (Decisions Log #140): how many of its runs the
/// measuring node was carrying, and the ceiling it was admitting against.
/// <see cref="Hall9k.Domain.Features.Project.ProjectRunCeiling"/> is what turns the pair into a
/// decision; this is only the record of it.
/// </summary>
/// <param name="ProjectId">The project, as <c>ProjectDetails.Id</c> identifies it.</param>
/// <param name="LiveRuns">Its live runs on the measuring node — interactive claims excluded, as at the node level.</param>
/// <param name="Cap">Its <c>ProjectDetails.MaxParallelTasks</c> at measurement time; null is uncapped, 0 is paused.</param>
public sealed record ProjectRunLoad(Guid ProjectId, int LiveRuns, int? Cap);
