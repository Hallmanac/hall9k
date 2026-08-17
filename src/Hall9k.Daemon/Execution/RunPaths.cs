namespace Hall9k.Daemon.Execution;

/// <summary>Filesystem layout for a run's artifacts: ~/.hall9k/runs/&lt;run-id&gt;/ (log #2).</summary>
public static class RunPaths
{
    // Resolved per call so tests can point HALL9K_HOME at a temp directory.
    public static string Root => Environment.GetEnvironmentVariable("HALL9K_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hall9k");

    public static string RunDirectory(Guid runId) => Path.Combine(Root, "runs", runId.ToString());

    public static string StreamFile(Guid runId) => Path.Combine(RunDirectory(runId), "stream.jsonl");

    public static string PromptFile(Guid runId) => Path.Combine(RunDirectory(runId), "prompt.md");

    public static string SettingsFile(Guid runId) => Path.Combine(RunDirectory(runId), "settings.json");

    public static string StandardErrorFile(Guid runId) => Path.Combine(RunDirectory(runId), "stderr.log");
}
