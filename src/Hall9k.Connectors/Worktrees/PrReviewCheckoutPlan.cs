namespace Hall9k.Connectors.Worktrees;

/// <summary>One git invocation of <see cref="PrReviewCheckoutPlan"/>: the directory it runs in and its arguments after <c>-C</c>.</summary>
public sealed record PrReviewCheckoutStep(string WorkingDirectory, string Arguments);

/// <summary>
/// The git commands that cut a pull-request review checkout as a standalone clone, kept apart from
/// the code that runs them so a test can pin the exact sequence without a process.
/// <para>
/// The checkout is its own repository with a real <c>.git</c> directory, borrowing every object
/// from the project's repository through <c>objects/info/alternates</c> (written between the first
/// two steps, see <see cref="AlternatesFile"/>) and so costing almost no disk. It has no remote and
/// no hooks. A linked worktree is not used because Claude Code's built-in read-only git allowance
/// treats a <c>.git</c> file pointing into a bare repository as plantable and refuses the command
/// under <c>dontAsk</c>, which left a review session unable to run <c>git show</c> or <c>git grep</c>;
/// a real <c>.git</c> directory passes that gate.
/// </para>
/// </summary>
public static class PrReviewCheckoutPlan
{
    /// <summary>
    /// Where the checkout learns about the project repository's objects. A file rather than a git
    /// command because git has no command that writes one.
    /// </summary>
    public static string AlternatesFile(string checkoutPath) =>
        Path.Combine(checkoutPath, ".git", "objects", "info", "alternates");

    /// <summary>
    /// Every pin lives under this prefix in the project's repository, one ref per checkout.
    /// </summary>
    public const string PinRefPrefix = "refs/hall9k/pr-review-checkouts/";

    /// <summary>
    /// The ref in the <em>project's</em> repository that keeps a checkout's head commit reachable for
    /// as long as the checkout stands. The checkout owns no objects, so nothing it holds is visible
    /// to the project repository's garbage collection: git's own <c>clone --shared</c> documentation
    /// warns about exactly this. A linked worktree was safe because git treats its detached
    /// <c>HEAD</c> as a root; this ref is the same protection, and it sits outside
    /// <c>refs/remotes/origin/</c> so the <c>fetch --prune origin</c> a merged-branch closeout runs
    /// cannot take it. Named for the checkout's directory, which is unique per task and run.
    /// <para>
    /// It pins the head only. The other remote-tracking refs and tags copied into the checkout are
    /// not pinned, so one deleted upstream and pruned from the project repository after git's
    /// two-week expiry stops resolving in the checkout (<c>git log --all</c> reports a bad object);
    /// the pull request's own head and its base are unaffected, and those are what a review reads.
    /// </para>
    /// </summary>
    public static string PinRef(string checkoutPath) =>
        PinRefPrefix + Path.GetFileName(Path.TrimEndingDirectorySeparator(checkoutPath));

    /// <summary>
    /// The four steps, in order. <paramref name="sourceRepositoryPath"/> is the project's
    /// repository, bare or ordinary; <paramref name="trackingRef"/> is the ref the pull request's
    /// head was already fetched into there.
    /// <list type="number">
    /// <item><c>init</c> with an empty template, so the new repository holds no hooks.</item>
    /// <item>
    /// <c>core.longpaths</c> set in the checkout's own config. A linked worktree inherited it from
    /// the project's repository (<c>RepoMaterialiser</c> sets it there because Windows path length is
    /// a repository-local setting), but a fresh <c>git init</c> starts with an empty config, so a
    /// tracked path past the Windows limit would fail the checkout step. Set in the config rather than
    /// passed as <c>-c</c> so the review session's own later git sees it too. Harmless elsewhere.
    /// </item>
    /// <item>
    /// One fetch from the project's repository by path (no remote is configured) of every
    /// remote-tracking ref it holds, which carries the pull request's head and <c>origin/&lt;base&gt;</c>,
    /// and of its tags. Automatic tag following is off because the refspec already names them.
    /// </item>
    /// <item>
    /// A detached checkout of the pull request's head with Git LFS smudging switched off: there is
    /// no remote to download large files from, so the pointer files stay pointers.
    /// </item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<PrReviewCheckoutStep> Steps(
        string checkoutPath, string sourceRepositoryPath, string trackingRef) =>
    [
        new(sourceRepositoryPath, $"init --quiet --template= \"{checkoutPath}\""),
        new(checkoutPath, "config core.longpaths true"),
        new(checkoutPath,
            $"-c gc.auto=0 fetch --quiet --no-tags \"{sourceRepositoryPath}\" "
            + "\"+refs/remotes/*:refs/remotes/*\" \"+refs/tags/*:refs/tags/*\""),
        new(checkoutPath,
            "-c filter.lfs.smudge= -c filter.lfs.process= -c filter.lfs.required=false "
            + $"checkout --quiet --detach {trackingRef}"),
    ];
}
