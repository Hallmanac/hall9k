using Hall9k.Cli.DaemonControl;

namespace Hall9k.Cli.Orchestrator;

/// <summary>
/// The one-line daemon liveness <c>h9k orchestrator node</c> and <c>h9k orchestrator project</c>
/// lead with (task: an operator starts a lean node or project orchestrator window): the attention
/// pane says nothing about a daemon that IS running, so an orchestrator's own opening view has to
/// ask directly (<c>h9k daemon status</c>'s own probe) rather than assume silence means healthy.
/// </summary>
public static class OrchestratorDaemonLiveness
{
    public static string Describe()
    {
        DaemonBootStatus status = DaemonProcess.ProbeBootStatus();
        return status switch
        {
            { State: DaemonBootState.Running, Running: { } running } =>
                $"h9kd: running (pid {running.ProcessId}, started {running.StartedAt:u})",
            { State: DaemonBootState.Starting } =>
                "h9kd: starting (a spawn is in flight)",
            _ => "h9kd: not running - tasks queue but do not dispatch (h9k daemon start)",
        };
    }
}
