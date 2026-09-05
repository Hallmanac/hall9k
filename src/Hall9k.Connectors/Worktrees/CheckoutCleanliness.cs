using System.ComponentModel;
using System.Diagnostics;

namespace Hall9k.Connectors.Worktrees;

/// <summary>
/// Whether a checkout is actually clean and on the branch a clean-base gate comparison is about
/// to describe it as ("a clean checkout of '&lt;base branch&gt;'") — observed, never assumed.
/// <see cref="IWorktreeManager.RefreshReadingCheckoutAsync"/>'s own <c>UpToDate</c> answer says
/// only that the checkout's commits match <c>origin/&lt;branch&gt;</c>; it says nothing about
/// uncommitted modifications, untracked files, or a checkout sitting on some other branch (or
/// detached) entirely — and the ordinary-clone checkout a project registered before homes existed
/// points at was never asked at all (independent pre-PR review, cycle 1, both lenses: an
/// operator's own half-finished edit in that checkout, or in the home's shared <c>repo/dev</c>,
/// used to be reported as "the gate also fails on a clean checkout" — a false statement about the
/// gate rather than an honest one about the checkout it actually ran against).
/// </summary>
public static class CheckoutCleanliness
{
    /// <summary>
    /// Null when <paramref name="checkout"/> is confirmed on <paramref name="expectedBranch"/>
    /// with nothing modified or untracked; otherwise a short, human-readable reason it could not
    /// be confirmed — a fact to say out loud alongside whatever the checkout is used for next,
    /// never a reason by itself to refuse or to suppress that next step (AGENTS.md's "never guess
    /// at unobserved facts": the unobserved is said plainly, not silently assumed either way).
    /// </summary>
    public static async Task<string?> DescribeNotConfirmedCleanAsync(
        string checkout, string expectedBranch, CancellationToken cancellationToken)
    {
        (int branchExit, string branchOutput) = await RunGitAsync(
            checkout, ["branch", "--show-current"], cancellationToken);
        if (branchExit != 0)
        {
            return "could not confirm which branch is checked out";
        }

        string currentBranch = branchOutput.Trim();
        if (currentBranch != expectedBranch)
        {
            return currentBranch.Length == 0
                ? $"is not on '{expectedBranch}' (detached HEAD)"
                : $"is on '{currentBranch}', not '{expectedBranch}'";
        }

        (int statusExit, string statusOutput) = await RunGitAsync(
            checkout, ["status", "--porcelain", "-z", "--untracked-files=all"], cancellationToken);
        if (statusExit != 0)
        {
            return "could not confirm the working tree is clean";
        }

        (IReadOnlyList<string> modified, IReadOnlyList<string> untracked) = WorktreeGitStatus.ParsePorcelain(statusOutput);
        return modified.Count > 0 || untracked.Count > 0
            ? $"carries {modified.Count} modified and {untracked.Count} untracked file(s)"
            : null;
    }

    private static async Task<(int ExitCode, string StandardOutput)> RunGitAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return (-1, string.Empty);
        }

        // Both streams are read concurrently with the wait, not just stdout: a git command chatty
        // enough on stderr (a warning per file, say) can fill that redirected pipe and block
        // WaitForExitAsync forever if nothing is draining it (independent pre-PR review, cycle 2,
        // adversarial lens — Copilot).
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await standardError;
        return (process.ExitCode, await standardOutput);
    }
}
