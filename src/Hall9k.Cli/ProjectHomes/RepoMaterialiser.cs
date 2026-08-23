using Hall9k.Cli.DaemonControl;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Cli.ProjectHomes;

/// <summary>
/// The <c>repo/</c> half of a project home: a bare clone of the project's remote with the fetch
/// refspec corrected, a <c>dev/</c> worktree on the primary branch, and nothing else needed
/// before dispatch can cut <c>wt-*</c> worktrees beside them.
/// <para>
/// The recipe is the one backlog 43 specified and this task absorbed. The step that matters and
/// is easy to miss is the refspec: <c>git clone --bare</c> writes
/// <c>+refs/heads/*:refs/heads/*</c>, which maps every remote branch onto a <em>local</em>
/// branch. A worktree cut from that has no upstream, <c>origin/main</c> does not exist to branch
/// <c>--no-track</c> from, and the repository fills with local branches nobody asked for. Setting
/// <c>+refs/heads/*:refs/remotes/origin/*</c> and deleting the local branches the bare clone
/// invented makes it behave exactly like an ordinary checkout.
/// </para>
/// <para>
/// It always materialises fresh from the recorded remote. An existing clone elsewhere on the
/// machine is inconsequential — git is distributed, including across one disk — and moving work
/// out of an old location is the cutover chore's agent-assisted job, never this recipe's.
/// </para>
/// </summary>
public static class RepoMaterialiser
{
    /// <summary>
    /// Materialises (or repairs, or refreshes) <c>repo/</c> under <paramref name="home"/>.
    /// Idempotent: an already-cloned repository is reported and left alone, never re-cloned; a
    /// missing <c>dev/</c> worktree beside an existing clone is created rather than treated as a
    /// reason to start over, stale registration and all; and <c>dev/</c> is brought onto the
    /// remote's primary branch whether it was found there or cut a moment ago, so the command
    /// that repairs a home is also the command that stops its checkout serving code from months
    /// ago.
    /// </summary>
    public static async Task<IReadOnlyList<ProjectHomeStep>> MaterialiseAsync(
        string home,
        string projectName,
        Uri? remote,
        string baseBranch,
        CancellationToken cancellationToken)
    {
        List<ProjectHomeStep> steps = [];
        string bare = ProjectHomePaths.BareRepository(home, projectName);
        string dev = ProjectHomePaths.DevWorktree(home);
        bool cloned = false;

        if (await GitAbsentAsync(cancellationToken))
        {
            steps.Add(ProjectHomeStep.Failed(
                "git is not on the PATH, so repo/ was not materialised. Install git and re-run "
                + "h9k project init; the full prerequisite check is h9k doctor's job (backlog 24)."));
            return steps;
        }

        if (Directory.Exists(Path.Combine(bare, "objects")))
        {
            steps.Add(ProjectHomeStep.AlreadyThere($"repo/ already materialised at {bare} — left alone, not re-cloned"));
        }
        else if (remote is null)
        {
            steps.Add(ProjectHomeStep.Skipped(
                "no remote is recorded for this project, so there is nothing to clone. Register the "
                + "project with --repo-url, or point --repo at a repository that already exists here."));
            return steps;
        }
        else if (await CloneAsync(bare, remote, steps, cancellationToken) is false)
        {
            return steps;
        }
        else
        {
            cloned = true;
        }

        // Both paths below decide what dev/ holds by reading this clone's refs, so the refs are
        // brought forward first. A clone that was left alone above may not have been fetched since
        // the day it was made: the refresh compares against refs/remotes/origin/<base>, and a
        // fresh worktree add resolves <base> against the same refs — or picks up the local branch
        // an earlier dev/ left behind, which is older still. CloneAsync fetches as its last step,
        // so a clone made a moment ago is not fetched a second time.
        string against = cloned
            ? $"origin/{baseBranch}"
            : await FetchAsync(bare, baseBranch, cancellationToken);

        if (Directory.Exists(Path.Combine(dev, ".git")) || File.Exists(Path.Combine(dev, ".git")))
        {
            steps.Add(Report(
                $"repo/dev already checked out at {dev}",
                await AlignToRemoteAsync(dev, baseBranch, against, cancellationToken),
                createdWhenUnmoved: false));
            return steps;
        }

        // A dev/ that was deleted off disk is still registered in the bare clone's worktree list,
        // and git refuses to create it again while that registration stands ("is a missing but
        // already registered worktree"). Pruning first is what makes h9k project init the repair
        // command it claims to be, rather than a step that fails identically on every re-run with
        // no documented way out. Prune only drops registrations whose directory is gone, so the
        // wt-* worktrees dispatch is working in are untouched. Origin incident (2026-08-23): the
        // second cycle of this branch's pre-PR review deleted repo/dev, ran the documented repair,
        // and got a Failed step blaming the base branch — forever.
        await GitAsync(cancellationToken, "-C", bare, "worktree", "prune");

        ExecResult worktree = await GitAsync(cancellationToken, "-C", bare, "worktree", "add", dev, baseBranch);
        if (!worktree.Succeeded)
        {
            steps.Add(ProjectHomeStep.Failed(
                $"the bare clone is in place at {bare} but the dev/ worktree on '{baseBranch}' could not be "
                + $"created: {GitSaid(worktree)}. Stale worktree registrations were pruned first, so this is "
                + $"the branch or the destination rather than a leftover: check that '{baseBranch}' is this "
                + "project's base branch (h9k project show), that no other worktree under "
                + $"{ProjectHomePaths.RepoDirectory(home)} already has it checked out, and that {dev} is not "
                + "a non-empty directory belonging to something else. Fix whichever it is, then re-run "
                + "h9k project init."));
            return steps;
        }

        // git worktree add resolves the base branch inside this clone, and that is not the same
        // thing as the remote: an earlier dev/ that was deleted leaves refs/heads/<base> behind at
        // whatever commit it was abandoned on, and the recreated worktree lands on that. So the
        // new checkout is aligned exactly like an existing one is, which is what makes the recreate
        // path perform the same catch-up h9k project init promises rather than quietly serving the
        // old commit under a step that reads as a successful repair.
        steps.Add(Report(
            $"repo/dev checked out on {baseBranch} at {dev}",
            await AlignToRemoteAsync(dev, baseBranch, against, cancellationToken),
            createdWhenUnmoved: true));
        return steps;
    }

