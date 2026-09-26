using FluentAssertions;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="OperatingSettingsRendering.Rows"/> is the one place both <c>h9k config show</c> and
/// <c>h9k daemon status</c> render a role's model row, so its two <c>ReviewVerify</c>-specific
/// helpers — the kebab-case label and the narrower fallthrough sentence — need their own coverage
/// rather than riding on the resolver tests that exercise the report they consume (adversarial
/// review, cycle 1 of Decisions Log #105: every other piece of that change gained a test except
/// these two).
/// </summary>
public sealed class OperatingSettingsRenderingTests
{
    private static OperatingSettingsReport ReportWithOneRole(
        string role, string? model, string? effort = null, SettingOrigin effortOrigin = SettingOrigin.Default,
        int mintHold = OperatingSettings.DefaultAutoPrReviewMintHoldSeconds,
        SettingOrigin mintHoldOrigin = SettingOrigin.Default,
        IReadOnlyList<RoleEffortSetting>? effortByRole = null) =>
        new(
            new ResolvedSetting<int>(OperatingSettings.DefaultMaxConcurrentAgentSessions, SettingOrigin.Default, null),
            false,
            new ResolvedSetting<int>(OperatingSettings.DefaultMaxConcurrentTaskRuns, SettingOrigin.Default, null),
            false,
            false,
            new ResolvedSetting<int>(OperatingSettings.DefaultSessionCapPerRun, SettingOrigin.Default, null),
            new ResolvedSetting<string>(AgentModel.PlatformFallback, SettingOrigin.Default, null),
            [new RoleModelSetting(role, new ResolvedSetting<string?>(model, SettingOrigin.Default, null))],
            null,
            [],
            new ResolvedSetting<int>(OperatingSettings.DefaultMaxComplianceReviewCycles, SettingOrigin.Default, null),
            new ResolvedSetting<int>(OperatingSettings.DefaultMaxAdversarialReviewCycles, SettingOrigin.Default, null),
            new ResolvedSetting<int>(OperatingSettings.DefaultMaxFinalFullPassRounds, SettingOrigin.Default, null),
            new ResolvedSetting<int>(OperatingSettings.DefaultLifetimeReviewCycleBudget, SettingOrigin.Default, null),
            new ResolvedSetting<long?>(null, SettingOrigin.Default, null),
            new ResolvedSetting<string>(OperatingSettings.DefaultSpendPeriod, SettingOrigin.Default, null),
            new ResolvedSetting<string>(
                Hall9k.Domain.Features.Run.ReviewStageComposition.FullPipeline.Value, SettingOrigin.Default, null),
            new ResolvedSetting<string?>(
                effort, effortOrigin, effortOrigin == SettingOrigin.PlatformConfigFile ? Hall9kDatabase.ConfigFile : null),
            new ResolvedSetting<int>(
                mintHold, mintHoldOrigin, mintHoldOrigin == SettingOrigin.PlatformConfigFile ? Hall9kDatabase.ConfigFile : null),
            effortByRole ?? []);

    [Fact]
    public void The_mint_hold_prints_in_whole_seconds_with_its_origin()
    {
        OperatingSettingsReport report = ReportWithOneRole(
            nameof(RoleModelSettings.Build), null, mintHold: 120, mintHoldOrigin: SettingOrigin.PlatformConfigFile);

        OperatingSettingsRendering.Rows(report).Single(r => r.Label == "auto-pr-review-mint-hold").Value
            .Should().Be($"120s (config: {Hall9kDatabase.ConfigFile})");
    }

    [Fact]
    public void The_default_mint_hold_is_five_minutes_and_says_it_is_the_default()
    {
        OperatingSettingsReport report = ReportWithOneRole(nameof(RoleModelSettings.Build), null);

        OperatingSettingsRendering.Rows(report).Single(r => r.Label == "auto-pr-review-mint-hold").Value
            .Should().Be("300s (default)");
    }

    [Fact]
    public void A_zero_mint_hold_says_this_node_never_defers()
    {
        OperatingSettingsReport report = ReportWithOneRole(
            nameof(RoleModelSettings.Build), null, mintHold: 0, mintHoldOrigin: SettingOrigin.PlatformConfigFile);

        OperatingSettingsRendering.Rows(report).Single(r => r.Label == "auto-pr-review-mint-hold").Value
            .Should().StartWith("0s, this node never defers");
    }

