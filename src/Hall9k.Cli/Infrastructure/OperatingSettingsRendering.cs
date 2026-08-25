using Hall9k.Domain.Infrastructure.Persistence;

namespace Hall9k.Cli.Infrastructure;

/// <summary>
/// The one rendering of an <see cref="OperatingSettingsReport"/>, shared by <c>h9k config show</c>
/// (the detailed view) and <c>h9k daemon status</c> (the same facts, condensed) so the two never
/// describe a setting's origin in different words (backlog 59).
/// </summary>
public static class OperatingSettingsRendering
{
    /// <summary>
    /// Every line that names a problem with how the settings resolved — a malformed or wrongly
    /// shaped config file, an environment variable set to a value the daemon cannot use — in the
    /// one wording both commands print, so editing the crash-consequence sentence on one surface
    /// cannot silently diverge from the other.
    /// </summary>
    public static IReadOnlyList<string> ProblemLines(OperatingSettingsReport report)
    {
        List<string> lines = [];

        if (report.ConfigFileProblem is { } problem)
        {
            string consequence = problem.Consequence switch
            {
                ConfigFileProblemConsequence.DaemonFailsToStart =>
                    "The daemon's own ConfigurationBinder fails on the same value, so it will crash outright at startup rather than fall back.",
                ConfigFileProblemConsequence.SettingIsIgnored =>
                    "The daemon's own ConfigurationBinder has no conversion for this value, so this setting does not take its value from the file — every other setting in the file, and environment variables and built-in defaults, still apply. What it resolved to instead (usually the built-in default, but zero for an empty JSON object on a numeric setting) is in the row below.",
                _ => "The daemon skips the file for this run — environment variables and built-in defaults still apply.",
            };
            lines.Add($"{problem.Message} {consequence}");
        }

        lines.AddRange(report.UnusableEnvironmentVariables);

        return lines;
    }

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
            (role.Model.Value, role.Model.Origin) switch
            {
                ({ Length: > 0 } value, _) => $"{value} ({role.Model.DescribeOrigin()})",
                // A blank value at this level is ordinarily just "not set", but a blank
                // environment variable is a set-but-empty mistake (the shell-quoting shape this
                // feature's origin incident named), not silence — naming its origin here is the
                // only way this command can surface that it is shadowing a config-file value.
                (_, SettingOrigin.EnvironmentVariable) =>
                    $"(empty) ({role.Model.DescribeOrigin()}) — falls through to the project or platform default",
                _ => "not set — falls through to the project or platform default",
            })));

        return rows;
    }
}
