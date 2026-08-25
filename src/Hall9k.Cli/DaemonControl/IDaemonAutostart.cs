namespace Hall9k.Cli.DaemonControl;

/// <summary>
/// The cross-platform seam for start-at-login registration (Decisions Log #3, #31).
/// Strictly opt-in: nothing implements "enable" as a side effect of install or start.
/// macOS = launchd LaunchAgent; Windows = Task Scheduler logon task, deferred to S1-14.
/// </summary>
public interface IDaemonAutostart
{
    bool IsSupported { get; }

    /// <summary>The teaching message printed when this platform has no implementation yet.</summary>
    string NotSupportedMessage { get; }

    /// <summary>
    /// What this platform's mechanism is called, for status and enable-confirmation
    /// messages that would otherwise have to guess or hardcode one platform's name
    /// (launchd LaunchAgent / Task Scheduler logon task).
    /// </summary>
    string MechanismDescription { get; }

    /// <summary>The registration exists (survives reboots), whether or not the job is loaded right now.</summary>
    bool IsEnabled { get; }

    /// <summary>The service manager currently owns the job — the signal that start/stop must go through it.</summary>
    Task<bool> IsLoadedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Registers start-at-login for the given binary, carrying the given environment
    /// into it: a service manager starts from its own minimal environment, not the
    /// operator's shell, and the daemon resolves claude, gh, and git through PATH
    /// (see <see cref="DaemonEnvironment"/>).
    /// </summary>
    Task EnableAsync(
        string daemonBinaryPath,
        IReadOnlyList<KeyValuePair<string, string>> environment,
        CancellationToken cancellationToken);

    /// <summary>
    /// Unregisters fully, reporting what that did to a running daemon — an observed
    /// live process seen gone, one still shutting down, or nothing running at all.
    /// </summary>
    Task<DaemonAutostartDisableOutcome> DisableAsync(CancellationToken cancellationToken);

    /// <summary>Start the daemon through the service manager (so the manager owns what it may restart).</summary>
    Task<bool> StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stop through the service manager — unloading the job so crash-restart policy
    /// cannot resurrect a daemon the human just killed. The registration itself stays.
    /// </summary>
    Task<bool> StopAsync(CancellationToken cancellationToken);
}
