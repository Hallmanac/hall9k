using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Hall9k.Connectors.Worktrees;

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

            // Worktrees are retained through closeout (Decisions Log #21), so the branch
            // is usually still checked out in the previous run's worktree — reuse it (it
            // IS the follow-up workspace; git also refuses to check the branch out twice).
            if (await FindWorktreeHoldingBranchAsync(repositoryPath, branch, cancellationToken) is { } retained)
            {
                if (Directory.Exists(retained))
                {
                    await SyncToOriginBestEffortAsync(repositoryPath, retained, branch, cancellationToken);
                    logger.LogInformation(
                        "Reusing retained worktree {Path} for follow-up on branch {Branch}", retained, branch);
                    return new Worktree(retained, branch, branch);
                }

                // Registered but purged from disk — collect the stale record and recreate.
                await RunGitAsync(repositoryPath, "worktree prune", cancellationToken);
            }

            string worktreePath = WorktreePathFor(repositoryPath, request.TaskId, request.RunId);
            bool localExists = await RefExistsAsync(repositoryPath, $"refs/heads/{branch}", cancellationToken);
            bool remoteExists = await RefExistsAsync(repositoryPath, $"refs/remotes/origin/{branch}", cancellationToken);

            if (localExists)
            {
                await RunGitAsync(repositoryPath, $"worktree add \"{worktreePath}\" {branch}", cancellationToken);
                await SyncToOriginBestEffortAsync(repositoryPath, worktreePath, branch, cancellationToken);
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

    public async Task<Worktree> CreatePrReviewCheckoutAsync(
        PrReviewWorktreeRequest request, CancellationToken cancellationToken)
    {
        string repositoryPath = Path.GetFullPath(request.RepositoryPath);
        SemaphoreSlim mutex = LockFor(repositoryPath);
        await mutex.WaitAsync(cancellationToken);
        try
        {
            // A plain `git fetch origin <refspec>` on the command line replaces the
            // configured `+refs/heads/*:refs/remotes/origin/*` for that invocation, so the
            // base branch's remote-tracking ref never advances on its own — refresh it first,
            // the same way CreateAsync and CheckoutExistingAsync do, so the diff every
            // downstream consumer runs against `origin/<base>` is not stale or missing.
            await BestEffortFetchAsync(repositoryPath, cancellationToken);

            string trackingRef = PrReviewTrackingRef(request.PullRequestNumber);
            await RunGitAsync(
                repositoryPath,
                $"fetch origin \"+refs/pull/{request.PullRequestNumber}/head:{trackingRef}\"",
                cancellationToken);

            string worktreePath = WorktreePathFor(repositoryPath, request.TaskId, request.RunId);
            await RunGitAsync(repositoryPath, $"worktree add --detach \"{worktreePath}\" {trackingRef}", cancellationToken);

            logger.LogInformation(
                "Read-only worktree {Path} checked out detached at pull request #{Number}'s head",
                worktreePath, request.PullRequestNumber);
            return new Worktree(worktreePath, $"pr/{request.PullRequestNumber}", trackingRef);
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

    public async Task DeletePrReviewTrackingRefAsync(
        string repositoryPath, int pullRequestNumber, CancellationToken cancellationToken)
    {
        repositoryPath = Path.GetFullPath(repositoryPath);
        SemaphoreSlim mutex = LockFor(repositoryPath);
        await mutex.WaitAsync(cancellationToken);
        try
        {
            // update-ref -d, not fetch --prune: nothing on origin ever named this ref (it
            // was fetched from refs/pull/<n>/head, a synthetic ref GitHub serves, into a
            // name of this platform's own choosing), so there is no remote-side deletion
            // for a prune to observe — only the local record this fetch created.
            await RunGitAsync(
                repositoryPath, $"update-ref -d {PrReviewTrackingRef(pullRequestNumber)}", cancellationToken);
            logger.LogInformation(
                "Pr-review tracking ref for pull request #{Number} deleted", pullRequestNumber);
        }
        finally
        {
            mutex.Release();
        }
    }

    private static string PrReviewTrackingRef(int pullRequestNumber) => $"refs/remotes/origin/pr-review/{pullRequestNumber}";

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

    public async Task<CheckoutRefresh> RefreshReadingCheckoutAsync(
        string checkoutPath, string branch, CancellationToken cancellationToken)
    {
        // Fetch into, and lock, the repository this checkout actually resolves refs through, which
        // is not reliably the project's recorded repository path: repo/dev is a worktree of the
        // home's bare clone, and both --keep-repo-path and h9k project set --repo leave a project
        // pointing at some other clone. Fetching there would update refs nothing here reads, and
        // the behind-count below — taken in the checkout, against the refs its own repository
        // holds — would then be computed from refs nobody moved and logged as up to date. Asking
        // git is also the only answer that stays true for a checkout cut some other way.
        if (await ResolveRepositoryAsync(checkoutPath, cancellationToken) is not { } repositoryPath)
        {
            return new CheckoutRefresh(
                UpToDate: false,
                "is not a git checkout this node can read, so whether it holds current code is unobserved");
        }

        SemaphoreSlim mutex = LockFor(repositoryPath);
        await mutex.WaitAsync(cancellationToken);
        try
        {
            await BestEffortFetchAsync(repositoryPath, cancellationToken);

            (int behindExit, string counted, string countError) = await TryRunGitAsync(
                checkoutPath, $"rev-list --count HEAD..origin/{branch}", cancellationToken);
            if (behindExit != 0 || !int.TryParse(counted.Trim(), out int behind))
            {
                return new CheckoutRefresh(
                    UpToDate: false,
                    $"could not be compared against origin/{branch} ({countError.Trim()}), so whether it "
                    + "holds current code is unobserved");
            }

            if (behind == 0)
            {
                return new CheckoutRefresh(UpToDate: true, $"already at origin/{branch}");
            }

            // Fast-forward only. Whatever is uncommitted or committed locally under a reading
            // checkout is somebody's, and this is not the place that gets to decide it was not
            // wanted — SyncToOriginBestEffortAsync resets, but that one owns a run's own worktree.
            (int mergeExit, _, string mergeError) = await TryRunGitAsync(
                checkoutPath, $"merge --ff-only origin/{branch}", cancellationToken);
            (_, string head, _) = await TryRunGitAsync(checkoutPath, "rev-parse --short HEAD", cancellationToken);

            return mergeExit == 0
                ? new CheckoutRefresh(UpToDate: true, $"fast-forwarded {behind} commit(s) to origin/{branch}")
                : new CheckoutRefresh(
                    UpToDate: false,
                    $"is {behind} commit(s) behind origin/{branch} at {head.Trim()} and could not be "
                    + $"fast-forwarded ({mergeError.Trim()}); it was left exactly as it is");
        }
        finally
        {
            mutex.Release();
        }
    }

    public async Task DeleteBranchEverywhereAsync(string repositoryPath, string branch, CancellationToken cancellationToken)
    {
        repositoryPath = Path.GetFullPath(repositoryPath);
        SemaphoreSlim mutex = LockFor(repositoryPath);
        await mutex.WaitAsync(cancellationToken);
        try
        {
            // -D, not -d: PRs land via rebase merge, so the branch tip is never an ancestor
            // of the base branch. The caller's merged-PR observation is the justification.
            (int localExit, _, string localError) = await TryRunGitAsync(
                repositoryPath, $"branch -D \"{branch}\"", cancellationToken);
            if (localExit != 0)
            {
                logger.LogDebug("Local branch {Branch} not deleted ({Error})", branch, localError.Trim());
            }

            (int originExit, _, _) = await TryRunGitAsync(repositoryPath, "remote get-url origin", cancellationToken);
            if (originExit == 0)
            {
                // The merge often deletes the remote branch already; a failure here is expected.
                (int remoteExit, _, string remoteError) = await TryRunGitAsync(
                    repositoryPath, $"push origin --delete \"{branch}\"", cancellationToken);
                if (remoteExit != 0)
                {
                    logger.LogDebug("Remote branch {Branch} not deleted ({Error})", branch, remoteError.Trim());
                }

                (int pruneExit, _, string pruneError) = await TryRunGitAsync(
                    repositoryPath, "fetch --prune origin", cancellationToken);
                if (pruneExit != 0)
                {
                    logger.LogWarning("git fetch --prune failed for {Repository} ({Error})", repositoryPath, pruneError.Trim());
                }
            }

            // Best effort by design: local/remote deletion failures are logged above and
            // are often expected (the merge may have deleted the remote branch already).
            logger.LogInformation(
                "Branch {Branch} cleanup pass finished for {Repository} (local {LocalOutcome}, remote push {RemoteOutcome})",
                branch, repositoryPath,
                localExit == 0 ? "deleted" : "not deleted",
                originExit == 0 ? "attempted" : "skipped");
        }
        finally
        {
            mutex.Release();
        }
    }

    /// <summary>Scans git worktree list for the worktree (other than the repo itself) holding the branch.</summary>
    private static async Task<string?> FindWorktreeHoldingBranchAsync(
        string repositoryPath, string branch, CancellationToken cancellationToken)
    {
        (int exitCode, string output, _) = await TryRunGitAsync(
            repositoryPath, "worktree list --porcelain", cancellationToken);
        if (exitCode != 0)
        {
            return null;
        }

        string? currentPath = null;
        foreach (string line in output.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                currentPath = line["worktree ".Length..];
            }
            else if (line == $"branch refs/heads/{branch}"
                && currentPath is not null
                && Path.GetFullPath(currentPath) != repositoryPath)
            {
                return currentPath;
            }
        }

        return null;
    }

    /// <summary>
    /// Brings a resumed branch up to date with origin before the follow-up run starts.
    /// Review feedback may have landed as commits on the PR itself (web-applied
    /// suggestions); a clean fast-forward picks them up, and a local tip strictly ahead
    /// of origin is kept as-is (unpushed work — a follow-up whose push never landed —
    /// that a reset would destroy), and a worktree holding uncommitted work is never
    /// touched at all (a retried run may be rescuing a stranded attempt). Diverged
    /// clean tips mean the branch was REWRITTEN on
    /// origin (narrative follow-ups force-push rebased history, Decisions Log #26): the
    /// remote tip is the pull request's truth, so the worktree resets to it instead of
    /// resuming a stale pre-rebase ref. The old tip stays reachable via the reflog.
    /// </summary>
    private async Task SyncToOriginBestEffortAsync(
        string repositoryPath, string worktreePath, string branch, CancellationToken cancellationToken)
    {
        if (!await RefExistsAsync(repositoryPath, $"refs/remotes/origin/{branch}", cancellationToken))
        {
            return;
        }

        // Ancestry decides, not merge exit codes: --ff-only also fails on a dirty
        // worktree it would overwrite, which is not divergence (review finding, PR #10).
        (int aheadExit, _, _) = await TryRunGitAsync(
            worktreePath, $"merge-base --is-ancestor origin/{branch} HEAD", cancellationToken);
        if (aheadExit == 0)
        {
            // Equal or strictly ahead: unpushed work a sync must not touch.
            return;
        }

        // Behind or diverged from here on. Uncommitted work vetoes every destructive
        // path — a retried run may be resuming a worktree holding a stranded attempt,
        // and rescuing that work is the point of resuming.
        (int statusExit, string status, _) = await TryRunGitAsync(worktreePath, "status --porcelain", cancellationToken);
        if (statusExit != 0 || status.Trim().Length > 0)
        {
            logger.LogWarning(
                "Branch {Branch} is behind or diverged from origin, but the worktree holds uncommitted work — keeping it untouched",
                branch);
            return;
        }

        (int behindExit, _, _) = await TryRunGitAsync(
            worktreePath, $"merge-base --is-ancestor HEAD origin/{branch}", cancellationToken);
        if (behindExit == 0)
        {
            // Strictly behind with a clean tree: a fast-forward picks up commits that
            // landed on the PR itself (web-applied suggestions).
            await TryRunGitAsync(worktreePath, $"merge --ff-only origin/{branch}", cancellationToken);
            return;
        }

        (_, string staleTip, _) = await TryRunGitAsync(worktreePath, "rev-parse HEAD", cancellationToken);
        (int resetExit, _, string resetError) = await TryRunGitAsync(
            worktreePath, $"reset --hard origin/{branch}", cancellationToken);
        if (resetExit == 0)
        {
            logger.LogInformation(
                "Branch {Branch} was rewritten on origin; worktree reset from stale tip {StaleTip} to the remote tip",
                branch, staleTip.Trim());
        }
        else
        {
            logger.LogWarning(
                "Branch {Branch} diverged from origin and could not reset ({Error}); continuing from the local tip",
                branch, resetError.Trim());
        }
    }

    private SemaphoreSlim LockFor(string repositoryPath) =>
        _repositoryLocks.GetOrAdd(repositoryPath, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// The repository a working tree resolves its refs through, in the same terms every other
    /// method here uses: the bare clone for a worktree cut from one, and the clone's own root
    /// (not its <c>.git</c>) for an ordinary checkout, so a lock taken on it is the same lock
    /// CreateAsync and the rest take. Null when the path is not a checkout git will answer for.
    /// </summary>
    private static async Task<string?> ResolveRepositoryAsync(string checkoutPath, CancellationToken cancellationToken)
    {
        (int exitCode, string commonDirectory, _) = await TryRunGitAsync(
            checkoutPath, "rev-parse --git-common-dir", cancellationToken);
        if (exitCode != 0 || commonDirectory.Trim() is not { Length: > 0 } answer)
        {
            return null;
        }

        // git answers relative to the directory it ran in for an ordinary checkout (".git") and
        // absolute for a linked worktree; both resolve against the checkout.
        string resolved = Path.GetFullPath(answer, Path.GetFullPath(checkoutPath));
        return Path.GetFileName(Path.TrimEndingDirectorySeparator(resolved)) == ".git"
            ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(resolved)) ?? resolved
            : resolved;
    }

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
