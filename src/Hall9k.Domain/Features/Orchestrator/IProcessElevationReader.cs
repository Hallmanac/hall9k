namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// Reads whether a process's token is elevated, for any process id. Seamed because the daemon's
/// courier sweep and the CLI's status, doctor, and register surfaces all ask it, and so their tests
/// run on every CI leg rather than only on an elevated Windows shell.
/// </summary>
public interface IProcessElevationReader
{
    /// <summary>
    /// Whether elevation means anything on this platform. False everywhere but Windows, where a
    /// surface that would show elevation shows nothing instead.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// The token elevation of the process with this id. <see cref="ProcessElevation.Unknown"/>
    /// when it cannot be read, never a guess. The current process is <c>Environment.ProcessId</c>.
    /// </summary>
    ProcessElevation Read(int processId);
}
