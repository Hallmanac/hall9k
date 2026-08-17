using Hall9k.Domain.Infrastructure.Storage;
using System.Diagnostics;
using Hall9k.Domain.Features.Run;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Spawns claude -p with the settled flag policy (Decisions Log #1): plain -p under the
/// subscription (full config inherited — agents act as the owner, with the owner's tools);
/// --bare only in api-key mode. Stdout redirects to the run's stream file via the shell,
/// so the CHILD owns the file handle and a daemon restart never breaks capture (log #2).
/// Prompt and settings travel as files — no shell-escaping of user content.
/// </summary>
public sealed class ClaudeExecutor(ILogger<ClaudeExecutor> logger) : IExecutor
{
    public async Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(RunPaths.RunDirectory(request.RunId));
        (string promptFile, string streamFile, string standardErrorFile) = request.SessionArtifactName is { } session
            ? (RunPaths.SessionPromptFile(request.RunId, session),
                RunPaths.SessionStreamFile(request.RunId, session),
                RunPaths.SessionStandardErrorFile(request.RunId, session))
            : (RunPaths.PromptFile(request.RunId),
                RunPaths.StreamFile(request.RunId),
                RunPaths.StandardErrorFile(request.RunId));

        await File.WriteAllTextAsync(promptFile, request.Prompt, cancellationToken);
        await File.WriteAllTextAsync(
            RunPaths.SettingsFile(request.RunId),
            """{"includeCoAuthoredBy": false}""",
            cancellationToken);

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
                "Agent spawned for run {RunId}: pid {ProcessId}, session {SessionId}, mode {Mode}",
                request.RunId, process.Id, request.SessionId, request.Mode.Value);
            return new SpawnedAgent(process.Id, startedAt);
        }
    }

    private static string ClaudeBinary() =>
        Environment.GetEnvironmentVariable("HALL9K_CLAUDE_PATH") ?? "claude";

    private static IEnumerable<string> Arguments(AgentSpawnRequest request)
    {
        yield return "-p";
        yield return "--output-format stream-json";
        yield return "--verbose";
        yield return $"--session-id {request.SessionId}";
        yield return $"--settings \"{RunPaths.SettingsFile(request.RunId)}\"";

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
