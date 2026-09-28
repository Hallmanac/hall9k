using FluentAssertions;
using Hall9k.Cli.Diagnostics;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="DatabaseDoctor.MaybeMigrateLegacyPasswordAsync"/>'s eligibility gate: everything up
/// to (but never including) the real <c>ALTER ROLE</c>, so these run with no Docker and no real
/// Postgres — a fake <see cref="RecordingProcessRunner"/> answers the one <c>docker inspect</c> call
/// the ownership guard makes, and every case here returns before this method would ever open an
/// Npgsql connection at all. The mechanics once every gate passes (the real transaction, the
/// verifier, the config and compose rewrites) are <c>DatabaseDoctorPasswordMigrationTests</c>,
/// which needs a real container.
/// </summary>
public sealed class DatabaseDoctorPasswordMigrationEligibilityTests : IDisposable
{
    private const string PlaceholderConnectionString = "Host=127.0.0.1;Port=5432;Database=hall9k;Username=postgres;Password=unused";

    private readonly ScopedTestHome scopedHome = new();

    public void Dispose() => scopedHome.Dispose();

    private static ConnectionStringResolution PlaceholderResolution() =>
        new(PlaceholderConnectionString, ConnectionStringOrigin.PlatformConfigFile, Hall9kDatabase.ConfigFile);

    private static RecordingProcessRunner GuardPassingRunner() =>
        new(() => new(0, $"|{PostgresRuntime.ComposeFile}|{PostgresRuntime.VolumeName} \n", string.Empty));

    [Fact]
    public async Task Silent_when_nothing_is_configured_yet()
    {
        (string connectionString, ConnectionStringResolution resolution, bool abort) =
            await DatabaseDoctor.MaybeMigrateLegacyPasswordAsync(
                PlaceholderConnectionString, PlaceholderResolution(), assumeYes: true,
                GuardPassingRunner().Runner, () => false, CancellationToken.None);

        abort.Should().BeFalse();
        connectionString.Should().Be(PlaceholderConnectionString, "no config file at all is not this migration's business");
        resolution.Value.Should().Be(PlaceholderConnectionString);
        File.Exists(Hall9kDatabase.ConfigFile).Should().BeFalse("nothing here should ever create a config file");
    }

    [Fact]
    public async Task Silent_when_the_config_file_names_something_other_than_the_legacy_default()
    {
        const string custom = "Host=elsewhere;Port=5433;Database=custom;Username=someone;Password=secret";
        await Hall9kDatabase.WriteConfiguredConnectionStringAsync(custom, CancellationToken.None);

        (string connectionString, _, bool abort) = await DatabaseDoctor.MaybeMigrateLegacyPasswordAsync(
            custom, PlaceholderResolution(), assumeYes: true, GuardPassingRunner().Runner, () => false, CancellationToken.None);

        abort.Should().BeFalse();
        connectionString.Should().Be(custom, "a hand-set connection string is never this migration's to touch");
        Hall9kDatabase.ConnectionStringStateAndValueInConfigFile().Value.Should().Be(custom);
    }

    [Fact]
    public async Task Reports_and_skips_when_the_environment_variable_outranks_the_legacy_config_file()
    {
        await Hall9kDatabase.WriteConfiguredConnectionStringAsync(Hall9kDatabase.ConnectionStringWithPassword("hall9k"), CancellationToken.None);
        ConnectionStringResolution fromEnvironment = new(
            "Host=env-wins;Port=5432;Database=x;Username=x;Password=x",
            ConnectionStringOrigin.EnvironmentVariable,
            Hall9kDatabase.EnvironmentVariableName);

        bool abort = true;
        string output = await ScopedAnsiConsoleCapture.CaptureAsync(async () =>
        {
            (_, _, abort) = await DatabaseDoctor.MaybeMigrateLegacyPasswordAsync(
                fromEnvironment.Value!, fromEnvironment, assumeYes: true, GuardPassingRunner().Runner, () => false, CancellationToken.None);
        });

        abort.Should().BeFalse();
        output.Should().Contain(Hall9kDatabase.EnvironmentVariableName, "the operator needs to be told which variable to unset");
        Hall9kDatabase.ConnectionStringStateAndValueInConfigFile().Value.Should().Be(
            Hall9kDatabase.ConnectionStringWithPassword("hall9k"),
            "the environment variable is what actually resolves, so the config file underneath it must be left alone");
    }

    [Fact]
    public async Task Proceeds_past_the_legacy_check_when_the_config_file_names_the_pre_6185ff2e8_localhost_form()
    {
        // Every install between 2026-08-16 and commit 6185ff2e8 (2026-09-27) recorded this exact
        // form, and that commit never rewrote an already-configured machine's config.json — so
        // this must be recognised as eligible too, or the real installed population is silently
        // never migrated (adversarial pre-PR review, cycle 1). Proven here via the --yes gate
        // (rather than a real Postgres connection) so this stays Docker-free like every other
        // case in this class: reaching that gate at all is what proves the eligibility check
        // itself passed.
        const string localhostLegacy = "Host=localhost;Port=5432;Database=hall9k;Username=postgres;Password=hall9k";
        await Hall9kDatabase.WriteConfiguredConnectionStringAsync(localhostLegacy, CancellationToken.None);

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(async () =>
        {
            await DatabaseDoctor.MaybeMigrateLegacyPasswordAsync(
                PlaceholderConnectionString, PlaceholderResolution(), assumeYes: false, GuardPassingRunner().Runner,
                () => false, CancellationToken.None);
        });

        output.Should().Contain("--yes was not given");
    }

