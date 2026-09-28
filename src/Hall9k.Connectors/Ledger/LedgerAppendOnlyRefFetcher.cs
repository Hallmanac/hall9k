using Hall9k.Connectors.Processes;

namespace Hall9k.Connectors.Ledger;

/// <summary>
/// A genuine fetch failure (network, credentials) while <see cref="LedgerAppendOnlyRefFetcher"/> was
/// staging a ref or a prefix — never thrown for "nothing there yet" (git's own "couldn't find remote
/// ref", or a wildcard refspec matching nothing), which is not a failure. Each of the three callers
/// (<see cref="GitLedger"/>, <c>Hall9k.Connectors.Ledger.GitLedgerCommitReader</c>,
/// <c>Hall9k.Connectors.Trust.GitLedgerChainReader</c>) decides for itself whether that is
/// something to let propagate or to catch and fall back to whatever this node last had locally — "a
/// network failure is unchanged per class" (idea 6be68ee2, trust finding 8). Derives from
/// <see cref="InvalidOperationException"/>, the exact type every one of those three fetch call sites
/// threw for this same failure before this class existed, because every caller several layers up
/// (<c>ProjectMembersCommand</c>, <c>TaskAssignCommand</c>, <c>PromptAddendumOwnerRoleGate</c> and
/// others) still catches only that type to fall back or report "re-run once the remote is
/// reachable" (independent pre-PR review, cycle 1, both lenses, medium).
/// </summary>
public sealed class LedgerFetchFailedException(string refName, string gitError)
    : InvalidOperationException($"git fetch of {refName} from origin failed: {gitError.Trim()}")
{
    public string RefName { get; } = refName;

    public string GitError { get; } = gitError;
}

/// <summary>
/// One ref's own outcome from <see cref="LedgerAppendOnlyRefFetcher.FetchAsync"/> or
/// <see cref="LedgerAppendOnlyRefFetcher.FetchPrefixAsync"/>: the tip a caller should actually read
/// or build a write on, and — only when <see cref="WasRefused"/> — enough to name both commits in an
/// <c>UnverifiedLedgerWrite</c> a caller wants to record.
/// </summary>
/// <param name="Tip">
/// What to read or write against. The freshly fetched tip on an ordinary fetch, a seed, or a
/// legitimate fast-forward; the standing verified tip (or, when the remote ref is confirmed gone,
/// this node's own unmoved local copy) on a refusal; null only when nothing exists anywhere yet.
/// </param>
/// <param name="WasRefused">
/// True for both refusal shapes this type carries — a rewind/side-merge, and a remote ref that
/// existed with a verified tip but is now missing from origin. Neither ever moves the live or the
/// verified ref; both are worth recording.
/// </param>
public sealed record LedgerAppendOnlyFetchResult(
    string? Tip, bool WasRefused, string? FetchedTip, string? VerifiedTip, string? RefusalReason);

