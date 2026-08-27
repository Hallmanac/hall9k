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
/// <see cref="Omissions"/> names every touched file the packet promises "full current text" for
/// but does not actually carry, and why — a deleted file has no text on disk, a binary file is
/// never decoded, an unreadable one hit an I/O error, and a file whose own text would have pushed
/// the running total past <see cref="ReviewPacketAssembler.MaxPacketBytes"/> is skipped rather
/// than charged against the budget of every file after it. Recorded so the prompt's "unless noted
/// otherwise" is a true statement rather than an unqualified promise the packet quietly breaks.
/// </para>
/// </summary>
public sealed record ReviewPacket(
    string RangeDescription,
    string Diff,
    IReadOnlyList<string> TouchedFiles,
    IReadOnlyDictionary<string, string> FileContents,
    IReadOnlyList<FileOmission> Omissions);

/// <summary>A touched file the packet's per-file text section omits, and the reason it does.</summary>
public sealed record FileOmission(string Path, FileOmissionReason Reason);

/// <summary>Why <see cref="ReviewPacketAssembler"/> left a touched file's text out of the packet.</summary>
public enum FileOmissionReason
{
    /// <summary>The file no longer exists on disk (deleted, or renamed away).</summary>
    Deleted,

    /// <summary>Git's own NUL-byte heuristic flagged the file as binary.</summary>
    Binary,

    /// <summary>Reading the file threw an I/O or permission error.</summary>
    Unreadable,

