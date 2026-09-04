using Hall9k.Connectors.Processes;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// The ancestor-or-reflog force-with-lease guard (Decisions Log #104), shared by every path that
/// pushes a task branch whose local history may have been rewritten: <see cref="PullRequestOpener"/>'s
/// own push (a follow-up's narrative-history rebase, or a fresh build session's checkpoint
/// recompose) and the closeout engine's mechanical rebase-onto-base fast path alike. Origin's
/// current tip for the branch is safe to overwrite when it fast-forwards into local HEAD, or when
/// it was ever this branch's own local tip per the branch ref's own reflog — the identical check
/// <c>Hall9k.Connectors.Worktrees.GitWorktreeManager.WasEverLocalHeadAsync</c> uses on the pull
/// side. Anything else is a tip this node has never incorporated — someone else moved the branch —
/// and the push is refused rather than forced.
/// </summary>
internal static class ForceWithLeasePusher
{
    public static async Task PushAsync(
        ProcessRunner processRunner, string worktreePath, string branch, CancellationToken cancellationToken)
    {
        // Asked of origin directly via ls-remote rather than read off the local
        // refs/remotes/origin/<branch> tracking ref: that local ref is only ever refreshed by a
        // fetch and never pruned, so a branch origin has since lost would still read as a stale
        // tip here.
        ProcessResult tip = await processRunner(
            "git", ["ls-remote", "--exit-code", "origin", $"refs/heads/{branch}"], worktreePath, cancellationToken);
        if (tip.ExitCode == 2)
        {
            // Exit code 2 is ls-remote's documented signal that no ref on origin matched. The
            // lease is pinned explicitly to "must not exist yet" rather than left bare: a bare
            // lease with no explicit value still falls back to protecting this node's own
            // (possibly stale) refs/remotes/origin/<branch>, which would reject exactly the push
            // this branch exists to allow.
            await RunOrThrowAsync(
                processRunner, worktreePath, ["push", $"--force-with-lease={branch}:", "origin", branch], cancellationToken);
            return;
        }

        if (tip.ExitCode != 0)
        {
            // Any other nonzero exit (an unreachable remote, a 5xx, a dropped connection) means
            // origin could not be read at all, not that origin is empty. Guessing "nothing there"
            // from a failed read is exactly the unobserved-fact guess AGENTS.md rules out; fail
            // honestly instead.
            throw new InvalidOperationException(
                $"could not read origin's current tip for {branch} (git ls-remote exited {tip.ExitCode}): "
                + $"{tip.StandardError}. This fails the push rather than guessing origin has nothing for "
                + "this branch.");
        }

        string originTip = tip.StandardOutput.Split('\t', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        ProcessResult ancestor = await processRunner(
            "git", ["merge-base", "--is-ancestor", originTip, "HEAD"], worktreePath, cancellationToken);
        bool safe = ancestor.ExitCode == 0;
        if (!safe)
        {
            ProcessResult reflog = await processRunner(
                "git", ["reflog", "show", branch, "--format=%H"], worktreePath, cancellationToken);
            safe = reflog.ExitCode == 0 && reflog.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(originTip, StringComparer.Ordinal);
        }

        if (!safe)
        {
            throw new InvalidOperationException(
                $"origin/{branch} is at {originTip}, a tip this node's own history never held and cannot "
                + "fast-forward into — someone else moved the branch since this node last accounted for "
                + "it. Refusing to force-push over it.");
        }

        await RunOrThrowAsync(
            processRunner, worktreePath,
            ["push", $"--force-with-lease={branch}:{originTip}", "origin", branch], cancellationToken);
    }

    private static async Task RunOrThrowAsync(
        ProcessRunner processRunner,
        string worktreePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ProcessResult result = await processRunner("git", arguments, worktreePath, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} exited {result.ExitCode}: {result.StandardError}");
        }
    }
}
