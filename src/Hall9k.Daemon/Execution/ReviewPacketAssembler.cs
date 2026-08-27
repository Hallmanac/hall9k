using System.Diagnostics;
using System.Text;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// The branch diff plus the full current text of every file it touches, assembled once per
/// review dispatch (task: a dispatched review session starts with the diff already assembled) so
/// a reviewer's first read does not require re-deriving the diff and re-opening every touched
/// file call by call — and re-sending the whole conversation on every one of those turns, which
/// is what multiplies the input tokens a long review session burns (Decisions Log #92's own
/// 576M-input-tokens-in-one-day record). <see cref="RangeDescription"/> is the exact `git diff`
/// argument the packet was built from, printed back into the prompt so the reviewer can
/// reproduce or extend it.
/// <para>
/// <see cref="Degraded"/> is true when the touched files' combined text would have pushed the
/// packet past <see cref="ReviewPacketAssembler.MaxPacketBytes"/>: the platform never truncates
/// a file's content silently, it drops every file's full text and leaves the diff and the file
/// list for the reviewer to read from directly. <see cref="FileContents"/> is null exactly when
/// <see cref="Degraded"/> is true.
/// </para>
/// </summary>
public sealed record ReviewPacket(
    string RangeDescription,
    string Diff,
    IReadOnlyList<string> TouchedFiles,
    IReadOnlyDictionary<string, string>? FileContents,
    bool Degraded);

/// <summary>
/// Assembles a <see cref="ReviewPacket"/> straight from the worktree on disk: a plain `git diff`
/// and a read of each touched file's current text — no different from what a reviewer's own
/// exploration would run, just run once ahead of dispatch instead of once per lens and re-sent
/// on every resumed turn. Read-only throughout, and every fallible step degrades to null or to a
/// smaller packet rather than failing the dispatch: a review session missing its packet still
/// gets the platform's older instruction to read the diff itself
/// (<c>AgentPromptBuilder.AppendReviewMechanics</c>), so an assembly failure costs the token
/// saving, never the review.
/// </summary>
public static class ReviewPacketAssembler
{
    /// <summary>
    /// The packet's total size ceiling, diff plus every touched file's full text, in UTF-8
    /// bytes. Chosen to comfortably cover an ordinary task's diff and files while staying well
    /// short of spending a meaningful share of a context window on the packet alone — a run
    /// whose diff already approaches this on its own was never going to be cheap to review
    /// call-by-call either.
    /// </summary>
    public const long MaxPacketBytes = 300_000;

    public static async Task<ReviewPacket?> AssembleAsync(
        string worktreePath, string baseBranch, string? sinceSha, CancellationToken cancellationToken)
    {
        (string? diff, string range) = sinceSha is { } sha
            ? (await RunGitAsync(worktreePath, ["diff", $"{sha}..HEAD"], cancellationToken), $"{sha}..HEAD")
            : await DiffAgainstBaseAsync(worktreePath, baseBranch, cancellationToken);
        if (diff is null)
        {
            // Git is unobservable here, or neither ref resolved — never guess at a diff that
            // could not be read; the caller falls back to the reviewer's own git commands.
            return null;
        }

        string? nameOnly = await RunGitAsync(worktreePath, ["diff", "--name-only", range], cancellationToken);
        IReadOnlyList<string> touchedFiles = nameOnly is null
            ? []
            : [.. nameOnly.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim())];

        (Dictionary<string, string> contents, bool degraded) =
            await ReadTouchedFilesAsync(worktreePath, touchedFiles, Encoding.UTF8.GetByteCount(diff), cancellationToken);
        return new ReviewPacket(range, diff, touchedFiles, degraded ? null : contents, degraded);
    }

    /// <summary>
    /// Reads each touched file's current worktree text, running total against
    /// <see cref="MaxPacketBytes"/> starting from the diff's own size — a diff alone already over
    /// the cap degrades before a single file is read. A file the diff renamed away or deleted is
    /// silently skipped: the diff itself already shows the removal, and there is no current text
    /// on disk to embed.
    /// </summary>
    private static async Task<(Dictionary<string, string> Contents, bool Degraded)> ReadTouchedFilesAsync(
        string worktreePath, IReadOnlyList<string> touchedFiles, long startingSize, CancellationToken cancellationToken)
    {
        long size = startingSize;
        Dictionary<string, string> contents = new();
        foreach (string file in touchedFiles)
        {
            string fullPath = Path.Combine(worktreePath, file);
            string text;
            try
            {
                if (!File.Exists(fullPath))
                {
                    continue;
                }

                text = await File.ReadAllTextAsync(fullPath, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            size += Encoding.UTF8.GetByteCount(text);
            if (size > MaxPacketBytes)
            {
                return (contents, true);
            }

            contents[file] = text;
        }

        return (contents, false);
    }

    /// <summary>
    /// Tries the local base-branch ref first, then `origin/{baseBranch}` — the same fallback the
    /// review prompt itself already states in prose (<c>AgentPromptBuilder.AppendReviewMechanics</c>):
    /// a task worktree is cut with `--no-track` off `origin/{baseBranch}` (AGENTS.md) and may carry
    /// no local ref of that name at all.
    /// </summary>
    private static async Task<(string? Diff, string Range)> DiffAgainstBaseAsync(
        string worktreePath, string baseBranch, CancellationToken cancellationToken)
    {
        string localRange = $"{baseBranch}...HEAD";
        string? diff = await RunGitAsync(worktreePath, ["diff", localRange], cancellationToken);
        if (diff is not null)
        {
            return (diff, localRange);
        }

        string originRange = $"origin/{baseBranch}...HEAD";
        return (await RunGitAsync(worktreePath, ["diff", originRange], cancellationToken), originRange);
    }

    /// <summary>
    /// Same shape as <c>ReviewEngine.GetWorktreeHeadShaAsync</c>: both streams are started and
    /// drained before the exit code decides anything, so a noisy hook or an advice block cannot
    /// block <c>WaitForExitAsync</c> on a full stderr pipe.
    /// </summary>
    private static async Task<string?> RunGitAsync(
        string worktreePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = worktreePath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            foreach (string argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            string output = await standardOutput;
            await standardError;
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }
}
