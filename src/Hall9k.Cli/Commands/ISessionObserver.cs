using Hall9k.Cli.DaemonControl;

namespace Hall9k.Cli.Commands;

/// <summary>
/// How the display asks the machine whether a recorded agent session is still there. A seam
/// rather than a direct call, because the phase composition is the part worth testing and a
/// test cannot conjure a live process (Decisions Log #66).
/// </summary>
internal interface ISessionObserver
{
    /// <summary>
    /// What can honestly be said about the session a run recorded.
    /// </summary>
    /// <param name="processId">The recorded pid; null when the run records no session.</param>
    /// <param name="startedAt">
    /// The recorded process start time — the other half of the identity (the PID-reuse guard,
    /// Decisions Log #2). Null on a resumed session, which records only a pid.
    /// </param>
    /// <param name="onThisMachine">
    /// Whether the run belongs to this node. A pid from another machine names a process in a
    /// process table this one cannot see, so checking it would answer about a stranger.
    /// </param>
    SessionLiveness Observe(int? processId, DateTimeOffset? startedAt, bool onThisMachine);
}

/// <summary>
/// The real observation: the recorded identity checked against this machine's process table,
/// exactly the way <see cref="DaemonProcess"/> checks the daemon's own pid file. Anything less
/// than a full identity on this machine is <see cref="SessionLiveness.Unobserved"/> — the
/// never-guess rule applied to liveness, which is what the phase line rests on.
/// </summary>
internal sealed class ProcessSessionObserver : ISessionObserver
{
    public static readonly ProcessSessionObserver Instance = new();

    public SessionLiveness Observe(int? processId, DateTimeOffset? startedAt, bool onThisMachine) =>
        (processId, startedAt, onThisMachine) switch
        {
            (null, _, _) => SessionLiveness.NotApplicable,
            (_, _, false) => SessionLiveness.Unobserved,
            (_, null, _) => SessionLiveness.Unobserved,
            ({ } pid, { } started, _) => DaemonProcess.IsAlive(pid, started)
                ? SessionLiveness.Alive
                : SessionLiveness.Gone,
        };
}
