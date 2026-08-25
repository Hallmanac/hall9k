using Hall9k.Domain.Infrastructure.Persistence;

namespace Hall9k.Cli.Infrastructure;

/// <summary>
/// The one rendering of an <see cref="OperatingSettingsReport"/>, shared by <c>h9k config show</c>
/// (the detailed view) and <c>h9k daemon status</c> (the same facts, condensed) so the two never
/// describe a setting's origin in different words (backlog 59).
/// </summary>
public static class OperatingSettingsRendering
{
    public static IReadOnlyList<(string Label, string Value)> Rows(OperatingSettingsReport report)
    {
        List<(string Label, string Value)> rows =
        [
            ("max-concurrent-agent-sessions",
                $"{report.MaxConcurrentAgentSessions.Value} ({report.MaxConcurrentAgentSessions.DescribeOrigin()})"),
            ("default-model", $"{report.DefaultModel.Value} ({report.DefaultModel.DescribeOrigin()})"),
        ];

        rows.AddRange(report.ModelByRole.Select(role => (
            $"model ({role.Role.ToLowerInvariant()})",
            role.Model.Value is { Length: > 0 } value
                ? $"{value} ({role.Model.DescribeOrigin()})"
                : "not set — falls through to the project or platform default")));

        return rows;
    }
}
