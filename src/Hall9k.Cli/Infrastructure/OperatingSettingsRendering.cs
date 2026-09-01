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
            string consequence = problem.Consequence == ConfigFileProblemConsequence.SettingIsIgnored
                ? problem.DescribeConsequence()
                    + " What it resolved to instead (usually the built-in default, but zero for an empty JSON "
                    + "object on a numeric setting) is in the row below."
                : problem.DescribeConsequence();
            lines.Add($"{problem.Message} {consequence}");
        }

        lines.AddRange(report.UnusableEnvironmentVariables);

        return lines;
    }

    public static IReadOnlyList<(string Label, string Value)> Rows(OperatingSettingsReport report)
    {
        string maxConcurrentTaskRunsValue = DescribeMaxConcurrentTaskRuns(report);

        List<(string Label, string Value)> rows =
        [
            ("max-concurrent-task-runs", maxConcurrentTaskRunsValue),
            ("session-cap-per-run", $"{report.SessionCapPerRun.Value} ({report.SessionCapPerRun.DescribeOrigin()})"),
            ("max-concurrent-agent-sessions (retired)", DescribeMaxConcurrentAgentSessions(report)),
            ("default-model", $"{report.DefaultModel.Value} ({report.DefaultModel.DescribeOrigin()})"),
            ("max-compliance-review-cycles",
                $"{report.MaxComplianceReviewCycles.Value} ({report.MaxComplianceReviewCycles.DescribeOrigin()})"),
            ("max-adversarial-review-cycles",
                $"{report.MaxAdversarialReviewCycles.Value} ({report.MaxAdversarialReviewCycles.DescribeOrigin()})"),
            ("max-final-full-pass-rounds",
                $"{report.MaxFinalFullPassRounds.Value} ({report.MaxFinalFullPassRounds.DescribeOrigin()})"),
            ("lifetime-review-cycle-budget",
                $"{report.LifetimeReviewCycleBudget.Value} ({report.LifetimeReviewCycleBudget.DescribeOrigin()})"),
        ];

        rows.AddRange(report.ModelByRole.Select(role => (
            $"model ({KebabCase(role.Role)})",
            (role.Model.Value, role.Model.Origin) switch
            {
                ({ Length: > 0 } value, _) => $"{value} ({role.Model.DescribeOrigin()})",
                // A blank value at this level is ordinarily just "not set", but a blank
                // environment variable is a set-but-empty mistake (the shell-quoting shape this
                // feature's origin incident named), not silence — naming its origin here is the
                // only way this command can surface that it is shadowing a config-file value.
                (_, SettingOrigin.EnvironmentVariable) =>
                    $"(empty) ({role.Model.DescribeOrigin()}) — falls through to {FallthroughDescription(role.Role)}",
                _ => $"not set — falls through to {FallthroughDescription(role.Role)}",
            })));

        return rows;
    }

    /// <summary>
    /// The max-concurrent-task-runs row's value: plain when nothing converted it, and one of two
    /// different remedies when the retired max-concurrent-agent-sessions key converted instead —
    /// "set it directly" only actually works when the conversion itself resolved at the config-file
    /// level. When it resolved at the environment level, an environment variable naming only the
    /// legacy key still outranks a config-file value for the new one regardless of whether the file
    /// already carries one, so "set it directly" is a no-op there; unsetting the legacy variable, or
    /// exporting the new one directly, is the only remedy that actually changes what this node runs
    /// with (independent pre-PR review, cycle 2, adversarial lens — the prior wording gave this
    /// no-op remedy in both shapes).
    /// </summary>
    private static string DescribeMaxConcurrentTaskRuns(OperatingSettingsReport report)
    {
        if (!report.MaxConcurrentTaskRunsConvertedFromLegacy)
        {
            return $"{report.MaxConcurrentTaskRuns.Value} ({report.MaxConcurrentTaskRuns.DescribeOrigin()})";
        }

        string origin = report.MaxConcurrentTaskRuns.DescribeOrigin();
        if (report.MaxConcurrentTaskRuns.Origin != SettingOrigin.EnvironmentVariable)
        {
            return $"{report.MaxConcurrentTaskRuns.Value} (converted from the retired max-concurrent-agent-sessions, "
                + $"{origin} — set max-concurrent-task-runs directly to stop relying on the conversion)";
        }

        string newEnvironmentVariable = $"{OperatingSettingsResolver.EnvironmentPrefix}{nameof(OperatingSettings.MaxConcurrentTaskRuns)}";
        string outranks = report.MaxConcurrentTaskRunsShadowsConfigFileValue
            ? "the platform config file already sets max-concurrent-task-runs directly, but "
            : string.Empty;
        return $"{report.MaxConcurrentTaskRuns.Value} (converted from the retired max-concurrent-agent-sessions, "
            + $"{origin} — {outranks}an environment variable naming only the legacy key still outranks a "
            + "config-file value for the new one, so setting max-concurrent-task-runs directly would not change "
            + $"this; unset max-concurrent-agent-sessions, or export {newEnvironmentVariable} directly, to stop "
            + "relying on the conversion)";
    }

    /// <summary>
    /// The retired key's own row: "read only as a fallback" is true whenever this value came from
    /// somewhere real (an environment variable or the config file — it is consulted at that same
    /// level whenever max-concurrent-task-runs is absent there), but it overclaims in two shapes:
    /// a fresh install where nothing sets either key anywhere, where the resolver never actually
    /// reads this setting for anything and just falls straight through to
    /// <c>DefaultMaxConcurrentTaskRuns</c>, so the value shown here is this key's own unused
    /// built-in default, not a fallback in force (independent pre-PR review, cycle 1, adversarial
    /// lens); and a config-file leaf that binds to
    /// <see cref="OperatingSettingsReport.MaxConcurrentAgentSessionsIsFabricatedZero"/>'s simulated
    /// zero, which <see cref="OperatingSettingsResolver.ResolveMaxConcurrentTaskRuns"/> treats as
    /// absent at the config-file level rather than converting into a run ceiling, so this value is
    /// never actually consulted there either (independent pre-PR review, cycle 1, both lenses).
    /// The fabricated-zero check only applies when this row's own value actually came from the
    /// config file — an environment variable can still win over a fabricated-zero file leaf with a
    /// real value of its own, and that value genuinely is consulted as a fallback at its own level.
    /// </summary>
    private static string DescribeMaxConcurrentAgentSessions(OperatingSettingsReport report)
    {
        string value = $"{report.MaxConcurrentAgentSessions.Value} ({report.MaxConcurrentAgentSessions.DescribeOrigin()})";
        if (report.MaxConcurrentAgentSessions.Origin == SettingOrigin.Default)
        {
            return MaxConcurrentAgentSessionsWasDiscarded(report)
                ? $"{value} — the value above was discarded rather than read, so there is nothing usable here for "
                    + "max-concurrent-task-runs to fall back to"
                : $"{value} — not set anywhere, so there is nothing here for max-concurrent-task-runs to fall back to";
        }

        return report.MaxConcurrentAgentSessions.Origin == SettingOrigin.PlatformConfigFile
            && report.MaxConcurrentAgentSessionsIsFabricatedZero
            ? $"{value} — a null or empty {{}} leaf binds to this fabricated zero, but max-concurrent-task-runs "
                + "treats it as absent rather than falling back to it"
            : $"{value} — read only as a fallback when max-concurrent-task-runs is absent";
    }

    /// <summary>
    /// <see cref="SettingOrigin.Default"/> on <see cref="OperatingSettingsReport.MaxConcurrentAgentSessions"/>
    /// means either of two different things: nothing ever set the retired key, or something did and
    /// <see cref="OperatingSettingsResolver.ResolveAsync"/> discarded it as unusable (an environment
    /// variable that failed to parse, or a config-file leaf recovered by
    /// <c>PlatformConfigFile.RecoverSectionIgnoring</c>) and fell through to the same built-in
    /// default either way. The row above already prints that mistake as a problem line
    /// (<see cref="ProblemLines"/>); this only decides which sentence this row pairs it with
    /// (independent pre-PR review, cycle 3, adversarial lens).
    /// </summary>
    private static bool MaxConcurrentAgentSessionsWasDiscarded(OperatingSettingsReport report) =>
        report.UnusableEnvironmentVariables.Any(message => message.StartsWith(
            $"{OperatingSettingsResolver.EnvironmentPrefix}MaxConcurrentAgentSessions ", StringComparison.Ordinal))
        || (report.ConfigFileProblem is { Consequence: ConfigFileProblemConsequence.SettingIsIgnored } problem
            && problem.Message.Contains("maxConcurrentAgentSessions", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every ordinary role falls through to the project or platform default, but
    /// <c>ReviewVerify</c> sits underneath the plain Review chain rather than beside it
    /// (<c>DaemonOptions.ResolveVerifyReviewModel</c>) — an unset knob resolves to whatever
    /// <c>--model-review</c> itself resolves to, which can outrank the project or platform
    /// default. Stating the generic fallthrough for this one role would tell an operator running
    /// on a configured <c>--model-review</c> that Verify passes run on the project or platform
    /// default when they in fact run on that configured review model.
    /// </summary>
    private static string FallthroughDescription(string role) =>
        role == nameof(RoleModelSettings.ReviewVerify)
            ? "whatever --model-review itself resolves to"
            : "the project or platform default";

    /// <summary>
    /// <c>role.Role</c> is a C# property name (<c>ReviewVerify</c>), matching the
    /// <c>--model-review-verify</c>-shaped CLI flag it is set through only once split at each
    /// internal capital, so the row label reads "review-verify" rather than "reviewverify".
    /// </summary>
    private static string KebabCase(string pascalCase) =>
        string.Concat(pascalCase.Select((character, index) =>
            index > 0 && char.IsUpper(character) ? $"-{character}" : character.ToString())).ToLowerInvariant();
}
