using System.Diagnostics;
using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Domain.Infrastructure.Storage;
using Xunit;

namespace Hall9k.Tests.Daemon;

public sealed class SingleInstanceGuardTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("hall9k-guard-").FullName;

    private string LockPath => Path.Combine(_directory, "h9kd.lock");
    private string PidPath => Path.Combine(_directory, "h9kd.pid");

    [Fact]
    public void Second_acquire_is_refused_while_the_first_holds_the_lock()
    {
        using SingleInstanceGuard? first = SingleInstanceGuard.TryAcquire(LockPath, PidPath);

        first.Should().NotBeNull();
        SingleInstanceGuard.TryAcquire(LockPath, PidPath).Should().BeNull(
            "one daemon per node — the lock is the race-proof backstop behind the CLI's polite refusal");
    }

    [Fact]
    public void Released_lock_can_be_acquired_again()
    {
        SingleInstanceGuard.TryAcquire(LockPath, PidPath)!.Dispose();

        using SingleInstanceGuard? second = SingleInstanceGuard.TryAcquire(LockPath, PidPath);
        second.Should().NotBeNull("a graceful stop must not brick the next start");
    }

    [Fact]
    public void Acquire_records_this_process_identity_and_dispose_removes_it()
    {
        using Process current = Process.GetCurrentProcess();

        SingleInstanceGuard guard = SingleInstanceGuard.TryAcquire(LockPath, PidPath)!;
        DaemonProcessDescriptor? recorded = DaemonPidFile.TryRead(PidPath);

        recorded.Should().NotBeNull();
        recorded!.ProcessId.Should().Be(current.Id);
        recorded.StartedAt.Should().BeCloseTo(
            new DateTimeOffset(current.StartTime.ToUniversalTime(), TimeSpan.Zero),
            TimeSpan.FromSeconds(1),
            "start time makes the pid an identity, not a guess (Decisions Log #2)");

        guard.Dispose();
        DaemonPidFile.TryRead(PidPath).Should().BeNull("a clean exit leaves no stale identity behind");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