    [Fact]
    public async Task Blocked_without_assume_yes_even_when_every_other_guard_condition_holds()
    {
        await Hall9kDatabase.WriteConfiguredConnectionStringAsync(Hall9kDatabase.ConnectionStringWithPassword("hall9k"), CancellationToken.None);

        string connectionString = string.Empty;
        bool aborted = true;
        string output = await ScopedAnsiConsoleCapture.CaptureAsync(async () =>
        {
            (connectionString, _, aborted) = await DatabaseDoctor.MaybeMigrateLegacyPasswordAsync(
                PlaceholderConnectionString, PlaceholderResolution(), assumeYes: false, GuardPassingRunner().Runner,
                () => false, CancellationToken.None);
        });

        aborted.Should().BeFalse();
        connectionString.Should().Be(PlaceholderConnectionString);
        output.Should().Contain("--yes was not given");
        Hall9kDatabase.ConnectionStringStateAndValueInConfigFile().Value.Should().Be(
            Hall9kDatabase.ConnectionStringWithPassword("hall9k"), "nothing may change without --yes");
    }

    [Fact]
    public async Task Blocked_while_a_daemon_is_running()
    {
        await Hall9kDatabase.WriteConfiguredConnectionStringAsync(Hall9kDatabase.ConnectionStringWithPassword("hall9k"), CancellationToken.None);

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(async () =>
        {
            await DatabaseDoctor.MaybeMigrateLegacyPasswordAsync(
                PlaceholderConnectionString, PlaceholderResolution(), assumeYes: true, GuardPassingRunner().Runner,
                () => true, CancellationToken.None);
        });

        output.Should().Contain("a daemon is running");
        Hall9kDatabase.ConnectionStringStateAndValueInConfigFile().Value.Should().Be(
            Hall9kDatabase.ConnectionStringWithPassword("hall9k"), "a live connection must never be caught mid-rotation");
    }

    [Fact]
    public async Task Blocked_when_the_container_does_not_mount_exactly_the_pinned_volume()
    {
        await Hall9kDatabase.WriteConfiguredConnectionStringAsync(Hall9kDatabase.ConnectionStringWithPassword("hall9k"), CancellationToken.None);
        RecordingProcessRunner runner = new(() => new(0, $"|{PostgresRuntime.ComposeFile}|dev_hall9k-pgdata \n", string.Empty));

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(async () =>
        {
            await DatabaseDoctor.MaybeMigrateLegacyPasswordAsync(
                PlaceholderConnectionString, PlaceholderResolution(), assumeYes: true, runner.Runner, () => false, CancellationToken.None);
        });

        output.Should().Contain("does not mount exactly the pinned");
        Hall9kDatabase.ConnectionStringStateAndValueInConfigFile().Value.Should().Be(Hall9kDatabase.ConnectionStringWithPassword("hall9k"));
    }

    [Fact]
    public async Task Blocked_when_the_container_was_created_from_a_different_compose_file()
    {
        await Hall9kDatabase.WriteConfiguredConnectionStringAsync(Hall9kDatabase.ConnectionStringWithPassword("hall9k"), CancellationToken.None);
        RecordingProcessRunner runner = new(() =>
            new(0, $"|{Path.Combine(Path.GetTempPath(), "other-compose.yml")}|{PostgresRuntime.VolumeName} \n", string.Empty));

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(async () =>
        {
            await DatabaseDoctor.MaybeMigrateLegacyPasswordAsync(
                PlaceholderConnectionString, PlaceholderResolution(), assumeYes: true, runner.Runner, () => false, CancellationToken.None);
        });

        output.Should().Contain("was not created from");
        Hall9kDatabase.ConnectionStringStateAndValueInConfigFile().Value.Should().Be(Hall9kDatabase.ConnectionStringWithPassword("hall9k"));
    }

    [Fact]
    public async Task Blocked_when_the_container_cannot_be_inspected()
    {
        await Hall9kDatabase.WriteConfiguredConnectionStringAsync(Hall9kDatabase.ConnectionStringWithPassword("hall9k"), CancellationToken.None);
        RecordingProcessRunner runner = RecordingProcessRunner.Failing("docker inspect failed");

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(async () =>
        {
            await DatabaseDoctor.MaybeMigrateLegacyPasswordAsync(
                PlaceholderConnectionString, PlaceholderResolution(), assumeYes: true, runner.Runner, () => false, CancellationToken.None);
        });

        output.Should().Contain("could not be inspected");
        Hall9kDatabase.ConnectionStringStateAndValueInConfigFile().Value.Should().Be(Hall9kDatabase.ConnectionStringWithPassword("hall9k"));
    }
}
