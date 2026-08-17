namespace Hall9k.Daemon.Worktrees;

public sealed record WorktreeRequest(
    string RepositoryPath,
    string BaseBranch,
    Guid TaskId,
    Guid RunId,
    string Objective);

public sealed record Worktree(string Path, string Branch, string StartPoint);

public sealed class WorktreeException(string message) : Exception(message);

/// <summary>
/// One worktree per run, siblings of the repository (Decisions Log #4). The executor
/// consumes this seam; nothing here spawns agents.
/// </summary>
public interface IWorktreeManager
{
    Task<Worktree> CreateAsync(WorktreeRequest request, CancellationToken cancellationToken);

    /// <summary>Removes a worktree (force — done worktrees may hold build debris). The branch survives.</summary>
    Task RemoveAsync(string repositoryPath, string worktreePath, CancellationToken cancellationToken);

    /// <summary>Startup sweep: collect worktree records orphaned by crashes.</summary>
    Task PruneAsync(string repositoryPath, CancellationToken cancellationToken);
}
