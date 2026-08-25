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

    /// <summary>
    /// Mirrors the case-insensitive lookup <c>EnvironmentVariablesConfigurationProvider</c>
    /// performs when the daemon binds this section: it loads every variable into an
    /// <c>OrdinalIgnoreCase</c> dictionary, so a shell export using all-caps (the form many shells
    /// favor) still reaches the daemon. <see cref="Environment.GetEnvironmentVariable(string)"/>
    /// is case-sensitive on Linux and macOS, so the resolver has to look the variable up its own
    /// way rather than delegate to it directly. Origin: the cycle-6 pre-PR review found
    /// <c>export HALL9K__MAXCONCURRENTAGENTSESSIONS=9</c> bound and run by the daemon while this
    /// resolver reported the setting as its built-in default.
    /// </summary>
    [Fact]
    public async Task A_differently_cased_environment_variable_is_still_resolved()
    {
        const string differentlyCasedName = "HALL9K__MAXCONCURRENTAGENTSESSIONS";
        Environment.SetEnvironmentVariable(differentlyCasedName, "9");
        try
        {
            OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

            report.MaxConcurrentAgentSessions.Value.Should().Be(9);
            report.MaxConcurrentAgentSessions.Origin.Should().Be(SettingOrigin.EnvironmentVariable);
        }
        finally
        {
            Environment.SetEnvironmentVariable(differentlyCasedName, null);
        }
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

    /// <summary>
    /// <c>ConfigSetCommand.ApplyModel</c> gates every model it writes through
    /// <c>AgentModel.IsWellFormed</c>, but a hand-edited config file never goes through that gate,
    /// and <c>ClaudeExecutor.SpawnAsync</c> throws for every fresh spawn on the node when it gets a
    /// value like this. Origin: the cycle-2 pre-PR review found the resolver reporting a value like
    /// this as a healthy, in-force <c>default-model</c> rather than naming the mistake.
    /// </summary>
    [Fact]
    public async Task An_unusable_model_name_in_the_config_file_is_reported_rather_than_presented_as_healthy()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.DefaultModel = "not a real model", CancellationToken.None);

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.DefaultModel.Value.Should().Be(AgentModel.PlatformFallback);
        report.DefaultModel.Origin.Should().Be(SettingOrigin.Default);
        report.UnusableEnvironmentVariables.Should().ContainSingle(
            warning => warning.Contains(Hall9kDatabase.ConfigFile) && warning.Contains("not a real model"));
    }

    [Fact]
    public async Task An_unusable_model_name_in_an_environment_variable_is_reported_rather_than_presented_as_healthy()
    {
        Environment.SetEnvironmentVariable("Hall9k__DefaultModel", "not a real model");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.DefaultModel.Value.Should().Be(AgentModel.PlatformFallback);
        report.DefaultModel.Origin.Should().Be(SettingOrigin.Default);
        report.UnusableEnvironmentVariables.Should().ContainSingle(
            warning => warning.Contains("Hall9k__DefaultModel") && warning.Contains("not a real model"));
    }

    /// <summary>
    /// <c>NodeLoad.MaxConcurrentRuns</c> floors any ceiling below 1 to exactly one concurrent run
    /// rather than dispatching nothing — the opposite of what <c>h9k config set</c>'s own refusal
    /// message says a ceiling of zero would do. Origin: the cycle-3 pre-PR review found this
    /// resolver reporting a zero or negative ceiling as a healthy, in-force setting with no line
    /// naming that gap.
    /// </summary>
    [Fact]
    public async Task A_zero_ceiling_in_the_config_file_is_reported_as_floored_rather_than_healthy()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile, """{"hall9k": {"maxConcurrentAgentSessions": 0}}""");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.MaxConcurrentAgentSessions.Value.Should().Be(0);
        report.MaxConcurrentAgentSessions.Origin.Should().Be(SettingOrigin.PlatformConfigFile);
        report.UnusableEnvironmentVariables.Should().ContainSingle(
            warning => warning.Contains(Hall9kDatabase.ConfigFile) && warning.Contains("floors"));
    }

    /// <summary>
    /// An explicit JSON <c>null</c> or an empty object for <c>maxConcurrentAgentSessions</c> binds
    /// to zero on the daemon's own <c>ConfigurationBinder</c> (its explicit-value handling has no
    /// null to assign a non-nullable <c>int</c>, so it resolves to <see langword="default"/>
    /// instead) rather than leaving the setting at its built-in default of three the way every
    /// other shape mismatch does. Reporting "3 (default)" with no warning here would be exactly the
    /// gap the sibling test above closes for a hand-written zero, just reached through a shape that
    /// never throws an exception to classify. Origin: cycle-7 pre-PR review.
    /// </summary>
    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task A_null_or_empty_object_ceiling_in_the_config_file_is_reported_as_floored_rather_than_healthy(string shape)
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile, "{\"hall9k\": {\"maxConcurrentAgentSessions\": " + shape + "}}");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.MaxConcurrentAgentSessions.Value.Should().Be(0);
        report.MaxConcurrentAgentSessions.Origin.Should().Be(SettingOrigin.PlatformConfigFile);
        report.UnusableEnvironmentVariables.Should().ContainSingle(
            warning => warning.Contains(Hall9kDatabase.ConfigFile) && warning.Contains("floors"));
    }

    [Fact]
    public async Task A_negative_ceiling_environment_variable_is_reported_as_floored_rather_than_healthy()
    {
        Environment.SetEnvironmentVariable("Hall9k__MaxConcurrentAgentSessions", "-1");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.MaxConcurrentAgentSessions.Value.Should().Be(-1);
        report.MaxConcurrentAgentSessions.Origin.Should().Be(SettingOrigin.EnvironmentVariable);
        report.UnusableEnvironmentVariables.Should().ContainSingle(
            warning => warning.Contains("Hall9k__MaxConcurrentAgentSessions") && warning.Contains("floors"));
    }

    /// <summary>
    /// <c>ResolveOptionalString</c> is the same bottom-of-the-chain resolution as
    /// <c>ResolveString</c>, so a per-role model a hand edit or a raw environment variable never
    /// ran through <c>ConfigSetCommand.ApplyModel</c>'s gate must be caught here too. Origin: the
    /// cycle-3 pre-PR review found this method reporting a value like this as a healthy, in-force
    /// per-role model rather than naming the mistake, even though the daemon spawns on it.
    /// </summary>
    [Fact]
    public async Task An_unusable_role_model_in_the_config_file_is_reported_rather_than_presented_as_healthy()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(
            s => s.ModelByRole.Build = "claude opus 5", CancellationToken.None);

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        RoleModelSetting build = report.ModelByRole.Single(role => role.Role == nameof(RoleModelSettings.Build));
        build.Model.Value.Should().BeNull();
        build.Model.Origin.Should().Be(SettingOrigin.Default);
        report.UnusableEnvironmentVariables.Should().ContainSingle(
            warning => warning.Contains(Hall9kDatabase.ConfigFile) && warning.Contains("claude opus 5"));
    }

    [Fact]
    public async Task An_unusable_role_model_in_an_environment_variable_is_reported_rather_than_presented_as_healthy()
    {
        Environment.SetEnvironmentVariable("Hall9k__ModelByRole__Build", "claude opus 5");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        RoleModelSetting build = report.ModelByRole.Single(role => role.Role == nameof(RoleModelSettings.Build));
        build.Model.Value.Should().BeNull();
        build.Model.Origin.Should().Be(SettingOrigin.Default);
        report.UnusableEnvironmentVariables.Should().ContainSingle(
            warning => warning.Contains("Hall9k__ModelByRole__Build") && warning.Contains("claude opus 5"));
    }

    /// <summary>
    /// <c>AgentModel.FromInput</c> maps the literal word <c>"default"</c> to <see
    /// cref="AgentModel.Unknown"/>, which <c>AgentModel.Resolve</c> never returns — it is the
    /// idiom that clears an override, not a spawnable model. Origin: the cycle-3 pre-PR review
    /// found <c>IsUsableModel</c> treating <c>Unknown</c> as usable, so this value read back as a
    /// healthy, in-force <c>default-model</c> while the daemon actually ran on the platform
    /// fallback instead.
    /// </summary>
    [Fact]
    public async Task A_default_model_set_to_the_literal_clearing_word_falls_through_rather_than_reporting_as_healthy()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.DefaultModel = "default", CancellationToken.None);

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.DefaultModel.Value.Should().Be(AgentModel.PlatformFallback);
        report.DefaultModel.Origin.Should().Be(SettingOrigin.Default);
        report.UnusableEnvironmentVariables.Should().ContainSingle(
            warning => warning.Contains(Hall9kDatabase.ConfigFile) && warning.Contains("default"));
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
    /// <c>ConfigurationBinder</c> binds "" as a perfectly valid string over whatever the config
    /// file holds, so the daemon really does run with <c>DaemonOptions.DefaultModel = ""</c> — but
    /// that value is the <c>platformDefault</c> argument to <see cref="AgentModel.Resolve"/>, which
    /// maps a blank value to <c>Unknown</c> and falls through to <see cref="AgentModel.PlatformFallback"/>.
    /// Reporting the empty string, or the masked config-file value, as the effective model would
    /// both be wrong: the daemon runs on neither. Origin: the cycle-2 pre-PR review found this
    /// resolver reporting the blank environment value itself as the effective
    /// <c>DefaultModel</c> — a value <c>AgentModel</c> never actually resolves to.
    /// </summary>
    [Fact]
    public async Task A_set_but_empty_default_model_env_var_falls_through_to_the_platform_fallback()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.DefaultModel = "sonnet", CancellationToken.None);
        Environment.SetEnvironmentVariable("Hall9k__DefaultModel", "");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.DefaultModel.Value.Should().Be(AgentModel.PlatformFallback);
        report.DefaultModel.Origin.Should().Be(SettingOrigin.Default);
        report.UnusableEnvironmentVariables.Should().ContainSingle(
            warning => warning.Contains("Hall9k__DefaultModel"),
            "an empty value shadows the config file's \"sonnet\" and is never itself a model the daemon runs on");
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
