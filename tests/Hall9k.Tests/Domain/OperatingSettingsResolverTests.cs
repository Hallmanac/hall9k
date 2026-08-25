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
        report.ConfigFileProblem.Should().BeNull();
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

        report.ConfigFileProblem.Should().NotBeNull();
        report.ConfigFileProblem!.Message.Should().Contain("is not valid JSON");
        report.ConfigFileProblem.Consequence.Should().Be(ConfigFileProblemConsequence.DaemonSkipsFile,
            "a syntax error is exactly what PlatformConfigFileSource guards and skips gracefully at daemon startup");
        report.MaxConcurrentAgentSessions.Origin.Should().Be(SettingOrigin.Default);
    }

    /// <summary>
    /// A value with the wrong shape is not the same failure as a syntax error: the document
    /// parses fine, so PlatformConfigFileSource's own guard lets it through, and the daemon's
    /// ConfigurationBinder then throws at options-resolution time instead of falling back — a
    /// fatal startup crash rather than the graceful "defaults still apply" a syntax error gets.
    /// Origin: the cycle-3 pre-PR review found both CLI surfaces reporting this shape as "not
    /// valid JSON ... defaults still apply", which is wrong on both the cause and the consequence.
    /// </summary>
    [Fact]
    public async Task A_value_with_the_wrong_shape_is_distinguished_from_a_syntax_error()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile, """{"hall9k": {"maxConcurrentAgentSessions": "four"}}""");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.ConfigFileProblem.Should().NotBeNull();
        report.ConfigFileProblem!.Message.Should().Contain("wrong shape");
        report.ConfigFileProblem.Consequence.Should().Be(ConfigFileProblemConsequence.DaemonFailsToStart,
            "ConfigurationBinder has no guard for a value-shape problem, so the daemon crashes on it rather than falling back");
        report.MaxConcurrentAgentSessions.Origin.Should().Be(SettingOrigin.Default);
    }

    /// <summary>
    /// A value-shape mismatch on a leaf other than <c>maxConcurrentAgentSessions</c> does not
    /// crash <c>ConfigurationBinder</c> — it has no conversion for the complex
    /// <c>modelByRole</c> object, so it leaves that one property at its default while binding
    /// every sibling key normally. Origin: the cycle-2 pre-PR review found this reported as
    /// "the daemon skips the file for this run", discarding the sibling
    /// <c>maxConcurrentAgentSessions</c> value the daemon would actually still be running with.
    /// </summary>
    [Fact]
    public async Task A_value_shape_mismatch_on_one_setting_still_resolves_its_sibling_settings()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile,
            """{"hall9k": {"maxConcurrentAgentSessions": 6, "modelByRole": "sonnet"}}""");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.ConfigFileProblem.Should().NotBeNull();
        report.ConfigFileProblem!.Consequence.Should().Be(ConfigFileProblemConsequence.SettingIsIgnored);
        report.MaxConcurrentAgentSessions.Value.Should().Be(6,
            "the daemon's ConfigurationBinder binds this sibling key fine even though modelByRole fails to convert");
        report.MaxConcurrentAgentSessions.Origin.Should().Be(SettingOrigin.PlatformConfigFile);
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

        report.ConfigFileProblem.Should().BeNull();
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

    /// <summary>
    /// A shell expanding an unset variable into an empty assignment
    /// (<c>Hall9k__MaxConcurrentAgentSessions=</c>, the origin incident's own failure shape) still
    /// sets the variable — <see cref="Environment.GetEnvironmentVariable"/> returns "", not null —
    /// and <c>ConfigurationBinder</c> fails to parse "" as an int exactly like it fails on "four".
    /// Origin: the cycle-4 pre-PR review found the resolver's <c>{ Length: > 0 }</c> guard treating
    /// this the same as unset, so it silently fell through to the config file or default with no
    /// warning while the real daemon would crash at startup on the same value.
    /// </summary>
    [Fact]
    public async Task A_set_but_empty_integer_env_var_is_reported_rather_than_treated_as_unset()
    {
        // Windows' own environment block cannot represent a variable set to an empty value
        // distinct from unset (SetEnvironmentVariable(name, "") deletes it there), so the shell
        // expansion this guards against is a Unix-only failure mode — nothing to assert on Windows.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Environment.SetEnvironmentVariable("Hall9k__MaxConcurrentAgentSessions", "");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.MaxConcurrentAgentSessions.Origin.Should().Be(SettingOrigin.Default);
        report.UnusableEnvironmentVariables.Should().ContainSingle(
            warning => warning.Contains("Hall9k__MaxConcurrentAgentSessions"),
            "an empty value still sets the variable and still fails ConfigurationBinder's int conversion at daemon startup");
    }

    /// <summary>
    /// Unlike the integer setting, <c>ConfigurationBinder</c> binds "" as a perfectly valid string
    /// over whatever the config file holds — it never throws, so the daemon really does run with
    /// <c>DefaultModel = ""</c>. Reporting the config-file value as still in force here would be
    /// exactly backwards: the environment variable outranks the file even when it is empty. Origin:
    /// the cycle-1 pre-PR review found <c>ResolveString</c>/<c>ResolveOptionalString</c>'s
    /// <c>{ Length: > 0 }</c> guard treating a set-but-empty variable as unset, left over from
    /// before commit 8b50e40 fixed the same defect for <c>ResolveInt</c>.
    /// </summary>
    [Fact]
    public async Task A_set_but_empty_string_env_var_outranks_the_config_file_rather_than_being_treated_as_unset()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.DefaultModel = "sonnet", CancellationToken.None);
        Environment.SetEnvironmentVariable("Hall9k__DefaultModel", "");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.DefaultModel.Value.Should().Be(string.Empty);
        report.DefaultModel.Origin.Should().Be(SettingOrigin.EnvironmentVariable);
    }

    [Fact]
    public async Task A_set_but_empty_role_env_var_outranks_the_config_file_rather_than_being_treated_as_unset()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.ModelByRole.Review = "sonnet", CancellationToken.None);
        Environment.SetEnvironmentVariable("Hall9k__ModelByRole__Review", "");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        RoleModelSetting review = report.ModelByRole.Single(role => role.Role == nameof(RoleModelSettings.Review));
        review.Model.Value.Should().Be(string.Empty);
        review.Model.Origin.Should().Be(SettingOrigin.EnvironmentVariable);
    }
}
