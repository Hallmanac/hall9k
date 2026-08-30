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
            new ResolvedSetting<string>(AgentModel.PlatformFallback, SettingOrigin.Default, null),
            [new RoleModelSetting(role, new ResolvedSetting<string?>(model, SettingOrigin.Default, null))],
            null,
            []);

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
}
