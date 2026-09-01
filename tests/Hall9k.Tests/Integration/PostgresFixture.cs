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
/// <see cref="ConcurrencyGate"/> bounds how many of these containers can be alive at once
/// per <c>dotnet test</c> process, at <see cref="MaxConcurrentContainers"/> (Decisions Log #108,
/// origin: decision #75 named up to eleven <c>PostgresFixture</c> instances starting concurrently
/// under <c>dotnet test</c> as the likelier source of the connection flakes backlog 53's retry now
/// absorbs, and that count stacked with other load — several agent sessions and a Parallels VM —
/// OOMed the machine on 2026-08-23 and again on 2026-08-24, forcing Brian to kill nearly
/// everything both times). The bound is per process, not per machine: N
/// concurrently dispatched sessions each running <c>dotnet test</c> still put up to 4N containers
/// on the host, exactly the kind of stacking that caused the origin OOM. The gate lives here
/// rather than on any xUnit collection attribute because xUnit collections only bound concurrency
/// among classes an author remembered to annotate — the <c>Hall9kHome</c> collection already
/// happens to serialize the Postgres-backed classes that need it for an unrelated reason
/// (HALL9K_HOME isolation), but the rest sit in their own implicit, parallel collection by
/// default, one container each. Gating inside the one fixture every container-backed test already
/// depends on bounds the total regardless of collection membership, and bounds it for a class
/// added next month with no extra annotation to remember — the corresponding guard,
/// <see cref="Hall9k.Tests.Domain.ContainerRoutingGuardTests"/>, fails the build if any test class starts a Postgres
/// container any other way. Four is chosen conservatively: it is nowhere near the eleven that
/// caused the OOM, while still giving the suite's largest tier (29 classes as of this writing)
/// real parallel throughput rather than serializing it outright — see PLAN.md §16 #108 for the
/// measured wall-clock cost of the bound.
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
    // At most 14 of those contend in parallel — the 13 standalone classes, each its own implicit
    // collection that xUnit starts regardless of maxParallelThreads, plus the Hall9kHome
    // collection, which runs its own 16 members one at a time — so with 4 permits any single
    // acquisition can sit behind several predecessors' full class durations, and the Hall9kHome
    // collection, which is both the majority of the tier's Postgres work and serialized, re-queues
    // for that wait 16 separate times rather than holding one permit straight through. Any one
    // wait is still bounded by the whole Postgres tier's own wall clock, measured at 7m29s-8m4s
    // locally under this bound (PLAN.md §16 #108). This budget covers only the queue wait, not
    // container startup (see ContainerStartTimeout below), so it is sized with real headroom above
    // that measured tier duration even under a loaded or slower-than-local machine; hitting it
    // means the gate or the Docker daemon is genuinely stuck rather than just busy, and failing
    // loudly beats the suite hanging with no diagnostic.
    private static readonly TimeSpan GateWaitTimeout = TimeSpan.FromMinutes(15);

    // Separate from GateWaitTimeout so a long queue wait never eats into this budget: once a
    // class has its permit, starting a single Postgres container should never come close to this
    // regardless of how long the wait before it was. Testcontainers pulls the image inside
    // StartAsync when it is not already cached, so on a cold host (a fresh CI runner, chiefly)
    // this budget also has to cover that pull, not just Postgres's own startup — sized with real
    // headroom above a warm-host start for that reason, rather than at the few seconds a warm
    // start alone would need.
    private static readonly TimeSpan ContainerStartTimeout = TimeSpan.FromMinutes(5);

    private static readonly SemaphoreSlim ConcurrencyGate = new(MaxConcurrentContainers, MaxConcurrentContainers);

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine")
        .WithPortBinding(PostgreSqlBuilder.PostgreSqlPort, assignRandomHostPort: true)
        .Build();

    // Tracks whether this instance currently holds a gate permit, so a failed InitializeAsync and
    // the DisposeAsync xUnit always calls afterward (even when InitializeAsync never completed)
    // cannot both release the same permit: over-releasing would let more than
    // MaxConcurrentContainers containers run at once, silently eroding the bound the gate exists
    // to enforce.
    private bool _gateAcquired;

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        using CancellationTokenSource gateTimeout = new(GateWaitTimeout);

        try
        {
            await ConcurrencyGate.WaitAsync(gateTimeout.Token);
        }
        catch (OperationCanceledException canceled) when (gateTimeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"timed out after {GateWaitTimeout} waiting for a concurrency gate permit " +
                $"({MaxConcurrentContainers} max concurrent Postgres containers) — the gate or " +
                "the Docker daemon is genuinely stuck rather than just busy",
                canceled);
        }

        _gateAcquired = true;

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
                // failure, so the blown ContainerStartTimeout the split timeouts above exist to
                // make visible would vanish from what xUnit shows; both are reported together
                // below instead, the start failure first.
                cleanupFailure = exception;
            }
            finally
            {
                ReleaseGate();
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
            ReleaseGate();
        }
    }

    private void ReleaseGate()
    {
        if (_gateAcquired)
        {
            _gateAcquired = false;
            ConcurrencyGate.Release();
        }
    }
}
