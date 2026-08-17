using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Hall9k.Daemon.Worktrees;

public sealed class GitWorktreeManager(ILogger<GitWorktreeManager> logger) : IWorktreeManager
{
    // Parallel worktree add/fetch against one repo hits git's internal locks; a per-repo
    // mutex is cleaner than retry loops (Decisions Log #4).
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _repositoryLocks = new();

    public async Task<Worktree> CreateAsync(WorktreeRequest request, CancellationToken cancellationToken)
    {
        string repositoryPath = Path.GetFullPath(request.RepositoryPath);
        SemaphoreSlim mutex = LockFor(repositoryPath);
        await mutex.WaitAsync(cancellationToken);
        try
        {
            await BestEffortFetchAsync(repositoryPath, cancellationToken);

            string startPoint = await ResolveStartPointAsync(repositoryPath, request.BaseBranch, cancellationToken);
            string branch = await ResolveBranchNameAsync(repositoryPath, request, cancellationToken);
            string worktreePath = WorktreePathFor(repositoryPath, request);

            await RunGitAsync(
                repositoryPath,
                $"worktree add --no-track -b {branch} \"{worktreePath}\" {startPoint}",
                cancellationToken);

            logger.LogInformation(
                "Worktree {Path} created on branch {Branch} from {StartPoint}",
                worktreePath, branch, startPoint);
            return new Worktree(worktreePath, branch, startPoint);
        }
        finally
        {
            mutex.Release();
        }
    }

    public async Task RemoveAsync(string repositoryPath, string worktreePath, CancellationToken cancellationToken)
    {
        repositoryPath = Path.GetFullPath(repositoryPath);
        SemaphoreSlim mutex = LockFor(repositoryPath);
        await mutex.WaitAsync(cancellationToken);
        try
        {
            await RunGitAsync(repositoryPath, $"worktree remove --force \"{worktreePath}\"", cancellationToken);
            logger.LogInformation("Worktree {Path} removed", worktreePath);
        }
        finally
        {
            mutex.Release();
        }
    }

    public async Task PruneAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        repositoryPath = Path.GetFullPath(repositoryPath);
        SemaphoreSlim mutex = LockFor(repositoryPath);
        await mutex.WaitAsync(cancellationToken);
        try
        {
            await RunGitAsync(repositoryPath, "worktree prune", cancellationToken);
        }
        finally
        {
            mutex.Release();
        }
    }

    private SemaphoreSlim LockFor(string repositoryPath) =>
        _repositoryLocks.GetOrAdd(repositoryPath, _ => new SemaphoreSlim(1, 1));

    private async Task BestEffortFetchAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        (int exitCode, _, _) = await TryRunGitAsync(repositoryPath, "remote get-url origin", cancellationToken);
        if (exitCode != 0)
        {
            return;
        }

        (int fetchExit, _, string fetchError) = await TryRunGitAsync(repositoryPath, "fetch origin", cancellationToken);
        if (fetchExit != 0)
        {
            logger.LogWarning("git fetch failed for {Repository} ({Error}); using local refs", repositoryPath, fetchError.Trim());
        }
    }

    private async Task<string> ResolveStartPointAsync(string repositoryPath, string baseBranch, CancellationToken cancellationToken)
    {
        // Prefer the remote-tracking ref (log #4); fall back to the local branch for
        // repos with no origin (tests, purely local projects).
        foreach (string candidate in new[] { $"origin/{baseBranch}", baseBranch })
        {
            (int exitCode, _, _) = await TryRunGitAsync(
                repositoryPath, $"rev-parse --verify --quiet \"{candidate}^{{commit}}\"", cancellationToken);
            if (exitCode == 0)
            {
                return candidate;
            }
        }

        throw new WorktreeException(
            $"Neither origin/{baseBranch} nor {baseBranch} resolves to a commit in {repositoryPath}.");
    }

    private async Task<string> ResolveBranchNameAsync(string repositoryPath, WorktreeRequest request, CancellationToken cancellationToken)
    {
        string branch = $"task/{Short(request.TaskId)}-{Slug(request.Objective)}";

        // Branch per task — but a retried task's failed worktree is retained (cleanup
        // policy, log #4) and still holds the branch, so retries get a run-suffixed name.
        (int exitCode, _, _) = await TryRunGitAsync(
            repositoryPath, $"rev-parse --verify --quiet \"refs/heads/{branch}\"", cancellationToken);
        return exitCode == 0 ? $"{branch}-r{Short(request.RunId)[..4]}" : branch;
    }

    private static string WorktreePathFor(string repositoryPath, WorktreeRequest request)
    {
        string parent = Path.GetDirectoryName(repositoryPath.TrimEnd(Path.DirectorySeparatorChar))
            ?? throw new WorktreeException($"Repository path {repositoryPath} has no parent directory.");
        return Path.Combine(parent, $"wt-{Short(request.TaskId)}-{Short(request.RunId)}");
    }

    // UUIDv7 front-loads the timestamp — same-instant ids share their FIRST chars, so a
    // short id must come from the random tail, never the head.
    private static string Short(Guid id) => id.ToString("N")[^8..];

    private static string Slug(string objective)
    {
        StringBuilder slug = new();
        foreach (char c in objective.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                slug.Append(c);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }

            if (slug.Length >= 30)
            {
                break;
            }
        }

        string result = slug.ToString().Trim('-');
        return result.IsBlank() ? "task" : result;
    }

    private async Task RunGitAsync(string repositoryPath, string arguments, CancellationToken cancellationToken)
    {
        (int exitCode, _, string standardError) = await TryRunGitAsync(repositoryPath, arguments, cancellationToken);
        if (exitCode != 0)
        {
            throw new WorktreeException($"git {arguments} failed in {repositoryPath}: {standardError.Trim()}");
        }
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> TryRunGitAsync(
        string repositoryPath, string arguments, CancellationToken cancellationToken)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"-C \"{repositoryPath}\" {arguments}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.Start();

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, await standardOutput, await standardError);
    }
}
