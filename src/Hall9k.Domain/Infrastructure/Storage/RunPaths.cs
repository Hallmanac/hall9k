namespace Hall9k.Domain.Infrastructure.Storage;

/// <summary>Filesystem layout for a run's artifacts: ~/.hall9k/runs/&lt;run-id&gt;/ (log #2).</summary>
public static class RunPaths
{
    /// <summary>The platform home, shared with every other on-disk layout (<see cref="PlatformPaths"/>).</summary>
    public static string Root => PlatformPaths.Home;

    public static string RunDirectory(Guid runId) => Path.Combine(Root, "runs", runId.ToString());

    public static string StreamFile(Guid runId) => Path.Combine(RunDirectory(runId), "stream.jsonl");

    public static string PromptFile(Guid runId) => Path.Combine(RunDirectory(runId), "prompt.md");

    public static string SettingsFile(Guid runId) => Path.Combine(RunDirectory(runId), "settings.json");

    public static string StandardErrorFile(Guid runId) => Path.Combine(RunDirectory(runId), "stderr.log");

    // Pre-PR review and fix sessions (log #24) share the run's directory; each session's
    // files are prefixed with a per-session name so cycles never collide.
    public static string SessionStreamFile(Guid runId, string sessionName) =>
        Path.Combine(RunDirectory(runId), $"{sessionName}.stream.jsonl");

    public static string SessionPromptFile(Guid runId, string sessionName) =>
        Path.Combine(RunDirectory(runId), $"{sessionName}.prompt.md");

    public static string SessionStandardErrorFile(Guid runId, string sessionName) =>
        Path.Combine(RunDirectory(runId), $"{sessionName}.stderr.log");

    /// <summary>The reviewer's verified findings and verdict for one review cycle, written by the daemon.</summary>
    public static string ReviewFindingsFile(Guid runId, int cycle) =>
        Path.Combine(RunDirectory(runId), $"review-{cycle}-findings.md");

    /// <summary>The fix session's closing summary — on a dispute, the second position the human reads.</summary>
    public static string ReviewFixPositionFile(Guid runId, int cycle) =>
        Path.Combine(RunDirectory(runId), $"review-{cycle}-fix-position.md");
}
