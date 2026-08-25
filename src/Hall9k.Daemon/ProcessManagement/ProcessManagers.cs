namespace Hall9k.Daemon.ProcessManagement;

public static class ProcessManagers
{
    public static IProcessManager ForCurrentPlatform() => OperatingSystem.IsWindows()
        ? new WindowsProcessManager()
        : new UnixProcessManager();
}
