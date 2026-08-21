using Hall9k.Domain.Infrastructure.Storage;
using System.Diagnostics;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Spawns claude -p with the settled flag policy (Decisions Log #1): plain -p under the
/// subscription (full config inherited — agents act as the owner, with the owner's tools);
/// --bare only in api-key mode. Stdout redirects to the run's stream file via the shell,
/// so the CHILD owns the file handle and a daemon restart never breaks capture (log #2).
/// Prompt and settings travel as files — no shell-escaping of user content.
/// The model is always passed explicitly on a fresh session (log #33): the one thing the
/// platform deliberately does NOT inherit from the owner's config, because a personal
/// default changed on a Tuesday is not a platform decision.
/// </summary>
public sealed class ClaudeExecutor(ILogger<ClaudeExecutor> logger) : IExecutor
{
    public async Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
    {
        // The chain always ends at an explicit platform default, so an unusable model here
        // means a caller skipped it. Refuse rather than spawn and inherit silently; that
        // silence is the origin incident (log #33). The value also lands in a /bin/sh
        // command, so a malformed one is never quoted-and-hoped-for.
        if (!request.Model.IsWellFormed && request.ResumeSessionId is null)
        {
            throw new InvalidOperationException(
                $"Run {request.RunId} reached the executor without a usable model "
                + $"('{request.Model.Value}'). Every spawn states its model explicitly "
                + "(Decisions Log #33); the platform never inherits the owner's personal default.");
        }

        Directory.CreateDirectory(RunPaths.RunDirectory(request.RunId));
        (string promptFile, string streamFile, string standardErrorFile) = request.SessionArtifactName is { } session
            ? (RunPaths.SessionPromptFile(request.RunId, session),
                RunPaths.SessionStreamFile(request.RunId, session),
                RunPaths.SessionStandardErrorFile(request.RunId, session))
            : (RunPaths.PromptFile(request.RunId),
                RunPaths.StreamFile(request.RunId),
                RunPaths.StandardErrorFile(request.RunId));

        await File.WriteAllTextAsync(promptFile, request.Prompt, cancellationToken);
        await File.WriteAllTextAsync(SettingsFile(request), SettingsContent, cancellationToken);

        string command =
            $"exec {ClaudeBinary()} {string.Join(' ', Arguments(request))} " +
            $"< \"{promptFile}\" " +
            $"> \"{streamFile}\" " +
            $"2> \"{standardErrorFile}\"";

        Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            WorkingDirectory = request.WorktreePath,
            UseShellExecute = false,
        };
        // The child inherits the owner's environment (log #1) with the caller's additions on
        // top — the caller states what this particular session needs, and nothing else changes.
        foreach ((string name, string value) in request.Environment)
        {
            process.StartInfo.Environment[name] = value;
        }
        // ArgumentList passes the command verbatim — .NET's Arguments string parser does
        // not understand single quotes, and shell syntax must reach sh untouched.
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(command);

        using (process)
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start agent for run {request.RunId}.");
            }

            DateTimeOffset startedAt = new(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            logger.LogInformation(
                "Agent spawned for run {RunId}: pid {ProcessId}, session {SessionId}, mode {Mode}, model {Model}",
                request.RunId, process.Id, request.SessionId, request.Mode.Value, request.Model.Value);
            return new SpawnedAgent(process.Id, startedAt);
        }
    }

    private static string ClaudeBinary() =>
        Environment.GetEnvironmentVariable("HALL9K_CLAUDE_PATH") ?? "claude";

    /// <summary>The one platform-imposed setting: agents never author co-authored-by trailers (PLAN.md §6.6).</summary>
    private const string SettingsContent = """{"includeCoAuthoredBy": false}""";

    /// <summary>
    /// The settings file this spawn writes and hands its child. It follows the session's own
    /// artifact name wherever there is one, because one file per run has a writer per spawn:
    /// a review cycle dispatches its lenses back to back (log #59), and the second spawn's
    /// truncate-and-rewrite would otherwise land inside the first child's config-loading
    /// window, handing it an empty file and losing the trailer suppression that is the whole
    /// point of the file. A session that owns its settings has no writer but itself.
    /// </summary>
    private static string SettingsFile(AgentSpawnRequest request) =>
        request.SessionArtifactName is { } session
            ? RunPaths.SessionSettingsFile(request.RunId, session)
            : RunPaths.SettingsFile(request.RunId);

    /// <summary>
    /// Internal for the argument-policy tests: the flag set IS the policy (logs #1, #5, #33),
    /// and it is worth asserting without spawning a process.
    /// </summary>
    internal static IEnumerable<string> Arguments(AgentSpawnRequest request)
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

        yield return $"--settings \"{SettingsFile(request)}\"";

        if (request.Mode.UsesBareFlag)
        {
            yield return "--bare";
        }

        if (request.SkipPermissions)
        {
            yield return "--dangerously-skip-permissions";
        }
    }
}
