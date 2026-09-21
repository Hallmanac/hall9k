using System.Diagnostics;
using FluentAssertions;
using Hall9k.Cli.DaemonControl;
using Hall9k.Domain.Infrastructure.Storage;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The launcher that replaced cmd.exe's <c>&gt;&gt;</c> redirect on both Windows daemon launch
/// paths (PLAN.md §16 PLACEHOLDER-d4e64dfa). The environment-block assertions run on every CI leg because the
/// block is ordinary string work; everything that actually creates a process is Windows-only,
/// the same convention <c>WindowsAppendOnlyLogTests</c> and <c>WindowsDaemonAutostartTests</c>
/// already follow — <c>CreateProcess</c>, inheritable handles and mandatory sharing have no
/// ubuntu equivalent to reproduce them with.
/// <para>
/// <c>[Collection("RealProcessSpawn")]</c> (PLAN.md §16 #172): every process this class creates
/// goes through <c>WindowsDaemonLaunch.Create</c>, which opens
/// <c>WindowsStandardHandleInheritance.SuppressForChildProcesses()</c> around its
/// <c>CreateProcess</c> call — a write to this process's own std-handle inherit flags, shared
/// with every test running at that moment. That made it the unseen half of a race against
/// <c>WindowsStandardHandleInheritanceTests</c>, which asserts on those same flags and carries
/// the full account; the two now share this lane. The real <c>cmd.exe</c> children below earn
/// the membership on #172's own process-creation-throughput grounds as well.
/// </para>
/// </summary>
[Collection("RealProcessSpawn")]
[Trait("Category", "RealProcessSpawn")]
public sealed class WindowsDaemonLaunchTests : IDisposable
{
    private readonly string logFile = Path.Combine(
        Path.GetTempPath(), $"h9k-daemon-launch-{Path.GetRandomFileName()}.log");

    public void Dispose()
    {
        try
        {
            File.Delete(logFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Teardown, not an assertion — every handle these tests open carries
            // FILE_SHARE_DELETE, so an unlinkable log means a child that has not finished dying
            // yet, which is worth a stray file in %TEMP% rather than a red test.
        }
    }

    [Fact]
    public void The_environment_block_carries_the_marker_that_tells_the_daemon_its_stdout_is_the_log()
    {
        // Program.cs takes over its own console output with WindowsAppendOnlyLog only when it
        // sees this, and it is set here rather than by each caller so the two launch paths
        // cannot diverge on it — nor can it be set by leaving it in the LAUNCHER's own
        // environment, where every other child h9k spawns would inherit it and redirect its own
        // console into the installed daemon's log.
        string block = WindowsDaemonLaunch.EnvironmentBlock([]);

        block.Should().Contain($"{DaemonRuntime.AppendOnlyLogEnvironmentVariable}=1\0");
        // Once, whatever this process's own environment happens to carry: the marker is a
        // launch fact, and an operator who exported it by hand must not end up with two
        // entries for it in the child's block.
        block.Split('\0')
            .Count(entry => entry.StartsWith(
                DaemonRuntime.AppendOnlyLogEnvironmentVariable, StringComparison.OrdinalIgnoreCase))
            .Should().Be(1);
    }

    [Fact]
    public void An_override_replaces_an_inherited_variable_of_the_same_name_whatever_its_case()
    {
        // Windows environment names are case-insensitive, so a block carrying both `PATH=` and
        // `Path=` leaves the child resolving one of them arbitrarily. The connection string
        // DaemonLifecycle passes down is exactly an override of this shape.
        string block = WindowsDaemonLaunch.EnvironmentBlock(
            [new("hall9k_daemon_launch_probe", "override")]);

        block.Should().Contain("hall9k_daemon_launch_probe=override\0");
        block.Split('\0')
            .Count(entry => entry.StartsWith("hall9k_daemon_launch_probe=", StringComparison.OrdinalIgnoreCase))
            .Should().Be(1);
    }

    [Fact]
    public void The_environment_block_is_sorted_and_doubly_null_terminated()
    {
        // The shape CreateProcess's CREATE_UNICODE_ENVIRONMENT expects: NAME=VALUE entries each
        // terminated by a NUL, the whole block terminated by one more. Sorted because that is
        // what the OS and cmd.exe both produce, so a child reading its own block sees the
        // ordinary thing.
        string block = WindowsDaemonLaunch.EnvironmentBlock([new("ZZ_hall9k_probe", "z"), new("AA_hall9k_probe", "a")]);

        block.Should().EndWith("\0\0");
        string[] names = [.. block.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(entry => entry.Split('=')[0])];
        names.Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
        names.Should().Contain("AA_hall9k_probe").And.Contain("ZZ_hall9k_probe");
    }

    [Fact]
    public async Task A_launched_process_writes_its_output_through_the_handle_it_was_handed()
    {
        // The whole point of the type, end to end: this process opens the log, the child gets it
        // as stdout and stderr, and the child's own output lands in it without this process ever
        // owning a pipe. cmd.exe is a stand-in for h9kd here, with an argument chosen so the
        // child both writes a line and exits promptly.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await File.WriteAllTextAsync(logFile, "a line from an earlier daemon\r\n");

        int exitCode = await WindowsDaemonLaunch.RunUntilExitAsync(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Path.GetTempPath(),
            logFile,
            [],
            CancellationToken.None,
            "/c echo a line from the launched process");

        exitCode.Should().Be(0);
        // Appended, never overwritten: FILE_APPEND_DATA re-resolves end-of-file on every write,
        // which is the same property DaemonLogRotation's copy-then-truncate depends on.
        string written = ReadSharing(logFile);
        written.Should().StartWith("a line from an earlier daemon");
        written.Should().Contain("a line from the launched process");
    }

    /// <summary>
    /// Reads the log through a share mode that admits other writers, never
    /// <see cref="File.ReadAllText(string)"/>'s own default of <see cref="FileShare.Read"/>.
    /// Nothing here asserts anything about who else has the file open, and on a loaded machine
    /// something transient (an on-access scanner, the search indexer) touches a freshly written
    /// temp file often enough that the stricter read is a coin flip: it failed exactly once under
    /// a full-suite run, 2026-09-17, on a log this test had already finished writing.
    /// <c>WindowsAppendOnlyLogTests</c> carries the same helper for the same reason.
    /// </summary>
    private static string ReadSharing(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader text = new(stream);
        return text.ReadToEnd();
    }

    [Fact]
    public async Task The_launcher_answers_with_the_launched_process_own_exit_code()
    {
        // Task Scheduler's RestartOnFailure restarts only on a nonzero action exit, and the
        // autostart chain's exit code is this one relayed up through cmd.exe and wscript.exe
        // (WindowsDaemonAutostartTests covers that half). A launcher that answered with its own
        // success would make a crashed daemon look like a clean stop and the restart never fire.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        int exitCode = await WindowsDaemonLaunch.RunUntilExitAsync(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Path.GetTempPath(),
            logFile,
            [],
            CancellationToken.None,
            "/c exit /b 7");

        exitCode.Should().Be(7);
    }

