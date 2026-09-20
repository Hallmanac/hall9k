using FluentAssertions;
using Hall9k.Daemon.Execution;
using Hall9k.Tests.Integration;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The dead-waiter sweep and the holder sidecar <see cref="CrossProcessContainerGate"/> now
/// carries (task: the container gate sweeps dead waiters' files and records each holder so a
/// killed gate names who held the permits), proven entirely through a fake
/// <see cref="ContainerGateDirectory.LivenessProbe"/> and fabricated files in a temp directory —
/// like <see cref="CrossProcessContainerGateTests"/>, this lives in the DB-free unit tier, and
/// unlike that class's own real-process-kill test, nothing here spawns or kills a real process
/// (let alone a container): the probe answers "alive or not" by fiat, so both the sweep and the
/// survival case are deterministic and fast.
/// </summary>
public sealed class CrossProcessContainerGateLivenessTests
{
    [Fact]
    public async Task Becoming_a_waiter_sweeps_a_dead_waiters_file()
    {
        string gateDirectory = Directory.CreateTempSubdirectory("h9k-gate-sweep-test-").FullName;
        try
        {
            Directory.CreateDirectory(gateDirectory);
            string deadWaiterFile = Path.Combine(gateDirectory, "waiting-999999-deadbeefdeadbeefdeadbeefdeadbeef.txt");
            File.WriteAllText(deadWaiterFile, "stale evidence from a process that no longer exists");

            using CancellationTokenSource patient = new(TimeSpan.FromSeconds(10));
            await using IAsyncDisposable holder = await CrossProcessContainerGate.AcquireAsync(
                gateDirectory, maxConcurrent: 1, patient.Token, livenessProbe: null);

            using CancellationTokenSource busy = new(TimeSpan.FromMilliseconds(500));
            Func<Task> waiterAttempt = async () =>
                await CrossProcessContainerGate.AcquireAsync(
                    gateDirectory, maxConcurrent: 1, busy.Token, livenessProbe: (processId, _) => processId != 999999);

            await waiterAttempt.Should().ThrowAsync<OperationCanceledException>(
                "the only permit is already held, so this call must actually wait");

            File.Exists(deadWaiterFile).Should().BeFalse(
                "a wait file whose recorded process the probe says is no longer alive is deleted the " +
                "moment this call becomes a waiter");
        }
        finally
        {
            Directory.Delete(gateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Becoming_a_waiter_never_sweeps_a_live_waiters_file()
    {
        string gateDirectory = Directory.CreateTempSubdirectory("h9k-gate-sweep-test-").FullName;
        try
        {
            Directory.CreateDirectory(gateDirectory);
            string liveWaiterFile = Path.Combine(gateDirectory, "waiting-424242-deadbeefdeadbeefdeadbeefdeadbeef.txt");
            File.WriteAllText(liveWaiterFile, "evidence from a process the probe reports as alive");

            using CancellationTokenSource patient = new(TimeSpan.FromSeconds(10));
            await using IAsyncDisposable holder = await CrossProcessContainerGate.AcquireAsync(
                gateDirectory, maxConcurrent: 1, patient.Token, livenessProbe: null);

            using CancellationTokenSource busy = new(TimeSpan.FromMilliseconds(500));
            Func<Task> waiterAttempt = async () =>
                await CrossProcessContainerGate.AcquireAsync(
                    gateDirectory, maxConcurrent: 1, busy.Token, livenessProbe: (_, _) => true);

            await waiterAttempt.Should().ThrowAsync<OperationCanceledException>(
                "the only permit is already held, so this call must actually wait");

            File.Exists(liveWaiterFile).Should().BeTrue(
                "a wait file whose recorded process the probe reports as still alive survives the sweep");
        }
        finally
        {
            Directory.Delete(gateDirectory, recursive: true);
        }
    }

    /// <summary>
    /// The sidecar names who held the permit — this process's own pid and working directory —
    /// and disappears with the permit on the graceful release path, so a later, ordinary
    /// acquire-then-release cycle never leaves it behind for a killed gate's own diagnostic to
    /// misread as a still-held slot.
    /// </summary>
    [Fact]
    public async Task An_acquired_permit_gets_a_holder_sidecar_that_disposal_removes()
    {
        string gateDirectory = Directory.CreateTempSubdirectory("h9k-gate-sidecar-test-").FullName;
        try
        {
            using CancellationTokenSource patient = new(TimeSpan.FromSeconds(10));
            IAsyncDisposable permit = await CrossProcessContainerGate.AcquireAsync(
                gateDirectory, maxConcurrent: 1, patient.Token);

            string sidecarPath = Path.Combine(gateDirectory, "permit-0.lock" + ContainerGateDirectory.SidecarSuffix);
            File.Exists(sidecarPath).Should().BeTrue("acquiring a permit writes a sidecar naming its holder");

            ContainerGateDirectory.SidecarHolder holder = ContainerGateDirectory.ParseSidecar(File.ReadAllText(sidecarPath));
            holder.ProcessId.Should().Be(Environment.ProcessId);
            holder.WorkingDirectory.Should().Be(Environment.CurrentDirectory);
            holder.ProcessStartTimeUtc.Should().NotBeNull();
            holder.AcquiredAtUtc.Should().NotBeNull();

            await permit.DisposeAsync();

            File.Exists(sidecarPath).Should().BeFalse("releasing the permit removes its own sidecar");
        }
        finally
        {
            Directory.Delete(gateDirectory, recursive: true);
        }
    }
}