    [Fact]
    public void An_unset_effort_says_the_models_own_default_decides()
    {
        OperatingSettingsReport report = ReportWithOneRole(nameof(RoleModelSettings.Build), null);

        OperatingSettingsRendering.Rows(report).Single(r => r.Label == "effort").Value
            .Should().Be("not set (default), so sessions run at the model's own default");
    }

    [Fact]
    public void A_configured_effort_prints_with_its_origin_the_way_default_model_does()
    {
        OperatingSettingsReport report = ReportWithOneRole(
            nameof(RoleModelSettings.Build), null, "high", SettingOrigin.PlatformConfigFile);

        IReadOnlyList<(string Label, string Value)> rows = OperatingSettingsRendering.Rows(report);

        rows.Single(r => r.Label == "effort").Value.Should().Be($"high (config: {Hall9kDatabase.ConfigFile})");
        rows.Single(r => r.Label == "default-model").Value.Should().EndWith("(default)");
    }

    private static RoleEffortSetting EffortRole(
        string role, string? value = null, SettingOrigin origin = SettingOrigin.Default) =>
        new(role, new ResolvedSetting<string?>(
            value, origin, origin switch
            {
                SettingOrigin.PlatformConfigFile => Hall9kDatabase.ConfigFile,
                SettingOrigin.EnvironmentVariable => $"Hall9k__EffortByRole__{role}",
                _ => null,
            }));

    private static IReadOnlyList<(string Label, string Value)> EffortRows(params RoleEffortSetting[] roles) =>
        OperatingSettingsRendering.Rows(ReportWithOneRole(nameof(RoleModelSettings.Build), null, effortByRole: roles));

    [Fact]
    public void Each_role_gets_one_effort_line_directly_beneath_the_node_wide_line()
    {
        IReadOnlyList<(string Label, string Value)> rows = EffortRows(
            EffortRole("Build"), EffortRole("Review"), EffortRole("ReviewVerify"), EffortRole("ReviewFinalFullPass"),
            EffortRole("Courier"));

        List<string> labels = [.. rows.Select(row => row.Label)];
        int node = labels.IndexOf("effort");
        labels.Skip(node).Take(6).Should().Equal(
            "effort", "effort (build)", "effort (review)", "effort (review-verify)", "effort (review-finalpass)",
            "effort (courier)");
    }

    [Fact]
    public void A_role_effort_from_the_config_file_names_its_value_and_origin()
    {
        IReadOnlyList<(string Label, string Value)> rows = EffortRows(
            EffortRole("Build", "xhigh", SettingOrigin.PlatformConfigFile));

        rows.Single(r => r.Label == "effort (build)").Value.Should().Be($"xhigh (config: {Hall9kDatabase.ConfigFile})");
    }

    [Fact]
    public void A_role_effort_from_the_environment_names_the_variable()
    {
        IReadOnlyList<(string Label, string Value)> rows = EffortRows(
            EffortRole("Fix", "low", SettingOrigin.EnvironmentVariable));

        rows.Single(r => r.Label == "effort (fix)").Value.Should().Be("low (env: Hall9k__EffortByRole__Fix)");
    }

    [Fact]
    public void An_unset_ordinary_role_effort_falls_through_to_the_node_wide_level()
    {
        IReadOnlyList<(string Label, string Value)> rows = EffortRows(EffortRole("Build"));

        rows.Single(r => r.Label == "effort (build)").Value.Should().Be(
            "not set, falls through to the node-wide effort above, or the model's own default when that is not set either");
    }

    [Theory]
    [InlineData("ReviewVerify", "effort (review-verify)")]
    [InlineData("ReviewFinalFullPass", "effort (review-finalpass)")]
    public void An_unset_pass_effort_falls_through_to_review_before_the_node_wide_level(string role, string label)
    {
        IReadOnlyList<(string Label, string Value)> rows = EffortRows(EffortRole(role));

        rows.Single(r => r.Label == label).Value.Should().Be(
            "not set, falls through to whatever --effort-review itself resolves to");
    }

