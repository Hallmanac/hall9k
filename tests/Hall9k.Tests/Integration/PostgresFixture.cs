using Testcontainers.PostgreSql;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// One Postgres container per test class (fresh database, real schema). The host port is
/// always OS-chosen (backlog 53): <c>PostgreSqlBuilder</c> already binds one at random by
/// default, and this states it explicitly rather than leaning on that default silently, so a
/// future library upgrade that changed it would be a visible diff here rather than a fixed
/// port colliding across the many container instances this suite starts concurrently.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine")
        .WithPortBinding(PostgreSqlBuilder.PostgreSqlPort, assignRandomHostPort: true)
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
