using System.Diagnostics;
using FluentAssertions;
using Hall9k.Tests.Integration;
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
/// </summary>
public sealed class CrossProcessContainerGateTests
{
    [Fact]
    public async Task At_most_maxConcurrent_permits_are_held_at_once_across_independent_acquisitions()
    {
        string gateDirectory = Directory.CreateTempSubdirectory("h9k-gate-test-").FullName;
        try
        {
            using CancellationTokenSource patient = new(TimeSpan.FromSeconds(30));

            // Deliberately not an `await using` declaration: it is disposed explicitly mid-test
            // below to free a slot for `third`, and a `using` variable cannot be reassigned to
            // null afterward to guard against a second, scope-exit disposal (independent review,
            // this cycle).
            IAsyncDisposable? first = await CrossProcessContainerGate.AcquireAsync(
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
            first = null;

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
