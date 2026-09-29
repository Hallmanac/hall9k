using FluentAssertions;
using Hall9k.Cli.Diagnostics;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="HomeDirectoryPermissions"/>'s two entry points against a temp home this class owns
/// (security review idea 6be68ee2, secrets-files-network finding 8): the doctor's own
/// report-then-repair-only-with-<c>--yes</c> shape (<see cref="HomeDirectoryPermissions.Check(bool)"/>),
/// and the Windows no-op both entry points share, since <see cref="File.SetUnixFileMode"/> throws
/// <see cref="PlatformNotSupportedException"/> there. <c>InstallCommandHomeDirectoryPermissionsTests</c>
/// covers <see cref="HomeDirectoryPermissions.NarrowIfWider"/> through the real
/// <c>InstallCommand.FinishAsync</c> call site instead of calling it directly, so that one call
/// site's own wiring is what gets proven. No test here asserts anything about a file underneath the
/// home — the whole point of a single directory mode is that it needs no per-file rule to check.
/// </summary>
public sealed class HomeDirectoryPermissionsTests : IDisposable
{
    private readonly ScopedTestHome _scopedHome = new();

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode WideMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    public void Dispose() => _scopedHome.Dispose();

    [Fact]
    public void Doctor_check_reports_a_wide_home_and_repairs_it_with_yes()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(_scopedHome.Home, WideMode);

        string output = ScopedAnsiConsoleCapture.Capture(() => HomeDirectoryPermissions.Check(assumeYes: true));

        File.GetUnixFileMode(_scopedHome.Home).Should().Be(OwnerOnly);
        output.Should().Contain("0755").And.Contain("Narrowed").And.Contain("0700");
    }

    [Fact]
    public void Doctor_check_without_yes_reports_but_leaves_the_mode_alone()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(_scopedHome.Home, WideMode);

        string output = ScopedAnsiConsoleCapture.Capture(() => HomeDirectoryPermissions.Check(assumeYes: false));

        File.GetUnixFileMode(_scopedHome.Home).Should().Be(WideMode, "no --yes was given, so nothing was repaired");
        output.Should().Contain("0755").And.Contain("--yes");
    }

    [Fact]
    public void Doctor_check_is_silent_when_the_home_is_already_0700()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(_scopedHome.Home, OwnerOnly);

        string output = ScopedAnsiConsoleCapture.Capture(() => HomeDirectoryPermissions.Check(assumeYes: true));

        output.Should().BeEmpty("there is nothing to report — the home was already 0700");
    }

    [Fact]
    public void Both_entry_points_are_no_ops_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Action narrow = HomeDirectoryPermissions.NarrowIfWider;
        Action check = () => HomeDirectoryPermissions.Check(assumeYes: true);

        narrow.Should().NotThrow("File.SetUnixFileMode throws PlatformNotSupportedException on Windows, "
            + "so both entry points must be guarded by OperatingSystem.IsWindows() rather than reach it");
        check.Should().NotThrow();

        string output = ScopedAnsiConsoleCapture.Capture(() => HomeDirectoryPermissions.Check(assumeYes: true));
        output.Should().BeEmpty("the profile ACL is the boundary on Windows, not a directory mode");
    }
}
