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
    /// The configured ceiling the count was measured against — <c>DaemonOptions.MaxConcurrentTaskRuns</c>,
    /// admission's own unit as of Decisions Log #108. Published rather than re-derived, so a reader
    /// never has to know how many sessions a run tree is worth, or that the setting behind this
    /// number was ever denominated any other way.
    /// </summary>
    public int MaxConcurrentRuns { get; set; }

    /// <summary>
    /// When the sweep took this measurement. A reader that finds it stale has learned nothing
    /// current and must say nothing rather than repeat a count from a daemon that has since
    /// stopped (AGENTS.md: never guess at unobserved facts).
    /// </summary>
    public DateTimeOffset ObservedAt { get; set; }
}
