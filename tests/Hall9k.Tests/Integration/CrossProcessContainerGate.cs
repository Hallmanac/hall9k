namespace Hall9k.Tests.Integration;

/// <summary>
/// A permit-file bound: at most <c>maxConcurrent</c> holders of everything under
/// <c>gateDirectory</c>-as-passed-to-<see cref="AcquireAsync"/> may be active at
/// once, enforced machine-wide rather than per <c>dotnet test</c> process (Decisions Log #130,
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
internal static class CrossProcessContainerGate
{
    // Between sweeps of the whole permit set: short enough that a permit released elsewhere is
    // picked up promptly, long enough not to spin a CPU core while every permit is legitimately
    // held by someone else.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    public static async Task<IAsyncDisposable> AcquireAsync(
        string gateDirectory, int maxConcurrent, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(gateDirectory);

        DateTimeOffset waitStarted = DateTimeOffset.UtcNow;
        DateTimeOffset nextLogAt = waitStarted.AddSeconds(1);

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
            // nothing for minutes and gave up; this mirrors its fix, same cadence.
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (now >= nextLogAt)
            {
                Console.Error.WriteLine(
                    $"Waiting on cross-process container gate {gateDirectory} " +
                    $"({(now - waitStarted).TotalSeconds:0}s elapsed, {maxConcurrent} max concurrent) " +
                    "— another process on this machine holds every permit");
                nextLogAt = now.AddSeconds(5);
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private static FileStream? TryOpen(string permitPath)
    {
        try
        {
            // Holding the stream open is the entire mechanism: FileShare.None refuses a second
            // concurrent open anywhere on the machine, and Dispose (below) is the only release
            // this gate ever performs on the happy path — process death releases it exactly the
            // same way, through the OS, without this method or anything else here running again.
            FileStream stream = new(permitPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

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
            throw new UnauthorizedAccessException(
                $"cannot open {permitPath}: it (or the gate directory containing it) is owned by " +
                "a different user than the one running this process — delete the gate directory " +
                "and retry, or run every process that uses it under the same account",
                accessDenied);
        }
        catch (IOException)
        {
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
