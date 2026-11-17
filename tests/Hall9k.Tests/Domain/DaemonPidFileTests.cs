using System.Text.Json;
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
    public void The_daemons_record_reads_back_with_the_value_it_inherited()
    {
        DaemonProcessDescriptor descriptor = new(4242, DateTimeOffset.Parse("2026-11-17T10:00:00Z"));

        DaemonPidFile.Write(PidPath, descriptor, new DaemonInheritedEnvironment("/Users/dev/.claude-alt"));

        DaemonPidFile.TryReadRecord(PidPath).Should().Be(
            new DaemonPidRecord(descriptor, new DaemonInheritedEnvironment("/Users/dev/.claude-alt")));
        DaemonPidFile.TryRead(PidPath).Should().Be(descriptor, "the identity read is unchanged by the extra field");
    }

    [Fact]
    public void The_daemons_record_reads_back_with_an_explicit_none()
    {
        DaemonProcessDescriptor descriptor = new(4242, DateTimeOffset.Parse("2026-11-17T10:00:00Z"));

        DaemonPidFile.Write(PidPath, descriptor, new DaemonInheritedEnvironment(null));

        DaemonPidFile.TryReadRecord(PidPath).Should().Be(new DaemonPidRecord(descriptor, new DaemonInheritedEnvironment(null)));
    }

    [Fact]
    public void A_pid_file_an_older_build_wrote_reads_as_no_record_of_the_environment()
    {
        File.WriteAllText(PidPath, """{"processId":4242,"startedAt":"2026-11-17T10:00:00+00:00"}""");

        DaemonPidRecord? record = DaemonPidFile.TryReadRecord(PidPath);

        record.Should().NotBeNull();
        record?.Daemon.Should().Be(new DaemonProcessDescriptor(4242, DateTimeOffset.Parse("2026-11-17T10:00:00Z")));
        record?.Inherited.Should().BeNull("an older build recorded nothing, which is not the same as recording none");
    }

    [Fact]
    public void The_record_carries_the_one_variable_and_nothing_else()
    {
        DaemonInheritedEnvironment.Read(name => name switch
        {
            "CLAUDE_CONFIG_DIR" => "/Users/dev/.claude-alt",
            _ => "something else",
        }).Should().Be(new DaemonInheritedEnvironment("/Users/dev/.claude-alt"));
        DaemonInheritedEnvironment.Read(_ => "").Should().Be(new DaemonInheritedEnvironment(null), "an empty value is none");
        DaemonInheritedEnvironment.Read(_ => null).Should().Be(new DaemonInheritedEnvironment(null));

        DaemonPidFile.Write(PidPath, new DaemonProcessDescriptor(1, DateTimeOffset.UtcNow), new DaemonInheritedEnvironment("/x"));
        using JsonDocument written = JsonDocument.Parse(File.ReadAllText(PidPath));
        written.RootElement.GetProperty("inheritedEnvironment").EnumerateObject().Select(property => property.Name)
            .Should().Equal("claudeConfigDirectory");
    }

    [Fact]
    public void Missing_file_reads_as_null_not_an_error()
    {
        DaemonPidFile.TryRead(PidPath).Should().BeNull();
        DaemonPidFile.TryReadRecord(PidPath).Should().BeNull();
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
