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
        "Hall9k__MaxConcurrentTaskRuns",
        "Hall9k__SessionCapPerRun",
        "Hall9k__DefaultModel",
        "Hall9k__ModelByRole__Build",
        "Hall9k__ModelByRole__Review",
        "Hall9k__ModelByRole__ReviewVerify",
        "Hall9k__ModelByRole__Fix",
        "Hall9k__ModelByRole__Synthesis",
        "Hall9k__ModelByRole__Refinement",
        "Hall9k__ModelByRole__Publication",
        "Hall9k__MaxComplianceReviewCycles",
        "Hall9k__MaxAdversarialReviewCycles",
        "Hall9k__MaxFinalFullPassRounds",
        "Hall9k__LifetimeReviewCycleBudget",
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
        report.MaxConcurrentTaskRuns.Value.Should().Be(OperatingSettings.DefaultMaxConcurrentTaskRuns);
        report.MaxConcurrentTaskRuns.Origin.Should().Be(SettingOrigin.Default);
        report.MaxConcurrentTaskRunsConvertedFromLegacy.Should().BeFalse();
        report.SessionCapPerRun.Value.Should().Be(OperatingSettings.DefaultSessionCapPerRun);
        report.SessionCapPerRun.Origin.Should().Be(SettingOrigin.Default);
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

    /// <summary>
    /// <c>ReviewVerify</c> rides the same generic <c>AsPairs()</c>/resolver machinery every other
    /// role does, so <c>h9k config show</c>/<c>h9k daemon status</c> report it exactly like any
    /// other role's knob — only <c>DaemonOptions.ResolveVerifyReviewModel</c> gives its blank value
    /// a different meaning (fall through to Review, not the platform default).
    /// </summary>
    [Fact]
    public async Task A_configured_review_verify_model_reports_alongside_the_other_roles()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.ModelByRole.ReviewVerify = "sonnet", CancellationToken.None);

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.ModelByRole.Single(role => role.Role == nameof(RoleModelSettings.ReviewVerify)).Model.Value.Should().Be("sonnet");
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
    /// parses fine, so PlatformConfigFileSource's own guard lets it through. No leaf in this
    /// section crashes the daemon's ConfigurationBinder any more (<c>DaemonOptionsBinding
    /// .ResolverOwnedKeys</c> excludes every concurrency setting from the daemon's own bind call,
    /// Decisions Log #111's follow-up), so the wrong-shape leaf is ignored and its siblings
    /// recovered instead — never the "not valid JSON ... defaults still apply" syntax-error
    /// diagnosis either. Origin: the cycle-3 pre-PR review found both CLI surfaces conflating the
    /// two failures; independent pre-PR review, cycle 1 of the concurrency-in-runs branch, found
    /// this test's own "crashes the daemon" expectation gone stale in turn.
    /// </summary>
    [Fact]
    public async Task A_value_with_the_wrong_shape_is_distinguished_from_a_syntax_error()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile, """{"hall9k": {"maxConcurrentAgentSessions": "four"}}""");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.ConfigFileProblem.Should().NotBeNull();
        report.ConfigFileProblem!.Message.Should().Contain("wrong shape");
        report.ConfigFileProblem.Consequence.Should().Be(ConfigFileProblemConsequence.SettingIsIgnored,
            "this leaf is excluded from the daemon's own ConfigurationBinder call entirely, so nothing crashes on it");
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
    /// The exact scenario the independent pre-PR review named (cycle 1, both lenses): a malformed
    /// retired <c>maxConcurrentAgentSessions</c> sitting beside a healthy
    /// <c>maxConcurrentTaskRuns</c> in the same file must not discard the healthy value. Before
    /// this fix, <c>PlatformConfigFile.DaemonFailsToStartOn</c> classified the malformed leaf as a
    /// startup crash, which wiped the whole section (<c>ConfigFileReadResult.DaemonCrashes</c>
    /// returned a brand-new, empty <c>OperatingSettings</c>) — so the node silently ran at the
    /// default ceiling of 1 instead of the configured 4, with no indication anything was wrong
    /// beyond a stale "it will crash outright at startup" message.
    /// </summary>
    [Fact]
    public async Task A_malformed_retired_setting_does_not_discard_a_healthy_sibling_ceiling()
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile,
            """{"hall9k": {"maxConcurrentTaskRuns": 4, "maxConcurrentAgentSessions": "three"}}""");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.ConfigFileProblem.Should().NotBeNull();
        report.ConfigFileProblem!.Consequence.Should().Be(ConfigFileProblemConsequence.SettingIsIgnored);
        report.MaxConcurrentTaskRuns.Value.Should().Be(4,
            "the healthy sibling must survive a malformed retired-setting leaf in the same file");
        report.MaxConcurrentTaskRuns.Origin.Should().Be(SettingOrigin.PlatformConfigFile);
        report.MaxConcurrentTaskRunsConvertedFromLegacy.Should().BeFalse();
    }

    /// <summary>
    /// The ordinary case the shadow flag must not false-positive on: nothing sets the new key at a
    /// lower level, so "set max-concurrent-task-runs directly" is a real, effective remedy.
    /// </summary>
    [Fact]
    public async Task Converting_from_an_environment_level_legacy_key_with_no_config_file_value_is_not_shadowed()
    {
        Environment.SetEnvironmentVariable("Hall9k__MaxConcurrentAgentSessions", "6");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.MaxConcurrentTaskRunsConvertedFromLegacy.Should().BeTrue();
        report.MaxConcurrentTaskRunsShadowsConfigFileValue.Should().BeFalse(
            "nothing sets max-concurrent-task-runs at a lower level, so there is nothing being shadowed");
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
    /// other shape mismatch does — <see cref="OperatingSettingsReport.MaxConcurrentAgentSessions"/>
    /// still reports that simulated <c>0</c>, since that field's whole job is describing what
    /// <c>ConfigurationBinder</c> would have bound. But unlike a hand-written <c>0</c> (the sibling
    /// test above), this leaf holds no real number at all, so it must not be treated as a legacy
    /// value the run ceiling actually converted: <c>max-concurrent-task-runs</c> falls straight
    /// through to the built-in default, and the "floors this to exactly one concurrent run" warning
    /// — which would otherwise claim the file forced that flooring — stays silent, because nothing
    /// in the file actually decided the ceiling here. Before this fix, the fabricated <c>0</c> was
    /// read as a real legacy value: the daemon status/config show output claimed a conversion that
    /// never had a number to convert, and warned that the file floors dispatch when the ceiling in
    /// fact comes from the unrelated built-in default (independent pre-PR review, cycle 1,
    /// adversarial lens). Origin: cycle-7 pre-PR review (the original binder-quirk fix).
    /// </summary>
    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task A_null_or_empty_object_ceiling_in_the_config_file_does_not_masquerade_as_a_legacy_conversion(string shape)
    {
        await File.WriteAllTextAsync(
            Hall9kDatabase.ConfigFile, "{\"hall9k\": {\"maxConcurrentAgentSessions\": " + shape + "}}");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.MaxConcurrentAgentSessions.Value.Should().Be(0,
            "this field still simulates what ConfigurationBinder would have bound, unrelated to the ceiling conversion");
        report.MaxConcurrentAgentSessions.Origin.Should().Be(SettingOrigin.PlatformConfigFile);
        report.MaxConcurrentTaskRunsConvertedFromLegacy.Should().BeFalse(
            "a fabricated zero holds no real number to convert, so the run ceiling must fall through to the default");
        report.MaxConcurrentTaskRuns.Origin.Should().Be(SettingOrigin.Default);
        report.UnusableEnvironmentVariables.Should().NotContain(
            warning => warning.Contains("floors"),
            "the file did not actually decide the ceiling here, so claiming it floors dispatch would be untrue");
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
    /// Unlike the concurrency ceiling, nothing floors a review-cycle cap back up to something
    /// usable: a cap of zero or less parks the very first cycle it is consulted on
    /// (<c>ReviewTrackPolicy</c>/<c>ReviewEngine.FinalFullPassCapReached</c>), and a lifetime
    /// budget of zero or less parks at the first settle point regardless of how cleanly the run
    /// converged. `h9k config set` refuses this on the write path, but a hand-edited config file
    /// or a raw environment variable skips that gate — the same bypass the concurrency floor test
    /// above covers. Origin: independent pre-PR review, cycle 1, adversarial lens.
    /// </summary>
    [Fact]
    public async Task A_zero_review_cap_environment_variable_is_reported_rather_than_presented_as_healthy()
    {
        Environment.SetEnvironmentVariable("Hall9k__MaxComplianceReviewCycles", "0");
        Environment.SetEnvironmentVariable("Hall9k__MaxAdversarialReviewCycles", "0");
        Environment.SetEnvironmentVariable("Hall9k__MaxFinalFullPassRounds", "0");
        Environment.SetEnvironmentVariable("Hall9k__LifetimeReviewCycleBudget", "0");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.MaxComplianceReviewCycles.Value.Should().Be(0);
        report.MaxAdversarialReviewCycles.Value.Should().Be(0);
        report.MaxFinalFullPassRounds.Value.Should().Be(0);
        report.LifetimeReviewCycleBudget.Value.Should().Be(0);
        report.UnusableEnvironmentVariables.Should().Contain(
            warning => warning.Contains("Hall9k__MaxComplianceReviewCycles") && warning.Contains("max-compliance-review-cycles"));
        report.UnusableEnvironmentVariables.Should().Contain(
            warning => warning.Contains("Hall9k__MaxAdversarialReviewCycles") && warning.Contains("max-adversarial-review-cycles"));
        report.UnusableEnvironmentVariables.Should().Contain(
            warning => warning.Contains("Hall9k__MaxFinalFullPassRounds") && warning.Contains("max-final-full-pass-rounds"));
        report.UnusableEnvironmentVariables.Should().Contain(
            warning => warning.Contains("Hall9k__MaxComplianceReviewCycles")
                && warning.Contains("parks for a human immediately rather than running"),
            "a per-run cap this low parks every cycle it is consulted on, not just at a settle point");
        report.UnusableEnvironmentVariables.Should().Contain(
            warning => warning.Contains("Hall9k__LifetimeReviewCycleBudget") && warning.Contains("lifetime-review-cycle-budget")
                && warning.Contains("parks for a human at its very first settle point"),
            "the lifetime budget is only ever consulted at a settle point — review cycles themselves still run");
    }

    /// <summary>
    /// Unlike <c>max-concurrent-agent-sessions</c>, the four review-cycle caps are not excluded
    /// from Program.cs's own <c>Bind()</c> call, so an unparseable value here makes
    /// <c>ConfigurationBinder</c> throw at daemon startup rather than fall back to the config file
    /// or default — the message must say so rather than reusing the retired setting's
    /// falls-back-gracefully wording. Origin: independent pre-PR review, cycle 8, adversarial lens.
    /// </summary>
    [Fact]
    public async Task An_unparseable_review_cap_environment_variable_reports_a_daemon_crash_not_a_fallback()
    {
        Environment.SetEnvironmentVariable("Hall9k__MaxComplianceReviewCycles", "four");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.MaxComplianceReviewCycles.Origin.Should().Be(SettingOrigin.Default);
        report.UnusableEnvironmentVariables.Should().ContainSingle(
            warning => warning.Contains("Hall9k__MaxComplianceReviewCycles")
                && warning.Contains("four")
                && warning.Contains("max-compliance-review-cycles")
                && warning.Contains("fails to start")
                && !warning.Contains("max-concurrent-agent-sessions"),
            "the daemon crashes converting this value through ConfigurationBinder rather than falling back, "
            + "unlike the retired max-concurrent-agent-sessions setting this message must not name instead");
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

    // Decisions Log #111: max-concurrent-task-runs replaces max-concurrent-agent-sessions as the
    // node's own admission unit; the retired key still converts (floor(sessions/2), minimum 1)
    // when the new key is absent, independently at each precedence level.

    [Fact]
    public async Task A_config_file_max_concurrent_task_runs_wins_outright()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.MaxConcurrentTaskRuns = 5, CancellationToken.None);

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.MaxConcurrentTaskRuns.Value.Should().Be(5);
        report.MaxConcurrentTaskRuns.Origin.Should().Be(SettingOrigin.PlatformConfigFile);
        report.MaxConcurrentTaskRunsConvertedFromLegacy.Should().BeFalse();
    }

    [Fact]
    public async Task A_config_file_legacy_session_ceiling_converts_when_the_new_key_is_absent()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.MaxConcurrentAgentSessions = 6, CancellationToken.None);

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.MaxConcurrentTaskRuns.Value.Should().Be(3, "floor(6/2) with a two-lens review cycle's peak");
        report.MaxConcurrentTaskRuns.Origin.Should().Be(SettingOrigin.PlatformConfigFile);
        report.MaxConcurrentTaskRunsConvertedFromLegacy.Should().BeTrue();
    }

    [Fact]
    public async Task A_config_file_legacy_session_ceiling_below_the_lens_count_still_converts_to_one_run()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.MaxConcurrentAgentSessions = 1, CancellationToken.None);

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.MaxConcurrentTaskRuns.Value.Should().Be(1, "the floor never drops to zero — a node that dispatches nothing is worse");
        report.MaxConcurrentTaskRunsConvertedFromLegacy.Should().BeTrue();
    }

    [Fact]
    public async Task An_environment_variable_max_concurrent_task_runs_outranks_the_config_files_legacy_key()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.MaxConcurrentAgentSessions = 6, CancellationToken.None);
        Environment.SetEnvironmentVariable("Hall9k__MaxConcurrentTaskRuns", "4");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.MaxConcurrentTaskRuns.Value.Should().Be(4);
        report.MaxConcurrentTaskRuns.Origin.Should().Be(SettingOrigin.EnvironmentVariable);
        report.MaxConcurrentTaskRunsConvertedFromLegacy.Should().BeFalse();
    }

    [Fact]
    public async Task A_new_key_at_one_level_wins_over_the_new_key_at_a_lower_level_even_when_the_lower_level_also_sets_the_legacy_key()
    {
        // The precedence chain compares each level's own answer (new key, else converted legacy),
        // never a flattened merge of every key across every level: an environment variable naming
        // only the new key must still outrank a config file that sets both, exactly as it would if
        // the config file set only the legacy key.
        await PlatformConfigFile.WriteOperatingSettingsAsync(s =>
        {
            s.MaxConcurrentTaskRuns = 5;
            s.MaxConcurrentAgentSessions = 6;
        }, CancellationToken.None);
        Environment.SetEnvironmentVariable("Hall9k__MaxConcurrentTaskRuns", "2");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.MaxConcurrentTaskRuns.Value.Should().Be(2);
        report.MaxConcurrentTaskRuns.Origin.Should().Be(SettingOrigin.EnvironmentVariable);
    }

    [Fact]
    public async Task An_environment_variable_naming_only_the_legacy_key_still_outranks_a_config_file_new_key()
    {
        // The conversion applies at each precedence level independently (the acceptance
        // criterion): the environment level's own answer is "no new key, but the legacy key
        // converts", and that still beats the config file's own new-key answer, the same way an
        // environment variable always outranks the config file. This is also the one shape that
        // makes a naive "set max-concurrent-task-runs to stop relying on the conversion" remedy a
        // no-op: the config file already sets it, and the environment variable still wins anyway
        // (independent pre-PR review, cycle 1, adversarial lens) — ShadowsConfigFileValue is what
        // lets a caller tell this shape apart from the ordinary "nothing set it anywhere" case.
        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.MaxConcurrentTaskRuns = 5, CancellationToken.None);
        Environment.SetEnvironmentVariable("Hall9k__MaxConcurrentAgentSessions", "6");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.MaxConcurrentTaskRuns.Value.Should().Be(3, "floor(6/2) — the environment level's own converted answer");
        report.MaxConcurrentTaskRuns.Origin.Should().Be(SettingOrigin.EnvironmentVariable);
        report.MaxConcurrentTaskRunsConvertedFromLegacy.Should().BeTrue();
        report.MaxConcurrentTaskRunsShadowsConfigFileValue.Should().BeTrue(
            "the config file already sets max-concurrent-task-runs, but the environment-level legacy "
            + "conversion outranks it regardless");
    }

    [Fact]
    public async Task A_below_floor_legacy_key_does_not_warn_when_a_new_key_at_the_same_level_decides_the_ceiling()
    {
        // The retired key's own below-1 warning used to fire unconditionally, even when
        // max-concurrent-task-runs — set at the same level — is what the ceiling actually resolved
        // to. That told an operator dispatch had floored to one run while the node genuinely
        // admitted eight (independent pre-PR review, cycle 4, adversarial lens).
        await PlatformConfigFile.WriteOperatingSettingsAsync(s =>
        {
            s.MaxConcurrentTaskRuns = 8;
            s.MaxConcurrentAgentSessions = 0;
        }, CancellationToken.None);

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.MaxConcurrentTaskRuns.Value.Should().Be(8);
        report.MaxConcurrentTaskRunsConvertedFromLegacy.Should().BeFalse();
        report.UnusableEnvironmentVariables.Should().NotContain(
            warning => warning.Contains("max-concurrent-agent-sessions") && warning.Contains("floors"));
    }

    [Fact]
    public async Task A_config_file_session_cap_per_run_outranks_the_default()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.SessionCapPerRun = 1, CancellationToken.None);

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.SessionCapPerRun.Value.Should().Be(1);
        report.SessionCapPerRun.Origin.Should().Be(SettingOrigin.PlatformConfigFile);
    }

    [Fact]
    public async Task An_environment_variable_session_cap_per_run_outranks_the_config_file()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.SessionCapPerRun = 1, CancellationToken.None);
        Environment.SetEnvironmentVariable("Hall9k__SessionCapPerRun", "2");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.SessionCapPerRun.Value.Should().Be(2);
        report.SessionCapPerRun.Origin.Should().Be(SettingOrigin.EnvironmentVariable);
    }

    // Both new settings are excluded from Program.cs's own Bind() call (DaemonOptionsBinding's own
    // doc: an internal DaemonOptions setter alone would not be enough, since ConfigurationBinder
    // converts a section's raw value before it ever checks whether it can assign it), so
    // ConfigurationBinder never binds them and an unparseable value cannot crash the daemon the way
    // the legacy max-concurrent-agent-sessions key still can — the unusable-variable message has to
    // say so accurately rather than reuse ResolveInt's crash-claiming wording, and it has to fall
    // through to a lower level rather than reading as though the level were unset.

    [Fact]
    public async Task An_unparseable_max_concurrent_task_runs_env_var_falls_through_rather_than_reporting_a_crash()
    {
        Environment.SetEnvironmentVariable("Hall9k__MaxConcurrentTaskRuns", "four");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.MaxConcurrentTaskRuns.Value.Should().Be(OperatingSettings.DefaultMaxConcurrentTaskRuns,
            "the daemon never binds this key through ConfigurationBinder, so it does not crash — it falls "
            + "through to a lower precedence level or the default instead");
        report.MaxConcurrentTaskRuns.Origin.Should().Be(SettingOrigin.Default);
        report.UnusableEnvironmentVariables.Should().ContainSingle(warning =>
            warning.Contains("Hall9k__MaxConcurrentTaskRuns") && warning.Contains("treated as absent")
            && !warning.Contains("fail to start"),
            "the message must not claim a crash that cannot happen for this key");
    }

    [Fact]
    public async Task An_unparseable_session_cap_per_run_env_var_falls_through_rather_than_reporting_a_crash()
    {
        Environment.SetEnvironmentVariable("Hall9k__SessionCapPerRun", "four");

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.SessionCapPerRun.Value.Should().Be(OperatingSettings.DefaultSessionCapPerRun);
        report.SessionCapPerRun.Origin.Should().Be(SettingOrigin.Default);
        report.UnusableEnvironmentVariables.Should().ContainSingle(warning =>
            warning.Contains("Hall9k__SessionCapPerRun") && warning.Contains("treated as absent")
            && !warning.Contains("fail to start"));
    }

    [Fact]
    public async Task A_zero_session_cap_per_run_in_the_config_file_is_reported_by_its_own_name_rather_than_the_ceilings()
    {
        await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.SessionCapPerRun = 0, CancellationToken.None);

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);

        report.SessionCapPerRun.Value.Should().Be(0);
        report.UnusableEnvironmentVariables.Should().ContainSingle(warning =>
            warning.Contains("session-cap-per-run") && !warning.Contains("max-concurrent-agent-sessions"),
            "reusing the ceiling's own floor-warning wording here would name the wrong setting");
    }
}
