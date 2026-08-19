namespace Hall9k.Cli.DaemonControl;

/// <summary>
/// The not-yet side of the autostart seam: platforms without an implementation refuse
/// with a teaching message instead of guessing. Windows (Task Scheduler logon task,
/// Decisions Log #3) lands with S1-14.
/// </summary>
public sealed class DeferredDaemonAutostart(string notSupportedMessage) : IDaemonAutostart
{
    public bool IsSupported => false;

    public string NotSupportedMessage => notSupportedMessage;

    public bool IsEnabled => false;

    public Task<bool> IsLoadedAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    public Task EnableAsync(
        string daemonBinaryPath,
        IReadOnlyList<KeyValuePair<string, string>> environment,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(notSupportedMessage);

    public Task<DaemonAutostartDisableOutcome> DisableAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException(notSupportedMessage);

    public Task<bool> StartAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException(notSupportedMessage);

    public Task<bool> StopAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException(notSupportedMessage);
}
