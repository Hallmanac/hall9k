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
/// process-wide, at <see cref="MaxConcurrentContainers"/> (Decisions Log #108, origin: decision
/// #75 named up to eleven <c>PostgresFixture</c> instances starting concurrently under
/// <c>dotnet test</c> as the likelier source of the connection flakes backlog 53's retry now
/// absorbs, and that count stacked with other load OOMed the machine twice in two days,
/// 2026-08-24). The gate lives here rather than on any xUnit collection attribute because xUnit
/// collections only bound concurrency among classes an author remembered to annotate — the
/// <c>Hall9kHome</c> collection already happens to serialize the Postgres-backed classes that
/// need it for an unrelated reason (HALL9K_HOME isolation), but the rest sit in their own
/// implicit, parallel collection by default, one container each. Gating inside the one fixture
/// every container-backed test already depends on bounds the total regardless of collection
/// membership, and bounds it for a class added next month with no extra annotation to
/// remember — the corresponding guard,
/// <see cref="ContainerRoutingGuardTests"/>, fails the build if any test class starts a Postgres
/// container any other way. Four is chosen conservatively: it is nowhere near the eleven that
/// caused the OOM, while still giving the suite's largest tier (28 classes as of this writing)
/// real parallel throughput rather than serializing it outright — see the PR wrap-up this
/// decision shipped with for the measured wall-clock cost of the bound.
/// </para>
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const int MaxConcurrentContainers = 4;

    private static readonly SemaphoreSlim ConcurrencyGate = new(MaxConcurrentContainers, MaxConcurrentContainers);

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine")
        .WithPortBinding(PostgreSqlBuilder.PostgreSqlPort, assignRandomHostPort: true)
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await ConcurrencyGate.WaitAsync();

        try
        {
            await _container.StartAsync();
        }
        catch
        {
            ConcurrencyGate.Release();
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
            ConcurrencyGate.Release();
        }
    }
}
