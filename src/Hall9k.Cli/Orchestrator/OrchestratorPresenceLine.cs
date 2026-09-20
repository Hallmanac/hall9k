using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Orchestrator;

namespace Hall9k.Cli.Orchestrator;

/// <summary>
/// The one sentence both presence surfaces print (idea 89471598, piece 1): the block
/// <c>h9k orchestrator status</c> leads with, and the single line the <c>h9k status</c> header
/// gained. Composed as plain text, with no Spectre markup in it, so the two callers cannot drift
/// apart in wording and a unit test can read it without a console.
/// <para>
/// Liveness is decided here rather than read off <see cref="OrchestratorPresenceDetails.Registered"/>
/// alone, because the daemon's sweep runs on an interval: a window closed twenty seconds ago is
/// still registered on the document and already gone from the process table, and this node can
/// see that for itself. The sweep is what makes the honest answer durable
/// (<see cref="OrchestratorLost"/>); this is what makes it immediate.
/// </para>
/// </summary>
public static class OrchestratorPresenceLine
{
    public static string Describe(
        OrchestratorPresenceDetails? presence, IOrchestratorProcessProbe probe, DateTimeOffset now)
    {
        if (presence is { Registered: true }
            && OrchestratorLiveness.IsStillRunning(presence.ProcessId, presence.ProcessStartedAt, probe))
        {
            string since = presence.LaunchedAt is { } launchedAt
                ? $"{launchedAt.ToLocalTime():yyyy-MM-dd HH:mm} ({DurationFormat.Short(Elapsed(launchedAt, now))} ago)"
                : "unknown";
            return $"orchestrator: live — '{presence.SessionName}' ({presence.Cli}, pid {presence.ProcessId}), since {since}";
        }

        return $"orchestrator: none live — {LastSeen(presence)}";
    }

    /// <summary>
    /// The trailing half of the "none live" line: the last thing that actually happened to a
    /// window here, named as what it was. A registered record whose process this node can no
    /// longer find has not been swept yet, and says so rather than borrowing the earlier
    /// shutdown's time — the sweep is what learns when it went.
    /// </summary>
    private static string LastSeen(OrchestratorPresenceDetails? presence) => presence switch
    {
        null => "none has ever registered on this machine",
        { Registered: true } => $"'{presence.SessionName}' (pid {presence.ProcessId}) is registered but its process is "
            + "gone; the daemon's next presence sweep records the loss",
        { ShutDownAt: { } shutDownAt, LostAt: { } lostAt } when lostAt > shutDownAt =>
            $"last lost {lostAt.ToLocalTime():yyyy-MM-dd HH:mm}",
        { ShutDownAt: { } shutDownAt } => $"last shut down {shutDownAt.ToLocalTime():yyyy-MM-dd HH:mm}",
        { LostAt: { } lostAt } => $"last lost {lostAt.ToLocalTime():yyyy-MM-dd HH:mm}",
        _ => "none has ever registered on this machine",
    };

    /// <summary>
    /// Clamped at zero: a launch stamped a moment in the future (a clock the operating system
    /// stepped backwards between the register and this read) would otherwise render as a
    /// negative age, which reads as nonsense rather than as the near-zero it actually is.
    /// </summary>
    private static TimeSpan Elapsed(DateTimeOffset since, DateTimeOffset now) =>
        now > since ? now - since : TimeSpan.Zero;
}
