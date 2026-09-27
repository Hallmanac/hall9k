using FluentAssertions;
using Hall9k.Cli.Diagnostics;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.Integration;
using Hall9k.Tests.TestSupport;
using Npgsql;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="DatabaseDoctor.MaybeMigrateLegacyPasswordAsync"/>'s mechanics once every eligibility
/// gate already passed (<see cref="DatabaseDoctorPasswordMigrationEligibilityTests"/> covers the
/// gate itself with no Docker at all): the real transaction — <c>ALTER ROLE</c> with a precomputed
/// SCRAM-SHA-256 verifier, <c>config.json</c> written, <c>COMMIT</c> — against a real Postgres, and
/// the rollback when the config write fails partway through.
/// <para>
/// The connection string this migration opens and the connection string
/// <see cref="Hall9kDatabase.ConnectionStringStateAndValueInConfigFile"/> reads for the eligibility
/// check are two independent things this method never cross-checks against each other (that
/// cross-check is <see cref="Hall9kDatabase.Resolve"/>'s job, exercised elsewhere) — which is what
/// lets these tests point the real ALTER at a real container's own address while <c>config.json</c>
/// still names the literal legacy default the eligibility gate looks for.
/// </para>
/// <para>
/// Each test builds and disposes its own <see cref="PostgresFixture"/> directly, rather than
/// sharing one via <c>IClassFixture</c>: the whole point of the first test is to leave the role's
/// password rotated away from the container's own recorded credentials, which a second test sharing
/// that same container would then find itself unable to authenticate against at all.
/// </para>
/// </summary>
[Collection("Environment")]
[Trait("Category", "RequiresDocker")]
[Trait("Category", "Environment")]
public sealed class DatabaseDoctorPasswordMigrationTests : IDisposable
{
    private readonly ScopedTestHome scopedHome = new();

    private readonly string? previousConnectionString =
        Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);

    public DatabaseDoctorPasswordMigrationTests() =>
        // The eligibility gate refuses to touch anything while this outranks the config file
        // (correctly — see DatabaseDoctorPasswordMigrationEligibilityTests), but a dispatched
        // session on this project routinely has its own real database named here, which would
        // silently no-op every test in this class rather than exercise the migration at all.
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, null);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previousConnectionString);
        scopedHome.Dispose();
    }

    private static async Task<PostgresFixture> StartFixtureAsync()
    {
        PostgresFixture fixture = new();
        await fixture.InitializeAsync();
        return fixture;
    }

    private static RecordingProcessRunner GuardPassingRunner() =>
        new(() => new(0, $"|{PostgresRuntime.ComposeFile}|{PostgresRuntime.VolumeName} \n", string.Empty));

    private static async Task AssertConnectsAsync(string connectionString)
    {
        await using NpgsqlConnection connection = new(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
    }

    [Fact]
    public async Task A_successful_migration_rotates_the_role_config_and_compose_file_together()
    {
        PostgresFixture fixture = await StartFixtureAsync();
        try
        {
            await Hall9kDatabase.WriteConfiguredConnectionStringAsync(Hall9kDatabase.LegacyDefaultConnectionString, CancellationToken.None);
            string oldConnectionString = fixture.ConnectionString;
            ConnectionStringResolution resolution = new(
                Hall9kDatabase.LegacyDefaultConnectionString, ConnectionStringOrigin.PlatformConfigFile, Hall9kDatabase.ConfigFile);

            (string newConnectionString, ConnectionStringResolution newResolution, bool abort) =
                await DatabaseDoctor.MaybeMigrateLegacyPasswordAsync(
                    oldConnectionString, resolution, assumeYes: true, GuardPassingRunner().Runner, () => false, CancellationToken.None);

            abort.Should().BeFalse();
            newConnectionString.Should().NotBe(oldConnectionString, "the whole point is a different password");
            string newPassword = new NpgsqlConnectionStringBuilder(newConnectionString).Password!;
            newPassword.Should().MatchRegex("^[0-9a-f]{64}$", "32 CSPRNG bytes rendered as lowercase hex");

            newResolution.Value.Should().Be(newConnectionString);
            Hall9kDatabase.ConnectionStringStateAndValueInConfigFile().Value.Should().Be(newConnectionString);
            PostgresRuntime.ReadPasswordFromComposeFile().Should().Be(newPassword, "the compose file is the password's durable record");

            await AssertConnectsAsync(newConnectionString);
            Func<Task> reconnectWithOldPassword = () => AssertConnectsAsync(oldConnectionString);
            await reconnectWithOldPassword.Should().ThrowAsync<PostgresException>(
                "the role's password actually changed at the database, not just in config.json");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_failed_config_write_rolls_the_alter_role_back()
    {
        if (OperatingSystem.IsWindows())
        {
            // The directory-permission trick below has no Windows equivalent this test can rely
            // on portably; the rollback logic itself is platform-agnostic Npgsql code with no
            // Unix-specific step, so this gap is in the test's own reproduction, not the feature.
            return;
        }

        PostgresFixture fixture = await StartFixtureAsync();
        try
        {
            await Hall9kDatabase.WriteConfiguredConnectionStringAsync(Hall9kDatabase.LegacyDefaultConnectionString, CancellationToken.None);
            string oldConnectionString = fixture.ConnectionString;
            ConnectionStringResolution resolution = new(
                Hall9kDatabase.LegacyDefaultConnectionString, ConnectionStringOrigin.PlatformConfigFile, Hall9kDatabase.ConfigFile);

            string homeDirectory = Path.GetDirectoryName(Hall9kDatabase.ConfigFile)!;
            UnixFileMode originalMode = File.GetUnixFileMode(homeDirectory);
            // Read-only and non-executable: AtomicFileWrite stages a sibling temp file in this
            // same directory before it ever touches config.json itself, and creating that temp
            // file needs write (and traversal) permission on the directory holding it, not on
            // config.json's own mode — so this fails the write itself while leaving config.json
            // fully readable, which is what lets the eligibility check above still see the
            // legacy value it needs to proceed.
            File.SetUnixFileMode(homeDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            try
            {
                (string resultConnectionString, _, bool abort) = await DatabaseDoctor.MaybeMigrateLegacyPasswordAsync(
                    oldConnectionString, resolution, assumeYes: true, GuardPassingRunner().Runner, () => false, CancellationToken.None);

                abort.Should().BeFalse(
                    "a failed config write rolls back to a fully known, unchanged state — there is nothing left uncertain to abort over");
                resultConnectionString.Should().Be(oldConnectionString);
            }
            finally
            {
                File.SetUnixFileMode(homeDirectory, originalMode);
            }

            await AssertConnectsAsync(oldConnectionString);
            Hall9kDatabase.ConnectionStringStateAndValueInConfigFile().Value.Should().Be(
                Hall9kDatabase.LegacyDefaultConnectionString, "the config write never actually landed, so this must read exactly as it did before");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
