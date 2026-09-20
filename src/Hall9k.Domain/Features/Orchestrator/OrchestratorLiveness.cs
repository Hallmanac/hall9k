namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// The one rule for "is this project's orchestrator window still up on this node" (idea 89471598,
/// piece 1), shared by the register refusal, the daemon's presence sweep, and the two CLI
/// surfaces that print it — so none of the four can drift into its own idea of live.
/// </summary>
public static class OrchestratorLiveness
{
    /// <summary>
    /// Mirrors <c>Hall9k.Daemon.ProcessManagement.ProcessManagerBase.StartTimeTolerance</c> and
    /// the CLI's own <c>InteractiveSessionLiveness</c> copy of it (Decisions Log #2): start times
    /// drift slightly between the process table read that recorded one and the read that checks
    /// it, and a match inside this window means "the same process", not a pid the operating
    /// system has since recycled for something else.
    /// </summary>
    public static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>Registered, and the registered process is still the one running under that id.</summary>
    public static bool IsLive(OrchestratorPresenceAggregate presence, IOrchestratorProcessProbe probe) =>
        presence.Registered && IsStillRunning(presence.ProcessId, presence.ProcessStartedAt, probe);

    /// <summary>
    /// The process-table half on its own, for a caller holding the facts rather than the
    /// aggregate (the projection document <c>h9k status</c> reads).
    /// <para>
    /// A start time neither side could observe never makes a running process read as gone: an
    /// unreadable start time is an unknown, not evidence (AGENTS.md, "never guess at unobserved
    /// facts"), and the cost of the two readings is asymmetric. Reading it as gone would let a
    /// second window register silently over a live one, which is exactly what the refusal exists
    /// to stop; reading it as live at worst leaves one stale line an operator clears with
    /// <c>h9k orchestrator register --replace</c>.
    /// </para>
    /// </summary>
    public static bool IsStillRunning(
        int processId, DateTimeOffset? registeredStartedAt, IOrchestratorProcessProbe probe)
    {
        if (probe.Probe(processId) is not { } sighting)
        {
            return false;
        }

        if (registeredStartedAt is not { } registered || sighting.StartedAt is not { } observed)
        {
            return true;
        }

        return (observed - registered).Duration() <= StartTimeTolerance;
    }
}
