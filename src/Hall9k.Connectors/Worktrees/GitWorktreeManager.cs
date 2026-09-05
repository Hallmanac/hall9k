using System.Collections.Concurrent;
using System.Diagnostics;
using Hall9k.Connectors.Processes;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Tasks;
using Microsoft.Extensions.Logging;

namespace Hall9k.Connectors.Worktrees;

public sealed class GitWorktreeManager(ILogger<GitWorktreeManager> logger) : IWorktreeManager
{
    // Parallel worktree add/fetch against one repo hits git's internal locks; a per-repo
    // mutex is cleaner than retry loops (Decisions Log #4). In-process only — it used to
    // be enough because the daemon's DI singleton was the only thing in the platform that
    // ever touched a worktree. h9k task work (adversarial review, cycle 4) now runs a
    // second GitWorktreeManager in the CLI process against the same repository, so the
    // cross-process lock below is what actually serializes the two.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _repositoryLocks = new();

    public async Task<Worktree> CreateAsync(WorktreeRequest request, CancellationToken cancellationToken)
    {
        string repositoryPath = Path.GetFullPath(request.RepositoryPath);
        await using RepositoryLock repositoryLock = await AcquireRepositoryLockCoreAsync(repositoryPath, cancellationToken);
        {
            await BestEffortFetchAsync(repositoryPath, cancellationToken);

            string startPoint = await ResolveStartPointAsync(repositoryPath, request.BaseBranch, cancellationToken);
            string branch = await ResolveBranchNameAsync(repositoryPath, request, cancellationToken);
            string worktreePath = WorktreePathFor(repositoryPath, request);

            // `worktree add -b` writes no reflog entry for the branch's creation unless
            // core.logAllRefUpdates is set on this repository — a config the platform only
            // sets on a repo it clones itself (RepoMaterialiser.CloneAsync), never on a
            // hand-cut bare clone a project was pointed at. For a fresh branch this rarely
            // bites on its own: a linked worktree is not bare, so log_all_ref_updates defaults
            // on there regardless of the shared bare repo's setting, and the checkpoint-recompose
            // protocol's own fork-point reset lands back on exactly this creation tip anyway,
            // which logs it independently of this line (verified empirically; adversarial
            // review, cycle 1). The real hazard is CheckoutExistingAsync's remoteExists arm
            // below, which recreates a local branch ref from origin's tip after the ref itself
            // was deleted while origin still held it — no later fork-point reset ever revisits
            // that exact tip again, so an unlogged recreation there is invisible to both
            // WasEverLocalHeadAsync below and its twin in PullRequestOpener.PushBranchAsync,
            // reading a resumed recompose as someone else's rewrite. Materialise the ref
            // explicitly here too, the same way that arm does, so both creation paths behave
            // identically — this copy costs nothing even though only the other one is
            // load-bearing. The trailing empty <oldvalue> makes this a create-only
            // compare-and-swap — `worktree add -b` refused outright when the branch already
            // existed, and a plain update-ref would silently force-move it instead, which is
            // unsafe when ResolveBranchNameAsync's run-suffixed retry name collides with
            // another retry's (independent pre-PR review, cycle 8).
            await RunGitAsync(
                repositoryPath,
                $"update-ref --create-reflog refs/heads/{branch} {startPoint} \"\" " +
                $"-m \"branch: Created from {startPoint} by CreateAsync\"",
                cancellationToken);
            await RunGitAsync(
                repositoryPath,
                $"worktree add \"{worktreePath}\" {branch}",
                cancellationToken);

            logger.LogInformation(
                "Worktree {Path} created on branch {Branch} from {StartPoint}",
                worktreePath, branch, startPoint);
            return new Worktree(worktreePath, branch, startPoint);
        }
    }

