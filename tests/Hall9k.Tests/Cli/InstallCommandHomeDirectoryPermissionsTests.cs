using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="InstallCommand.FinishAsync"/>'s own call to <c>HomeDirectoryPermissions.NarrowIfWider</c>
/// (security review idea 6be68ee2, secrets-files-network finding 8): both <c>h9k install</c> and
/// <c>h9k update</c> (through this shared method) narrow the resolved home to 0700 whenever it is
/// currently wider. Runs through the real call site rather than the helper directly, so it is this
/// wiring — not just the helper in isolation — that is proven; <see cref="HomeDirectoryPermissionsTests"/>
/// covers the doctor's own report-then-repair shape and the Windows no-op.
/// </summary>
public sealed class InstallCommandHomeDirectoryPermissionsTests : IDisposable
{
    private readonly ScopedTestHome _scopedHome = new();
    private readonly string _staging = Path.Combine(Path.GetTempPath(), $"h9k-install-home-mode-{Path.GetRandomFileName()}");

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode WideMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    public InstallCommandHomeDirectoryPermissionsTests() => Directory.CreateDirectory(_staging);

    public void Dispose()
    {
        _scopedHome.Dispose();
        InstallCommand.TryDelete(_staging);
    }

    [Fact]
    public async Task FinishAsync_narrows_a_wide_home_to_0700()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(_scopedHome.Home, WideMode);

        int exitCode = await InstallCommand.FinishAsync(
            _staging,
            skillsSource: null,
            version: "0.0.0-test",
            restart: false,
            noRestart: true,
            linkOntoPath: false,
            containerRuntimeRunner: RecordingProcessRunner.Failing("docker not reached in this test").Runner,
            cancellationToken: CancellationToken.None);

        exitCode.Should().Be(0);
        File.GetUnixFileMode(_scopedHome.Home).Should().Be(OwnerOnly);
    }
}
