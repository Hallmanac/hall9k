using FluentAssertions;
using Hall9k.Domain.Infrastructure.Persistence;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The connection-string precedence Decisions Log #73 resolves (§15 row 29): environment
/// variable, then the platform config file, then a per-project override file — and, above
/// all, no plausible-looking default when nothing is configured (Decisions Log #58).
/// </summary>
// HALL9K_HOME and HALL9K_CONNECTION_STRING are process-wide state every test here
// redirects; sharing the collection serializes this file against every other test that
// does the same (IdeaSurfaceTests and friends), so a concurrent swap can never be read
// mid-assertion.
[Collection("Hall9kHome")]
public sealed class Hall9kDatabaseTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), $"h9k-db-{Path.GetRandomFileName()}");
    private readonly string? previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");
    private readonly string? previousConnectionString =
        Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);

    public Hall9kDatabaseTests()
    {
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("HALL9K_HOME", home);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", previousHome);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previousConnectionString);
        Directory.Delete(home, recursive: true);
    }

    [Fact]
    public void Nothing_configured_anywhere_resolves_to_not_configured()
    {
        ConnectionStringResolution resolution = Hall9kDatabase.Resolve(startDirectory: home);

        resolution.IsConfigured.Should().BeFalse("a plausible-looking default is exactly what Decisions Log #58 refuses to guess");
        resolution.Origin.Should().Be(ConnectionStringOrigin.None);
    }

    [Fact]
    public void A_malformed_platform_config_file_resolves_as_malformed_not_unconfigured()
    {
        File.WriteAllText(Path.Combine(home, "config.json"), "{ not valid json");
        File.WriteAllText(Path.Combine(home, Hall9kDatabase.ProjectOverrideFileName), "project-value");

        ConnectionStringResolution resolution = Hall9kDatabase.Resolve(startDirectory: home);

        resolution.IsConfigured.Should().BeFalse();
        resolution.Origin.Should().Be(
            ConnectionStringOrigin.PlatformConfigFileMalformed,
            "a broken config file needs repair, not the 'nothing configured' guidance — and it must not silently fall through to the project override behind it");
        resolution.Source.Should().Be(Path.Combine(home, "config.json"));
    }

    [Fact]
    public void An_explicitly_configured_value_outranks_everything()
    {
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, "env-value");
        File.WriteAllText(Path.Combine(home, "config.json"), """{"connectionString": "config-value"}""");

        ConnectionStringResolution resolution = Hall9kDatabase.Resolve(configured: "explicit-value", startDirectory: home);

        resolution.Value.Should().Be("explicit-value");
        resolution.Origin.Should().Be(ConnectionStringOrigin.Configured);
    }

    [Fact]
    public void The_environment_variable_outranks_the_platform_config_file()
    {
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, "env-value");
        File.WriteAllText(Path.Combine(home, "config.json"), """{"connectionString": "config-value"}""");

        ConnectionStringResolution resolution = Hall9kDatabase.Resolve(startDirectory: home);

        resolution.Value.Should().Be("env-value");
        resolution.Origin.Should().Be(ConnectionStringOrigin.EnvironmentVariable);
        resolution.Source.Should().Be(Hall9kDatabase.EnvironmentVariableName);
    }

    [Fact]
    public void The_platform_config_file_outranks_a_project_override()
    {
        File.WriteAllText(Path.Combine(home, "config.json"), """{"connectionString": "config-value"}""");
        File.WriteAllText(Path.Combine(home, Hall9kDatabase.ProjectOverrideFileName), "project-value");

        ConnectionStringResolution resolution = Hall9kDatabase.Resolve(startDirectory: home);

        resolution.Value.Should().Be("config-value");
        resolution.Origin.Should().Be(ConnectionStringOrigin.PlatformConfigFile);
        resolution.Source.Should().Be(Path.Combine(home, "config.json"));
    }

    [Fact]
    public void A_project_override_is_found_by_walking_up_from_the_working_directory()
    {
        string nested = Path.Combine(home, "repo", "src", "deep");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(home, "repo", Hall9kDatabase.ProjectOverrideFileName), "project-value\n");

        ConnectionStringResolution resolution = Hall9kDatabase.Resolve(startDirectory: nested);

        resolution.Value.Should().Be("project-value", "the file's own whitespace is trimmed");
        resolution.Origin.Should().Be(ConnectionStringOrigin.ProjectOverride);
        resolution.Source.Should().Be(Path.Combine(home, "repo", Hall9kDatabase.ProjectOverrideFileName));
    }

    /// <summary>
    /// <c>JsonConfigurationFileParser</c> — what the daemon actually binds this file through —
    /// parses with comments skipped and trailing commas allowed, the same leniency
    /// <see cref="PlatformConfigFile.LenientDocumentOptions"/> already reads the "hall9k" section
    /// with. Origin: the cycle-3 pre-PR review found this type's own connection-string read still
    /// using System.Text.Json's strict defaults, so a file the daemon's operating-settings section
    /// calls healthy failed this read as "not valid JSON" and reported
    /// <see cref="ConnectionStringOrigin.PlatformConfigFileMalformed"/> for every database command.
    /// </summary>
    [Fact]
    public void A_config_file_with_a_comment_and_a_trailing_comma_still_resolves_the_connection_string()
    {
        File.WriteAllText(
            Path.Combine(home, "config.json"),
            """
            {
                "connectionString": "config-value", // set by h9k doctor
            }
            """);

        ConnectionStringResolution resolution = Hall9kDatabase.Resolve(startDirectory: home);

        resolution.Value.Should().Be("config-value");
        resolution.Origin.Should().Be(ConnectionStringOrigin.PlatformConfigFile);
    }

    [Fact]
    public async Task Writing_the_configured_connection_string_makes_it_the_platform_config_file_answer()
    {
        await Hall9kDatabase.WriteConfiguredConnectionStringAsync("written-value", CancellationToken.None);

        ConnectionStringResolution resolution = Hall9kDatabase.Resolve(startDirectory: home);

        resolution.Value.Should().Be("written-value");
        resolution.Origin.Should().Be(ConnectionStringOrigin.PlatformConfigFile);
    }

    [Fact]
    public async Task Writing_the_connection_string_preserves_other_keys_already_in_the_config_file()
    {
        File.WriteAllText(Path.Combine(home, "config.json"), """{"someOtherSetting": "keep-me"}""");

        await Hall9kDatabase.WriteConfiguredConnectionStringAsync("written-value", CancellationToken.None);

        string written = await File.ReadAllTextAsync(Path.Combine(home, "config.json"));
        written.Should().Contain("keep-me").And.Contain("written-value");
    }

    [Fact]
    public async Task Writing_the_connection_string_recovers_from_a_malformed_existing_config_file()
    {
        File.WriteAllText(Path.Combine(home, "config.json"), "{ not valid json");

        await Hall9kDatabase.WriteConfiguredConnectionStringAsync("written-value", CancellationToken.None);

        ConnectionStringResolution resolution = Hall9kDatabase.Resolve(startDirectory: home);
        resolution.Value.Should().Be("written-value", "the doctor's own write is what fixes a broken file, not a reason to crash mid-fix");
        resolution.Origin.Should().Be(ConnectionStringOrigin.PlatformConfigFile);
    }

    /// <summary>
    /// Missing, present-without-the-key, and malformed each want their own remedy text (cycle-6
    /// review): a caller reporting all three as "does not exist" would say so even when the file
    /// was sitting right there with a typo in it or with only its operating-settings section.
    /// <see cref="ConnectionStringStateInConfigFile"/> keeps them apart so a warning can name the
    /// actual remedy.
    /// </summary>
    [Fact]
    public void No_config_file_at_all_reports_missing()
    {
        Hall9kDatabase.ConnectionStringStateInConfigFile().Should().Be(ConfigFileConnectionStringState.Missing);
    }

    [Fact]
    public void A_config_file_with_only_operating_settings_reports_present_without_a_connection_string()
    {
        File.WriteAllText(Path.Combine(home, "config.json"), """{"hall9k": {"maxConcurrentAgentSessions": 4}}""");

        Hall9kDatabase.ConnectionStringStateInConfigFile().Should().Be(ConfigFileConnectionStringState.PresentWithoutConnectionString);
    }

    [Fact]
    public void An_unparsable_config_file_reports_malformed_rather_than_missing()
    {
        File.WriteAllText(Path.Combine(home, "config.json"), "{ not valid json");

        Hall9kDatabase.ConnectionStringStateInConfigFile().Should().Be(ConfigFileConnectionStringState.Malformed);
    }

    [Fact]
    public void A_config_file_carrying_a_connection_string_reports_supplied()
    {
        File.WriteAllText(Path.Combine(home, "config.json"), """{"connectionString": "config-value"}""");

        Hall9kDatabase.ConnectionStringStateInConfigFile().Should().Be(ConfigFileConnectionStringState.Supplied);
    }
}
