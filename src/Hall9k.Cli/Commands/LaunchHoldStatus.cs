using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Node;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// This machine's own node-wide launch hold, as its own node stream last recorded it (task: a
/// session that exits at once with no work done is treated as the node failing to launch
/// sessions) — read fresh from <see cref="NodeDetails"/> rather than a published, freshness-gated
/// measurement like <see cref="DispatchPressure"/>/<see cref="SpendPressure"/>: the hold is
/// event-sourced state, not a sweep's own snapshot, so there is no "daemon confirmed alive"
/// question to answer here the way those two have — an inline projection updated the instant the
/// event committed is already as current as this shell can read.
/// </summary>
/// <param name="ProbeCount">Relaunch attempts this episode has spent so far.</param>
/// <param name="HeldRunCount">Distinct runs this episode has caught.</param>
internal sealed record LaunchHoldStatus(
    bool Active, string CauseText, DateTimeOffset? RaisedAt, int ProbeCount, int HeldRunCount)
{
    /// <summary>
    /// The one unmissable line <c>h9k status</c> prints while the hold stands (acceptance: naming
    /// the cause text and the likely fix) — an authentication failure is the one the platform
    /// never confirms, since classification is on the zero-work SHAPE rather than the message, so
    /// the fix names both plausible causes the origin outage actually had rather than committing
    /// to either.
    /// </summary>
    public string NeedsYouLine =>
        $"[red bold]NEEDS YOU[/] [red]sessions on this node are exiting immediately with no work "
        + $"done — the dispatcher stopped claiming at {RaisedAt:u}. Likely cause: "
        + $"{ExternalText.OneLineMarkup(CauseText)} Sign in to the agent CLI on this node, or check "
        + "its network access — the daemon retries automatically and resumes every held run once a "
        + $"relaunch succeeds ({ProbeCount} probe(s) so far, {HeldRunCount} run(s) waiting).[/]";

    /// <summary>
    /// The <c>h9k daemon status</c> line while the hold stands: the start time and probe count an
    /// operator needs to judge whether this is a fresh outage or one that has been running a
    /// while.
    /// </summary>
    public string DaemonStatusLine =>
        $"[red bold]launch hold:[/] [red]raised {RaisedAt:u} — {ProbeCount} probe(s), {HeldRunCount} "
        + $"run(s) waiting. {ExternalText.OneLineMarkup(CauseText)}[/]";

    /// <summary>
    /// This machine's current node, found by machine name exactly as <c>DispatchPressure</c>'s own
    /// doc explains for the identical reason: the node id itself is not something either CLI
    /// command already has to hand. Newest registration first, so a machine that re-registered as
    /// a new node reads that node's own hold rather than a retired one's.
    /// </summary>
    public static async Task<LaunchHoldStatus?> ReadAsync(IQuerySession session, CancellationToken cancellationToken)
    {
        string machineName = Environment.MachineName;
        NodeDetails? node = (await session.Query<NodeDetails>()
            .Where(record => record.MachineName == machineName)
            .OrderByDescending(record => record.RegisteredAt)
            .Take(1)
            .ToListAsync(cancellationToken)).FirstOrDefault();

        return node is null
            ? null
            : new LaunchHoldStatus(
                node.LaunchHoldActive, node.LaunchHoldCauseText, node.LaunchHoldRaisedAt,
                node.LaunchHoldProbeCount, node.LaunchHoldRunCount);
    }
}
