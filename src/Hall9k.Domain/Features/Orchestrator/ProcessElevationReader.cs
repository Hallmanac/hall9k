namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>The real <see cref="IProcessElevationReader"/> for the platform this process runs on.</summary>
public static class ProcessElevationReader
{
    public static IProcessElevationReader ForCurrentPlatform() =>
        OperatingSystem.IsWindows()
            ? new WindowsProcessElevationReader()
            : new UnsupportedProcessElevationReader();
}

/// <summary>
/// macOS and Linux have no token elevation for a courier to trip over, so every read is
/// <see cref="ProcessElevation.Unknown"/> and nothing is ever blocked on it.
/// </summary>
public sealed class UnsupportedProcessElevationReader : IProcessElevationReader
{
    public bool IsSupported => false;

    public ProcessElevation Read(int processId) => ProcessElevation.Unknown;
}
