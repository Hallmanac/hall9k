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
    /// The three steps, in order. <paramref name="sourceRepositoryPath"/> is the project's
    /// repository, bare or ordinary; <paramref name="trackingRef"/> is the ref the pull request's
    /// head was already fetched into there.
    /// <list type="number">
    /// <item><c>init</c> with an empty template, so the new repository holds no hooks.</item>
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
        new(checkoutPath,
            $"-c gc.auto=0 fetch --quiet --no-tags \"{sourceRepositoryPath}\" "
            + "\"+refs/remotes/*:refs/remotes/*\" \"+refs/tags/*:refs/tags/*\""),
        new(checkoutPath,
            "-c filter.lfs.smudge= -c filter.lfs.process= -c filter.lfs.required=false "
            + $"checkout --quiet --detach {trackingRef}"),
    ];
}
