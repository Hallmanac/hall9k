namespace Hall9k.Connectors.Prompts;

/// <summary>
/// The one platform-imposed Claude Code setting (PLAN.md §6.6): agents never author
/// co-authored-by trailers. Shared between the daemon's headless spawn
/// (<c>Hall9k.Daemon.Execution.ClaudeExecutor</c>) and an operator's interactive claim
/// (<c>h9k task work</c>) so the override reaches a session's commits identically regardless of
/// who launched it — the CLI cannot reference <c>Hall9k.Daemon</c>, so this is the shared home
/// both sides read the content from, the same pattern <see cref="WorkPromptBuilder"/> already
/// uses for the prompt itself.
/// </summary>
public static class ClaudeSettingsFile
{
    public const string Content = """{"includeCoAuthoredBy": false}""";
}