/// <summary>
/// The one place an append-only ledger ref is ever fetched (idea 6be68ee2, trust finding 8):
/// replaces the four independent <c>FetchRefAsync</c>/<c>FetchRefsAsync</c> copies this task found in
/// <see cref="GitLedger"/>, <c>GitLedgerCommitReader</c>, and <c>GitLedgerChainReader</c> (the single-
/// ref one, twice; the wildcard one, once) — <c>GitLedgerMessageTransport</c> stays exempt, since a
/// node's own outbox is squashed and force-pushed with a lease by design, the one ref shape
/// <see cref="LedgerRefEntry.AppendOnly"/> is false for.
/// <para>
/// Every fetch lands in a private, per-call staging name outside <c>refs/hall9k/</c>
/// (<c>refs/hall9k-staging/&lt;nonce&gt;/&lt;path&gt;</c>, <c>--no-write-fetch-head</c>, removed in a
/// finally) — never <c>FETCH_HEAD</c>, one file in the shared bare repository the daemon, every CLI
/// read, and every worktree would otherwise clobber. What the fetch brought down is then checked
/// against a local-only, never-fetched-or-pushed <c>refs/hall9k-verified/&lt;path&gt;</c> ref in the
/// same bare repository (<c>RepoMaterialiser</c> always bare-clones; refs are shared across
/// worktrees) — the last tip this node itself actually verified — before either the live ref or the
/// verified ref ever moves, and only by <c>update-ref &lt;ref&gt; &lt;new&gt; &lt;old&gt;</c>, so a
/// slow concurrent reader on this same node can never move either backward.
/// </para>
/// <para>
/// The decision table (idea 6be68ee2, trust finding 8): no verified ref yet seeds it from the
/// existing local live ref when one exists, else adopts whatever origin holds outright (a fresh
/// clone or a new joiner has nothing of its own to prefer); the freshly fetched tip is not itself
/// adopted that same call, so a fleet that had already rewound before this ships is never silently
/// re-trusted the moment the daemon happens to fetch fresh content on the same call that seeds the
/// anchor. A remote ref that is confirmed gone, but this node already holds a verified tip for it, is
/// a refusal: the local copy is kept and read, never deleted (the bug this task's own review named at
/// the two call sites that used to run <c>update-ref -d</c> on a "couldn't find remote ref"). A
/// freshly fetched tip that is on the verified tip's own <c>git rev-list --first-parent</c> chain — or
/// equal to it — is a legitimate fast-forward: move. A verified tip that resolves to no object at all
/// (the ref survived, but the object it names did not) or a freshly fetched tip whose first-parent
/// chain does not contain the verified tip is a refusal too: a rewind, or a side merge whose first
/// parent is the old tip and second parent the current one — a plain fast-forward to origin, and a
/// descendant to <c>merge-base --is-ancestor</c>, but never on the mainline chain this ref's own
/// replay in <c>GitLedgerChainReader</c> and <c>GitLedgerCommitReader</c> actually walks
/// (<c>--topo-order --first-parent</c> throughout). A refusal never throws: the caller keeps reading
/// the verified tip, and a caller building a write builds on it too, so the very next write heals a
/// pure rewind on its own by pushing forward from wherever origin now sits.
/// </para>
/// </summary>
public static class LedgerAppendOnlyRefFetcher
{
    private const string StagingNamespace = "refs/hall9k-staging/";
    private const string VerifiedNamespace = "refs/hall9k-verified/";

    /// <summary>The all-zero object id <c>update-ref &lt;ref&gt; &lt;new&gt; &lt;old&gt;</c> reads as
    /// "this ref must not already exist" — passed as the old value on a create, so a slower
    /// concurrent caller can never overwrite a ref a faster one already created or advanced.</summary>
    private const string NullObjectId = "0000000000000000000000000000000000000000";

    /// <summary>Fetches one append-only ref fresh and decides whether to move the live and verified
    /// refs, refuse, or simply report nothing there yet.</summary>
    public static async Task<LedgerAppendOnlyFetchResult> FetchAsync(
        ProcessRunner runner, string repositoryPath, string refName, CancellationToken cancellationToken)
    {
        RequireAppendOnly(refName);
        string path = PathFor(refName);
        string stagingRef = $"{StagingNamespace}{Guid.NewGuid():N}/{path}";

        string? fetchedTip;
        try
        {
            ProcessResult fetch = await runner(
                "git", ["fetch", "origin", "--no-write-fetch-head", $"+{refName}:{stagingRef}"],
                repositoryPath, cancellationToken);
            if (fetch.ExitCode == 0)
            {
                fetchedTip = await ResolveTipQuietAsync(runner, repositoryPath, stagingRef, cancellationToken);
            }
            else if (fetch.StandardError.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase))
            {
                fetchedTip = null;
            }
            else
            {
                throw new LedgerFetchFailedException(refName, fetch.StandardError);
            }
        }
        finally
        {
            // CancellationToken.None, deliberately: a cancelled caller (a daemon shutdown, say) must
            // not skip this cleanup and leave the staging ref behind in the shared bare repository
            // forever, holding its fetched objects reachable (independent pre-PR review, cycle 1,
            // conformance lens, low).
            await DeleteRefBestEffortAsync(runner, repositoryPath, stagingRef, CancellationToken.None);
        }

