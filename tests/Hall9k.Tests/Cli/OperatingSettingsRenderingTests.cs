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
    private static OperatingSettingsReport ReportWithOneRole(string role, string? model) =>
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
            new ResolvedSetting<int>(OperatingSettings.DefaultLifetimeReviewCycleBudget, SettingOrigin.Default, null));

    [Fact]
    public void An_unset_review_verify_role_falls_through_to_review_rather_than_the_generic_default()
    {
        OperatingSettingsReport report = ReportWithOneRole(nameof(RoleModelSettings.ReviewVerify), null);

        (string Label, string Value) row = OperatingSettingsRendering.Rows(report)
            .Single(r => r.Label == "model (review-verify)");

        row.Value.Should().Be("not set — falls through to whatever --model-review itself resolves to");
    }

    [Fact]
    public void An_unset_ordinary_role_falls_through_to_the_generic_project_or_platform_default()
    {
        OperatingSettingsReport report = ReportWithOneRole(nameof(RoleModelSettings.Build), null);

        (string Label, string Value) row = OperatingSettingsRendering.Rows(report)
            .Single(r => r.Label == "model (build)");

        row.Value.Should().Be("not set — falls through to the project or platform default");
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
}