    /// <summary>
    /// Updates this clone's view of the remote, and answers with the name a step should use for
    /// it. A fetch that did not get through is named in that answer rather than swallowed: what
    /// the comparisons below then measure is the last state this machine saw, and calling that
    /// "up to date" would be a guess.
    /// </summary>
    private static async Task<string> FetchAsync(string bare, string baseBranch, CancellationToken cancellationToken)
    {
        ExecResult fetch = await GitAsync(cancellationToken, "-C", bare, "fetch", "origin");
        return fetch.Succeeded
            ? $"origin/{baseBranch}"
            : $"origin/{baseBranch} as this machine last saw it (the fetch did not get through: {GitSaid(fetch)})";
    }

    /// <summary>
    /// What bringing <c>dev/</c> onto the remote's primary branch managed.
    /// </summary>
    /// <param name="Moved">The checkout was fast-forwarded, so something on disk changed.</param>
    /// <param name="Blocked">
    /// The remote is ahead and the checkout could not be taken there — local work is in the way.
    /// </param>
    /// <param name="Detail">The clause a step's message ends with, in past tense.</param>
    private sealed record DevAlignment(bool Moved, bool Blocked, string Detail);

    /// <summary>
    /// Brings <c>dev/</c> onto the remote's primary branch, and says what it found either way.
    /// <para>
    /// It matters because <c>dev/</c> is not decoration. It is the working directory a reading
    /// session is spawned into (<c>ProjectCheckout.ForReading</c>) and the checkout the generated
    /// <c>AGENTS.md</c> sends a human to, so a checkout made once and never touched again serves
    /// a months-old copy of the project's own skills to an agent that was told to read them from
    /// the repository it is standing in. Origin incident (2026-08-23): the second cycle of this
    /// branch's pre-PR review traced exactly that through <c>h9k task push-to-jira</c> and found
    /// nothing anywhere reporting the checkout was behind; the third cycle found the recreate
    /// path checking a stale local branch out with the same silence.
    /// </para>
    /// <para>
    /// Fast-forward only, and never a failure. Local commits or uncommitted changes under
    /// <c>dev/</c> are somebody's, and this is a refresh rather than a reset; when one blocks the
    /// update, the step says so and names the commit the checkout is actually serving, because a
    /// stale checkout nobody is told about is the whole defect.
    /// </para>
    /// </summary>
    private static async Task<DevAlignment> AlignToRemoteAsync(
        string dev, string baseBranch, string against, CancellationToken cancellationToken)
    {
        string remote = $"refs/remotes/origin/{baseBranch}";

        ExecResult counted = await GitAsync(cancellationToken, "-C", dev, "rev-list", "--count", $"HEAD..{remote}");
        if (!counted.Succeeded || !int.TryParse(counted.StandardOutput.Trim(), out int behind))
        {
            return new DevAlignment(
                Moved: false,
                Blocked: false,
                $"it could not be compared against origin/{baseBranch} ({GitSaid(counted)}), so whether "
                + "it holds current code is unobserved rather than assumed");
        }

        if (behind == 0)
        {
            return new DevAlignment(Moved: false, Blocked: false, $"already up to date with {against}");
        }

        ExecResult merge = await GitAsync(cancellationToken, "-C", dev, "merge", "--ff-only", remote);
        if (merge.Succeeded)
        {
            return new DevAlignment(Moved: true, Blocked: false, $"fast-forwarded {behind} commit(s) to {against}");
        }

        ExecResult head = await GitAsync(cancellationToken, "-C", dev, "rev-parse", "--short", "HEAD");
        string serving = head.Succeeded ? head.StandardOutput.Trim() : "a commit git would not name";
        return new DevAlignment(
            Moved: false,
            Blocked: true,
            $"{behind} commit(s) behind {against}, and left exactly as it is: {GitSaid(merge)}. "
            + $"Anything reading code there — a person, or the session h9k task push-to-jira spawns — reads "
            + $"it as of {serving}. Commit, stash or discard what is in {dev} (or reconcile the branch by "
            + "hand where it has diverged), then re-run h9k project init.");
    }

