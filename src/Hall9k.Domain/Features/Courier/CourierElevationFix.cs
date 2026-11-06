namespace Hall9k.Domain.Features.Courier;

/// <summary>
/// The wording every surface uses for a courier blocked by elevation
/// (<see cref="CourierGate.ElevationBlocks"/>), so the daemon's log, <c>h9k status</c>,
/// <c>h9k orchestrator status</c>, <c>h9k doctor</c>, and <c>h9k orchestrator register</c> name
/// the same state and the same fixes.
/// </summary>
public static class CourierElevationFix
{
    /// <summary>The blocked state, as the courier line on <c>h9k status</c> prints it after <c>courier: </c>.</summary>
    public const string BlockedState = "blocked, the window is elevated and the daemon is not";

    private const string WithAutostart =
        "run h9k daemon autostart enable --run-level highest from an elevated shell, then h9k daemon stop and h9k daemon start";

    private const string WithoutAutostart = "run h9k daemon stop, then h9k daemon start from an elevated shell";

    private const string OrWindow = "or open the window without elevation";

    /// <summary>
    /// The fixes for one blocked window. The CLI knows whether autostart is registered and passes
    /// it; the daemon cannot see that and passes null, which names both daemon fixes as alternatives.
    /// </summary>
    public static string For(bool? autostartRegistered) => autostartRegistered switch
    {
        true => $"to fix, {WithAutostart}; {OrWindow}",
        false => $"to fix, {WithoutAutostart}; {OrWindow}",
        null => $"to fix, with autostart registered {WithAutostart}; with no autostart {WithoutAutostart}; {OrWindow}",
    };
}
