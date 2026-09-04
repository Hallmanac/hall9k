using Testcontainers.PostgreSql;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// One Postgres container per test class (fresh database, real schema). The host port is
/// always OS-chosen (backlog 53): <c>PostgreSqlBuilder</c> already binds one at random by
/// default, and this states it explicitly rather than leaning on that default silently, so a
/// future library upgrade that changed it would be a visible diff here rather than a fixed
/// port colliding across the many container instances this suite starts concurrently.
/// <para>
/// <see cref="CrossProcessContainerGate"/> bounds how many of these containers can be alive at
/// once machine-wide, at <see cref="MaxConcurrentContainers"/> (Decisions Log #108,
/// origin: decision #75 named up to eleven <c>PostgresFixture</c> instances starting concurrently
/// under <c>dotnet test</c> as the likelier source of the connection flakes backlog 53's retry now
/// absorbs, and that count stacked with other load — several agent sessions and a Parallels VM —
/// OOMed the machine on 2026-08-23 and again on 2026-08-24, forcing Brian to kill nearly
/// everything both times; the follow-up task that made the gate cross-process closes a gap
/// #108's own text named without ever closing — four concurrent full-suite runs would each hold
/// their own independent in-process bound, multiplying 4 permits into 16 containers between
/// them). The bound is machine-wide, not per
/// process: N concurrently dispatched sessions each running <c>dotnet test</c>, a fix session's
/// own foreground suite, and an operator's own run all draw from the same
/// <see cref="MaxConcurrentContainers"/> permits rather than each getting an independent set.
/// The gate lives here rather than on any xUnit collection attribute because xUnit collections
/// only bound concurrency among classes an author remembered to annotate — the
/// <c>Hall9kHome</c> collection already happens to serialize the Postgres-backed classes that
/// need it for an unrelated reason (HALL9K_HOME isolation), but the rest sit in their own
/// implicit, parallel collection by default, one container each. Gating inside the one fixture
/// every container-backed test already depends on bounds the total regardless of collection
/// membership, and bounds it for a class added next month with no extra annotation to remember —
/// the corresponding guard, <see cref="Hall9k.Tests.Domain.ContainerRoutingGuardTests"/>, fails
/// the build if any test class starts a Postgres container any other way. Four is chosen
/// conservatively: it is nowhere near the eleven that caused the OOM, while still giving the
/// suite's largest tier (29 classes as of this writing) real parallel throughput rather than
/// serializing it outright — see PLAN.md §16 #108 for the measured wall-clock cost of the bound.
/// </para>
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const int MaxConcurrentContainers = 4;

    // The permit is held for a class's entire run, not just its container startup (see
    // InitializeAsync/DisposeAsync below). Every Postgres-backed class declares
    // IClassFixture<PostgresFixture> for itself, and xUnit builds one instance per test class, so
    // the permit is acquired and released at every class boundary: the tier makes 29 acquisitions
    // (the 13 standalone classes plus the Hall9kHome collection's 16), not one per collection.
    //
    // There is deliberately no timeout on the wait itself. A fixed deadline was tried and removed:
    // it was sized against this one process's own tier duration (PLAN.md §16 #108's measured
    // 7m29s-8m4s), which stopped being the right number the moment the gate went cross-process —
    // N overlapping dotnet test invocations queue behind each other's permits too, not just this
    // process's own 29 acquisitions, so the same deadline that was generous for one process starts
    // firing under two or three and misreports genuine contention as "the gate or the Docker daemon
    // is genuinely stuck". GitWorktreeManager.AcquireCrossProcessLockAsync already settled this for
    // the same shape of wait: "there is no safe value to time this out to". What that wait uses
    // instead of a deadline — a periodic progress line so a wedged Docker daemon still reads
    // differently from ordinary queueing — CrossProcessContainerGate.AcquireAsync now does too.
    //
    // This budget covers only container startup, once a class already holds its permit, and is
    // sized independently of the (now-unbounded) queue wait above. Testcontainers pulls the image
    // inside StartAsync when it is not already cached, so on a cold host (a fresh CI runner,
    // chiefly) this budget also has to cover that pull, not just Postgres's own startup — sized
    // with real headroom above a warm-host start for that reason, rather than at the few seconds
    // a warm start alone would need.
    private static readonly TimeSpan ContainerStartTimeout = TimeSpan.FromMinutes(5);

    // Fixed and machine-wide, so every dotnet test process, whatever repository or worktree it
    // runs from, contends for the identical set of permit files — that is what makes the bound
    // machine-wide rather than per process (see CrossProcessContainerGate's own doc comment).
    // Named for what it gates rather than for this fixture, since the directory itself is the
    // shared, process-external state — a second gate for something unrelated would get its own
    // subdirectory rather than colliding here. Resolved by ResolveGateDirectory below rather than
    // Path.GetTempPath() directly — see that method for why.
    private static readonly string GateDirectory = ResolveGateDirectory();

    // Path.GetTempPath() is not actually the same location for every process on this machine: on
    // Unix it reads $TMPDIR, which is unset (falls back to /tmp) in plenty of same-user contexts
    // that are not the caller's own interactive shell — a systemd unit with PrivateTmp=true, a
    // scrubbed service environment, sudo without -E — so two processes on the same machine can
    // resolve two different temp roots, form two independent permit sets, and silently double
    // the bound this gate exists to enforce, reappearing between an operator's own shell and a
    // daemon-launched agent session rather than between dotnet test invocations. /tmp is the one
    // location POSIX guarantees regardless of $TMPDIR, so Unix hardcodes it instead of asking
    // Path.GetTempPath() to resolve it. Windows keeps Path.GetTempPath(): this repo's own Windows
    // hosts do not scrub %TEMP% between an operator's shell and a headless dispatch the way a
    // Unix service environment scrubs $TMPDIR, so the ambiguity this guards against does not
    // arise there today. A mount-namespace-level isolation (PrivateTmp=true itself) still defeats
    // even a hardcoded /tmp — no path choice can see across a namespace boundary — and a second,
    // differently-permissioned user account does not silently form its own independent
    // contention set the way a different $TMPDIR resolution would: the fixed path is shared, but
    // the permit files under it are not, so a second account fails outright with a clear,
    // actionable error (CrossProcessContainerGate.TryOpen's own UnauthorizedAccessException
    // handling) instead of quietly doubling the bound; neither case is what this closes.
    private static string ResolveGateDirectory()
    {
        string root = OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp";
        return Path.Combine(root, "hall9k-postgres-container-gate");
    }

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine")
        .WithPortBinding(PostgreSqlBuilder.PostgreSqlPort, assignRandomHostPort: true)
        .Build();

    // Tracks whether this instance currently holds a gate permit, so a failed InitializeAsync and
    // the DisposeAsync xUnit always calls afterward (even when InitializeAsync never completed)
    // cannot both release the same permit: over-releasing would let more than
    // MaxConcurrentContainers containers run at once, silently eroding the bound the gate exists
    // to enforce.
    private IAsyncDisposable? _gatePermit;

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        // Unbounded — see the comment above ContainerStartTimeout for why a queue-wait deadline
        // is the wrong tool here now that the gate is machine-wide.
        _gatePermit = await CrossProcessContainerGate.AcquireAsync(
            GateDirectory, MaxConcurrentContainers, CancellationToken.None);

        try
        {
            using CancellationTokenSource startTimeout = new(ContainerStartTimeout);

            try
            {
                await _container.StartAsync(startTimeout.Token);
            }
            catch (OperationCanceledException canceled) when (startTimeout.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"the Postgres container did not finish starting within {ContainerStartTimeout} " +
                    "(a cold host's image pull counts against this budget too) — Docker or the pull is genuinely stuck",
                    canceled);
            }
        }
        catch (Exception startFailure)
        {
            // Docker may have created and partially started the container before the failure, so
            // dispose it before releasing the permit — otherwise a container the gate no longer
            // knows about can briefly outlive the bound it exists to enforce.
            Exception? cleanupFailure = null;

            try
            {
                await _container.DisposeAsync();
            }
            catch (Exception exception)
            {
                // Removing a half-started container routinely fails against the same broken Docker
                // daemon that failed the start, or answers a 409 because removal is already in
                // progress. Letting that out of this block would report it *instead of* the start
                // failure, so the blown ContainerStartTimeout the timeout above exists to make
                // visible would vanish from what xUnit shows; both are reported together below
                // instead, the start failure first.
                cleanupFailure = exception;
            }
            finally
            {
                await ReleaseGateAsync();
            }

            if (cleanupFailure is not null)
            {
                throw new AggregateException(
                    "the Postgres container failed to start, and disposing the half-started container then failed too",
                    startFailure,
                    cleanupFailure);
            }

            throw;
        }
    }

    public async Task DisposeAsync()
    {
        try
        {
            await _container.DisposeAsync();
        }
        finally
        {
            await ReleaseGateAsync();
        }
    }

    private async Task ReleaseGateAsync()
    {
        if (_gatePermit is { } permit)
        {
            _gatePermit = null;
            await permit.DisposeAsync();
        }
    }
}
