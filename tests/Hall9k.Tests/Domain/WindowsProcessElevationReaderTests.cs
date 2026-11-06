using FluentAssertions;
using Hall9k.Domain.Features.Orchestrator;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The real token read, against this test process. Only that a level was read is asserted: the
/// windows-latest runner is elevated and a developer's shell may not be, so which level is not a
/// fact this test can pin. Off Windows the platform reader is the unsupported one.
/// </summary>
public sealed class WindowsProcessElevationReaderTests
{
    [Fact]
    public void Reads_this_process_s_own_elevation_on_Windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        new WindowsProcessElevationReader().Read(Environment.ProcessId).Should().NotBe(ProcessElevation.Unknown);
    }

    [Fact]
    public void Off_Windows_the_platform_reader_is_unsupported_and_reads_unknown()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        IProcessElevationReader reader = ProcessElevationReader.ForCurrentPlatform();
        reader.IsSupported.Should().BeFalse();
        reader.Read(Environment.ProcessId).Should().Be(ProcessElevation.Unknown);
    }
}
