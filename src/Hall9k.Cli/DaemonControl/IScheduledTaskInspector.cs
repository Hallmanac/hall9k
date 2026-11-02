namespace Hall9k.Cli.DaemonControl;

/// <summary>
/// What <see cref="WindowsDaemonAutostart"/> asks Task Scheduler about the registration that
/// already exists, behind a seam so the run level chosen for each combination of existing task
/// runs on every CI leg.
/// </summary>
public interface IScheduledTaskInspector
{
    /// <summary>The task is registered, by the <c>schtasks /Query</c> exit code.</summary>
    bool Exists();

    /// <summary>
    /// The registered task's <c>Principal.RunLevel.ToString()</c> output, or <c>null</c> when it
    /// could not be read at all. Never called for a task that does not exist.
    /// </summary>
    Task<string?> ReadRunLevelAsync(CancellationToken cancellationToken);
}
