using Hall9k.Cli.DaemonControl;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Infrastructure;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The newer release this node's daemon last told its person about (idea 93c1d24d, card 2), read
/// from <see cref="NodeDetails"/> with no network call, and the one line <c>h9k status</c> prints
/// while this CLI is still older than it. The line is worded as information, never as an
/// instruction: it states what is available and which command installs it. Nothing here installs
/// or restarts anything.
/// </summary>
internal static class ReleaseNoticeStatus
{
    /// <summary>
    /// The one line, or null when there is nothing to say: no notice was ever recorded, or the
    /// recorded tag is not newer than <paramref name="runningVersion"/> (this CLI, so a node that
    /// has updated stops showing it before the daemon's next check). Pure, so the wording and the
    /// two commands are unit-testable.
    /// </summary>
    public static string? Line(string? noticedTag, string runningVersion, DaemonBootState daemon) =>
        noticedTag is { } tag && ReleaseTagVersion.IsNewerThan(tag, runningVersion)
            ? $"[yellow]update available:[/] hall9k {ExternalText.OneLineMarkup(ReleaseTagVersion.Bare(tag))} is available "
                + $"(this node runs {ExternalText.OneLineMarkup(runningVersion)}); {UpdateCommandFor(daemon)} installs it"
            : null;

    /// <summary>
    /// <c>h9k update --restart</c> when a daemon is running on this node, a booting one included,
    /// because the restart is what puts the new binaries to work; plain <c>h9k update</c> when none
    /// is, because a stopped daemon is deliberate on some nodes and <c>--restart</c> would start it.
    /// </summary>
    public static string UpdateCommandFor(DaemonBootState daemon) =>
        daemon == DaemonBootState.NotRunning
            ? "h9k update"
            : "h9k update --restart";

    /// <summary>
    /// The last tag this machine's own node recorded, found the way <see cref="LaunchHoldStatus.ReadAsync"/>
    /// finds it: by machine name, newest registration first, so a machine that re-registered as a new
    /// node reads that node's notice rather than a retired one's.
    /// </summary>
    public static async Task<string?> ReadNoticedTagAsync(IQuerySession session, CancellationToken cancellationToken)
    {
        string machineName = Environment.MachineName;
        NodeDetails? node = (await session.Query<NodeDetails>()
            .Where(record => record.MachineName == machineName)
            .OrderByDescending(record => record.RegisteredAt)
            .Take(1)
            .ToListAsync(cancellationToken)).FirstOrDefault();
        return node?.LastReleaseNoticeTag;
    }
}
