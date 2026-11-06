using Hall9k.Domain.Features.Courier;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Cli.Orchestrator;

/// <summary>
/// The CLI's live copy of the daemon's elevation verdict (<see cref="CourierGate.ElevationBlocks"/>):
/// the registered window's process against the running daemon, read with the same reader the daemon
/// uses and never stored. Nothing is blocked with no daemon running or off Windows.
/// </summary>
public static class CourierElevationCheck
{
    /// <summary>
    /// The running daemon's elevation, or null when the comparison does not apply: elevation is not
    /// supported here, or no daemon is running (<paramref name="daemon"/> is
    /// <c>DaemonProcess.Probe()</c>, already checked by pid and start time).
    /// </summary>
    public static ProcessElevation? DaemonElevation(IProcessElevationReader reader, DaemonProcessDescriptor? daemon) =>
        reader.IsSupported && daemon is not null
            ? reader.Read(daemon.ProcessId)
            : null;

    /// <summary>
    /// Whether a live window is blocked against a daemon at <paramref name="daemonElevation"/>. The
    /// window's token is read only when the daemon is known not to be elevated, the one case that can block.
    /// </summary>
    public static bool Blocks(
        ProcessElevation? daemonElevation, bool windowLive, int windowProcessId, IProcessElevationReader reader) =>
        windowLive
        && daemonElevation is ProcessElevation.NotElevated
        && CourierGate.ElevationBlocks(ProcessElevation.NotElevated, reader.Read(windowProcessId));

    /// <summary>The blocked state and its fixes, as one sentence for a surface that prints no courier line of its own.</summary>
    public static string BlockedSentence(bool autostartRegistered) =>
        $"courier: {CourierElevationFix.BlockedState}; {CourierElevationFix.For(autostartRegistered)}";

    /// <summary>How a doctor line names one elevation level.</summary>
    public static string Describe(ProcessElevation elevation) => elevation switch
    {
        ProcessElevation.Elevated => "elevated",
        ProcessElevation.NotElevated => "not elevated",
        _ => "unknown",
    };
}
