namespace Hall9k.Cli.DaemonControl;

/// <summary>
/// What unregistering start-at-login actually did to a running daemon. An in-process
/// outcome that is never persisted, so an enum is the right shape here (AGENTS.md).
/// </summary>
public enum DaemonAutostartDisableOutcome
{
    /// <summary>The service manager was running no daemon; whatever runs now is untouched.</summary>
    NothingStopped,

    /// <summary>A live manager-owned process was observed, signalled, and seen gone.</summary>
    DaemonStopped,

    /// <summary>A live manager-owned process was signalled and was still shutting down when we stopped watching.</summary>
    DaemonStopping,
}
