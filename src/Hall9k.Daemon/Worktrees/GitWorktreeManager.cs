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

    public async Task<Worktree> CheckoutExistingAsync(FollowUpWorktreeRequest request, CancellationToken cancellationToken)
    {
        string repositoryPath = Path.GetFullPath(request.RepositoryPath);
        SemaphoreSlim mutex = LockFor(repositoryPath);
        await mutex.WaitAsync(cancellationToken);
        try
        {
            await BestEffortFetchAsync(repositoryPath, cancellationToken);

            string branch = request.Branch;
            string worktreePath = WorktreePathFor(repositoryPath, request.TaskId, request.RunId);
            bool localExists = await RefExistsAsync(repositoryPath, $"refs/heads/{branch}", cancellationToken);
            bool remoteExists = await RefExistsAsync(repositoryPath, $"refs/remotes/origin/{branch}", cancellationToken);

            if (localExists)
            {
                await RunGitAsync(repositoryPath, $"worktree add \"{worktreePath}\" {branch}", cancellationToken);
                if (remoteExists)
                {
                    // Review feedback may have landed as commits on the PR itself (web-applied
                    // suggestions); pick them up when it's a clean fast-forward. A divergence
                    // is a human problem — the run proceeds from the local tip.
                    (int ffExit, _, string ffError) = await TryRunGitAsync(
                        worktreePath, $"merge --ff-only origin/{branch}", cancellationToken);
                    if (ffExit != 0)
                    {
                        logger.LogWarning(
                            "Branch {Branch} could not fast-forward to origin ({Error}); continuing from the local tip",
                            branch, ffError.Trim());
                    }
                }
            }
            else if (remoteExists)
            {
                await RunGitAsync(
                    repositoryPath,
                    $"worktree add --no-track -b {branch} \"{worktreePath}\" origin/{branch}",
                    cancellationToken);
            }
            else
            {
                throw new WorktreeException(
                    $"Branch {branch} exists neither locally nor on origin in {repositoryPath} — cannot resume it.");
            }

            logger.LogInformation(
                "Worktree {Path} checked out on existing branch {Branch}", worktreePath, branch);
            return new Worktree(worktreePath, branch, localExists ? branch : $"origin/{branch}");
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
        return await RefExistsAsync(repositoryPath, $"refs/heads/{branch}", cancellationToken)
            ? $"{branch}-r{Short(request.RunId)[..4]}"
            : branch;
    }

    private static async Task<bool> RefExistsAsync(string repositoryPath, string reference, CancellationToken cancellationToken)
    {
        (int exitCode, _, _) = await TryRunGitAsync(
            repositoryPath, $"rev-parse --verify --quiet \"{reference}^{{commit}}\"", cancellationToken);
        return exitCode == 0;
    }

    private static string WorktreePathFor(string repositoryPath, WorktreeRequest request) =>
        WorktreePathFor(repositoryPath, request.TaskId, request.RunId);

    private static string WorktreePathFor(string repositoryPath, Guid taskId, Guid runId)
    {
        string parent = Path.GetDirectoryName(repositoryPath.TrimEnd(Path.DirectorySeparatorChar))
            ?? throw new WorktreeException($"Repository path {repositoryPath} has no parent directory.");
        return Path.Combine(parent, $"wt-{Short(taskId)}-{Short(runId)}");
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
