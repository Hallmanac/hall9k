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

    /// <summary>
    /// One session's own settings file. The run-level <see cref="SettingsFile"/> was written
    /// afresh by every spawn, which was safe only while a run's sessions were strictly
    /// sequential; a review cycle now spawns its lenses together (log #59), and the second
    /// spawn's truncate-and-rewrite would land inside the first child's config-loading window.
    /// A session that owns its file has no writer but itself.
    /// </summary>
    public static string SessionSettingsFile(Guid runId, string sessionName) =>
        Path.Combine(RunDirectory(runId), $"{sessionName}.settings.json");

    public static string SessionStandardErrorFile(Guid runId, string sessionName) =>
        Path.Combine(RunDirectory(runId), $"{sessionName}.stderr.log");

    /// <summary>
    /// One review cycle's merged findings: every lens's verified findings under its own
    /// heading (log #59), written by the daemon when the cycle's last pass lands. This is the
    /// document the fix session is handed and the one a park points a human at, so the name
    /// is unchanged from the single-lens loop it replaces.
    /// </summary>
    public static string ReviewFindingsFile(Guid runId, int cycle) =>
        Path.Combine(RunDirectory(runId), $"review-{cycle}-findings.md");

    /// <summary>
    /// One lens's own findings for a cycle, exactly as that pass wrote them (log #59) — the
    /// unmerged record behind each section of <see cref="ReviewFindingsFile"/>.
    /// </summary>
    public static string ReviewLensFindingsFile(Guid runId, int cycle, string lensSlug) =>
        Path.Combine(RunDirectory(runId), $"review-{cycle}-{lensSlug}-findings.md");

    /// <summary>The fix session's closing summary — on a dispute, the second position the human reads.</summary>
    public static string ReviewFixPositionFile(Guid runId, int cycle) =>
        Path.Combine(RunDirectory(runId), $"review-{cycle}-fix-position.md");

    /// <summary>
    /// A follow-up's closing position when it disputed a review thread rather than settling it
    /// (Decisions Log #62) — the text a park points the human at, holding both the reviewer's
    /// position and the agent's. One per run, because a run parks at most once.
    /// </summary>
    public static string ReviewThreadDisputeFile(Guid runId) =>
        Path.Combine(RunDirectory(runId), "review-thread-dispute.md");

    /// <summary>
    /// The run's handoff to whatever depends on it (Decisions Log #36), written at session end
    /// beside the review findings so it is inspectable outside the ledger. The file's three
    /// states are three observations, which is what lets the closeout append record an honest
    /// HandoffOutcome without guessing: present and non-blank means the agent authored a
    /// handoff, present and empty means the session's result was read and carried none, and
    /// absent means there was no session-end capture at all.
    /// </summary>
    public static string HandoffFile(Guid runId) => Path.Combine(RunDirectory(runId), "handoff.md");

    /// <summary>The condensed blocker context a synthesis session produced for this run (log #36).</summary>
    public static string BlockerContextFile(Guid runId) => Path.Combine(RunDirectory(runId), "blocker-context.md");
}
