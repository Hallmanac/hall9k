using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Tests.TestSupport;

/// <summary>
/// Redirects <see cref="PlatformPaths.Home"/> — and, when <paramref name="connectionString"/> is
/// given, <see cref="Hall9kDatabase.Resolve"/>'s connection string too — to values scoped to this
/// instance's own async flow, and to every background task that flow starts, rather than the
/// process-wide <c>HALL9K_HOME</c>/<c>HALL9K_CONNECTION_STRING</c> environment variables the two
/// production readers otherwise fall back to. Two tests opening one of these each, running in
/// xUnit's own parallel collections, never observe each other's redirected home or connection
/// string: each override is an <see cref="AsyncLocal{T}"/>
/// (<see cref="PlatformPaths.HomeOverrideForTests"/>, <see cref="Hall9kDatabase.ConnectionStringOverrideForTests"/>),
/// which flows with <see cref="ExecutionContext"/> rather than living process-wide.
/// <para>
/// Open it from a constructor or a field initializer — <c>private readonly ScopedTestHome _home =
/// new();</c>, or <c>new(postgres.ConnectionString)</c> when the class also needs its Postgres
/// fixture's own container pointed at — never inside an <c>async Task InitializeAsync()</c>. xUnit
/// runs that method as its own async flow; an override written there is gone the moment it
/// returns, before the test method that follows ever runs, so a class that opens the scope there
/// races <c>HALL9K_HOME</c> exactly as the raw <c>Environment.SetEnvironmentVariable</c> pattern
/// this type replaces did. <see cref="Hall9k.Tests.Domain.HomeEnvironmentIsolationTests"/>'s
/// second rule fails the build on one that does.
/// </para>
/// <para>
/// Replaces every class's own private <c>SetTempHome</c> copy and every direct write of
/// <c>HALL9K_HOME</c>/<c>HALL9K_CONNECTION_STRING</c> in this project (Decisions Log
/// PLACEHOLDER-98484f36) — a test never sets either variable itself except to hand
/// <c>HALL9K_HOME</c> to a real child process this test spawns, which still needs the environment
/// variable, since a child process inherits the parent's environment, not its parent's
/// <see cref="AsyncLocal{T}"/> state.
/// </para>
/// </summary>
internal sealed class ScopedTestHome : IDisposable
{
    private readonly string? _previousHome;
    private readonly string? _previousConnectionString;
    private readonly bool _connectionStringScoped;
    private bool _disposed;

    /// <param name="connectionString">
    /// When given, also redirects <see cref="Hall9kDatabase.Resolve"/> for this flow — typically a
    /// <c>PostgresFixture</c>'s own <c>ConnectionString</c>, so store-backed commands this test
    /// drives reach that fixture's container rather than whatever the process environment or the
    /// platform config file would otherwise resolve to. Left null for a class that never resolves
    /// a connection string at all.
    /// </param>
    public ScopedTestHome(string? connectionString = null)
    {
        Home = Path.Combine(Path.GetTempPath(), $"hall9k-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Home);

        _previousHome = PlatformPaths.HomeOverrideForTests;
        PlatformPaths.HomeOverrideForTests = Home;

        if (connectionString is not null)
        {
            _connectionStringScoped = true;
            _previousConnectionString = Hall9kDatabase.ConnectionStringOverrideForTests;
            Hall9kDatabase.ConnectionStringOverrideForTests = connectionString;
        }
    }

    /// <summary>The fresh temporary directory this scope redirected <see cref="PlatformPaths.Home"/> to.</summary>
    public string Home { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        PlatformPaths.HomeOverrideForTests = _previousHome;
        if (_connectionStringScoped)
        {
            Hall9kDatabase.ConnectionStringOverrideForTests = _previousConnectionString;
        }

        TemporaryTree.TryDelete(Home);
    }
}

/// <summary>
/// The connection-string-only half of <see cref="ScopedTestHome"/>: redirects
/// <see cref="Hall9kDatabase.Resolve"/> for this flow without touching
/// <see cref="PlatformPaths.Home"/> at all. For a one-off swap made from inside a class whose own
/// <see cref="ScopedTestHome"/> is already open and already owns <see cref="PlatformPaths.Home"/>
/// for that flow — opening a second <see cref="ScopedTestHome"/> there would silently swap
/// <see cref="PlatformPaths.Home"/> out from under it for the swap's duration too, which is never
/// what a connection-string-only caller wants. Lives beside <see cref="ScopedTestHome"/> so this
/// project's two flow-scoped overrides are opened and closed from exactly one file between them
/// for every ordinary caller. One test reaches past this file on purpose:
/// <c>Hall9k.Tests.Daemon.PlatformConfigFileSourceTests.A_relative_home_directory_does_not_crash_the_insert</c>
/// clears <see cref="PlatformPaths.HomeOverrideForTests"/> directly for one test's own duration, a
/// need neither this type nor <see cref="ScopedTestHome"/> has a constructor argument for: it has
/// to exercise the environment-variable tier the override would otherwise always outrank.
/// </summary>
internal sealed class ScopedConnectionString : IDisposable
{
    private readonly string? _previous;
    private bool _disposed;

    public ScopedConnectionString(string connectionString)
    {
        _previous = Hall9kDatabase.ConnectionStringOverrideForTests;
        Hall9kDatabase.ConnectionStringOverrideForTests = connectionString;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Hall9kDatabase.ConnectionStringOverrideForTests = _previous;
    }
}
