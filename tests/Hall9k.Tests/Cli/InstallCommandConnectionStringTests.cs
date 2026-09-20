using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// Windows install friction log item 1: the installer already writes a compose file whose
/// POSTGRES_DB/POSTGRES_USER/POSTGRES_PASSWORD fully determine
/// <see cref="Hall9kDatabase.DefaultConnectionString"/>, yet config.json was left empty and
/// <c>h9k doctor</c>'s first run failed with "No connection string is configured" for no
/// reason a fresh install couldn't already see. <see cref="InstallCommand.FinishAsync"/> now
/// writes that answer down for a genuinely unconfigured machine, and — just as important —
/// never touches a connection string that already resolves from somewhere.
/// <para>
/// This class still writes <c>HALL9K_CONNECTION_STRING</c> directly (clearing it in the
/// constructor, and — in one test — setting it to a specific value to prove it still outranks
/// install's own write): it is exercising that environment-variable precedence tier itself, which
/// <c>ScopedConnectionString</c> cannot stand in for (Decisions Log PLACEHOLDER-98484f36). That
/// literal write is what keeps this class in <c>[Collection("Environment")]</c>; <c>HALL9K_HOME</c>
/// is redirected through <c>ScopedTestHome</c> like everywhere else.
/// </para>
/// </summary>
[Collection("Environment")]
[Trait("Category", "Environment")]
public sealed class InstallCommandConnectionStringTests : IDisposable
{
    private readonly ScopedTestHome scopedHome = new();
    private readonly string staging = Path.Combine(Path.GetTempPath(), $"h9k-install-conn-staging-{Path.GetRandomFileName()}");

    private readonly string? previousConnectionString =
        Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);

    private string home => scopedHome.Home;

    public InstallCommandConnectionStringTests()
    {
        Directory.CreateDirectory(staging);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previousConnectionString);
        scopedHome.Dispose();
        InstallCommand.TryDelete(staging);
    }

    [Fact]
    public async Task An_unconfigured_machine_gets_the_connection_string_the_compose_file_stands_up()
    {
        int exitCode = await Finish();

        exitCode.Should().Be(0);
        File.Exists(Hall9kDatabase.ConfigFile).Should().BeTrue();
        Hall9kDatabase.Resolve().Value.Should().Be(Hall9kDatabase.DefaultConnectionString);
    }

    [Fact]
    public async Task An_already_configured_connection_string_is_never_overwritten()
    {
        const string existing = "Host=elsewhere;Port=5433;Database=custom;Username=someone;Password=secret";
        await Hall9kDatabase.WriteConfiguredConnectionStringAsync(existing, CancellationToken.None);

        int exitCode = await Finish();

        exitCode.Should().Be(0);
        Hall9kDatabase.Resolve().Value.Should().Be(existing,
            "install must never overrule a connection string the operator (or an earlier doctor run) already set");
    }

    [Fact]
    public async Task An_environment_variable_outranks_installs_own_write_too()
    {
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, "Host=env-wins;Port=5432;Database=x;Username=x;Password=x");

        int exitCode = await Finish();

        exitCode.Should().Be(0);
        File.Exists(Hall9kDatabase.ConfigFile).Should().BeFalse(
            "something already resolves (the environment variable), so install has nothing to write");
    }

    /// <summary>
    /// Something already listening on the default port — a native Postgres the operator runs
    /// there, say — is not install's to guess past: writing its own compose credentials over
    /// it would replace h9k doctor's honest "something is already listening" diagnosis with a
    /// manufactured authentication failure against a credential install itself invented
    /// (cycle-1 review).
    /// </summary>
    [Fact]
    public async Task A_listening_port_is_left_unconfigured_rather_than_guessed_at()
    {
        int exitCode = await Finish(portListeningProbe: static _ => Task.FromResult(true));

        exitCode.Should().Be(0);
        File.Exists(Hall9kDatabase.ConfigFile).Should().BeFalse(
            "something is already listening on the default port, so install must not write a credential for it");
    }

    private Task<int> Finish(Func<CancellationToken, Task<bool>>? portListeningProbe = null) =>
        InstallCommand.FinishAsync(
            staging,
            skillsSource: null,
            version: "0.0.0-test",
            restart: false,
            noRestart: false,
            linkOntoPath: false,
            writeDefaultConnectionStringIfUnconfigured: true,
            // Anchors the project-override walk-up at the isolated home directory rather than
            // wherever the test host's own working directory happens to sit, so a contributor
            // whose real checkout carries a .hall9k-connection file at its root does not make
            // this test's outcome depend on their local environment (cycle-1 review).
            connectionStringStartDirectory: home,
            // A real port-5432 check would make this test's outcome depend on whether the
            // machine running it happens to have Postgres listening there (this repository's
            // own dev-loop compose Postgres, say) — stubbed false unless a test overrides it,
            // so "nothing configured" stays hermetic regardless (cycle-1 review).
            portListeningProbe: portListeningProbe ?? (static _ => Task.FromResult(false)),
            // The production method also consults the real current directory in addition to
            // connectionStringStartDirectory (both directions of the walk-up, cycle-1 review),
            // so connectionStringStartDirectory alone does not make this hermetic — anchoring
            // this one at home too closes the gap: a contributor whose real checkout (or an
            // ancestor of it, e.g. ~/.hall9k itself) carries a .hall9k-connection file no
            // longer makes this test's outcome depend on the test host's own working
            // directory (cycle-6 review, which found the earlier comment above claiming
            // hermeticity that connectionStringStartDirectory alone did not actually provide).
            currentDirectoryOverride: home,
            cancellationToken: CancellationToken.None);
}
