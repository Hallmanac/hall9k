using FluentAssertions;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <c>h9k daemon status</c> and <c>h9k config show</c> both need to say where an operating
/// setting's effective value came from (backlog 59), resolved the same way <c>DaemonOptions</c>
/// binds at daemon startup: environment variable, then the platform config file, then the
/// built-in default.
/// </summary>
[Collection("Hall9kHome")]
public sealed class OperatingSettingsResolverTests : IDisposable
{
    // Every environment variable this resolver reads, so a variable the dev-loop happens to
    // export into this shell (the ModelByRole role-split experiment the origin incident names)
    // never leaks into an assertion about what "nothing configured" resolves to.
    private static readonly string[] EnvironmentVariables =
    [
        "Hall9k__MaxConcurrentAgentSessions",
        "Hall9k__DefaultModel",
        "Hall9k__ModelByRole__Build",
        "Hall9k__ModelByRole__Review",
        "Hall9k__ModelByRole__Fix",
        "Hall9k__ModelByRole__Synthesis",
        "Hall9k__ModelByRole__Refinement",
        "Hall9k__ModelByRole__Publication",
    ];

    private readonly string home = Path.Combine(Path.GetTempPath(), $"h9k-resolve-{Path.GetRandomFileName()}");
    private readonly string? previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");
    private readonly Dictionary<string, string?> previous =
        EnvironmentVariables.ToDictionary(name => name, Environment.GetEnvironmentVariable);

    public OperatingSettingsResolverTests()
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

    [Fact]
    public async Task Nothing_configured_anywhere_resolves_to_the_built_in_defaults()
    {
        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.MaxConcurrentAgentSessions.Value.Should().Be(OperatingSettings.DefaultMaxConcurrentAgentSessions);
        report.MaxConcurrentAgentSessions.Origin.Should().Be(SettingOrigin.Default);
        report.DefaultModel.Value.Should().Be(AgentModel.PlatformFallback);
        report.DefaultModel.Origin.Should().Be(SettingOrigin.Default);
        report.ModelByRole.Should().OnlyContain(role => role.Model.Origin == SettingOrigin.Default && role.Model.Value == null);
        report.ConfigFileMalformed.Should().BeFalse();
    }

    [Fact]
    public async Task A_config_file_setting_outranks_the_default()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.MaxConcurrentAgentSessions = 7, CancellationToken.None);

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.MaxConcurrentAgentSessions.Value.Should().Be(7);
        report.MaxConcurrentAgentSessions.Origin.Should().Be(SettingOrigin.PlatformConfigFile);
        report.MaxConcurrentAgentSessions.Source.Should().Be(Hall9kDatabase.ConfigFile);
    }

    [Fact]
    public async Task An_environment_variable_outranks_the_config_file()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.MaxConcurrentAgentSessions = 7, CancellationToken.None);
        Environment.SetEnvironmentVariable("Hall9k__MaxConcurrentAgentSessions", "9");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.MaxConcurrentAgentSessions.Value.Should().Be(9);
        report.MaxConcurrentAgentSessions.Origin.Should().Be(SettingOrigin.EnvironmentVariable);
        report.MaxConcurrentAgentSessions.Source.Should().Be("Hall9k__MaxConcurrentAgentSessions");
    }

    [Fact]
    public async Task A_role_left_unset_reports_as_the_default_with_no_value()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.ModelByRole.Review = "sonnet", CancellationToken.None);

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.ModelByRole.Single(role => role.Role == nameof(RoleModelSettings.Review)).Model.Value.Should().Be("sonnet");
        report.ModelByRole.Single(role => role.Role == nameof(RoleModelSettings.Build)).Model.Value.Should().BeNull();
    }

    [Fact]
    public async Task An_environment_variable_for_one_role_outranks_that_roles_config_file_value()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.ModelByRole.Review = "sonnet", CancellationToken.None);
        Environment.SetEnvironmentVariable("Hall9k__ModelByRole__Review", "haiku");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        RoleModelSetting review = report.ModelByRole.Single(role => role.Role == nameof(RoleModelSettings.Review));
        review.Model.Value.Should().Be("haiku");
        review.Model.Origin.Should().Be(SettingOrigin.EnvironmentVariable);
    }

    [Fact]
    public async Task A_malformed_config_file_is_flagged_rather_than_thrown_and_still_resolves_defaults()
    {
        await File.WriteAllTextAsync(Hall9kDatabase.ConfigFile, "{ not valid json");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.ConfigFileMalformed.Should().BeTrue();
        report.MaxConcurrentAgentSessions.Origin.Should().Be(SettingOrigin.Default);
    }

    /// <summary>
    /// The daemon binds this section through <c>IConfiguration</c>, where a quoted number and a bare
    /// one are identical, so this resolver must agree with what the daemon actually runs on. Origin:
    /// the pre-PR review of this branch found <c>h9k config show</c>/<c>h9k daemon status</c> both
    /// reporting a healthy, in-force file as broken and ignored on exactly this shape.
    /// </summary>
    [Fact]
    public async Task A_quoted_number_in_the_config_file_resolves_the_same_way_the_daemon_binds_it()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile, """{"hall9k": {"maxConcurrentAgentSessions": "4"}}""");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.ConfigFileMalformed.Should().BeFalse();
        report.MaxConcurrentAgentSessions.Value.Should().Be(4);
        report.MaxConcurrentAgentSessions.Origin.Should().Be(SettingOrigin.PlatformConfigFile);
    }

    [Fact]
    public async Task An_explicit_null_model_by_role_section_does_not_crash_the_resolver()
    {
        await File.WriteAllTextAsync(Hall9kDatabase.ConfigFile, """{"hall9k": {"modelByRole": null}}""");

        Func<Task> resolve = () => OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        await resolve.Should().NotThrowAsync(
            "an explicit JSON null for modelByRole must not leave OperatingSettings.ModelByRole null");
    }

    [Fact]
    public async Task A_set_but_unparseable_integer_env_var_is_reported_rather_than_silently_discarded()
    {
        Environment.SetEnvironmentVariable("Hall9k__MaxConcurrentAgentSessions", "four");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.MaxConcurrentAgentSessions.Origin.Should().Be(SettingOrigin.Default);
        report.UnusableEnvironmentVariables.Should().ContainSingle(
            warning => warning.Contains("Hall9k__MaxConcurrentAgentSessions") && warning.Contains("four"),
            "the daemon fails to start on this value rather than falling back, so hiding it here would mislead");
    }
}
