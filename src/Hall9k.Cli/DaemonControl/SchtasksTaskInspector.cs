namespace Hall9k.Cli.DaemonControl;

/// <summary>The real Task Scheduler: existence by the <c>schtasks /Query</c> exit code, the level through PowerShell's <c>Get-ScheduledTask</c>.</summary>
public sealed class SchtasksTaskInspector : IScheduledTaskInspector
{
    public bool Exists() => WindowsDaemonAutostart.QueryExists();

    public async Task<string?> ReadRunLevelAsync(CancellationToken cancellationToken)
    {
        ExecResult result = await Exec.RunAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", WindowsDaemonAutostart.RunLevelQueryCommand()],
            cancellationToken);
        return result.Succeeded
            ? result.StandardOutput
            : null;
    }
}
