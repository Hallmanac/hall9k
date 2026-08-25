using Hall9k.Domain.Features.Project;

namespace Hall9k.Domain.Infrastructure.Storage;

/// <summary>
/// Filesystem layout for a run's artifacts (log #2), keyed off the run's own directory rather
/// than its id: prompt, stream, verify logs and review files all live beside one another, and
/// every method here just names a file within whatever directory the caller passes.
/// <para>
/// Where that directory IS is resolved once, at dispatch (<see cref="ResolveDirectory"/>), and
/// recorded on <c>RunDispatched</c> exactly as <c>WorktreePath</c> is — never rederived from the
/// run id later. A run belongs to exactly one task, so a new run's directory lands under that
/// task's own directory when the project has a home
/// (<c>&lt;home&gt;/tasks/&lt;shortid&gt;-&lt;slug&gt;/runs/&lt;run-id&gt;/</c>, ruled 2026-08-23,
/// backlog 49) and falls back to the platform-global location otherwise. A stream written before
/// this existed carries no recorded directory, and replaying it falls back to
/// <see cref="GlobalDirectory"/> — the same place its files have always actually been — so an old
/// run's paths stay exactly as readable as they always were without this type pretending to know
/// where a home did not yet exist to put them.
/// </para>
/// </summary>
public static class RunPaths
{
    /// <summary>The platform home, shared with every other on-disk layout (<see cref="PlatformPaths"/>).</summary>
    public static string Root => PlatformPaths.Home;

    /// <summary>
    /// The location every run used before homes existed, and still the fallback for a project
    /// with none: ~/.hall9k/runs/&lt;run-id&gt;/.
    /// </summary>
    public static string GlobalDirectory(Guid runId) => Path.Combine(Root, "runs", runId.ToString());

    /// <summary>
    /// Where a NEW run's directory goes: under its owning task's directory when the project has
    /// a home, so the task directory is the whole story of the task — contract, workspace, every
    /// attempt — and there is no top-level runs/ in the home. Falls back to
    /// <see cref="GlobalDirectory"/> when the project has none. Resolved once, at dispatch, by
    /// the caller that already knows the task's current directory name; never called again for
    /// the same run.
    /// </summary>
    public static string ResolveDirectory(ProjectHome home, string taskDirectoryName, Guid runId) =>
        home.HasValue
            ? ResolveDirectoryUnderTaskDirectory(ProjectHomePaths.TaskDirectory(home.Value, taskDirectoryName), runId)
            : GlobalDirectory(runId);

    /// <summary>
    /// Where a run's directory goes underneath a task directory that the caller has already
    /// resolved on disk — the <c>runs/&lt;run-id&gt;/</c> segment named here rather than at each
    /// call site, so the layout stays defined in one place even when the caller found the task
    /// directory itself (a live-vs-archived search, for instance) rather than deriving it from
    /// <see cref="ProjectHomePaths.TaskDirectory"/>.
    /// </summary>
    public static string ResolveDirectoryUnderTaskDirectory(string taskDirectory, Guid runId) =>
        Path.Combine(taskDirectory, "runs", runId.ToString());

