using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Spawns claude -p with the settled flag policy (Decisions Log #1): plain -p under the
/// subscription (full config inherited — agents act as the owner, with the owner's tools);
/// --bare only in api-key mode. Stdout redirects to the run's stream file through
/// <see cref="IProcessManager"/>'s platform shell, so the CHILD owns the file handle and a
/// daemon restart never breaks capture (log #2). Prompt and settings travel as files — no
/// shell-escaping of user content.
/// The model is always passed explicitly on a fresh session (log #33): the one thing the
/// platform deliberately does NOT inherit from the owner's config, because a personal
/// default changed on a Tuesday is not a platform decision.
/// </summary>
public sealed class ClaudeExecutor(ILogger<ClaudeExecutor> logger, IProcessManager processManager) : IExecutor
{
    public async Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
    {
        // The chain always ends at an explicit platform default, so an unusable model here
        // means a caller skipped it. Refuse rather than spawn and inherit silently; that
        // silence is the origin incident (log #33). The value also lands in the platform
        // shell's command line, so a malformed one is never quoted-and-hoped-for.
        if (!request.Model.IsWellFormed && request.ResumeSessionId is null)
        {
            throw new InvalidOperationException(
                $"Run {request.RunId} reached the executor without a usable model "
                + $"('{request.Model.Value}'). Every spawn states its model explicitly "
                + "(Decisions Log #33); the platform never inherits the owner's personal default.");
        }

        // request.RunDirectory is whatever RunDispatched recorded at this run's ORIGINAL
        // dispatch — stale for a resumed or redispatched session whose task has since crossed
        // the tasks/_archive/ boundary (adversarial review, backlog 51 cycle 5): a fresh
        // dispatch's caller always passes the directory it just resolved on disk, so this is a
        // no-op there, but a resumed review/fix pass or a budget retry reads the recorded value
        // straight from the run's projection, which never updates after dispatch.
        string runDirectory = RunPaths.ResolveCurrentDirectory(request.RunDirectory);
        Directory.CreateDirectory(runDirectory);
        (string promptFile, string streamFile, string standardErrorFile) = request.SessionArtifactName is { } session
            ? (RunPaths.SessionPromptFile(runDirectory, session),
                RunPaths.SessionStreamFile(runDirectory, session),
                RunPaths.SessionStandardErrorFile(runDirectory, session))
            : (RunPaths.PromptFile(runDirectory),
                RunPaths.StreamFile(runDirectory),
                RunPaths.StandardErrorFile(runDirectory));

        await File.WriteAllTextAsync(promptFile, request.Prompt, cancellationToken);
        await File.WriteAllTextAsync(SettingsFile(request, runDirectory), SettingsContent, cancellationToken);

        string command = $"\"{ClaudeBinary()}\" {string.Join(' ', Arguments(request, runDirectory))}";

        // The child inherits the owner's environment (log #1) with the caller's additions on
        // top — the caller states what this particular session needs, and nothing else changes.
        SpawnedProcess spawned = processManager.Spawn(new ProcessSpawnRequest(
            command, request.WorktreePath, [.. request.Environment], promptFile, streamFile, standardErrorFile));

        logger.LogInformation(
            "Agent spawned for run {RunId}: pid {ProcessId}, session {SessionId}, mode {Mode}, model {Model}",
            request.RunId, spawned.ProcessId, request.SessionId, request.Mode.Value, request.Model.Value);
        return new SpawnedAgent(spawned.ProcessId, spawned.StartedAt);
    }

    private static string ClaudeBinary() =>
        Environment.GetEnvironmentVariable("HALL9K_CLAUDE_PATH") ?? "claude";

    /// <summary>The one platform-imposed setting: agents never author co-authored-by trailers (PLAN.md §6.6).</summary>
    private const string SettingsContent = """{"includeCoAuthoredBy": false}""";

    /// <summary>
    /// The settings file this spawn writes and hands its child, under <paramref name="runDirectory"/>
    /// — the value the caller already resolved once via <see cref="RunPaths.ResolveCurrentDirectory"/>,
    /// never re-resolved here, so a render sweep that relocates the task directory mid-spawn cannot
    /// leave the write, the <c>--settings</c> argument and each other pointed at different sides of
    /// the move (adversarial review, backlog 51 cycle 2). It follows the session's own artifact name
    /// wherever there is one, because one file per run has a writer per spawn: a review cycle
    /// dispatches its lenses back to back (log #59), and the second spawn's truncate-and-rewrite
    /// would otherwise land inside the first child's config-loading window, handing it an empty file
    /// and losing the trailer suppression that is the whole point of the file. A session that owns
    /// its settings has no writer but itself.
    /// </summary>
    private static string SettingsFile(AgentSpawnRequest request, string runDirectory) =>
        request.SessionArtifactName is { } session
            ? RunPaths.SessionSettingsFile(runDirectory, session)
            : RunPaths.SettingsFile(runDirectory);

    /// <summary>
    /// Internal for the argument-policy tests: the flag set IS the policy (logs #1, #5, #33),
    /// and it is worth asserting without spawning a process. Resolves its own run directory when
    /// called this way, since a test constructs a request without ever having resolved one.
    /// </summary>
    internal static IEnumerable<string> Arguments(AgentSpawnRequest request) =>
        Arguments(request, RunPaths.ResolveCurrentDirectory(request.RunDirectory));

    private static IEnumerable<string> Arguments(AgentSpawnRequest request, string runDirectory)
    {
        yield return "-p";
        yield return "--output-format stream-json";
        yield return "--verbose";

        // A resume re-enters the recorded session (log #5); --session-id is for fresh
        // sessions only and would conflict with it.
        if (request.ResumeSessionId is { } resumeSessionId)
        {
            // A resumed session keeps the model it started with; the request carries that
            // model so the milestone can record it, not so it can be re-applied (log #33).
            yield return $"--resume {resumeSessionId}";
        }
        else
        {
            yield return $"--session-id {request.SessionId}";
            yield return $"--model \"{request.Model.Value}\"";
        }

        yield return $"--settings \"{SettingsFile(request, runDirectory)}\"";

        if (request.Mode.UsesBareFlag)
        {
            yield return "--bare";
        }

        if (request.SkipPermissions)
        {
            yield return "--dangerously-skip-permissions";
        }

        // request.WorktreePath is another contributor's pull-request head for a pr-review
        // spawn (AgentSpawnRequest.UntrustedWorkingDirectory) — the first checkout this
        // platform ever hands an agent that it did not cut itself, so its own
        // .claude/settings.json (hooks included), .mcp.json and CLAUDE.md/AGENTS.md cannot
        // be trusted the way this platform's own worktrees are. --setting-sources user drops
        // the checkout's project- and local-scoped settings.json AND its CLAUDE.md/AGENTS.md
        // from the merge (verified empirically: a checkout's CLAUDE.md is not read into
        // context under this flag), leaving only the owner's own ~/.claude/settings.json
        // (still loaded — it is the owner's, not the pull request's); --strict-mcp-config,
        // given with no --mcp-config of its own, connects to no MCP server at all rather than
        // whatever the checkout's .mcp.json names. AgentPromptBuilder's own pr-review framing
        // (AppendSettledRulings' override) is defense in depth on top of this, not the only
        // thing standing between the session and the pull request author's doctrine files.
        if (request.UntrustedWorkingDirectory)
        {
            yield return "--setting-sources user";
            yield return "--strict-mcp-config";
        }
    }
}