    [Fact]
    public void A_blank_role_effort_environment_variable_is_named_rather_than_read_as_silence()
    {
        IReadOnlyList<(string Label, string Value)> rows = EffortRows(
            new RoleEffortSetting("Build", new ResolvedSetting<string?>(
                string.Empty, SettingOrigin.EnvironmentVariable, "Hall9k__EffortByRole__Build")));

        rows.Single(r => r.Label == "effort (build)").Value.Should().StartWith("(empty) (env: Hall9k__EffortByRole__Build)");
    }

    [Fact]
    public void An_unset_review_verify_role_falls_through_to_review_rather_than_the_generic_default()
    {
        OperatingSettingsReport report = ReportWithOneRole(nameof(RoleModelSettings.ReviewVerify), null);

        (string Label, string Value) row = OperatingSettingsRendering.Rows(report)
            .Single(r => r.Label == "model (review-verify)");

        row.Value.Should().Be("not set — falls through to whatever --model-review itself resolves to");
    }

    [Fact]
    public void An_unset_review_finalpass_role_falls_through_to_review_rather_than_the_generic_default()
    {
        OperatingSettingsReport report = ReportWithOneRole(nameof(RoleModelSettings.ReviewFinalFullPass), null);

        (string Label, string Value) row = OperatingSettingsRendering.Rows(report)
            .Single(r => r.Label == "model (review-finalpass)");

        row.Value.Should().Be("not set — falls through to whatever --model-review itself resolves to");
    }

    [Fact]
    public void An_unset_ordinary_role_falls_through_to_the_platform_default()
    {
        OperatingSettingsReport report = ReportWithOneRole(nameof(RoleModelSettings.Build), null);

        (string Label, string Value) row = OperatingSettingsRendering.Rows(report)
            .Single(r => r.Label == "model (build)");

        row.Value.Should().Be("not set — falls through to the platform default");
    }

    [Fact]
    public void An_unset_courier_falls_through_to_its_own_sonnet_floor()
    {
        OperatingSettingsReport report = ReportWithOneRole(nameof(RoleModelSettings.Courier), null);

        (string Label, string Value) row = OperatingSettingsRendering.Rows(report)
            .Single(r => r.Label == "model (courier)");

        row.Value.Should().Be($"not set — falls through to {AgentModel.CourierDefault}, the courier's own floor");
    }

    /// <summary>
    /// On a fresh install the retired row's own value is its unused built-in default, never
    /// actually read from anywhere — claiming it is "read only as a fallback" would assert a
    /// relationship that does not hold, since the resolver never consults this setting at all when
    /// nothing sets either key anywhere (independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    [Fact]
    public void A_retired_key_at_its_own_unused_default_is_not_described_as_a_fallback_in_force()
    {
        OperatingSettingsReport report = ReportWithOneRole(nameof(RoleModelSettings.Build), null);

        (string Label, string Value) row = OperatingSettingsRendering.Rows(report)
            .Single(r => r.Label == "max-concurrent-agent-sessions (retired)");

        row.Value.Should().NotContain("read only as a fallback");
        row.Value.Should().Contain("nothing here for max-concurrent-task-runs to fall back to");
    }

    /// <summary>The counterpart case: something genuinely configured the retired key, so the fallback claim is true.</summary>
    [Fact]
    public void A_retired_key_actually_configured_somewhere_is_described_as_a_fallback()
    {
        OperatingSettingsReport template = ReportWithOneRole(nameof(RoleModelSettings.Build), null);
        OperatingSettingsReport report = template with
        {
            MaxConcurrentAgentSessions = new ResolvedSetting<int>(6, SettingOrigin.PlatformConfigFile, "config.json"),
        };

        (string Label, string Value) row = OperatingSettingsRendering.Rows(report)
            .Single(r => r.Label == "max-concurrent-agent-sessions (retired)");

        row.Value.Should().Contain("read only as a fallback when max-concurrent-task-runs is absent");
    }

