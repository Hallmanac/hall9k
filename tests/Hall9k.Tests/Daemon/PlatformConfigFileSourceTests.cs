using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Domain.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// <see cref="PlatformConfigFileSource"/> is what makes an autostart-launched daemon (no operator
/// shell to export anything into) run with the durable settings in the platform config file
/// (backlog 59). These tests build the same <see cref="IConfigurationBuilder"/> shape
/// <c>Host.CreateApplicationBuilder</c> hands <c>Program</c> — an environment variables source
/// already present — and bind the real <see cref="DaemonOptions"/> against the result, because the
/// property that matters is what ends up bound, not which provider produced it.
/// </summary>
[Collection("Hall9kHome")]
public sealed class PlatformConfigFileSourceTests : IDisposable
{
    // Every DaemonOptions env var this feature covers, so a variable the dev-loop happens to
    // export into this shell (the ModelByRole role-split experiment the origin incident names)
    // never leaks into an assertion about what the config file alone binds.
    private static readonly string[] EnvironmentVariables =
    [
        "Hall9k__MaxConcurrentAgentSessions",
        "Hall9k__ModelByRole__Build",
        "Hall9k__ModelByRole__Review",
        "Hall9k__ModelByRole__Fix",
    ];

    private readonly string home = Path.Combine(Path.GetTempPath(), $"h9k-cfgsrc-{Path.GetRandomFileName()}");
    private readonly string? previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");
    private readonly Dictionary<string, string?> previous =
        EnvironmentVariables.ToDictionary(name => name, Environment.GetEnvironmentVariable);

    public PlatformConfigFileSourceTests()
    {
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("HALL9K_HOME", home);
        foreach (string name in EnvironmentVariables)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", previousHome);
        foreach ((string name, string? value) in previous)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        Directory.Delete(home, recursive: true);
    }

    private static DaemonOptions Bind(IConfigurationBuilder builder)
    {
        DaemonOptions options = new();
        builder.Build().GetSection(DaemonOptions.SectionName).Bind(options);
        return options;
    }

    [Fact]
    public void No_config_file_leaves_the_built_in_default_untouched()
    {
        ConfigurationBuilder builder = new();
        builder.AddEnvironmentVariables();

        PlatformConfigFileSource.Insert(builder);

        Bind(builder).MaxConcurrentAgentSessions.Should().Be(OperatingSettings.DefaultMaxConcurrentAgentSessions);
    }

