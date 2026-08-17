using Hall9k.Domain.Features.Run;

namespace Hall9k.Daemon.Execution;

public sealed record AgentSpawnRequest(
    Guid RunId,
    Guid SessionId,
    string WorktreePath,
    string Prompt,
    ExecutorMode Mode,
    bool SkipPermissions);

public sealed record SpawnedAgent(int ProcessId, DateTimeOffset StartedAt);

/// <summary>
/// The executor seam (PLAN.md §6.3): spawn an agent working a prepared worktree, output
/// captured to the run's stream file. v0 implements Claude Code headless only.
/// </summary>
public interface IExecutor
{
    Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken);
}
