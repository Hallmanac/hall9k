namespace Hall9k.Cli.DaemonControl;

public static class DaemonAutostart
{
    public static IDaemonAutostart ForCurrentPlatform() => OperatingSystem.IsMacOS()
        ? new LaunchdDaemonAutostart()
        : new DeferredDaemonAutostart(OperatingSystem.IsWindows()
            ? "Autostart on Windows (a Task Scheduler logon task, Decisions Log #3) arrives with S1-14. "
              + "Until then, start the daemon on demand with: h9k daemon start"
            : "Autostart is implemented for macOS only in Slice 1 (Decisions Log #3). "
              + "Start the daemon on demand with: h9k daemon start");
}
