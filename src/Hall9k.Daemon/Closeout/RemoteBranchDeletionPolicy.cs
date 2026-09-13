using System.Collections.Concurrent;
using Hall9k.Connectors.Worktrees;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Daemon.Closeout;

/// <summary>
/// Who deletes a merged run's branch from origin, per repository, read once per closeout sweep
/// (task: a stacked child's pull request survives its parent's merge; Decisions Log
/// #PLACEHOLDER-c9a3a6c8). One read, cached, because a sweep that closes out four merged pull
/// requests in the same repository would otherwise spend four identical <c>gh</c> calls on a
/// setting nobody changes mid-sweep.
/// <para>
/// The cache is keyed by repository path rather than by project id: the setting is a fact about the
/// repository, and two registrations of the same repository have the same answer. It is cleared at
/// the top of every sweep (<see cref="Forget"/>), which is what bounds how stale an answer can be —
/// a setting turned on mid-sweep is honoured on the next one, and the only cost of the stale read is
/// one branch deleted the way it always was.
/// </para>
/// <para>
/// Keyed through <see cref="ProjectHomePaths.DirectoryComparer"/>, which is the same platform-aware
/// comparison every path-ownership check in the platform makes, rather than a flat
/// case-insensitive one: two registrations whose repository paths differ only in case are the same
/// repository on Windows and macOS and two different ones on Linux, and <c>ProjectHomeClaims</c>
/// already lets both be registered there. A case-insensitive key would hand the second repository
/// the first one's setting, which is the wrong deletion policy rather than a stale one (external
/// review of this branch, 2026-09-13).
/// </para>
/// <para>
/// Two callers on separate loops share this cache (see below), so the write is fenced against the
/// sweep it was read for: <see cref="Forget"/> bumps a sweep counter before it clears, and a read
/// still in flight when that happens returns its answer to its own caller but does not insert it.
/// Without that fence an answer from the previous sweep could land in the cleared cache moments
/// after the new sweep started and outlive the clear that was supposed to bound it, which is the
/// one way a setting flipped between sweeps could go unhonoured indefinitely rather than for one
/// sweep (external review of this branch, 2026-09-13). Concurrent misses are deliberately NOT
/// deduplicated into one shared read: sharing a single <c>Task</c> across callers means sharing the
/// first caller's <see cref="CancellationToken"/>, so a dispatch loop shutting down would cancel
/// the closeout sweep's read as well, and two callers racing costs one extra <c>gh</c> call while
/// both write the same answer.
/// </para>
/// <para>
/// A read that fails is cached too, as <see cref="RemoteBranchDeletionOwner.Daemon"/>: within one
/// sweep, a <c>gh</c> that cannot answer for this repository will not answer for the next merged run
/// in it either, and retrying per run would spend the sweep's time relearning the same failure.
/// </para>
/// <para>
/// Built by <see cref="CloseoutEngine"/> from the seam and the logger it already holds rather than
/// registered in the container, and asked through that engine by its one other caller
/// (<c>RunLauncher</c>, which already routes its merged-task closeout through the same engine): the
/// cache has to be the same one the sweep clears, and a second registration threaded through both
/// constructors would be one more positional argument in three dozen test call sites for an object
/// neither of them needs to substitute.
/// </para>
/// </summary>
public sealed class RemoteBranchDeletionPolicy(IPullRequestInspector inspector, ILogger logger)
{
    private readonly ConcurrentDictionary<string, RemoteBranchDeletionOwner> _byRepository =
        new(ProjectHomePaths.DirectoryComparer);

    /// <summary>
    /// Which sweep the answers in the cache belong to. Bumped by <see cref="Forget"/> and read back
    /// after the <c>gh</c> call, which is what keeps a previous sweep's in-flight answer out of the
    /// new sweep's cache.
    /// </summary>
    private int _sweep;

    /// <summary>Drops every cached answer, so the next sweep reads each repository's setting fresh.</summary>
    public void Forget()
    {
        // Bumped BEFORE the clear, never after: a read that observes the new number writes into the
        // cache this clear is about to empty, which costs one re-read, while a read that observes
        // the old one is refused outright. The reverse order would leave a window where a stale
        // answer is inserted after the clear and still passes the fence.
        Interlocked.Increment(ref _sweep);
        _byRepository.Clear();
    }

    /// <summary>
    /// The owner for <paramref name="repositoryPath"/>, from this sweep's cache or from one
    /// <c>gh</c> read. Never throws: a read that could not be made is reported as the platform's own
    /// deletion — today's behaviour, and the only honest fallback, since an unread setting is not
    /// evidence the repository has one turned on (AGENTS.md's never-guess rule).
    /// </summary>
    public async Task<RemoteBranchDeletionOwner> OwnerAsync(
        string repositoryPath, CancellationToken cancellationToken)
    {
        string key = ProjectHomePaths.DirectoryKey(repositoryPath);
        int sweep = Volatile.Read(ref _sweep);
        if (_byRepository.TryGetValue(key, out RemoteBranchDeletionOwner cached))
        {
            return cached;
        }

        bool? setting;
        try
        {
            setting = await inspector.DeletesHeadBranchOnMergeAsync(repositoryPath, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Logged at Debug, not Warning: a repository with no gh authentication, or no GitHub
            // remote at all, reaches here on every sweep that closes anything out, and the
            // consequence is that branch cleanup behaves exactly as it did before this read
            // existed. The stacked-child hazard the setting guards against is named where it
            // matters — MergedBranchCleanup's own doc — not once per sweep in the log.
            logger.LogDebug(
                exception,
                "Could not read whether {Repository} deletes head branches on merge; deleting merged branches "
                + "from origin here, as this platform always has",
                repositoryPath);
            setting = null;
        }

        RemoteBranchDeletionOwner owner = MergedBranchCleanup.OwnerOf(setting);
        if (Volatile.Read(ref _sweep) == sweep)
        {
            // Still the sweep this answer was read for. A sweep that started while the read was in
            // flight gets its own read instead of this one; the caller here is answered either way,
            // because the answer is true about the repository whichever sweep asked for it.
            _byRepository[key] = owner;
        }

        if (owner == RemoteBranchDeletionOwner.GitHub)
        {
            logger.LogInformation(
                "{Repository} deletes head branches on merge, so GitHub owns the deletion of every merged branch "
                + "on origin here — this platform deletes only its local copy, which is what lets GitHub retarget "
                + "any pull request stacked on it instead of closing it",
                repositoryPath);
        }

        return owner;
    }
}
