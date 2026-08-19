using FluentAssertions;
using Hall9k.Domain.Infrastructure.Storage;
using Xunit;

namespace Hall9k.Tests.Domain;

public sealed class DaemonPidFileTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("hall9k-pidfile-").FullName;

    private string PidPath => Path.Combine(_directory, "h9kd.pid");

    [Fact]
    public void Round_trips_pid_and_start_time()
    {
        DaemonProcessDescriptor descriptor = new(4242, DateTimeOffset.Parse("2026-08-19T10:00:00Z"));

        DaemonPidFile.Write(PidPath, descriptor);

        DaemonPidFile.TryRead(PidPath).Should().Be(descriptor,
            "pid + start time is the process identity contract (Decisions Log #2)");
    }

    [Fact]
    public void Missing_file_reads_as_null_not_an_error()
    {
        DaemonPidFile.TryRead(PidPath).Should().BeNull();
    }

    [Fact]
    public void Corrupt_file_reads_as_null_not_an_error()
    {
        File.WriteAllText(PidPath, "not json at all {");

        DaemonPidFile.TryRead(PidPath).Should().BeNull(
            "a half-written pid file must read as 'not running', never crash the CLI");
    }

    [Fact]
    public void Unreadable_file_reads_as_null_not_an_error()
    {
        DaemonPidFile.Write(PidPath, new DaemonProcessDescriptor(4242, DateTimeOffset.UtcNow));
        if (!MadeUnreadable(PidPath))
        {
            // Windows has no POSIX mode, and root reads through one; on either the case
            // this test describes cannot be staged, so there is nothing to assert.
            return;
        }

        DaemonPidFile.TryRead(PidPath).Should().BeNull(
            "a pid file this account cannot read tells us nothing about the daemon — "
            + "'not running' is the honest reading, not a crash in the CLI");
    }

    /// <summary>
    /// Strips every permission bit and confirms the read is actually denied. False when
    /// the platform or the caller's privileges make the denial impossible to stage.
    /// </summary>
    private static bool MadeUnreadable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            File.ReadAllText(path);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    [Fact]
    public void Delete_is_idempotent()
    {
        DaemonPidFile.Write(PidPath, new DaemonProcessDescriptor(1, DateTimeOffset.UtcNow));

        DaemonPidFile.Delete(PidPath);
        DaemonPidFile.Delete(PidPath);

        File.Exists(PidPath).Should().BeFalse();
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
