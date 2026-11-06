using Hall9k.Domain.Features.Orchestrator;

namespace Hall9k.Cli.DaemonControl;

/// <summary>
/// The current process's own <c>TokenElevation</c>, read through the same
/// <see cref="IProcessElevationReader"/> the courier's elevation check uses for any pid, so the
/// autostart refusals and the courier surfaces can never read a token two different ways.
/// </summary>
public sealed class WindowsProcessElevation(IProcessElevationReader reader) : IProcessElevation
{
    public WindowsProcessElevation()
        : this(new WindowsProcessElevationReader())
    {
    }

    public bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Token elevation is read from a Windows access token.");
        }

        return reader.Read(Environment.ProcessId) switch
        {
            ProcessElevation.Elevated => true,
            ProcessElevation.NotElevated => false,
            _ => throw new InvalidOperationException("Could not read this process's token elevation."),
        };
    }
}
