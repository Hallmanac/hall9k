namespace Hall9k.Cli.DaemonControl;

public static class DaemonAutostart
{
    public static IDaemonAutostart ForCurrentPlatform() => (OperatingSystem.IsMacOS(), OperatingSystem.IsWindows()) switch
    {
        (true, _) => new LaunchdDaemonAutostart(),
        (_, true) => new WindowsDaemonAutostart(),
        _ => new DeferredDaemonAutostart(
            "Autostart is implemented for macOS and Windows only in Slice 1 (Decisions Log #3). "
            + "Start the daemon on demand with: h9k daemon start"),
    };
}