    public async Task<Worktree> CheckoutExistingAsync(FollowUpWorktreeRequest request, CancellationToken cancellationToken)
    {
        string repositoryPath = Path.GetFullPath(request.RepositoryPath);
        await using RepositoryLock repositoryLock = await AcquireRepositoryLockCoreAsync(repositoryPath, cancellationToken);
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
                // `worktree add -b` writes no reflog entry for the branch's creation unless
                // core.logAllRefUpdates is set on this repository — a config the platform
                // only sets on a repo it clones itself (RepoMaterialiser.CloneAsync), never
                // on a hand-cut bare clone a project was pointed at. Without that entry,
                // origin's tip at creation time never appears in the branch reflog, so a
                // later checkpoint recompose that diverges from it looks indistinguishable
                // from someone else rewriting the branch to both WasEverLocalHeadAsync below
                // and its twin in PullRequestOpener.PushBranchAsync — refusing a legitimate
                // push, or hard-resetting a real recompose away, on a false "rewrite" read.
                // Materialise the ref explicitly so the creation point is always recorded,
                // regardless of repo config. The trailing empty <oldvalue> keeps this
                // create-only, matching CreateAsync's own guard above.
                await RunGitAsync(
                    repositoryPath,
                    $"update-ref --create-reflog refs/heads/{branch} refs/remotes/origin/{branch} \"\" " +
                    $"-m \"branch: Created from origin/{branch} by CheckoutExistingAsync\"",
                    cancellationToken);
                await RunGitAsync(repositoryPath, $"worktree add \"{worktreePath}\" {branch}", cancellationToken);
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
    }

    public async Task<Worktree> CreatePrReviewCheckoutAsync(
        PrReviewWorktreeRequest request, CancellationToken cancellationToken)
    {
        string repositoryPath = Path.GetFullPath(request.RepositoryPath);
        await using RepositoryLock repositoryLock = await AcquireRepositoryLockCoreAsync(repositoryPath, cancellationToken);
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
    }

    public async Task RemoveAsync(string repositoryPath, string worktreePath, CancellationToken cancellationToken)
    {
        repositoryPath = Path.GetFullPath(repositoryPath);
        await using RepositoryLock repositoryLock = await AcquireRepositoryLockCoreAsync(repositoryPath, cancellationToken);
        {
            await RunGitAsync(repositoryPath, $"worktree remove --force \"{worktreePath}\"", cancellationToken);
            logger.LogInformation("Worktree {Path} removed", worktreePath);
        }
    }

    public async Task DeletePrReviewTrackingRefAsync(
        string repositoryPath, int pullRequestNumber, CancellationToken cancellationToken)
    {
        repositoryPath = Path.GetFullPath(repositoryPath);
        await using RepositoryLock repositoryLock = await AcquireRepositoryLockCoreAsync(repositoryPath, cancellationToken);
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
    }

    private static string PrReviewTrackingRef(int pullRequestNumber) => $"refs/remotes/origin/pr-review/{pullRequestNumber}";

    public async Task PruneAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        repositoryPath = Path.GetFullPath(repositoryPath);
        await using RepositoryLock repositoryLock = await AcquireRepositoryLockCoreAsync(repositoryPath, cancellationToken);
        {
            await RunGitAsync(repositoryPath, "worktree prune", cancellationToken);
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

        await using RepositoryLock repositoryLock = await AcquireRepositoryLockCoreAsync(repositoryPath, cancellationToken);
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
    }

