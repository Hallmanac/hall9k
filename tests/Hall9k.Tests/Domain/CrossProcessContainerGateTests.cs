using System.Diagnostics;
using FluentAssertions;
using Hall9k.Tests.Integration;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <see cref="CrossProcessContainerGate"/> backs <see cref="Hall9k.Tests.Integration.PostgresFixture"/>'s
/// container bound (Decisions Log #108's follow-up), but this class itself starts no Postgres
/// container and needs no Docker, so — like <see cref="ContainerRoutingGuardTests"/> — it lives
/// in the DB-free unit tier even though the thing it tests lives in the integration one.
/// <para>
/// The two claims this repo's own AGENTS.md insists are "stated and tested rather than assumed"
/// get one test each: that the bound actually holds across independent acquisitions (not just
/// within one caller's own await chain), and that a permit held by a process that dies without
/// ever running a release path is reclaimed anyway. The second one is the whole reason this
/// project references <c>Hall9k.Tests.LockHolder</c>: an in-process simulation of "the holder
/// died" always ends up calling <see cref="IAsyncDisposable.DisposeAsync"/> somewhere, which
/// proves this gate's own Dispose works and nothing about what happens when nothing runs it —
/// only a real second process, killed hard enough that the OS itself tears down its open-file
/// table, proves that.
/// </para>
/// <para>
/// <c>[Collection("RealProcessSpawn")]</c> (Decisions Log PLACEHOLDER-f70cc244): the real
/// <c>dotnet</c> child this class spawns for the second claim above overlapped PR #312's own
/// <c>ProcessManagerParityTests</c> failure window on windows-latest — confirmed from that job's
/// own log, not assumed — while carrying no <c>[Collection]</c> of its own; see
/// <see cref="Hall9k.Tests.Daemon.ProcessManagerParityTests"/>'s own doc comment for the evidence
/// and the shared collection this joins.
/// </para>
/// </summary>
[Collection("RealProcessSpawn")]
public sealed class CrossProcessContainerGateTests
{
    [Fact]
    public async Task At_most_maxConcurrent_permits_are_held_at_once_across_independent_acquisitions()
    {
        string gateDirectory = Directory.CreateTempSubdirectory("h9k-gate-test-").FullName;
        try
        {
            using CancellationTokenSource patient = new(TimeSpan.FromSeconds(30));

            // An `await using` declaration despite being disposed explicitly mid-test below to
            // free a slot for `third`: Permit.DisposeAsync only calls FileStream.Dispose(), which
            // is idempotent, so the scope-exit disposal this declaration adds on top of the
            // explicit one below is harmless — and it is what keeps a failure of the assertion
            // between the two (`thirdAttempt` unexpectedly not throwing) from leaking this held
            // permit into the outer `finally`'s Directory.Delete, the same masking the third
            // test's own `finally` comment below guards against (conformance/adversarial review,
            // this cycle).
            await using IAsyncDisposable first = await CrossProcessContainerGate.AcquireAsync(
                gateDirectory, maxConcurrent: 2, patient.Token);
            await using IAsyncDisposable second = await CrossProcessContainerGate.AcquireAsync(
                gateDirectory, maxConcurrent: 2, patient.Token);

            using CancellationTokenSource busy = new(TimeSpan.FromMilliseconds(300));
            Func<Task> thirdAttempt = async () =>
                await CrossProcessContainerGate.AcquireAsync(gateDirectory, maxConcurrent: 2, busy.Token);

            await thirdAttempt.Should().ThrowAsync<OperationCanceledException>(
                "both permits this gate hands out are already held by independent acquisitions, so a " +
                "third must wait rather than the gate handing out a fifth-wheel permit and letting the " +
                "bound be exceeded");

            await first.DisposeAsync();

            await using IAsyncDisposable third = await CrossProcessContainerGate.AcquireAsync(
                gateDirectory, maxConcurrent: 2, patient.Token);
        }
        finally
        {
            Directory.Delete(gateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task A_released_permit_can_be_acquired_again()
    {
        string gateDirectory = Directory.CreateTempSubdirectory("h9k-gate-test-").FullName;
        try
        {
            using CancellationTokenSource patient = new(TimeSpan.FromSeconds(30));

            IAsyncDisposable first = await CrossProcessContainerGate.AcquireAsync(
                gateDirectory, maxConcurrent: 1, patient.Token);
            await first.DisposeAsync();

            await using IAsyncDisposable second = await CrossProcessContainerGate.AcquireAsync(
                gateDirectory, maxConcurrent: 1, patient.Token);
        }
        finally
        {
            Directory.Delete(gateDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Kills a real, separate OS process mid-hold — <see cref="Process.Kill(bool)"/> maps to
    /// SIGKILL on Unix and TerminateProcess on Windows, neither of which ever lets the target run
    /// a <c>finally</c> or <c>using</c> block — and confirms the permit it held is reclaimed
    /// without this gate doing anything to notice the death: the same polling loop
    /// <see cref="CrossProcessContainerGate.AcquireAsync"/> always runs just finds the file
    /// openable again, because the OS released the lock as part of tearing the process down.
    /// </summary>
    [Fact]
    public async Task A_permit_held_by_a_process_that_dies_without_releasing_is_reclaimed()
    {
        string gateDirectory = Directory.CreateTempSubdirectory("h9k-gate-test-").FullName;
        try
        {
            // Matches CrossProcessContainerGate's own slot-naming convention (permit-<n>.lock)
            // so the holder process and this test's own later acquisition contend for the exact
            // same file the gate would hand out as permit 0 of 1.
            string permitPath = Path.Combine(gateDirectory, "permit-0.lock");

            using Process holder = StartLockHolder(permitPath);
            try
            {
                await WaitForLockedSignalAsync(holder, TimeSpan.FromSeconds(30));

                using (CancellationTokenSource busy = new(TimeSpan.FromMilliseconds(300)))
                {
                    Func<Task> whileHeld = async () =>
                        await CrossProcessContainerGate.AcquireAsync(gateDirectory, maxConcurrent: 1, busy.Token);

                    await whileHeld.Should().ThrowAsync<OperationCanceledException>(
                        "the holder process is alive and holds the only permit, so this must actually be " +
                        "contended — otherwise the reclaim this test proves below is not proving anything");
                }

                holder.Kill(entireProcessTree: true);
                using (CancellationTokenSource exitWait = new(TimeSpan.FromSeconds(10)))
                {
                    await holder.WaitForExitAsync(exitWait.Token);
                }

                using CancellationTokenSource reclaim = new(TimeSpan.FromSeconds(10));
                await using IAsyncDisposable reclaimed = await CrossProcessContainerGate.AcquireAsync(
                    gateDirectory, maxConcurrent: 1, reclaim.Token);
            }
            finally
            {
                // Every path above but the happy one (the contention assertion failing, the
                // reclaim itself failing, WaitForLockedSignalAsync timing out before the holder is
                // ever killed) would otherwise leave this child sitting in Task.Delay(Timeout.Infinite)
                // forever: a `using Process` disposes the .NET wrapper object, not the OS process it
                // points at, and nothing else here kills it except the happy path's own explicit
                // call above. On Windows a still-alive holder also still has permit-0.lock open
                // with FileShare.None, so the outer finally's Directory.Delete would throw
                // IOException and replace whatever assertion failure got us here with a
                // file-in-use error instead — waiting for exit here (not just issuing the kill)
                // closes that race too.
                KillAndWaitForExit(holder);
            }
        }
        finally
        {
            Directory.Delete(gateDirectory, recursive: true);
        }
    }

    /// <summary>
    /// The wait notice goes to <see cref="CrossProcessContainerGate.WaitNotice"/> and to the
    /// evidence files, and to no console anywhere — so a test capturing the console cannot observe
    /// it however unlucky its timing is. This drives a real queued wait rather than calling the
    /// notice directly: the point is what the production loop writes, not what a hand-rolled line
    /// would.
    /// <para>
    /// The capture scope is opened <em>around</em> the queued acquisition on purpose, which is the
    /// strongest form of the claim: even a wait running inside the capturing flow's own async
    /// context — where a console write would certainly be captured — leaves the buffer empty,
    /// because there is no console write. Origin incident (2026-09-17 03:25 EDT, run 01a0ad80):
    /// <c>TrackerAssignmentTests</c> asserted an empty stderr capture and found this notice in it,
    /// written by a different class entirely (PLAN.md §16 #220).
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_wait_notice_reaches_the_trace_source_and_no_captured_console()
    {
        string gateDirectory = Directory.CreateTempSubdirectory("h9k-gate-test-").FullName;

        // Scoped to this test's own gate directory, not "the first notice this listener sees":
        // PostgresFixture's own waits emit through the same trace source, and one of them landing
        // while this listener is attached would release the wait below before it had been queued
        // long enough to emit anything of its own — leaving the console assertions proving nothing
        // while the test still passed.
        RecordingTraceListener notices = new(gateDirectory);
        CrossProcessContainerGate.WaitNotice.Listeners.Add(notices);
        try
        {
            using CancellationTokenSource patient = new(TimeSpan.FromSeconds(30));
            await using IAsyncDisposable held = await CrossProcessContainerGate.AcquireAsync(
                gateDirectory, maxConcurrent: 1, patient.Token);

            using ScopedConsoleCapture standardError = ScopedConsoleCapture.StandardError();
            using ScopedConsoleCapture standardOutput = ScopedConsoleCapture.StandardOutput();

            // No deadline of this test's own on the wait itself: the listener completing is the
            // signal that the loop has emitted its first notice (one second in), so this neither
            // polls nor races a duration.
            using CancellationTokenSource abandoned = new();
            Task<IAsyncDisposable> queued = CrossProcessContainerGate.AcquireAsync(
                gateDirectory, maxConcurrent: 1, abandoned.Token);

            string notice = await notices.FirstNotice.WaitAsync(patient.Token);

            await abandoned.CancelAsync();
            Func<Task> abandonedWait = async () => await queued;
            await abandonedWait.Should().ThrowAsync<OperationCanceledException>(
                "the permit is still held, so this wait only ever ends by being abandoned");

            notice.Should().Contain("Waiting on cross-process container gate")
                .And.Contain("max concurrent",
                    "the notice still has to say what it always said — this moved which channel carries it, not what it carries");
            standardError.Text.Should().BeEmpty(
                "a wait notice on the process-wide stderr is exactly what put another class's line inside " +
                "TrackerAssignmentTests' own assertion; it must reach the trace source and the evidence files only");
            standardOutput.Text.Should().BeEmpty("nor may it have moved to the other process-wide stream");
        }
        finally
        {
            CrossProcessContainerGate.WaitNotice.Listeners.Remove(notices);
            Directory.Delete(gateDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Completes <see cref="FirstNotice"/> with the first notice
    /// <see cref="CrossProcessContainerGate.WaitNotice"/> emits <em>for one named gate
    /// directory</em> — every other wait in the process emits through the same trace source, and a
    /// listener that took whichever notice arrived first would answer for someone else's wait.
    /// <see cref="TraceListener.TraceEvent(TraceEventCache, string, TraceEventType, int, string)"/>
    /// writes its header through <see cref="Write(string)"/> and the message through
    /// <see cref="WriteLine(string)"/>, so the message alone is what the completion carries.
    /// </summary>
    private sealed class RecordingTraceListener(string gateDirectory) : TraceListener
    {
        private readonly TaskCompletionSource<string> first =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> FirstNotice => first.Task;

        public override void Write(string? message)
        {
            // The header ("Hall9k.Tests.ContainerGate Information: 0 : "), which says nothing this
            // test asserts on.
        }

        public override void WriteLine(string? message)
        {
            if (message is not null && message.Contains(gateDirectory, StringComparison.Ordinal))
            {
                first.TrySetResult(message);
            }
        }
    }

    private static void KillAndWaitForExit(Process holder)
    {
        try
        {
            holder.Kill(entireProcessTree: true);
            holder.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (InvalidOperationException)
        {
            // Already exited on its own — nothing to kill or wait for.
        }
    }

    private static Process StartLockHolder(string permitPath)
    {
        string lockHolderDll = Path.Combine(AppContext.BaseDirectory, "Hall9k.Tests.LockHolder.dll");
        File.Exists(lockHolderDll).Should().BeTrue(
            $"the Hall9k.Tests.LockHolder project reference should have copied its build output " +
            $"beside this test assembly at {lockHolderDll} — check the ProjectReference in " +
            "Hall9k.Tests.csproj if this starts failing");

        Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add(lockHolderDll);
        process.StartInfo.ArgumentList.Add(permitPath);
        process.Start();
        return process;
    }

    /// <summary>
    /// Never reads <see cref="Process.StandardError"/> to EOF while <paramref name="holder"/> is
    /// still alive: <see cref="StreamReader.ReadToEndAsync()"/> blocks until the pipe closes,
    /// which for a live child only happens at exit, so doing that for a diagnostic message on the
    /// happy path — or on a timeout, where the process may simply not have signaled yet rather
    /// than having died — would itself deadlock the caller. <paramref name="holder"/> is killed
    /// first in both failure paths below so the diagnostic read is only ever attempted once
    /// stderr is guaranteed to reach EOF on its own.
    /// </summary>
    private static async Task WaitForLockedSignalAsync(Process holder, TimeSpan timeout)
    {
        using CancellationTokenSource cts = new(timeout);
        string? line;
        try
        {
            line = await holder.StandardOutput.ReadLineAsync(cts.Token);
        }
        catch (OperationCanceledException canceled) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"timed out after {timeout} waiting for Hall9k.Tests.LockHolder to report it acquired " +
                $"the lock (stderr: {await KillAndReadStandardErrorAsync(holder)})",
                canceled);
        }

        if (line != "LOCKED")
        {
            throw new InvalidOperationException(
                "Hall9k.Tests.LockHolder must report it holds the lock before this test proceeds, but " +
                $"printed {(line is null ? "nothing (EOF)" : $"\"{line}\"")} instead " +
                $"(stderr: {await KillAndReadStandardErrorAsync(holder)})");
        }
    }

    private static async Task<string> KillAndReadStandardErrorAsync(Process holder)
    {
        try
        {
            holder.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited on its own — nothing to kill, and stderr is already at EOF.
        }

        return await holder.StandardError.ReadToEndAsync();
    }
}