    /// <summary>
    /// One step out of what the caller did and what the alignment managed. <paramref name="here"/>
    /// is the caller's own half — "already checked out" or "checked out on main" — because the two
    /// paths did different things to arrive at the same alignment.
    /// </summary>
    /// <param name="createdWhenUnmoved">
    /// Whether an alignment that moved nothing still counts as this step having made something.
    /// True for the path that just cut the worktree, false for the one that found it already there.
    /// </param>
    private static ProjectHomeStep Report(string here, DevAlignment alignment, bool createdWhenUnmoved) =>
        alignment switch
        {
            { Blocked: true } => ProjectHomeStep.Skipped($"{here}, {alignment.Detail}"),
            { Moved: true } => ProjectHomeStep.Created($"{here}, {alignment.Detail}"),
            _ when createdWhenUnmoved => ProjectHomeStep.Created($"{here}, {alignment.Detail}"),
            _ => ProjectHomeStep.AlreadyThere($"{here}, {alignment.Detail}"),
        };

    /// <summary>
    /// The clone and the configuration that has to follow it, as one unit: a bare clone whose
    /// refspec was never corrected is worse than no clone at all, because it looks finished.
    /// A failed clone takes its own half-written directory with it so a re-run starts clean — but
    /// only when this invocation is the one that made that directory. <c>git clone --bare</c>
    /// refuses outright to write into a destination that already exists and is not empty, and
    /// leaves it untouched when it does; deleting it anyway would destroy somebody else's
    /// directory (a manual cutover's checkout, an operator's own directory, a concurrent
    /// <c>h9k project init</c> still populating it) on a clone that never wrote to it at all.
    /// </summary>
    private static async Task<bool> CloneAsync(
        string bare, Uri remote, List<ProjectHomeStep> steps, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(bare)!);
        bool ownsDestination = !Directory.Exists(bare);

