namespace Hall9k.Cli.DaemonControl;

/// <summary>
/// The not-yet side of the autostart seam: platforms without an implementation refuse
/// with a teaching message instead of guessing. macOS and Windows both have real
/// implementations now (<see cref="LaunchdDaemonAutostart"/>, <see cref="WindowsDaemonAutostart"/>,
/// Decisions Log #3, #84) — this is what <see cref="DaemonAutostart.ForCurrentPlatform"/>
/// falls back to on every other platform (Linux, as of this writing).
/// </summary>
public sealed class DeferredDaemonAutostart(string notSupportedMessage) : IDaemonAutostart
{
    public bool IsSupported => false;

    public string NotSupportedMessage => notSupportedMessage;

    public string MechanismDescription => "not available on this platform yet";

    public bool IsEnabled => false;

    public Task<bool> IsLoadedAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<IReadOnlyList<string>> EnableAsync(
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
