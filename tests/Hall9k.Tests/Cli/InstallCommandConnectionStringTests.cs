using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Infrastructure.Persistence;
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
/// </summary>
// HALL9K_HOME and HALL9K_CONNECTION_STRING are process-wide state; sharing the collection
// serializes this against every other test that redirects the same environment.
[Collection("Hall9kHome")]
public sealed class InstallCommandConnectionStringTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), $"h9k-install-conn-{Path.GetRandomFileName()}");
    private readonly string staging = Path.Combine(Path.GetTempPath(), $"h9k-install-conn-staging-{Path.GetRandomFileName()}");
    private readonly string? previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");
    private readonly string? previousConnectionString =
        Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);

    public InstallCommandConnectionStringTests()
    {
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(staging);
        Environment.SetEnvironmentVariable("HALL9K_HOME", home);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", previousHome);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previousConnectionString);
        InstallCommand.TryDelete(home);
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

    private Task<int> Finish() =>
        InstallCommand.FinishAsync(
            staging,
            skillsSource: null,
            version: "0.0.0-test",
            restart: false,
            noRestart: false,
            linkOntoPath: false,
            cancellationToken: CancellationToken.None);
}