        ExecResult clone = await GitAsync(cancellationToken, "clone", "--bare", remote.ToString(), bare);
        if (!clone.Succeeded)
        {
            if (ownsDestination && Directory.Exists(bare))
            {
                Directory.Delete(bare, recursive: true);
            }

            steps.Add(ProjectHomeStep.Failed(DescribeFailedClone(remote, clone)));
            return false;
        }

        await GitAsync(cancellationToken, "-C", bare, "config", "remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*");
        await GitAsync(cancellationToken, "-C", bare, "config", "core.logAllRefUpdates", "true");
        await GitAsync(cancellationToken, "-C", bare, "config", "push.default", "simple");

        // Windows path length is a repository-local setting, so it has to be set on every clone
        // rather than once on the machine; a deep node_modules or an obj/ tree under a worktree
        // is what trips it, and it trips at checkout rather than at clone (backlog 43).
        if (OperatingSystem.IsWindows())
        {
            await GitAsync(cancellationToken, "-C", bare, "config", "core.longpaths", "true");
        }

        await DeleteInventedLocalBranchesAsync(bare, cancellationToken);
        await GitAsync(cancellationToken, "-C", bare, "fetch", "origin");

        steps.Add(ProjectHomeStep.Created(
            $"repo/ bare-cloned from {remote} into {bare}, fetch refspec mapped into refs/remotes/origin/"));
        return true;
    }

    /// <summary>
    /// The local branches <c>git clone --bare</c> created before the refspec was corrected. They
    /// are the reason a bare clone behaves unlike a checkout: a worktree add would silently pick
    /// one of them up instead of branching from <c>origin/&lt;branch&gt;</c>.
    /// </summary>
    private static async Task DeleteInventedLocalBranchesAsync(string bare, CancellationToken cancellationToken)
    {
        ExecResult listed = await GitAsync(
            cancellationToken, "-C", bare, "for-each-ref", "--format=%(refname:short)", "refs/heads/");
        foreach (string branch in listed.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            await GitAsync(cancellationToken, "-C", bare, "branch", "-D", branch);
        }
    }

    /// <summary>
    /// A failed clone is nearly always credentials, and the fix is one command the operator has
    /// probably run before on another machine — so it is named here rather than left to be
    /// guessed at, per the CLI standard that a failure teaches its own correction.
    /// </summary>
    private static string DescribeFailedClone(Uri remote, ExecResult clone)
    {
        string detail = GitSaid(clone);
        bool looksLikeAuthentication =
            detail.Contains("Authentication", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("could not read Username", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("access rights", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("not found", StringComparison.OrdinalIgnoreCase);

        return looksLikeAuthentication
            ? $"could not clone {remote}: {detail}. Credentials come from this machine's own git "
              + "credential helper — for a GitHub remote, 'gh auth login' then 'gh auth setup-git' "
              + "installs one. h9k doctor (backlog 24) owns the full prerequisite check."
            : $"could not clone {remote}: {detail}";
    }

    private static async Task<bool> GitAbsentAsync(CancellationToken cancellationToken)
    {
        try
        {
            return !(await GitAsync(cancellationToken, "--version")).Succeeded;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return true;
        }
    }

    private static Task<ExecResult> GitAsync(CancellationToken cancellationToken, params string[] arguments) =>
        Exec.RunAsync("git", arguments, cancellationToken);

    /// <summary>
    /// git says the useful thing on the last line of stderr and the noise ("Cloning into…") on
    /// the first, so the report quotes the last non-empty line rather than the first.
    /// </summary>
    private static string GitSaid(ExecResult result)
    {
        string[] lines = [.. (result.StandardError.IsNotBlank() ? result.StandardError : result.StandardOutput)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        return lines.Length > 0 ? lines[^1] : $"git exited {result.ExitCode} with nothing to say";
    }
}
