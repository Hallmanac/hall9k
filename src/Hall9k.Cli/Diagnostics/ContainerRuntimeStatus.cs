namespace Hall9k.Cli.Diagnostics;

/// <summary>
/// Whether Docker is even in the picture — the boundary the doctor check stops at
/// (Decisions Log #73): starting Docker Desktop is a machine-level action and always
/// the human's, so <see cref="NotRunning"/> is named and nothing further is attempted.
/// </summary>
public enum ContainerRuntimeStatus
{
    NotInstalled,
    NotRunning,
    Running,
}

/// <summary>The hall9k-postgres container's own state, only meaningful once the runtime is <see cref="ContainerRuntimeStatus.Running"/>.</summary>
public enum PostgresContainerStatus
{
    Absent,
    Stopped,
    Running,
}