    public async Task DeleteBranchEverywhereAsync(string repositoryPath, string branch, CancellationToken cancellationToken)
    {
        repositoryPath = Path.GetFullPath(repositoryPath);
        await using RepositoryLock repositoryLock = await AcquireRepositoryLockCoreAsync(repositoryPath, cancellationToken);
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
    /// touched at all (a retried run may be rescuing a stranded attempt). Diverged clean
    /// tips are ambiguous, though, since the build-session checkpoint-and-recompose
    /// protocol (task: build sessions stop stranding finished work uncommitted) can make
    /// THIS worktree's own history diverge from a tip it pushed itself: a resumed run
    /// resets to the branch's fork point and recomposes fresh commits over it, so a later
    /// resume whose recompose never got pushed sees a local tip that shares no ancestry
    /// with origin, exactly what an external rewrite looks like. <see cref="WasEverLocalHeadAsync"/>
    /// picks a side rather than proving one: if origin's tip ever WAS this local branch's own
    /// tip (recorded in the branch ref's own reflog, which every worktree that ever moved it
    /// shares — not just this one), the local tip is kept, the same as the strictly-ahead case.
    /// This is chosen, not entailed — the reflog condition cannot distinguish "this local
    /// checkpoint recompose never reached origin" from "origin was deliberately rolled back
    /// (a human force-pushing over a bad automated tip) to a commit this branch once held", and
    /// picking the local tip is wrong in the second case: nothing downstream re-checks the
    /// human's intent, so a subsequent push can silently force the discarded tip back onto
    /// origin. It is the chosen default anyway because the first case is the routine one this
    /// task adds and the second is a deliberate, comparatively rare operator intervention. Only
    /// a tip this branch
    /// never held anywhere is treated as a genuine rewrite on origin (narrative follow-ups
    /// force-push rebased history, Decisions Log #26), where the remote tip is the pull
    /// request's truth and the worktree resets to it. Checking the branch's own reflog rather
    /// than this worktree's private HEAD reflog matters because a worktree can be removed and
    /// re-added on a surviving local branch (an operator or a temp cleaner deleting the
    /// directory outside the platform's own remove-then-delete-branch paths): the new
    /// worktree's HEAD reflog starts empty even though the branch itself still remembers every
    /// tip it ever held (independent pre-PR review, cycle 2). The old tip stays reachable via
    /// the reflog either way.
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

        (_, string originTip, _) = await TryRunGitAsync(worktreePath, $"rev-parse origin/{branch}", cancellationToken);
        if (await WasEverLocalHeadAsync(worktreePath, branch, originTip.Trim(), cancellationToken))
        {
            logger.LogInformation(
                "Branch {Branch} diverged from a tip this branch held itself — treating it as a local " +
                "checkpoint recompose that never reached origin rather than a rewrite — keeping the " +
                "local tip",
                branch);
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

    /// <summary>
    /// Whether <paramref name="commit"/> was ever <paramref name="branch"/>'s own tip, per the
    /// branch ref's <c>logs/refs/heads/&lt;branch&gt;</c> reflog. Unlike <c>logs/HEAD</c> (which
    /// <c>git worktree</c> support gives every linked worktree separately, under its own gitdir),
    /// a branch ref and its reflog live in the shared repository and are visible from any
    /// worktree that has the branch checked out, so this survives the worktree that made the
    /// commit being removed and re-added elsewhere on the same branch. A commit that shows up
    /// here is one this branch was reset through or committed to, in any worktree; a commit that
    /// never appears here came from somewhere else entirely.
    /// </summary>
    private static async Task<bool> WasEverLocalHeadAsync(
        string worktreePath, string branch, string commit, CancellationToken cancellationToken)
    {
        (int exitCode, string output, _) = await TryRunGitAsync(
            worktreePath, $"reflog show {branch} --format=%H", cancellationToken);
        if (exitCode != 0)
        {
            return false;
        }

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(commit, StringComparer.Ordinal);
    }

    private SemaphoreSlim LockFor(string repositoryPath) =>
        _repositoryLocks.GetOrAdd(repositoryPath, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Serializes worktree/fetch operations against one repository both within this process
    /// (the semaphore) and across processes (a lock file dropped in the repository itself):
    /// h9k task work now runs a second GitWorktreeManager in the CLI process against the same
    /// repository the daemon's own DI singleton touches, so the in-process semaphore alone no
    /// longer covers every writer (adversarial review, cycle 4).
    /// </summary>
    private async Task<RepositoryLock> AcquireRepositoryLockCoreAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        SemaphoreSlim mutex = LockFor(repositoryPath);
        await mutex.WaitAsync(cancellationToken);
        try
        {
            FileStream crossProcessLock = await AcquireCrossProcessLockAsync(repositoryPath, cancellationToken);
            return new RepositoryLock(mutex, crossProcessLock);
        }
        catch
        {
            mutex.Release();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<IAsyncDisposable> AcquireRepositoryLockAsync(string repositoryPath, CancellationToken cancellationToken) =>
        // Normalized exactly like every method above, so a caller's relative or differently
        // formatted path still keys the same in-process semaphore (_repositoryLocks) as the
        // fully-qualified path CreateAsync/CheckoutExistingAsync/etc. already normalize to —
        // otherwise two equivalent paths would take two different in-process locks and this
        // method would fail to serialize against them at all.
        await AcquireRepositoryLockCoreAsync(Path.GetFullPath(repositoryPath), cancellationToken);

    /// <summary>
    /// FileShare.None maps to an exclusive advisory lock on Unix (the same mechanism
    /// SingleInstanceGuard already uses for the daemon's own single-instance check) and to a
    /// real exclusive lock on Windows: whichever process's FileStream opens it first holds it
    /// until disposed, so a second process racing the same repository waits here instead of
    /// losing a `git worktree add` / `git fetch` to a locked ref file.
    /// </summary>
    private async Task<FileStream> AcquireCrossProcessLockAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        string lockDirectory = await ResolveLockDirectoryAsync(repositoryPath, cancellationToken);
        string lockFilePath = Path.Combine(lockDirectory, ".h9k-worktree.lock");
        DateTimeOffset waitStarted = DateTimeOffset.UtcNow;
        DateTimeOffset nextLogAt = waitStarted.AddSeconds(1);
        while (true)
        {
            try
            {
                return new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (DirectoryNotFoundException)
            {
                // repositoryPath itself no longer exists (removed out-of-band, or pruned by
                // an earlier step in the same call chain) — no process can ever release a
                // lock for a directory that is gone, so retrying here would spin until the
                // caller's own cancellation fires instead of failing honestly right away.
                // CloseoutEngine.RemoveWorktreeBestEffortAsync's own best-effort catch already
                // treats any non-cancellation exception as "safe to log and continue", which
                // is exactly the right outcome for a repository that has already vanished.
                throw new WorktreeException($"Repository {repositoryPath} no longer exists on disk.");
            }
            catch (IOException)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Unbounded on purpose (a holder wedged on a hung fetch still has to release
                // eventually, and there is no safe value to time this out to), but silent for
                // the whole wait was the actual defect: before this lock existed, the CLI never
                // contended for anything the daemon held, so an operator running h9k task work
                // against a repository the daemon's own GitWorktreeManager was mid-fetch on saw
                // nothing at all print until they gave up and interrupted it (adversarial
                // review, cycle 4). A periodic line naming the lock file is the minimum needed
                // to tell "waiting on another h9k process" apart from "hung".
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (now >= nextLogAt)
                {
                    logger.LogInformation(
                        "Waiting on cross-process worktree lock {LockFile} ({Elapsed:0}s elapsed) — another h9k process is using this repository",
                        lockFilePath, (now - waitStarted).TotalSeconds);
                    nextLogAt = now.AddSeconds(5);
                }

                await Task.Delay(50, cancellationToken);
            }
        }
    }

    /// <summary>
    /// git's own directory — the bare clone itself, or an ordinary checkout's <c>.git</c> — so
    /// the lock file lands somewhere git already owns rather than in a working tree a human
    /// might `git add -A` and commit by accident: a project registered against an ordinary
    /// (non-bare) clone has <c>repositoryPath</c> pointing straight at that clone's root, same
    /// as <see cref="ResolveRepositoryAsync"/> resolves for it, and the hall9k project's own bare
    /// clone hid this because a bare repository's git-common-dir already is its root (conformance
    /// review, cycle 1). Falls back to <paramref name="repositoryPath"/> itself — the previous,
    /// working-tree-visible location — when git cannot answer (the checkout doesn't exist yet, or
    /// has already vanished), so the DirectoryNotFoundException case above still behaves exactly
    /// as it did before this method existed.
    /// </summary>
    private static async Task<string> ResolveLockDirectoryAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        (int exitCode, string commonDirectory, _) = await TryRunGitAsync(
            repositoryPath, "rev-parse --git-common-dir", cancellationToken);
        if (exitCode != 0 || commonDirectory.Trim() is not { Length: > 0 } answer)
        {
            return repositoryPath;
        }

        return Path.GetFullPath(answer, Path.GetFullPath(repositoryPath));
    }

    private sealed class RepositoryLock(SemaphoreSlim mutex, FileStream crossProcessLock) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            crossProcessLock.Dispose();
            mutex.Release();
            return ValueTask.CompletedTask;
        }
    }

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

    /// <summary>
    /// The one place a task branch is ever named. The project's template decides the shape (the
    /// default renders the same <c>task/&lt;shortid&gt;-&lt;slug&gt;</c> this method hard-coded
    /// before projects could state a convention), and the result is handed straight back to the
    /// caller, which records it on <c>RunDispatched</c> — nothing re-renders it later, which is
    /// what keeps <c>PullRequestOpener</c>'s much-later verbatim push pointed at a ref that exists.
    /// <para>
    /// The collision retry is unchanged and template-agnostic: whatever the template rendered gets
    /// the same run suffix. It cannot make a legal name illegal, because
    /// <see cref="BranchNameTemplate.Render"/> guarantees a name ending in a character git allows
    /// and the suffix adds only <c>-r</c> and four hex digits.
    /// </para>
    /// <para>
    /// The collision check itself has to look at <c>refs/remotes/origin/*</c> as well as
    /// <c>refs/heads/*</c> — a guarantee the hard-coded name used to get for free, since it always
    /// embedded the task's own unique short id, but a project's template need not contain
    /// <c>{shortid}</c> at all. A tracker-keyed template such as <c>{key}</c> can render a name
    /// someone already pushed to origin under their own local branch of the same name, in which
    /// case only the local check would miss it: <c>CreateAsync</c>'s fetch lands that branch at
    /// <c>refs/remotes/origin/&lt;branch&gt;</c>, never at <c>refs/heads/&lt;branch&gt;</c>
    /// (<c>RepoMaterialiser</c> maps the bare clone's refspec that way), so a local-only check
    /// would create a fresh local branch that shares no history with origin's tip under that name
    /// and let the run build its whole change there — only for the eventual push to be refused
    /// once the run has already done all its work. <see cref="CheckoutExistingAsync"/> already
    /// checks both refs for the same reason (adversarial pre-PR review, cycle 3).
    /// </para>
    /// </summary>
    private async Task<string> ResolveBranchNameAsync(string repositoryPath, WorktreeRequest request, CancellationToken cancellationToken)
    {
        // Parse answers an empty reference for a task carrying none, and Render turns that into a
        // stated "no key" rather than an empty segment — so there is no null case to special-case.
        string branch = request.BranchNameTemplate.Render(
            request.TaskId, request.Objective, ExternalReference.Parse(request.ExternalReference).Key);

        // Branch per task — but a retried task's failed worktree is retained (cleanup policy, log
        // #4) and still holds the branch, so retries get a run-suffixed name. A name that already
        // exists on origin, but never locally, needs the same suffix: nothing else notices until
        // the eventual push is refused for a branch this node never accounted for.
        bool collides =
            await RefExistsAsync(repositoryPath, $"refs/heads/{branch}", cancellationToken)
            || await RefExistsAsync(repositoryPath, $"refs/remotes/origin/{branch}", cancellationToken);

        return collides
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
        NonInteractiveGit.Apply(process.StartInfo);
        process.Start();

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, await standardOutput, await standardError);
    }
}