    /// <summary>
    /// Where a run's directory actually sits on disk right now, when that may no longer be
    /// <paramref name="recordedDirectory"/> — the value carried on <c>RunDispatched</c> and never
    /// updated afterward (adversarial review, backlog 51 cycle 1). A task's directory can move for
    /// two independent reasons, either of which can happen with or without the other: the render
    /// sweep flips it across the <c>tasks/_archive/</c> boundary as the task crosses the terminal
    /// boundary, and a slug-changing revise renames it within whichever root it currently sits
    /// under (the daemon's <c>HomeEntryWriter.Write</c> moves a stale-named directory the same way
    /// regardless of which of the two crossed a root boundary). Either move
    /// carries every one of the task's runs' <c>runs/&lt;run-id&gt;/</c> along with it, so this
    /// resolves by the recorded task directory's short-id prefix rather than assuming its full name
    /// is still current: a caller with the recorded path still readable, or a run that never had a
    /// project home, sees this return the unchanged input.
    /// <para>
    /// The task-directory segment is found by position from the END of the path, not by searching
    /// the string for the FIRST or LAST literal match (adversarial review, backlog 51 cycles 2 and
    /// 7): a project home is an arbitrary path a human names (<c>h9k project add --home</c>), so it
    /// can itself contain a <c>tasks</c> segment, or even a <c>tasks/_archive</c> one — a home at
    /// <c>/h/tasks/_archive/proj</c> made <c>LastIndexOf</c> match the HOME's own archive segment
    /// instead of the task's, silently stripping the wrong one and then giving up when the result
    /// did not exist on disk. The run's own directory always sits at a fixed number of segments
    /// from the end — <c>…/tasks/&lt;shortid&gt;-&lt;slug&gt;/runs/&lt;run-id&gt;</c> (live) or
    /// <c>…/tasks/_archive/&lt;shortid&gt;-&lt;slug&gt;/runs/&lt;run-id&gt;</c> (archived) — so
    /// splitting the path and checking the segment at that fixed offset can never be confused by
    /// anything the home's own path happens to contain.
    /// </para>
    /// <para>
    /// Once the home is known, the current task directory is found by searching both
    /// <c>tasks/</c> and <c>tasks/_archive/</c> for a directory whose name starts with the recorded
    /// directory's own short-id prefix (backlog 51 cycle 9) — never by recomputing the recorded
    /// name and checking whether that literal name exists on the other side, which only covers an
    /// archive flip that left the slug untouched. This call has no full task id to confirm a
    /// candidate against the way <see cref="HomeEntryLookup.FindExisting"/> does when it has one, so
    /// a match is trusted only when it is the sole directory under a root carrying that prefix;
    /// zero or more than one leaves this exactly as unable to resolve as a literal-name check would
    /// have been, and returns the recorded path unchanged rather than guessing.
    /// </para>
    /// <para>
    /// The search lands on the TASK directory, never the run's own leaf directory (adversarial
    /// review, backlog 51 cycle 8): a run recorded moments before the render sweep relocates its
    /// task directory out from under it has no leaf at either the recorded path or its current one
    /// yet — <c>ClaudeExecutor</c> creates that leaf itself, after this call returns — so checking
    /// the leaf can never tell which side is current for a run that has not been dispatched yet.
    /// The task directory, though, is what <c>HomeEntryWriter.Write</c> actually moves whole, so its
    /// presence at one side or the other is the true signal — and checking there is strictly better
    /// even for an already-populated run, since a moved task directory carries its run leaves along
    /// with it.
    /// </para>
    /// </summary>
    public static string ResolveCurrentDirectory(string recordedDirectory)
    {
        if (Directory.Exists(recordedDirectory))
        {
            return recordedDirectory;
        }

        string[] segments = recordedDirectory.Split(Path.DirectorySeparatorChar);

        string home;

        // archived: […, "tasks", "_archive", taskDirectory, "runs", runId] — five trailing segments.
        if (segments.Length >= 5 && segments[^4] == ProjectHomePaths.ArchiveDirectoryName)
        {
            home = string.Join(Path.DirectorySeparatorChar, segments[..^5]);
        }
        // live: […, "tasks", taskDirectory, "runs", runId] — four trailing segments.
        else if (segments.Length >= 4 && segments[^4] == "tasks")
        {
            home = string.Join(Path.DirectorySeparatorChar, segments[..^4]);
        }
        else
        {
            return recordedDirectory;
        }

        string recordedTaskDirectoryName = segments[^3];
        string? currentTaskDirectory =
            FindTaskDirectoryByShortIdPrefix(ProjectHomePaths.TasksDirectory(home), recordedTaskDirectoryName)
            ?? FindTaskDirectoryByShortIdPrefix(ProjectHomePaths.ArchivedTasksDirectory(home), recordedTaskDirectoryName);

        return currentTaskDirectory is null
            ? recordedDirectory
            : Path.Combine(currentTaskDirectory, segments[^2], segments[^1]);
    }

    /// <summary>
    /// The one directory directly under <paramref name="root"/> whose name starts with
    /// <paramref name="recordedTaskDirectoryName"/>'s own short-id prefix (backlog 51 cycle 9) — the
    /// part of <c>&lt;shortid&gt;-&lt;slug&gt;</c> before the slug, which a rename changes and a
    /// short id never does. Zero or several candidates both come back null: with no full task id to
    /// confirm a match against, a short-id collision between two directories under one root is
    /// indistinguishable from a genuine miss, and guessing between them would be exactly the thing
    /// <see cref="ResolveCurrentDirectory"/> exists to avoid doing with a stale path.
    /// </summary>
    private static string? FindTaskDirectoryByShortIdPrefix(string root, string recordedTaskDirectoryName)
    {
        if (recordedTaskDirectoryName.Length < 9 || !Directory.Exists(root))
        {
            return null;
        }

        string prefix = recordedTaskDirectoryName[..8] + "-";
        string[] matches = [.. Directory.EnumerateDirectories(root)
            .Where(directory => Path.GetFileName(directory).StartsWith(prefix, StringComparison.Ordinal))];
        return matches.Length == 1 ? matches[0] : null;
    }

    public static string StreamFile(string runDirectory) => Path.Combine(runDirectory, "stream.jsonl");

    public static string PromptFile(string runDirectory) => Path.Combine(runDirectory, "prompt.md");

    public static string SettingsFile(string runDirectory) => Path.Combine(runDirectory, "settings.json");

    public static string StandardErrorFile(string runDirectory) => Path.Combine(runDirectory, "stderr.log");

    // Pre-PR review and fix sessions (log #24) share the run's directory; each session's
    // files are prefixed with a per-session name so cycles never collide.
    public static string SessionStreamFile(string runDirectory, string sessionName) =>
        Path.Combine(runDirectory, $"{sessionName}.stream.jsonl");

