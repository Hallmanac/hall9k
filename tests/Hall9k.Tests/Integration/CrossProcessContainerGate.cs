using Hall9k.Daemon.Execution;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// A permit-file bound: at most <c>maxConcurrent</c> holders of everything under
/// <c>gateDirectory</c>-as-passed-to-<see cref="AcquireAsync"/> may be active at
/// once, enforced machine-wide rather than per <c>dotnet test</c> process (Decisions Log #132,
/// following up #108). <see cref="PostgresFixture"/>'s original <c>ConcurrencyGate</c> was a static
/// <c>SemaphoreSlim</c>, which only ever bounded one process: N of them running at once —
/// concurrent gate runs under a raised ceiling, a fix session's own foreground suite, an
/// operator's own run — each got an independent 4, multiplying the bound into the exact OOM
/// #108 exists to prevent, a gap #108's own text named without ever closing. A permit here
/// <em>is</em> an exclusive file lock
/// (<see cref="FileShare.None"/>) held open on one of <c>maxConcurrent</c> fixed slot files
/// under that directory — the same mechanism
/// <c>GitWorktreeManager.AcquireCrossProcessLockAsync</c> already uses to serialize the daemon
/// and the CLI against one repository (<see cref="FileShare.None"/> maps to a real exclusive
/// lock on Windows and an exclusive advisory lock on Unix). Any process on the machine —
/// another <c>dotnet test</c> invocation, this one's own next class, or a process this gate has
/// never heard of — contends for the same fixed set of files, so the bound holds regardless of
/// how many processes are asking, with no shared counter or heartbeat of this gate's own to keep
/// consistent across them.
/// <para>
/// Reclaiming a dead holder's permit needs no code of its own. An exclusive file lock lives on
/// the operating system's own per-process open-file table, not in anything this gate writes
/// down, so the OS releases it the instant the holding process's handles close — a crash or a
/// kill exactly as much as a graceful <see cref="IAsyncDisposable.DisposeAsync"/>. That is the
/// property this repo's own AGENTS.md insists is "stated and tested rather than assumed": see
/// <c>CrossProcessContainerGateTests</c> for the case that kills a real holder mid-hold and
/// confirms its permit is immediately acquirable again.
/// </para>
/// </summary>
// Not itself HALL9K_HOME-related — the AcquireAsync change that added
// Environment.GetEnvironmentVariable reads GateWaitEvidenceDirectoryEnvironmentVariable, a
// per-verify-run wait-evidence directory that has nothing to do with the platform home — but
// HomeEnvironmentIsolationTests' own guard matches by member name alone, not by which variable
// is being read, and its own failure message says to add this attribute rather than special-case
// the scan; it is inert here since this class carries no test methods of its own for xUnit to
// schedule.
[Collection("Hall9kHome")]
internal static class CrossProcessContainerGate
{
    // Between sweeps of the whole permit set: short enough that a permit released elsewhere is
    // picked up promptly, long enough not to spin a CPU core while every permit is legitimately
    // held by someone else.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    public static async Task<IAsyncDisposable> AcquireAsync(
        string gateDirectory, int maxConcurrent, CancellationToken cancellationToken)
    {
        // Below 1, the slot loop below never runs, so every caller would spin at PollInterval
        // until its own cancellation fires instead of ever finding out why (independent review,
        // this cycle) — fail fast here instead, since this is a general-purpose gate helper, not
        // one hardcoded to PostgresFixture's own fixed 4.
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrent, 1);

        Directory.CreateDirectory(gateDirectory);

        DateTimeOffset waitStarted = DateTimeOffset.UtcNow;
        DateTimeOffset nextLogAt = waitStarted.AddSeconds(1);

        // Two independent evidence targets, because they answer two different questions and
        // conflating them would misinform the one that matters more (adversarial review, this
        // cycle: reproduced against this repo's own package versions — a wait line written only
        // via Console.Error never reaches anyone, live or after the fact, because vstest.console
        // buffers a testhost's console output internally and only relays it if it survives long
        // enough to report the testhost's own death, which VerificationRunner's own
        // process.Kill(entireProcessTree: true) never allows: root, dotnet test, vstest.console
        // and testhost all die together).
        //
        // 1. discoverableWaitFile, unconditional, inside gateDirectory itself: the fixed, shared,
        // machine-wide location this gate's own doc comment already names as the one place a
        // wedged wait can be told apart from ordinary queueing. Nothing here can make a live
        // progress line reach an operator's own terminal from inside a test host — that guarantee
        // genuinely does not exist the way it does for GitWorktreeManager.AcquireCrossProcessLockAsync,
        // whose wait runs in the daemon or CLI process, whose console really is the operator's own
        // — but `ls`/`cat` against a fixed, documented path is a real substitute an operator (or a
        // fix session running its own mandatory foreground suite per AGENTS.md) can reach for
        // whenever `dotnet test` looks stuck.
        // 2. waitEvidenceFile, set only by VerificationRunner on a dotnet-test-shaped gate's own
        // process tree, naming a directory scoped to this one gate run
        // (GateInfrastructureFailureClassifier.IsUnresolvedGateWaitTimeout is the reader). Kept
        // separate from gateDirectory on purpose: gateDirectory is shared by every dotnet test
        // invocation on the machine, so a file dropped there could belong to an entirely unrelated
        // process's own wait and falsely tell this run's own timeout classification that it, too,
        // was still queued.
        string discoverableWaitFile = Path.Combine(gateDirectory, $"waiting-{Environment.ProcessId}-{Guid.NewGuid():N}.txt");
        string? waitEvidenceDirectory = Environment.GetEnvironmentVariable(
            GateInfrastructureFailureClassifier.GateWaitEvidenceDirectoryEnvironmentVariable);
        string? waitEvidenceFile = waitEvidenceDirectory is null
            ? null
            : Path.Combine(waitEvidenceDirectory, $"waiting-{Environment.ProcessId}-{Guid.NewGuid():N}.txt");

        try
        {
            while (true)
            {
                for (int slot = 0; slot < maxConcurrent; slot++)
                {
                    if (TryOpen(Path.Combine(gateDirectory, $"permit-{slot}.lock")) is { } stream)
                    {
                        return new Permit(stream);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();

                // At least one caller of this gate waits with no deadline at all (PostgresFixture's
                // own InitializeAsync, since a fixed deadline sized for one process's load
                // misdiagnoses genuine cross-process contention as a stuck gate — see its own comment
                // above ContainerStartTimeout). GitWorktreeManager.AcquireCrossProcessLockAsync's own
                // unbounded wait was silent for exactly that reason until an operator watched it print
                // nothing for minutes and gave up; the evidence files below are this wait's own
                // equivalent of that fix — a progress line alone cannot be, for the reasons above.
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (now >= nextLogAt)
                {
                    // Deliberately does not name "another process on this machine" as the holder:
                    // unlike GitWorktreeManager.AcquireCrossProcessLockAsync, this gate has no
                    // in-process semaphore in front of it, so this process's own other test classes
                    // are routinely the contenders too (this file's own doc comment above names
                    // "this one's own next class" as a legitimate holder) — stating only what was
                    // actually observed (independent pre-PR review, cycle 1).
                    string diagnostic =
                        $"Waiting on cross-process container gate {gateDirectory} " +
                        $"({(now - waitStarted).TotalSeconds:0}s elapsed, {maxConcurrent} max concurrent) " +
                        "— every permit is currently held (by this process's own other classes, " +
                        "or by another process on this machine)";
                    Console.Error.WriteLine(diagnostic);
                    TryWriteEvidence(discoverableWaitFile, diagnostic);

                    if (waitEvidenceFile is not null)
                    {
                        TryWriteEvidence(waitEvidenceFile, diagnostic);
                    }

                    nextLogAt = now.AddSeconds(5);
                }

                await Task.Delay(PollInterval, cancellationToken);
            }
        }
        finally
        {
            // Cleared on every exit from the wait — success, cancellation, or an unexpected
            // exception alike — so a permit acquired cleanly (or a wait abandoned by the caller)
            // never leaves either file behind for a later, unrelated timeout (or a curious
            // operator, for discoverableWaitFile) to mistake for a wait still in progress.
            TryDeleteEvidence(discoverableWaitFile);
            if (waitEvidenceFile is not null)
            {
                TryDeleteEvidence(waitEvidenceFile);
            }
        }
    }

    private static void TryWriteEvidence(string path, string diagnostic)
    {
        try
        {
            File.WriteAllText(path, diagnostic);
        }
        catch (DirectoryNotFoundException)
        {
            // The containing directory vanished (or, for the env-var-provided target, was never
            // created — a caller that names one but never provisions it). Best-effort: a lost
            // evidence write degrades this call back to whatever other visibility it still has,
            // it never fails the wait itself.
        }
    }

    private static void TryDeleteEvidence(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Nothing else reads this file once the wait is over; a failed best-effort cleanup
            // is not this call's problem to solve.
        }
    }

    private static FileStream? TryOpen(string permitPath)
    {
        // Tracked outside the try so every catch below can dispose it: the exclusive lock is
        // taken the instant the FileStream constructor succeeds, before the mtime-refresh write
        // below ever runs, so a failure in that write still has a locked stream to release —
        // otherwise this method would return null or throw while quietly holding a permit open
        // forever, since Dispose is the only release this gate ever performs (conformance
        // review, cycle 1: an ENOSPC on the write used to leak exactly this way).
        FileStream? stream = null;

        try
        {
            // Holding the stream open is the entire mechanism: FileShare.None refuses a second
            // concurrent open anywhere on the machine, and Dispose (below) is the only release
            // this gate ever performs on the happy path — process death releases it exactly the
            // same way, through the OS, without this method or anything else here running again.
            stream = new FileStream(permitPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            // The one write this gate ever makes through a held permit — not to record anything
            // (nothing here ever reads it back), but to refresh the file's mtime the instant a
            // hold begins. A permit file otherwise never touched again after its first creation
            // is exactly what a temp-directory reaper ages off by mtime regardless of how heavily
            // it is actually used (systemd-tmpfiles' default /tmp rule deletes anything untouched
            // for 10 days, evaluated against the file's on-disk timestamp, not against whether a
            // flock currently holds it): the flock would survive on the now-unlinked inode while
            // the next acquirer's OpenOrCreate at the same path silently creates and locks a
            // fresh one, doubling the bound with no error and no log line. Writing through the
            // already-open handle (rather than a second File.SetLastWriteTimeUtc call on the
            // path) is deliberate: a second open of any kind would collide with this same
            // stream's own FileShare.None on Windows, where a share mode of None blocks every
            // later open regardless of what access it requests, attribute-only writes included.
            stream.WriteByte(0);
            stream.Flush();
            stream.Seek(0, SeekOrigin.Begin);

            return stream;
        }
        catch (DirectoryNotFoundException)
        {
            // gateDirectory itself vanished after the one Directory.CreateDirectory call above
            // (a temp-directory reaper — systemd-tmpfiles, macOS's own /var/folders cleanup —
            // racing a mid-wait caller). No process can ever release a permit for a directory
            // that no longer exists, so retrying here would spin at PollInterval until the
            // caller's own cancellation fires instead of failing honestly right away — the exact
            // reasoning GitWorktreeManager.AcquireCrossProcessLockAsync already applies to this
            // same exception for the same reason. DirectoryNotFoundException derives from
            // IOException, so this has to be caught ahead of the catch below or it would be
            // swallowed as an ordinary "permit already held" result instead.
            stream?.Dispose();
            throw;
        }
        catch (UnauthorizedAccessException accessDenied)
        {
            // gateDirectory or this permit file is owned by a different user than the one
            // running this process — most often a prior run under sudo (or a different account)
            // created it first, since the directory is a fixed, shared path rather than a
            // per-user one (see ResolveGateDirectory's own comment in PostgresFixture for why).
            // Retrying here would spin at PollInterval until the caller's own cancellation
            // fires, misreporting a permanent permission mismatch as ordinary contention — every
            // other holder of a permit this process *can* open still releases it eventually, but
            // a permit this process can never open never will — so this fails immediately and
            // names the fix rather than letting .NET's own unadorned "Access to the path ... is
            // denied" propagate out of all 29 PostgresFixture acquisitions with no hint that a
            // stale, differently-owned gate directory is the cause.
            stream?.Dispose();
            throw new UnauthorizedAccessException(
                $"cannot open {permitPath}: it (or the gate directory containing it) is owned by " +
                "a different user than the one running this process — delete the gate directory " +
                "and retry, or run every process that uses it under the same account",
                accessDenied);
        }
        catch (IOException)
        {
            // Either the open itself found the permit already held elsewhere (the ordinary,
            // expected case — stream is still null here), or the open succeeded but the
            // mtime-refresh write then failed (ENOSPC on a full /tmp, most plausibly): either way
            // this permit is not usable, so any stream that was actually opened must be released
            // before returning null, or the exclusive lock it holds outlives this call forever.
            //
            // The write-failure case leaves Dispose() itself able to throw: WriteByte buffers
            // into the FileStream's own internal buffer, a failed Flush() does not clear that
            // buffer, and Dispose() flushes it again on the way out — the identical ENOSPC (or
            // whatever else failed the write) rethrows from here rather than this method
            // returning null as promised above (adversarial review, this cycle). Swallowed
            // rather than let escape: the outcome this comment documents — this permit is not
            // usable, the caller retries another slot — does not change just because releasing
            // it was noisier than releasing an untouched one.
            try
            {
                stream?.Dispose();
            }
            catch (IOException)
            {
            }

            return null;
        }
    }

    private sealed class Permit(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
