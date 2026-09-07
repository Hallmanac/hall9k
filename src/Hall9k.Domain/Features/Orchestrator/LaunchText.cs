namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// The exact command an operator pastes to start a lean orchestrator window for one agent CLI
/// (task: an operator starts a lean node or project orchestrator window). One record per CLI
/// name, kept on the project for a project window and on the node (the platform config file)
/// for the node window — never a single scalar, because a second runtime (Codex, another agent
/// CLI) gets its own line rather than replacing Claude Code's.
/// <para>
/// <see cref="MeasuredTurnOneTokens"/> and <see cref="MeasuredAt"/> are the stamp
/// <c>h9k orchestrator measure</c> leaves on this exact record (Decisions Log): they are cleared
/// whenever <see cref="Text"/> changes, because a token count measured against a since-replaced
/// line is not an observed fact about the line that replaced it — never guessed forward.
/// </para>
/// </summary>
public sealed record LaunchText(string Cli, string Text, int? MeasuredTurnOneTokens = null, DateTimeOffset? MeasuredAt = null)
{
    /// <summary>The CLI <c>h9k orchestrator launch-text show</c> reads when <c>--cli</c> is not given.</summary>
    public const string DefaultCli = "claude-code";

    /// <summary>
    /// The stable key this record is looked up and replaced by: lowercase, trimmed, so
    /// "Claude-Code" and "claude-code " name the same setting.
    /// </summary>
    public static string NormalizeCli(string cli) => cli.Trim().ToLowerInvariant();
}