    public static string SessionPromptFile(string runDirectory, string sessionName) =>
        Path.Combine(runDirectory, $"{sessionName}.prompt.md");

    /// <summary>
    /// One session's own settings file. The run-level <see cref="SettingsFile"/> was written
    /// afresh by every spawn, which was safe only while a run's sessions were strictly
    /// sequential; a review cycle now spawns its lenses together (log #59), and the second
    /// spawn's truncate-and-rewrite would land inside the first child's config-loading
    /// window. A session that owns its file has no writer but itself.
    /// </summary>
    public static string SessionSettingsFile(string runDirectory, string sessionName) =>
        Path.Combine(runDirectory, $"{sessionName}.settings.json");

    public static string SessionStandardErrorFile(string runDirectory, string sessionName) =>
        Path.Combine(runDirectory, $"{sessionName}.stderr.log");

    /// <summary>
    /// One review cycle's merged findings: every lens's verified findings under its own
    /// heading (log #59), written by the daemon when the cycle's last pass lands. This is the
    /// document the fix session is handed and the one a park points a human at, so the name
    /// is unchanged from the single-lens loop it replaces.
    /// </summary>
    public static string ReviewFindingsFile(string runDirectory, int cycle) =>
        Path.Combine(runDirectory, $"review-{cycle}-findings.md");

    /// <summary>
    /// One lens's own findings for a cycle, exactly as that pass wrote them (log #59) — the
    /// unmerged record behind each section of <see cref="ReviewFindingsFile"/>.
    /// </summary>
    public static string ReviewLensFindingsFile(string runDirectory, int cycle, string lensSlug) =>
        Path.Combine(runDirectory, $"review-{cycle}-{lensSlug}-findings.md");

    /// <summary>The fix session's closing summary — on a dispute, the second position the human reads.</summary>
    public static string ReviewFixPositionFile(string runDirectory, int cycle) =>
        Path.Combine(runDirectory, $"review-{cycle}-fix-position.md");

    /// <summary>
    /// A follow-up's closing position when it disputed a review thread rather than settling it
    /// (Decisions Log #62) — the text a park points the human at, holding both the reviewer's
    /// position and the agent's. One well-known path per run — a resumed dispute (backlog 44)
    /// can park here again, so <see cref="AppendDisputePositionAsync"/> is what writes it: each
    /// attempt's position is appended, never overwritten, so a human deciding between two
    /// disputed rounds can still read the earlier one.
    /// </summary>
    public static string ReviewThreadDisputeFile(string runDirectory) =>
        Path.Combine(runDirectory, "review-thread-dispute.md");

    /// <summary>
    /// A rebase follow-up's closing position when it disputed a merge conflict rather than
    /// resolving it (backlog 44) — the conflicting files and both positions, which is what a
    /// park points the human at. The rebase counterpart of <see cref="ReviewThreadDisputeFile"/>,
    /// appended the same way across repeated disputes on the same run.
    /// </summary>
    public static string RebaseConflictDisputeFile(string runDirectory) =>
        Path.Combine(runDirectory, "rebase-conflict-dispute.md");

    /// <summary>
    /// Appends one dispute's closing position to the well-known path
    /// (<see cref="RebaseConflictDisputeFile"/> or <see cref="ReviewThreadDisputeFile"/>) rather
    /// than overwriting it. A resumed pre-gate dispute can dispute again on that same path
    /// (backlog 44), and the human resolving is pointed at it to decide between the positions —
    /// a plain overwrite would erase the first the moment the second landed, leaving only the
    /// newest attempt to read. Best-effort, like every other run artifact write: losing it must
    /// not turn a park into a failure, and the caller's own park reason names the path either way.
    /// Returns the caught exception on failure (null on success) so the caller can log why —
    /// a permissions problem, a full disk, and a sharing violation all look identical from the
    /// outside otherwise.
    /// </summary>
    public static async Task<Exception?> AppendDisputePositionAsync(
        string filePath, string? summary, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? filePath);
            await File.AppendAllTextAsync(
                filePath, $"## Dispute position, {DateTimeOffset.UtcNow:u}\n\n{summary}\n\n", cancellationToken);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return exception;
        }
    }

    /// <summary>
    /// The run's handoff to whatever depends on it (Decisions Log #36), written at session end
    /// beside the review findings so it is inspectable outside the ledger. The file's three
    /// states are three observations, which is what lets the closeout append record an honest
    /// HandoffOutcome without guessing: present and non-blank means the agent authored a
    /// handoff, present and empty means the session's result was read and carried none, and
    /// absent means there was no session-end capture at all.
    /// </summary>
    public static string HandoffFile(string runDirectory) => Path.Combine(runDirectory, "handoff.md");

    /// <summary>The condensed blocker context a synthesis session produced for this run (log #36).</summary>
    public static string BlockerContextFile(string runDirectory) => Path.Combine(runDirectory, "blocker-context.md");
}