        return await DecideAndApplyAsync(runner, repositoryPath, refName, path, fetchedTip, cancellationToken);
    }

    /// <summary>
    /// Fetches every ref under <paramref name="prefix"/> named by <paramref name="suffixes"/> in one
    /// round trip, by a single wildcard refspec into a per-call staging prefix, then decides each one
    /// independently exactly as <see cref="FetchAsync"/> would. <paramref name="suffixes"/> is the
    /// caller's own responsibility to have unioned with whatever this node's local
    /// <c>refs/hall9k-verified/</c> tree already holds under this prefix — this method has no way to
    /// notice a suffix that origin has stopped listing on its own. A genuine failure fetching the
    /// whole prefix throws before any per-suffix decision runs, so a partial failure never leaves one
    /// live or verified ref moved while a sibling's own fetch never even landed.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, LedgerAppendOnlyFetchResult>> FetchPrefixAsync(
        ProcessRunner runner, string repositoryPath, string prefix, IReadOnlyList<string> suffixes,
        CancellationToken cancellationToken)
    {
        RequireAppendOnly(prefix);
        if (suffixes.Count == 0)
        {
            return new Dictionary<string, LedgerAppendOnlyFetchResult>();
        }

        string path = PathFor(prefix);
        string stagingPrefix = $"{StagingNamespace}{Guid.NewGuid():N}/{path}";
        try
        {
            ProcessResult fetch = await runner(
                "git", ["fetch", "origin", "--no-write-fetch-head", $"+{prefix}*:{stagingPrefix}*"],
                repositoryPath, cancellationToken);
            if (fetch.ExitCode != 0)
            {
                throw new LedgerFetchFailedException(prefix, fetch.StandardError);
            }

            Dictionary<string, LedgerAppendOnlyFetchResult> results = [];
            foreach (string suffix in suffixes)
            {
                string? fetchedTip = await ResolveTipQuietAsync(runner, repositoryPath, $"{stagingPrefix}{suffix}", cancellationToken);
                results[suffix] = await DecideAndApplyAsync(
                    runner, repositoryPath, $"{prefix}{suffix}", $"{path}{suffix}", fetchedTip, cancellationToken);
            }

            return results;
        }
        finally
        {
            // CancellationToken.None, deliberately — see FetchAsync's own identical cleanup.
            await DeleteStagedPrefixBestEffortAsync(runner, repositoryPath, stagingPrefix, CancellationToken.None);
        }
    }

    /// <summary>Every suffix this node's own local <c>refs/hall9k-verified/</c> tree already holds
    /// under <paramref name="prefix"/> — a caller (<c>GitLedgerChainReader</c>'s own owners and nodes
    /// discovery) unions this with whatever a fresh <c>ls-remote</c> just listed, so a ref origin has
    /// since deleted is still named as the missing-remote-ref refusal it is, rather than silently
    /// dropped from discovery the moment origin no longer lists it.</summary>
    public static async Task<IReadOnlyList<string>> DiscoverLocallyVerifiedSuffixesAsync(
        ProcessRunner runner, string repositoryPath, string prefix, CancellationToken cancellationToken)
    {
        RequireAppendOnly(prefix);
        string verifiedPrefix = $"{VerifiedNamespace}{PathFor(prefix)}";
        ProcessResult result = await runner(
            "git", ["for-each-ref", "--format=%(refname)", $"{verifiedPrefix}*"], repositoryPath, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git for-each-ref {verifiedPrefix}* failed in {repositoryPath}: {result.StandardError.Trim()}");
        }

        return [.. result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(name => name.StartsWith(verifiedPrefix, StringComparison.Ordinal))
            .Select(name => name[verifiedPrefix.Length..])];
    }

    /// <summary>
    /// Advances the verified ref to <paramref name="newTip"/>, a commit the caller's own write just
    /// got origin to accept, compare-and-swapped from <paramref name="previousTip"/> — the tip that
    /// write was built on. This is the one move a write itself has to make on its own behalf: every
    /// other caller here only ever fetches, so <see cref="DecideAndApplyAsync"/>'s own compare-and-
    /// swap covers it, but a writer never re-fetches before building the next thing on top of what
    /// it just pushed, and without this call a rewind of that write reads back as an ordinary
    /// no-change fetch the next time anything on this node touches the ref (independent pre-PR
    /// review, cycle 2, both lenses, high). Best-effort, the same as every other verified-ref move
    /// in this class: losing the compare-and-swap only ever means a concurrent caller on this same
    /// node already advanced the verified ref at least this far forward.
    /// </summary>
    public static async Task AdvanceVerifiedRefAfterOwnWriteAsync(
        ProcessRunner runner, string repositoryPath, string refName, string? previousTip, string newTip,
        CancellationToken cancellationToken)
    {
        RequireAppendOnly(refName);
        string verifiedRefName = $"{VerifiedNamespace}{PathFor(refName)}";
        await UpdateRefBestEffortAsync(
            runner, repositoryPath, verifiedRefName, newTip, previousTip ?? NullObjectId, cancellationToken);
    }

    private static async Task<LedgerAppendOnlyFetchResult> DecideAndApplyAsync(
        ProcessRunner runner, string repositoryPath, string refName, string path, string? fetchedTip,
        CancellationToken cancellationToken)
    {
        string verifiedRefName = $"{VerifiedNamespace}{path}";
        string? localLiveTip = await ResolveTipQuietAsync(runner, repositoryPath, refName, cancellationToken);
        bool verifiedRefExists = await ShowRefExistsAsync(runner, repositoryPath, verifiedRefName, cancellationToken);

        if (!verifiedRefExists)
        {
            if (localLiveTip is not null)
            {
                // Seed the trust anchor from what this node already had, rather than from whatever
                // origin's own fetch just brought down: a fleet that had already rewound before this
                // check shipped must not be re-trusted the moment it happens to fetch fresh content on
                // this same call. The next call is the first one that actually verifies a fetch
                // against this baseline. The null object id as the old value makes this create-only
                // (independent pre-PR review, cycle 1, both lenses, low): without it, a slower
                // concurrent caller could still overwrite a verified ref a faster one had already
                // seeded or fast-forwarded past, moving the trust anchor backward.
                await UpdateRefBestEffortAsync(runner, repositoryPath, verifiedRefName, localLiveTip, oldValue: NullObjectId, cancellationToken);
                return new LedgerAppendOnlyFetchResult(localLiveTip, false, fetchedTip, localLiveTip, null);
            }

            if (fetchedTip is null)
            {
                return new LedgerAppendOnlyFetchResult(null, false, null, null, null);
            }

            // Nothing local to prefer: a fresh clone or a new joiner adopts whatever origin holds.
            // Create-only, for the identical reason the seed above is.
            await UpdateRefBestEffortAsync(runner, repositoryPath, refName, fetchedTip, oldValue: NullObjectId, cancellationToken);
            await UpdateRefBestEffortAsync(runner, repositoryPath, verifiedRefName, fetchedTip, oldValue: NullObjectId, cancellationToken);
            return new LedgerAppendOnlyFetchResult(fetchedTip, false, fetchedTip, fetchedTip, null);
        }

        string? verifiedTip = await ResolveTipQuietAsync(runner, repositoryPath, verifiedRefName, cancellationToken);
        if (verifiedTip is null)
        {
            // The ref survived but the object it names did not — corruption, never an ordinary
            // "nothing here yet": the verified ref is exactly what keeps that object reachable.
            return new LedgerAppendOnlyFetchResult(
                localLiveTip, true, fetchedTip, null, BuildCorruptionReason(path, refName, fetchedTip));
        }

        if (fetchedTip is null)
        {
            return new LedgerAppendOnlyFetchResult(
                localLiveTip ?? verifiedTip, true, null, verifiedTip, BuildMissingRemoteReason(path, refName, verifiedTip));
        }

        if (fetchedTip == verifiedTip)
        {
            return new LedgerAppendOnlyFetchResult(fetchedTip, false, fetchedTip, verifiedTip, null);
        }

        bool verifiedIsOnChain = await IsOnFirstParentChainAsync(runner, repositoryPath, fetchedTip, verifiedTip, cancellationToken);
        if (!verifiedIsOnChain)
        {
            return new LedgerAppendOnlyFetchResult(
                verifiedTip, true, fetchedTip, verifiedTip, BuildRewindReason(path, refName, fetchedTip, verifiedTip));
        }

        await UpdateRefBestEffortAsync(runner, repositoryPath, refName, fetchedTip, localLiveTip, cancellationToken);
        await UpdateRefBestEffortAsync(runner, repositoryPath, verifiedRefName, fetchedTip, verifiedTip, cancellationToken);
        return new LedgerAppendOnlyFetchResult(fetchedTip, false, fetchedTip, fetchedTip, null);
    }

    private static void RequireAppendOnly(string refName)
    {
        if (LedgerRefRegistry.TryGetEntry(refName) is { AppendOnly: false })
        {
            throw new ArgumentException(
                $"{refName} is registered as squash-and-force-push, not append-only — "
                + $"{nameof(LedgerAppendOnlyRefFetcher)} must never run its rewind check against it.",
                nameof(refName));
        }
    }

    private static string PathFor(string refName) =>
        refName.StartsWith(LedgerRefRegistry.Namespace, StringComparison.Ordinal)
            ? refName[LedgerRefRegistry.Namespace.Length..]
            : throw new ArgumentException($"{refName} does not start with {LedgerRefRegistry.Namespace}.", nameof(refName));

    /// <summary>Whether <paramref name="refName"/> exists in the ref store at all, independent of
    /// whether the object it names still resolves — <c>show-ref --verify</c> exits 128 ("bad ref"),
    /// not the "not found" 1, the moment the ref's own commit object is missing, which made the
    /// corruption branch below dead code (independent pre-PR review, cycle 1, conformance lens,
    /// medium): a verified ref whose object was lost to local corruption looked identical to no
    /// verified ref ever existing, so the next fetch silently re-seeded or adopted origin instead of
    /// refusing. <c>for-each-ref</c> only ever reads the ref store, never the object it points at.
    /// </summary>
    private static async Task<bool> ShowRefExistsAsync(
        ProcessRunner runner, string repositoryPath, string refName, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner("git", ["for-each-ref", "--format=%(refname)", refName], repositoryPath, cancellationToken);
        return result.ExitCode == 0 && result.StandardOutput.Trim() == refName;
    }

    private static async Task<string?> ResolveTipQuietAsync(
        ProcessRunner runner, string repositoryPath, string refName, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner(
            "git", ["rev-parse", "--verify", "--quiet", $"{refName}^{{commit}}"], repositoryPath, cancellationToken);
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
    }

    private static async Task<bool> IsOnFirstParentChainAsync(
        ProcessRunner runner, string repositoryPath, string fetchedTip, string candidateTip, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner(
            "git", ["rev-list", "--first-parent", fetchedTip], repositoryPath, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git rev-list --first-parent {fetchedTip} failed in {repositoryPath}: {result.StandardError.Trim()}");
        }

        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Contains(candidateTip, StringComparer.Ordinal);
    }

    /// <summary>Moves a ref unconditionally when <paramref name="oldValue"/> is null — only ever
    /// passed that way when this call's own caller has no old value of its own to compare against —
    /// or compare-and-swapping it into place otherwise
    /// (<c>update-ref &lt;ref&gt; &lt;new&gt; &lt;old&gt;</c>, <see cref="NullObjectId"/> included, which
    /// reads as "must not already exist") so a slow concurrent reader on this same node can never
    /// move it backward. Best-effort: losing the race here only ever means a concurrent caller
    /// already advanced this ref to at least as far forward as this call itself would have — never a
    /// data-integrity problem, so this call never throws over it.</summary>
    private static async Task UpdateRefBestEffortAsync(
        ProcessRunner runner, string repositoryPath, string refName, string newValue, string? oldValue,
        CancellationToken cancellationToken)
    {
        List<string> arguments = ["update-ref", refName, newValue];
        if (oldValue is not null)
        {
            arguments.Add(oldValue);
        }

        try
        {
            await runner("git", arguments, repositoryPath, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A losing race, or a transient local git failure — either way this call's own result
            // already reflects what it decided; nothing here is worth failing the caller over.
        }
    }

    private static async Task DeleteRefBestEffortAsync(
        ProcessRunner runner, string repositoryPath, string refName, CancellationToken cancellationToken)
    {
        try
        {
            await runner("git", ["update-ref", "-d", refName], repositoryPath, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Best-effort cleanup of a staging ref; nothing downstream ever reads it again.
        }
    }

    private static async Task DeleteStagedPrefixBestEffortAsync(
        ProcessRunner runner, string repositoryPath, string stagingPrefix, CancellationToken cancellationToken)
    {
        try
        {
            ProcessResult listed = await runner(
                "git", ["for-each-ref", "--format=%(refname)", $"{stagingPrefix}*"], repositoryPath, cancellationToken);
            foreach (string refName in listed.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                await DeleteRefBestEffortAsync(runner, repositoryPath, refName.Trim(), cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Best-effort cleanup of staging refs; nothing downstream ever reads them again.
        }
    }

    private static string BuildCorruptionReason(string path, string refName, string? fetchedTip) =>
        $"the last verified tip for {refName} cannot be resolved locally on this node (its object is "
        + $"missing or corrupt), so the freshly fetched tip {fetchedTip ?? "(none)"} is refused rather "
        + "than trusted blind. Repair by clearing the stale marker on this node "
        + $"(git -C <bare> update-ref -d refs/hall9k-verified/{path}) and letting the next read re-seed "
        + "it from this node's own local live ref, or from origin if this node has none.";

    private static string BuildMissingRemoteReason(string path, string refName, string verifiedTip) =>
        $"origin no longer holds {refName} (last verified tip {verifiedTip}); this node keeps its local "
        + $"copy and keeps reading it rather than treating the absence as though nothing was ever there. "
        + $"If the ref was deliberately deleted, clear the marker to match: git -C <bare> update-ref -d "
        + $"refs/hall9k-verified/{path}. Otherwise this is unexpected and worth investigating.";

    private static string BuildRewindReason(string path, string refName, string fetchedTip, string verifiedTip) =>
        $"{refName} was fetched at {fetchedTip}, but the last verified tip {verifiedTip} is not on that "
        + "commit's own first-parent chain (a rewind, or a side merge whose first parent is the old tip "
        + "and second parent the current one). Refused; this node keeps reading the verified tip. Repair "
        + $"with 'git push origin refs/hall9k-verified/{path}:{refName}' (no plus sign, fast-forward only, "
        + "safe to run from any node: an older tip pushed by a node that is behind is itself seen as a "
        + "rewind by every current node and pushed forward again, converging on the newest). git "
        + "rejecting that push as diverged is a human decision: reconcile which history is correct, or, "
        + "for a legitimate owner rewrite such as purging a leaked secret, run "
        + $"'git -C <bare> update-ref -d refs/hall9k-verified/{path}' on each node to accept it as the new baseline.";
}
