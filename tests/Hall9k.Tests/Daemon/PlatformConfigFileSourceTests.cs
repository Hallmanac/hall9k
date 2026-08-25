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
}