    /// <summary>
    /// The file's own on-disk text would have pushed the packet past its size cap. Only this one
    /// file is skipped — every other touched file is still weighed against whatever budget
    /// remains, so one oversized file never costs the files that come after it in the touched list.
    /// </summary>
    TooLarge,
}

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
    /// bytes. This bounds the packet's worst-case cost; it does not promise to cover every
    /// task's full file set. A task that touches a large repo-doctrine file — this repository's
    /// own PLAN.md alone is over 350,000 bytes — has that one file skipped as a
    /// <see cref="FileOmissionReason.TooLarge"/> omission rather than embedded (adversarial
    /// review, cycle 2); every other touched file is still weighed against its own remaining
    /// share of the cap, since a file too large for the budget on its own is not a reason to
    /// deny the budget to the files after it.
    /// </summary>
    public const long MaxPacketBytes = 300_000;

    public static async Task<ReviewPacket?> AssembleAsync(
        string worktreePath, string baseBranch, string? sinceSha, CancellationToken cancellationToken)
    {
        (string? diff, string range) = sinceSha is { } sha
            ? (await RunGitAsync(worktreePath, ["diff", $"{sha}..HEAD"], MaxPacketBytes, cancellationToken), $"{sha}..HEAD")
            : await DiffAgainstBaseAsync(worktreePath, baseBranch, cancellationToken);
        if (diff is null)
        {
            // Git is unobservable here, neither ref resolved, or the diff alone already broke
            // the packet's own size ceiling (RunGitAsync bounds the read to MaxPacketBytes so an
            // oversized diff is never buffered into memory just to be measured and discarded) —
            // either way, ship no packet; the caller falls back to AppendReviewMechanics's own
            // `git diff` instruction (conformance and adversarial review, cycle 1).
            return null;
        }

        // -z: a NUL-terminated, unquoted list, the same treatment
        // VerificationRunner.ListUncommittedFilesAsync already gives `git status` — without it,
        // core.quotePath's default C-quotes any non-ASCII path and the quoted, unopenable name
        // then fails File.Exists below, silently dropping that file's text from the packet.
        string? nameOnly = await RunGitAsync(worktreePath, ["diff", "--name-only", "-z", range], cancellationToken);
        if (nameOnly is null)
        {
            // The diff itself was readable but the touched-file enumeration was not — never
            // render that as "zero files touched"; an unobserved list stays unobserved rather
            // than standing in for an empty one (conformance review, cycle 1).
            return null;
        }

        IReadOnlyList<string> touchedFiles = [.. nameOnly.Split('\0', StringSplitOptions.RemoveEmptyEntries)];

        (Dictionary<string, string> contents, List<FileOmission> omissions) =
            await ReadTouchedFilesAsync(worktreePath, touchedFiles, Encoding.UTF8.GetByteCount(diff), cancellationToken);
        return new ReviewPacket(range, diff, touchedFiles, contents, omissions);
    }

    /// <summary>
    /// Reads each touched file's current worktree text, running total against
    /// <see cref="MaxPacketBytes"/> starting from the diff's own size (the caller already
    /// confirmed the diff alone is under the cap). A file the diff renamed away or deleted is
    /// recorded as a <see cref="FileOmissionReason.Deleted"/> omission and skipped: the diff
    /// itself already shows the removal, and there is no current text on disk to embed. A
    /// probably-binary file is skipped the same way, and skipped *before* its on-disk length is
    /// weighed against the remaining budget (cycle-2 conformance and adversarial review): its
    /// lossy UTF-8 decode would be unusable noise in the prompt, and charging its length to the
    /// budget would degrade the whole packet over a file that was never going to be embedded in
    /// the first place. A file that survives the binary check but whose own length (pre-read) or
    /// own decoded size (post-read) would push the running total past the cap is recorded as a
    /// <see cref="FileOmissionReason.TooLarge"/> omission and skipped on its own — the running
    /// total is left unchanged by a skipped file, so every touched file after it is still weighed
    /// against the full remaining budget rather than losing its own chance to fit (conformance
    /// and adversarial review, cycle 1: an early oversized file used to discard every file's text
    /// after it, including files that would have fit).
    /// </summary>
    private static async Task<(Dictionary<string, string> Contents, List<FileOmission> Omissions)> ReadTouchedFilesAsync(
        string worktreePath, IReadOnlyList<string> touchedFiles, long startingSize, CancellationToken cancellationToken)
    {
        long size = startingSize;
        Dictionary<string, string> contents = new();
        List<FileOmission> omissions = new();
        foreach (string file in touchedFiles)
        {
            string fullPath = Path.Combine(worktreePath, file);
            string text;
            try
            {
                if (!File.Exists(fullPath))
                {
                    omissions.Add(new FileOmission(file, FileOmissionReason.Deleted));
                    continue;
                }

                if (await IsProbablyBinaryAsync(fullPath, cancellationToken))
                {
                    omissions.Add(new FileOmission(file, FileOmissionReason.Binary));
                    continue;
                }

                long fileLength = new FileInfo(fullPath).Length;
                if (size + fileLength > MaxPacketBytes)
                {
                    omissions.Add(new FileOmission(file, FileOmissionReason.TooLarge));
                    continue;
                }

                text = await File.ReadAllTextAsync(fullPath, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                omissions.Add(new FileOmission(file, FileOmissionReason.Unreadable));
                continue;
            }

            long textSize = Encoding.UTF8.GetByteCount(text);
            if (size + textSize > MaxPacketBytes)
            {
                omissions.Add(new FileOmission(file, FileOmissionReason.TooLarge));
                continue;
            }

            size += textSize;
            contents[file] = text;
        }

        return (contents, omissions);
    }

    /// <summary>
    /// Git's own binary heuristic — a NUL byte within the first sampled chunk — applied to
    /// avoid decoding a binary touched file (an image, an archive, a compiled artifact) as UTF-8
    /// just to measure or embed it (adversarial review, cycle 1).
    /// </summary>
    private static async Task<bool> IsProbablyBinaryAsync(string fullPath, CancellationToken cancellationToken)
    {
        const int SampleBytes = 8000;
        byte[] buffer = new byte[SampleBytes];
        await using FileStream stream = File.OpenRead(fullPath);
        int read = await stream.ReadAsync(buffer.AsMemory(0, SampleBytes), cancellationToken);
        return Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
    }

    /// <summary>
    /// Tries `origin/{baseBranch}` first, then the local base-branch ref — the same
    /// remote-tracking-preferred convention <c>VerificationRunner.CountBranchCommitsAsync</c> and
    /// <c>GitWorktreeManager.ResolveStartPointAsync</c> already follow (the log #4 convention). A
    /// task worktree is cut with `--no-track` off `origin/{baseBranch}` (AGENTS.md), so its local
    /// base-branch ref, when one exists at all, is shared with the project home's `dev/` worktree
    /// and reflects whatever that last fast-forwarded to — routinely stale relative to the task's
    /// actual base — while `origin/{baseBranch}` is always current. The local ref is only a
    /// fallback for the worktree that carries no `origin/{baseBranch}` at all.
    /// </summary>
    private static async Task<(string? Diff, string Range)> DiffAgainstBaseAsync(
        string worktreePath, string baseBranch, CancellationToken cancellationToken)
    {
        string originRange = $"origin/{baseBranch}...HEAD";
        (string? diff, bool exitedZero) = await RunGitCoreAsync(worktreePath, ["diff", originRange], MaxPacketBytes, cancellationToken);
        if (exitedZero)
        {
            // The origin ref resolved. A null diff here means the read hit MaxPacketBytes, not
            // that the ref failed — falling back to the local ref on an oversized-but-successful
            // origin diff would silently trade a current diff for a stale one (the origin-first
            // fix, Decisions Log #94/cycle-1 review). The caller already treats a null diff as
            // "ship no packet", the same outcome an oversized diff always had.
            return (diff, originRange);
        }

        string localRange = $"{baseBranch}...HEAD";
        return (await RunGitAsync(worktreePath, ["diff", localRange], MaxPacketBytes, cancellationToken), localRange);
    }

    /// <summary>
    /// Same shape as <c>ReviewEngine.GetWorktreeHeadShaAsync</c>: both streams are started and
    /// drained before the exit code decides anything, so a noisy hook or an advice block cannot
    /// block <c>WaitForExitAsync</c> on a full stderr pipe.
    /// </summary>
    private static async Task<string?> RunGitAsync(
        string worktreePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        => (await RunGitCoreAsync(worktreePath, arguments, maxBytes: null, cancellationToken)).Output;

    /// <summary>
    /// Bounds the read to <paramref name="maxBytes"/> (see <see cref="ReadBoundedAsync"/>) so a
    /// diff far larger than the packet's own cap is never buffered in full just to be measured
    /// and discarded — the daemon is a long-lived process shared by every task it dispatches, and
    /// an unbounded `ReadToEndAsync` on a branch that adds a large generated or vendored file
    /// would otherwise spike its memory once per review cycle for a packet that was always going
    /// to be rejected (adversarial review, cycle 1).
    /// </summary>
    private static async Task<string?> RunGitAsync(
        string worktreePath, IReadOnlyList<string> arguments, long maxBytes, CancellationToken cancellationToken)
        => (await RunGitCoreAsync(worktreePath, arguments, maxBytes, cancellationToken)).Output;

    private static async Task<(string? Output, bool ExitedZero)> RunGitCoreAsync(
        string worktreePath, IReadOnlyList<string> arguments, long? maxBytes, CancellationToken cancellationToken)
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
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    UseShellExecute = false,
                },
            };
            foreach (string argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            Task<string?> standardOutput = ReadBoundedAsync(process.StandardOutput, maxBytes, cancellationToken);
            Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            string? output = await standardOutput;
            await standardError;
            return process.ExitCode == 0 ? (output, true) : (null, false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (null, false);
        }
    }

    /// <summary>
    /// Reads a process stream in chunks, tracking the UTF-8 byte count as it goes rather than
    /// after a full <c>ReadToEndAsync</c>: once the running total crosses <paramref name="maxBytes"/>
    /// the buffered text is dropped and every further chunk is discarded rather than accumulated,
    /// so the read never allocates meaningfully more than the cap regardless of how much output
    /// the process still has queued. Draining continues to end-of-stream even after the cap trips
    /// so a full pipe never blocks the writer mid-write, which would otherwise hang
    /// <c>WaitForExitAsync</c>. Returns null exactly when the cap was crossed; <paramref name="maxBytes"/>
    /// null means unbounded (the existing <c>ReadToEndAsync</c> behavior, used for output that is
    /// never large enough to matter).
    /// </summary>
    private static async Task<string?> ReadBoundedAsync(
        StreamReader reader, long? maxBytes, CancellationToken cancellationToken)
    {
        if (maxBytes is null)
        {
            return await reader.ReadToEndAsync(cancellationToken);
        }

        StringBuilder builder = new();
        long bytes = 0;
        bool exceeded = false;
        char[] buffer = new char[81_920];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            if (exceeded)
            {
                continue;
            }

            bytes += Encoding.UTF8.GetByteCount(buffer, 0, read);
            if (bytes > maxBytes)
            {
                exceeded = true;
                builder.Clear();
                continue;
            }

            builder.Append(buffer, 0, read);
        }

        return exceeded ? null : builder.ToString();
    }
}