    /// <summary>
    /// A null or empty <c>{}</c> leaf binds to a fabricated zero the resolver treats as absent
    /// rather than a fallback in force (independent pre-PR review, cycle 1, both lenses) — this
    /// must read differently from a genuinely configured zero, which the previous test covers.
    /// </summary>
    [Fact]
    public void A_retired_key_bound_to_a_fabricated_zero_is_not_described_as_a_fallback()
    {
        OperatingSettingsReport template = ReportWithOneRole(nameof(RoleModelSettings.Build), null);
        OperatingSettingsReport report = template with
        {
            MaxConcurrentAgentSessions = new ResolvedSetting<int>(0, SettingOrigin.PlatformConfigFile, "config.json"),
            MaxConcurrentAgentSessionsIsFabricatedZero = true,
        };

        (string Label, string Value) row = OperatingSettingsRendering.Rows(report)
            .Single(r => r.Label == "max-concurrent-agent-sessions (retired)");

        row.Value.Should().NotContain("read only as a fallback");
        row.Value.Should().Contain("treats it as absent");
    }

    /// <summary>
    /// An environment variable can still win over a fabricated-zero file leaf with a real value of
    /// its own — that value genuinely is consulted as a fallback, so the fabricated-zero wording
    /// must not leak into a row whose displayed origin is not the config file.
    /// </summary>
    [Fact]
    public void A_fabricated_zero_at_the_file_level_does_not_suppress_the_fallback_wording_for_an_environment_value()
    {
        OperatingSettingsReport template = ReportWithOneRole(nameof(RoleModelSettings.Build), null);
        OperatingSettingsReport report = template with
        {
            MaxConcurrentAgentSessions = new ResolvedSetting<int>(5, SettingOrigin.EnvironmentVariable, "Hall9k__MaxConcurrentAgentSessions"),
            MaxConcurrentAgentSessionsIsFabricatedZero = true,
        };

        (string Label, string Value) row = OperatingSettingsRendering.Rows(report)
            .Single(r => r.Label == "max-concurrent-agent-sessions (retired)");

        row.Value.Should().Contain("read only as a fallback when max-concurrent-task-runs is absent");
    }

    /// <summary>
    /// A fresh node with nothing overridden must render the shipped review-cap defaults — 4, 2
    /// and 20 — not whatever an earlier build happened to compile in (independent pre-PR review,
    /// cycle 1, conformance lens: nothing previously pinned these three rows to their defaults).
    /// </summary>
    [Fact]
    public void Fresh_node_review_cap_rows_render_the_shipped_defaults()
    {
        OperatingSettingsReport report = ReportWithOneRole(nameof(RoleModelSettings.Build), null);

        IReadOnlyList<(string Label, string Value)> rows = OperatingSettingsRendering.Rows(report);

        rows.Single(r => r.Label == "max-adversarial-review-cycles").Value.Should().Be("4 (default)");
        rows.Single(r => r.Label == "max-final-full-pass-rounds").Value.Should().Be("2 (default)");
        rows.Single(r => r.Label == "lifetime-review-cycle-budget").Value.Should().Be("20 (default)");
    }

    /// <summary>The counterpart case: a node's own configured value renders instead, with its origin named.</summary>
    [Fact]
    public void A_configured_review_cap_row_renders_its_own_value_and_origin()
    {
        OperatingSettingsReport template = ReportWithOneRole(nameof(RoleModelSettings.Build), null);
        OperatingSettingsReport report = template with
        {
            MaxAdversarialReviewCycles = new ResolvedSetting<int>(7, SettingOrigin.PlatformConfigFile, "config.json"),
            MaxFinalFullPassRounds = new ResolvedSetting<int>(5, SettingOrigin.PlatformConfigFile, "config.json"),
            LifetimeReviewCycleBudget = new ResolvedSetting<int>(30, SettingOrigin.PlatformConfigFile, "config.json"),
        };

        IReadOnlyList<(string Label, string Value)> rows = OperatingSettingsRendering.Rows(report);

        rows.Single(r => r.Label == "max-adversarial-review-cycles").Value.Should().Be("7 (config: config.json)");
        rows.Single(r => r.Label == "max-final-full-pass-rounds").Value.Should().Be("5 (config: config.json)");
        rows.Single(r => r.Label == "lifetime-review-cycle-budget").Value.Should().Be("30 (config: config.json)");
    }
}
