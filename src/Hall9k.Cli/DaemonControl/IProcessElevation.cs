namespace Hall9k.Cli.DaemonControl;

/// <summary>
/// Whether the current process holds an elevated token, behind a seam so the refusals that
/// depend on it run on every CI leg rather than only on an elevated Windows shell.
/// </summary>
public interface IProcessElevation
{
    /// <summary>The current process's own <c>TokenElevation</c>: true only when it runs as an administrator.</summary>
    bool IsElevated();
}
