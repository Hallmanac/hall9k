using System.Globalization;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Worktrees;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The worktree <c>h9k task drive</c> cuts for a task whose pull request is open: the same
/// <see cref="IWorktreeManager.CheckoutExistingAsync"/> on the task's follow-up branch that a headless
/// follow-up lap uses, so the claim reuses the retained worktree or recreates it, fast-forwards a clean
/// tree that is behind origin, keeps a tree that is ahead of origin or holds uncommitted work, and resets
/// a clean tree to origin only when origin holds a tip the branch never held. That behavior belongs to the
/// worktree manager and is pinned there; what this adds is the one line <c>drive</c> owes the person at the
/// terminal afterwards when the checkout left the tree behind origin, or reset it, because either changes
/// what they are about to build on.
/// </summary>
internal static class PullRequestStageCheckout
{
    /// <summary>The checked-out worktree, and the one-line notice <c>drive</c> prints when the checkout left the tree behind origin or reset it (null when it did neither).</summary>
    public sealed record Result(Worktree Worktree, string? Notice);

    public static async Task<Result> CheckoutAsync(
        IWorktreeManager worktrees, ProcessRunner git, string repositoryPath, string branch, Guid taskId, Guid runId,
        CancellationToken cancellationToken)
    {
        // Read before the checkout touches anything: a reset replaces the local tip, and this is the only
        // place the tip it replaced can still be told apart from the one that replaced it.
        string? before = await RevParseAsync(git, repositoryPath, $"refs/heads/{branch}", cancellationToken);
        Worktree worktree = await worktrees.CheckoutExistingAsync(
            new FollowUpWorktreeRequest(repositoryPath, branch, taskId, runId), cancellationToken);

        string? head = await RevParseAsync(git, worktree.Path, "HEAD", cancellationToken);
        string? origin = await RevParseAsync(git, worktree.Path, $"refs/remotes/origin/{branch}", cancellationToken);
        return new Result(worktree, await NoticeAsync(git, worktree.Path, branch, before, head, origin, cancellationToken));
    }

    private static async Task<string?> NoticeAsync(
        ProcessRunner git, string worktreePath, string branch, string? before, string? head, string? origin,
        CancellationToken cancellationToken)
    {
        // Nothing is said about a fact that could not be read: an unreadable ref is not a tree that is up to date.
        if (head is null || origin is null)
        {
            return null;
        }

        if (head == origin)
        {
            bool replaced = before is not null && before != head
                && await IsAncestorAsync(git, worktreePath, before, head, cancellationToken) == false;
            return replaced
                ? $"The worktree was reset to origin/{branch} ({Short(head)}): origin held a tip the branch never held, "
                  + $"so the local tip {Short(before!)} was replaced. It stays in the branch's reflog."
                : null;
        }

        if (await IsAncestorAsync(git, worktreePath, origin, head, cancellationToken) != false)
        {
            // Ahead of origin: unpushed work the checkout keeps. An unreadable ancestry is not reported either way.
            return null;
        }

        int behind = await CountAsync(git, worktreePath, $"{head}..{origin}", cancellationToken);
        int ahead = await CountAsync(git, worktreePath, $"{origin}..{head}", cancellationToken);
        string where = ahead > 0
            ? $"has diverged from origin/{branch} ({ahead} commit(s) here that origin lacks, {behind} on origin that this tree lacks)"
            : $"is behind origin/{branch} by {behind} commit(s)";
        return $"The worktree {where} and the checkout left it that way, because it holds uncommitted work or commits "
            + "origin never had. Bring it up to date yourself before you deliver, or the push guard will refuse.";
    }

    private static async Task<string?> RevParseAsync(
        ProcessRunner git, string workingDirectory, string reference, CancellationToken cancellationToken)
    {
        ProcessResult result = await git(
            "git", ["rev-parse", "--verify", "--quiet", $"{reference}^{{commit}}"], workingDirectory, cancellationToken);
        string tip = result.StandardOutput.Trim();
        return result.ExitCode == 0 && tip.Length > 0 ? tip : null;
    }

    /// <summary>True, false, or null when git could not answer, which is neither.</summary>
    private static async Task<bool?> IsAncestorAsync(
        ProcessRunner git, string workingDirectory, string ancestor, string descendant, CancellationToken cancellationToken)
    {
        ProcessResult result = await git(
            "git", ["merge-base", "--is-ancestor", ancestor, descendant], workingDirectory, cancellationToken);
        return result.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => null,
        };
    }

    private static async Task<int> CountAsync(
        ProcessRunner git, string workingDirectory, string range, CancellationToken cancellationToken)
    {
        ProcessResult result = await git("git", ["rev-list", "--count", range], workingDirectory, cancellationToken);
        return result.ExitCode == 0 && int.TryParse(result.StandardOutput.Trim(), CultureInfo.InvariantCulture, out int count)
            ? count
            : 0;
    }

    private static string Short(string commit) => commit.Length > 10 ? commit[..10] : commit;
}