    [Fact]
    public void The_launcher_leaves_no_handle_of_its_own_on_the_log_once_the_child_exists()
    {
        // The criterion this type exists for. A launcher still holding the log is what cmd.exe's
        // >> redirect was, and it is why h9kd's own takeover and DaemonLogRotation's truncation
        // were both refused on Windows forever. So: with a child alive and holding the log as its
        // stdout, the rotation's own open (FileAccess.ReadWrite) must succeed and its truncation
        // must land — which it cannot if anything is holding the file with a share mode that
        // excludes writers.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        int processId = WindowsDaemonLaunch.StartDetached(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Path.GetTempPath(),
            logFile,
            [],
            "/c ping -n 60 127.0.0.1");

        using Process child = Process.GetProcessById(processId);
        try
        {
            using (FileStream rotator = new(
                logFile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                rotator.SetLength(0);
            }
        }
        finally
        {
            child.Kill(entireProcessTree: true);
            child.WaitForExit();
        }
    }

    [Fact]
    public void A_binary_that_does_not_exist_fails_loudly_rather_than_reporting_a_started_daemon()
    {
        // CreateProcess answers a missing image with a Win32 error, not an exception, so an
        // unchecked return would hand DaemonLifecycle a process id of zero and let its poll
        // report "still starting" for twenty seconds about a daemon that was never created.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string missing = Path.Combine(Path.GetTempPath(), $"h9k-no-such-binary-{Path.GetRandomFileName()}.exe");
        Action start = () => WindowsDaemonLaunch.StartDetached(missing, Path.GetTempPath(), logFile, []);

        // The path and the error number both, so a caller's own framing plus this message is
        // enough to act on: 2 here is ERROR_FILE_NOT_FOUND.
        start.Should().Throw<System.ComponentModel.Win32Exception>()
            .Which.Message.Should().Contain(missing).And.Contain("Win32 error 2");
    }
}
