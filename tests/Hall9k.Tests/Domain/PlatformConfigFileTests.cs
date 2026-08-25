using System.Text.Json;
using FluentAssertions;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The "hall9k" section of the platform config file (backlog 59) is a merge target, never a
/// clobber: writing a setting here must never disturb <c>connectionString</c> (owned by
/// <see cref="Hall9kDatabase"/>) or a hand-edited key this feature does not model.
/// </summary>
[Collection("Hall9kHome")]
public sealed class PlatformConfigFileTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), $"h9k-cfg-{Path.GetRandomFileName()}");
    private readonly string? previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");
    private readonly string? previousConnectionString =
        Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);

    public PlatformConfigFileTests()
    {
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("HALL9K_HOME", home);
        // The connection string chain checks this before it ever reads the config file, so a
        // value already in this shell would otherwise outrank what this test just wrote there.
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", previousHome);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previousConnectionString);
        Directory.Delete(home, recursive: true);
    }

    [Fact]
    public async Task Reading_with_no_file_yet_returns_every_field_unset()
    {
        OperatingSettings settings = await PlatformConfigFile.ReadOperatingSettingsAsync(CancellationToken.None);

        settings.MaxConcurrentAgentSessions.Should().BeNull();
        settings.DefaultModel.Should().BeNull();
        settings.ModelByRole.Build.Should().BeNull();
    }

    [Fact]
    public async Task Writing_a_setting_creates_the_file_and_reports_that_it_did()
    {
        bool created = await PlatformConfigFile.WriteOperatingSettingsAsync(
            settings => settings.MaxConcurrentAgentSessions = 5, CancellationToken.None);

        created.Should().BeTrue("backlog 59 requires a missing file to be created with defaults on first need, stated out loud");
        File.Exists(Hall9kDatabase.ConfigFile).Should().BeTrue();
    }

    [Fact]
    public async Task Writing_a_second_setting_does_not_report_creation_again()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.MaxConcurrentAgentSessions = 5, CancellationToken.None);

        bool created = await PlatformConfigFile.WriteOperatingSettingsAsync(
            s => s.DefaultModel = "sonnet", CancellationToken.None);

        created.Should().BeFalse();
    }

    [Fact]
    public async Task Writing_operating_settings_preserves_the_connection_string_key()
    {
        await Hall9kDatabase.WriteConfiguredConnectionStringAsync("keep-me", CancellationToken.None);

        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.MaxConcurrentAgentSessions = 4, CancellationToken.None);

        ConnectionStringResolution resolution = Hall9kDatabase.Resolve(startDirectory: home);
        resolution.Value.Should().Be("keep-me", "a daemon-settings write must never disturb the connection string that lives in the same file");
    }

    [Fact]
    public async Task Writing_a_known_setting_preserves_an_unrelated_hand_edited_key_in_the_same_section()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile,
            """{"hall9k": {"maxConcurrentAgentSessions": 3, "someFutureSetting": "keep-me"}}""");

        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.MaxConcurrentAgentSessions = 4, CancellationToken.None);

        string written = await File.ReadAllTextAsync(Hall9kDatabase.ConfigFile);
        written.Should().Contain("keep-me", "a hand-edited key this feature does not model still has to survive a CLI write");
    }

    [Fact]
    public async Task Round_tripping_model_by_role_keeps_every_configured_role()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(s =>
        {
            s.ModelByRole.Review = "sonnet";
            s.ModelByRole.Fix = "haiku";
        }, CancellationToken.None);

        OperatingSettings settings = await PlatformConfigFile.ReadOperatingSettingsAsync(CancellationToken.None);

        settings.ModelByRole.Review.Should().Be("sonnet");
        settings.ModelByRole.Fix.Should().Be("haiku");
        settings.ModelByRole.Build.Should().BeNull("a role never set stays unset rather than being filled in");
    }

    [Fact]
    public async Task A_malformed_config_file_refuses_the_write_rather_than_silently_starting_fresh()
    {
        await File.WriteAllTextAsync(Hall9kDatabase.ConfigFile, "{ not valid json");

        Func<Task> write = () => PlatformConfigFile.WriteOperatingSettingsAsync(
            s => s.MaxConcurrentAgentSessions = 4, CancellationToken.None);

        await write.Should().ThrowAsync<DomainValidationException>(
            "a broken file needs repair, not a merge write that could silently drop whatever else it held");
    }

    [Fact]
    public async Task A_malformed_config_file_refuses_the_read_the_same_way()
    {
        await File.WriteAllTextAsync(Hall9kDatabase.ConfigFile, "{ not valid json");

        Func<Task> read = () => PlatformConfigFile.ReadOperatingSettingsAsync(CancellationToken.None);

        await read.Should().ThrowAsync<DomainValidationException>();
    }

    /// <summary>
    /// The daemon binds this same section through <c>IConfiguration</c>, where every JSON leaf is
    /// already a string, so a hand-quoted number is a value the daemon runs on fine. Origin: the
    /// pre-PR review of this branch found the CLI reporting a healthy, in-force file as broken and
    /// ignored because System.Text.Json's strict typing rejected the exact shape the daemon accepts.
    /// </summary>
    [Fact]
    public async Task A_quoted_number_reads_the_same_way_the_daemon_binds_it()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile, """{"hall9k": {"maxConcurrentAgentSessions": "4"}}""");

        OperatingSettings settings = await PlatformConfigFile.ReadOperatingSettingsAsync(CancellationToken.None);

        settings.MaxConcurrentAgentSessions.Should().Be(4);
    }

    [Fact]
    public async Task A_non_numeric_string_for_a_numeric_setting_is_still_a_diagnosable_exception()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile, """{"hall9k": {"maxConcurrentAgentSessions": "four"}}""");

        Func<Task> read = () => PlatformConfigFile.ReadOperatingSettingsAsync(CancellationToken.None);

        await read.Should().ThrowAsync<DomainValidationException>(
            "a string that still cannot convert to a number is genuinely the wrong shape, and the daemon's own "
            + "ConfigurationBinder fails to start on it too");
    }

    [Fact]
    public async Task A_config_file_whose_top_level_is_not_a_json_object_is_a_diagnosable_exception()
    {
        await File.WriteAllTextAsync(Hall9kDatabase.ConfigFile, "[1, 2, 3]");

        Func<Task> read = () => PlatformConfigFile.ReadOperatingSettingsAsync(CancellationToken.None);

        await read.Should().ThrowAsync<DomainValidationException>(
            "a merge write needs a { ... } document, and silently substituting an empty one would erase whatever "
            + "the file actually held on the next write");
    }

    [Fact]
    public async Task A_value_of_the_wrong_json_type_also_refuses_the_write()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile, """{"hall9k": {"modelByRole": "sonnet"}}""");

        Func<Task> write = () => PlatformConfigFile.WriteOperatingSettingsAsync(
            s => s.MaxConcurrentAgentSessions = 4, CancellationToken.None);

        await write.Should().ThrowAsync<DomainValidationException>();
    }

    [Fact]
    public async Task Writing_a_setting_leaves_no_temp_file_behind()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(
            s => s.MaxConcurrentAgentSessions = 4, CancellationToken.None);

        Directory.GetFiles(home, "config.json.tmp-*").Should().BeEmpty(
            "the atomic write stages into a temp file and renames it away; nothing should remain named after it");
    }

    [Fact]
    public async Task An_explicit_null_model_by_role_reads_back_as_an_empty_instance_rather_than_null()
    {
        await File.WriteAllTextAsync(Hall9kDatabase.ConfigFile, """{"hall9k": {"modelByRole": null}}""");

        OperatingSettings settings = await PlatformConfigFile.ReadOperatingSettingsAsync(CancellationToken.None);

        settings.ModelByRole.Should().NotBeNull();
        settings.ModelByRole.Build.Should().BeNull();
    }

    /// <summary>
    /// The daemon binds this section through <c>IConfiguration</c>, where every key comparison is
    /// case-insensitive, so a hand-edit using the casing the env-var table and the daemon's own
    /// startup log print ("Hall9k") has to be read exactly like the canonical lowercase key.
    /// Origin: the cycle-4 pre-PR review found the CLI's <see cref="JsonObject"/> indexer lookup
    /// treating "Hall9k" as a different, absent section from "hall9k".
    /// </summary>
    [Fact]
    public async Task A_section_key_spelled_with_the_daemons_own_casing_still_reads()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile, """{"Hall9k": {"maxConcurrentAgentSessions": 6}}""");

        OperatingSettings settings = await PlatformConfigFile.ReadOperatingSettingsAsync(CancellationToken.None);

        settings.MaxConcurrentAgentSessions.Should().Be(6);
    }

    /// <summary>
    /// Writing must replace whatever key already names the section rather than adding a second,
    /// differently-cased one beside it: <c>JsonConfigurationFileParser</c>'s keys are also
    /// case-insensitive, so two such keys is a duplicate-key <see cref="FormatException"/> at
    /// daemon startup, not two settings.
    /// </summary>
    [Fact]
    public async Task Writing_over_a_differently_cased_section_key_replaces_it_rather_than_duplicating_it()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile, """{"Hall9k": {"maxConcurrentAgentSessions": 6}}""");

        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.DefaultModel = "sonnet", CancellationToken.None);

        string written = await File.ReadAllTextAsync(Hall9kDatabase.ConfigFile);
        using JsonDocument document = JsonDocument.Parse(written);
        PlatformConfigFile.HasCaseInsensitiveDuplicateKeys(document.RootElement).Should().BeFalse(
            "the write must replace the existing (differently-cased) section key rather than add a second one");
    }

    /// <summary>
    /// A file already holding two case-variant section keys (from a hand-edit, or from the bug
    /// the previous two tests guard against) is exactly what crashes the real daemon with a raw
    /// duplicate-key <see cref="FormatException"/>; a merge write must refuse rather than silently
    /// picking one of the two.
    /// </summary>
    [Fact]
    public async Task A_config_file_with_a_case_variant_duplicate_key_is_a_diagnosable_exception()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile,
            """{"hall9k": {"maxConcurrentAgentSessions": 3}, "Hall9k": {"maxConcurrentAgentSessions": 6}}""");

        Func<Task> read = () => PlatformConfigFile.ReadOperatingSettingsAsync(CancellationToken.None);

        await read.Should().ThrowAsync<DomainValidationException>(
            "Microsoft.Extensions.Configuration.Json's keys are case-insensitive, so this file crashes the "
            + "daemon at startup rather than resolving to either section");
    }

    /// <summary>
    /// This shape does not actually crash the daemon: <c>ModelByRole</c> is a complex object, and
    /// <c>ConfigurationBinder</c> has no string-to-object conversion for it, so it silently binds
    /// no children rather than throwing the way it does for the numeric <c>MaxConcurrentAgentSessions</c>
    /// leaf. Origin: the cycle-4 pre-PR review found <see cref="PlatformConfigFile.TryReadOperatingSettingsAsync"/>
    /// reporting every shape mismatch as a startup crash, which is wrong for this one.
    /// </summary>
    [Fact]
    public async Task A_scalar_given_for_the_model_by_role_object_is_reported_as_not_crashing_the_daemon()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile, """{"hall9k": {"modelByRole": "sonnet"}}""");

        ConfigFileReadResult result = await PlatformConfigFile.TryReadOperatingSettingsAsync(CancellationToken.None);

        result.Problem.Should().NotBeNull();
        result.Problem!.DaemonFailsToStart.Should().BeFalse(
            "ConfigurationBinder has no converter for this complex type, so it binds no children rather than throwing");
    }
}