    [Fact]
    public async Task A_config_file_setting_binds_onto_DaemonOptions()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.MaxConcurrentAgentSessions = 7, CancellationToken.None);
        ConfigurationBuilder builder = new();
        builder.AddEnvironmentVariables();

        PlatformConfigFileSource.Insert(builder);

        Bind(builder).MaxConcurrentAgentSessions.Should().Be(7);
    }

    [Fact]
    public async Task Model_by_role_binds_from_the_config_file()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.ModelByRole.Review = "sonnet", CancellationToken.None);
        ConfigurationBuilder builder = new();
        builder.AddEnvironmentVariables();

        PlatformConfigFileSource.Insert(builder);

        Bind(builder).ModelByRole.Review.Should().Be("sonnet");
    }

    [Fact]
    public async Task An_environment_variable_still_outranks_the_config_file()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.MaxConcurrentAgentSessions = 7, CancellationToken.None);
        Environment.SetEnvironmentVariable("Hall9k__MaxConcurrentAgentSessions", "9");
        ConfigurationBuilder builder = new();
        builder.AddEnvironmentVariables();

        PlatformConfigFileSource.Insert(builder);

        Bind(builder).MaxConcurrentAgentSessions.Should().Be(
            9, "Host.CreateApplicationBuilder had already added the environment source before Insert ran, "
            + "so the file has to be inserted ahead of it rather than appended after");
    }

    [Fact]
    public async Task A_config_file_setting_outranks_appsettings_json_even_with_a_second_host_level_env_source()
    {
        // Host.CreateApplicationBuilder registers two EnvironmentVariablesConfigurationSource
        // instances: a DOTNET_-prefixed one before appsettings.json, and the ordinary unprefixed
        // one after it. Insert must land ahead of the *last* one, not the first, or the config
        // file would rank below appsettings.json instead of above it.
        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.MaxConcurrentAgentSessions = 7, CancellationToken.None);
        ConfigurationBuilder builder = new();
        builder.AddEnvironmentVariables("DOTNET_");
        builder.AddInMemoryCollection([new("Hall9k:MaxConcurrentAgentSessions", "2")]);
        builder.AddEnvironmentVariables();

        PlatformConfigFileSource.Insert(builder);

        Bind(builder).MaxConcurrentAgentSessions.Should().Be(
            7, "the config file must outrank the earlier in-memory/appsettings-shaped source, "
            + "not just the DOTNET_-prefixed host bootstrap source");
    }

    [Fact]
    public async Task A_malformed_config_file_is_skipped_rather_than_crashing_configuration_build()
    {
        await File.WriteAllTextAsync(Hall9kDatabase.ConfigFile, "{ not valid json");
        ConfigurationBuilder builder = new();
        builder.AddEnvironmentVariables();

        Action insert = () => PlatformConfigFileSource.Insert(builder);

        insert.Should().NotThrow();
        Bind(builder).MaxConcurrentAgentSessions.Should().Be(
            OperatingSettings.DefaultMaxConcurrentAgentSessions,
            "a broken file falls back to the built-in default rather than taking configuration binding down with it");
    }

    /// <summary>
    /// <c>JsonConfigurationFileParser</c> parses with comments skipped and trailing commas
    /// allowed, so this pre-parse guard must accept exactly what it accepts — a stricter guard
    /// would report a file the daemon's own parser loads fine as "not valid JSON". Origin: the
    /// cycle-2 pre-PR review found <see cref="JsonDocument.Parse(string)"/>'s default (strict)
    /// options used here instead.
    /// </summary>
    [Fact]
    public async Task A_config_file_with_a_comment_and_a_trailing_comma_is_not_rejected()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile,
            """
            {
                "hall9k": {
                    "maxConcurrentAgentSessions": 4, // laptop OOMs above 4
                },
            }
            """);
        ConfigurationBuilder builder = new();
        builder.AddEnvironmentVariables();

        Action insert = () => PlatformConfigFileSource.Insert(builder);

        insert.Should().NotThrow();
        Bind(builder).MaxConcurrentAgentSessions.Should().Be(4);
    }

    /// <summary>
    /// Valid JSON with a non-object root (an array, a bare string, a bare number) passes
    /// <c>JsonDocument.Parse</c>, so a guard that only catches <see cref="System.Text.Json.JsonException"/>
    /// lets it through to <c>JsonConfigurationFileParser</c>, which throws a raw
    /// <see cref="FormatException"/> and kills the daemon at startup — the cycle-3 pre-PR review's
    /// high-severity finding. <see cref="PlatformConfigFile.ReadDocumentAsync"/> already guards this
    /// exact shape on the CLI side; the daemon side has to as well.
    /// </summary>
    [Fact]
    public async Task A_valid_json_non_object_root_is_skipped_rather_than_crashing_configuration_build()
    {
        await File.WriteAllTextAsync(Hall9kDatabase.ConfigFile, "[1, 2, 3]");
        ConfigurationBuilder builder = new();
        builder.AddEnvironmentVariables();

        Action insert = () => PlatformConfigFileSource.Insert(builder);

        insert.Should().NotThrow();
        Bind(builder).MaxConcurrentAgentSessions.Should().Be(
            OperatingSettings.DefaultMaxConcurrentAgentSessions,
            "a non-object root falls back to the built-in default rather than taking configuration binding down with it");
    }

    /// <summary>
    /// <c>JsonDocument</c> — what this pre-parse guard uses — accepts a key that repeats under a
    /// different case without complaint, but <c>JsonConfigurationFileParser</c>'s own keys are
    /// ordinal-ignore-case and throws a raw <see cref="FormatException"/> on the collision when
    /// <c>builder.Build()</c> actually loads the source. Origin: the cycle-4 pre-PR review found
    /// this shape — reachable by hand-editing the casing the env-var table prints, or by the
    /// CLI's own pre-fix write bug — killed the daemon with exactly the unguarded crash this
    /// pre-parse check exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_case_variant_duplicate_key_is_skipped_rather_than_crashing_configuration_build()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile,
            """{"hall9k": {"maxConcurrentAgentSessions": 3}, "Hall9k": {"maxConcurrentAgentSessions": 6}}""");
        ConfigurationBuilder builder = new();
        builder.AddEnvironmentVariables();

        Action insert = () => PlatformConfigFileSource.Insert(builder);

        insert.Should().NotThrow();
        Action build = () => Bind(builder);
        build.Should().NotThrow(
            "a duplicate case-variant key must be skipped by this guard rather than reaching " +
            "JsonConfigurationFileParser, which throws a raw FormatException on it");
    }

    /// <summary>
    /// <see cref="Microsoft.Extensions.FileProviders.PhysicalFileProvider"/>'s constructor throws
    /// <see cref="ArgumentException"/> for a non-rooted root, which turns this method's
    /// never-crash-the-daemon contract into an unhandled startup exception when
    /// <c>HALL9K_HOME</c> is set to a relative path. Origin: the cycle-4 pre-PR review.
    /// </summary>
    [Fact]
    public async Task A_relative_home_directory_does_not_crash_the_insert()
    {
        string relativeHome = Path.GetRelativePath(Directory.GetCurrentDirectory(), home);
        Environment.SetEnvironmentVariable("HALL9K_HOME", relativeHome);
        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.MaxConcurrentAgentSessions = 7, CancellationToken.None);
        ConfigurationBuilder builder = new();
        builder.AddEnvironmentVariables();

        Action insert = () => PlatformConfigFileSource.Insert(builder);

        insert.Should().NotThrow();
        Bind(builder).MaxConcurrentAgentSessions.Should().Be(7);
    }

    /// <summary>
    /// The pre-parse guard's <c>catch</c> only covered <see cref="System.Text.Json.JsonException"/>,
    /// so <see cref="File.ReadAllText(string)"/> throwing for an IO or permission reason (the file
    /// this account cannot read, a transient IO error) bubbled straight out of <c>Insert</c> and
    /// crashed daemon startup instead of being reported and skipped like every other malformed-file
    /// case. Origin: PR #45 review.
    /// </summary>
    [Fact]
    public async Task An_unreadable_config_file_is_skipped_rather_than_crashing_configuration_build()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.MaxConcurrentAgentSessions = 7, CancellationToken.None);
        if (!MadeUnreadable(Hall9kDatabase.ConfigFile))
        {
            // Windows has no POSIX mode, and root reads through one; on either the case this
            // test describes cannot be staged, so there is nothing to assert.
            return;
        }

        ConfigurationBuilder builder = new();
        builder.AddEnvironmentVariables();

        Action insert = () => PlatformConfigFileSource.Insert(builder);

        insert.Should().NotThrow();
        Bind(builder).MaxConcurrentAgentSessions.Should().Be(
            OperatingSettings.DefaultMaxConcurrentAgentSessions,
            "a file this account cannot read falls back to the built-in default rather than taking " +
            "configuration binding down with it");
    }

    /// <summary>
    /// Strips every permission bit and confirms the read is actually denied. False when the
    /// platform or the caller's privileges make the denial impossible to stage.
    /// </summary>
    private static bool MadeUnreadable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            File.ReadAllText(path);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
