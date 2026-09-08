using System.Text.RegularExpressions;
using Hall9k.Connectors.Processes;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Daemon.Review;

/// <summary>What the mechanical renumbering step actually did, for the caller to log.</summary>
public enum DecisionsLogRenumberOutcome
{
    /// <summary>Nothing at the Decisions Log's tail needed this branch's own rebase to act on it.</summary>
    NoActionNeeded,

    /// <summary>The tail entry was reassigned a real number and every citation of its prior token was rewritten.</summary>
    Renumbered,
}

/// <summary>The result of one <see cref="DecisionsLogRenumberer.RenumberIfNeededAsync"/> call.</summary>
public sealed record DecisionsLogRenumberResult(
    DecisionsLogRenumberOutcome Outcome,
    string? OldToken,
    int? NewNumber,
    int FilesRewritten);

/// <summary>
/// The mechanical pre-final-pass rebase step's own half of the placeholder-numbering convention
/// (Decisions Log #PLACEHOLDER-6df5f975; the idea's origin is Windows's 24940ccd of 2026-09-07,
/// drafted here after three Windows branches and two Mac branches all wrote #148 the same
/// afternoon). A branch writes its own PLAN.md §16 entry
/// under a placeholder derived from its task's short id
/// (<see cref="DecisionsLogPlaceholder"/>) rather than guessing the log's true next number and
/// racing every other branch reading the same tail. <c>ReviewEngine.EnsureRebasedBeforeFinalPassAsync</c>
/// calls <see cref="RenumberIfNeededAsync"/> right there, before the mandatory gate re-runs, for
/// every outcome that leaves the branch current with its base — a clean rebase, a stuck-pipe
/// rebase confirmed landed, and the no-op case where the base had not moved at all (which is also
/// the shape a conflict already resolved by hand takes once the loop re-enters here) — not only a
/// clean apply. No agent session ever picks the number or edits a citation by hand.
/// <para>
/// <b>Two shapes, one outcome.</b> The ordinary shape is a placeholder entry belonging to THIS
/// branch's own task sitting at the log's tail: it is unconditionally assigned the next free
/// number. The transition shape is a branch cut before this convention shipped, which chose a
/// real number by hand at write time and now collides with an entry that reached the base after
/// this branch's own fork point — the exact parallel-merge shape merge-time assignment exists to
/// prevent, just caught one rebase later than a placeholder would have been. That second shape is
/// deliberately narrow: it fires only when the branch's own colliding entry sits at the tail AND
/// the number it collides with was absent from PLAN.md at the branch's own fork point (so both
/// occurrences are provably independent, parallel additions rather than one branch reusing a
/// number that was already taken when it was written) — any other duplicate shape is a genuine
/// hand-numbering mistake and is left for <c>DecisionsLogNumberingGuardTests</c> to fail exactly
/// as it always has, never silently papered over.
/// </para>
/// <para>
/// <b>The next number.</b> The highest real (non-placeholder) entry number anywhere in the
/// current section, plus one — read fresh off the rebased tree, never cached, so two entries
/// from the same install racing this step in sequence still land dense and gap-free.
/// </para>
/// </summary>
public static class DecisionsLogRenumberer
{
    private const string PlanMarkdownFileName = "PLAN.md";
    private const string SectionStartHeading = "## 16. v0 Decisions Log";
    private const string SectionEndHeadingPrefix = "## 17.";
    private const string SectionDivider = "---";

    private static readonly Regex RealEntryHeadingPattern = new(@"^(\d+)\. \*\*", RegexOptions.Compiled);
    private static readonly string[] ExcludedDirectoryNames = [".git", "bin", "obj", "node_modules"];

    /// <summary>
    /// Renumbers the Decisions Log's tail entry in <paramref name="worktreePath"/>'s PLAN.md, if
    /// it needs it, and commits the rewrite as its own commit. <paramref name="forkPointSha"/> is
    /// the merge-base this branch's own rebase computed <b>before</b> it ran — the fork point the
    /// transition shape's "was this number already taken when the branch wrote it" test reads
    /// against. <paramref name="baseTipSha"/> is the base branch's own tip <b>after</b> this
    /// branch is current with it (the base commit this branch's own commits now sit on top of) —
    /// the revision the citation sweep's "is this line the base's own, or this branch's" test
    /// reads against, which is deliberately not <paramref name="forkPointSha"/>: content the base
    /// added after the fork point but before this call is the base's own, never this branch's,
    /// even though it is equally absent from the fork point. <paramref name="taskShortId"/> is
    /// this run's own task's short id (<c>DomainId.Short(context.TaskId)</c>) — the only
    /// placeholder this call may ever assign a number to, since a placeholder belonging to some
    /// OTHER task is that task's own branch's job, not this one's.
    /// </summary>
    public static async Task<DecisionsLogRenumberResult> RenumberIfNeededAsync(
        ProcessRunner git,
        string worktreePath,
        string forkPointSha,
        string baseTipSha,
        string taskShortId,
        CancellationToken cancellationToken)
    {
        string planPath = Path.Combine(worktreePath, PlanMarkdownFileName);
        if (!File.Exists(planPath))
        {
            return NoAction();
        }

        string planText = await File.ReadAllTextAsync(planPath, cancellationToken);
        (string[] lines, string planNewline, bool planTrailingNewline) = SplitPreservingLineEnding(planText);
        (int sectionStart, int sectionEnd) = FindSection(lines);
        if (sectionStart < 0 || sectionEnd < 0)
        {
            return NoAction();
        }

        TailScan scan = ScanTail(lines, sectionStart, sectionEnd);
        if (scan.TailLine < 0)
        {
            return NoAction();
        }

        string oldCitationToken;
        if (scan.TailIsPlaceholder)
        {
            if (scan.TailToken != taskShortId)
            {
                // The tail placeholder belongs to a different task — only that task's own
                // branch's rebase may assign it a number.
                return NoAction();
            }

            oldCitationToken = DecisionsLogPlaceholder.TokenFor(taskShortId);
        }
        else
        {
            int tailNumber = int.Parse(scan.TailToken);
            if (!scan.RealEntryLinesByNumber.TryGetValue(tailNumber, out List<int>? duplicateLines)
                || duplicateLines.Count < 2)
            {
                // Not a duplicate at all — the ordinary case for a branch cut after this
                // convention shipped, which never wrote a real number itself.
                return NoAction();
            }

            // Only the transition shape ever reads forkPointSha or baseTipSha — the ordinary
            // placeholder shape above never calls ReadFileAtRevisionAsync at all — so the check is
            // scoped here rather than before the caller even knows which shape it has: a caller
            // whose baseTipSha could not be resolved (most often
            // ReviewEngine.ResolveObservedOntoCommitAsync's own UnreadableCommit fallback after a
            // stuck output pipe) must never reach RewriteCitationsAsync's transition branch, which
            // hands baseTipSha to git as a literal revision — and by the time that call would
            // throw, this method has already rewritten PLAN.md's heading to disk, leaving a
            // half-applied rewrite for the mandatory final pass to read (independent pre-PR
            // review, cycle 3, adversarial lens). Checked before ANY write below, not only before
            // this one read, so a bad forkPointSha is caught here on the identical terms.
            if (forkPointSha == RunRebasedOntoBase.UnreadableCommit || baseTipSha == RunRebasedOntoBase.UnreadableCommit)
            {
                return NoAction();
            }

            string forkPointPlan = await ReadFileAtRevisionAsync(
                git, worktreePath, forkPointSha, PlanMarkdownFileName, cancellationToken);
            if (ContainsRealEntryNumber(forkPointPlan, tailNumber))
            {
                // The number was already taken at this branch's own fork point — a hand-numbering
                // mistake, not a parallel merge. Left for the guard to fail.
                return NoAction();
            }

            oldCitationToken = tailNumber.ToString();
        }

        int newNumber = scan.MaxRealNumber + 1;

        // The heading is rewritten and committed to disk BEFORE the citation sweep runs, and the
        // placement note is inserted only AFTER it, in a second write — never in the same pass.
        // The transition-shape note's own text names the old number (`#{oldCitationToken}`), and
        // the sweep below cannot tell that mention apart from a genuine citation once it is on
        // disk; writing it after the sweep has already run is what keeps the sweep from rewriting
        // the very sentence recording what it did (independent pre-PR review, cycle 1, both
        // lenses). This also preserves the entry's own body: unlike a full rebuild that assumes a
        // one-line entry, the region between the old heading and the boundary is carried forward
        // untouched, in full, including any placement note already there — whether hand-authored
        // under the repo's own pre-existing convention or left by an earlier run of this same
        // mechanical step — since this pass's own note is always appended after it, never
        // overwriting older history (independent pre-PR review, cycle 3, conformance lens: an
        // earlier version of this pass matched the marker text and discarded whatever followed it,
        // which destroyed hand-authored, multi-paragraph provenance carried by 24 existing entries).
        (string[] headingOnlyLines, int insertionIndex) = RewriteHeadingPreservingBody(
            lines, scan, sectionEnd, newNumber, taskShortId, oldCitationToken);
        await File.WriteAllTextAsync(
            planPath, JoinPreservingLineEnding(headingOnlyLines, planNewline, planTrailingNewline), cancellationToken);

        List<string> citationFilesRewritten = await RewriteCitationsAsync(
            git, worktreePath, baseTipSha, taskShortId, oldCitationToken, newNumber, scan.TailIsPlaceholder,
            cancellationToken);

        string postSweepPlanText = await File.ReadAllTextAsync(planPath, cancellationToken);
        (string[] postSweepLines, _, _) = SplitPreservingLineEnding(postSweepPlanText);
        string[] finalLines = InsertPlacementNote(
            postSweepLines, insertionIndex, newNumber, taskShortId, scan.TailIsPlaceholder, oldCitationToken);
        await File.WriteAllTextAsync(
            planPath, JoinPreservingLineEnding(finalLines, planNewline, planTrailingNewline), cancellationToken);

        string commitMessage = scan.TailIsPlaceholder
            ? $"chore: assign Decisions Log #{newNumber} to placeholder PLACEHOLDER-{taskShortId}"
            : $"chore: renumber Decisions Log #{oldCitationToken} to #{newNumber}";
        await RunGitAsync(
            git, worktreePath, ["add", "--", PlanMarkdownFileName, .. citationFilesRewritten], cancellationToken);
        await RunGitAsync(git, worktreePath, ["commit", "-m", commitMessage], cancellationToken);

        return new DecisionsLogRenumberResult(DecisionsLogRenumberOutcome.Renumbered, oldCitationToken, newNumber, citationFilesRewritten.Count);

        static DecisionsLogRenumberResult NoAction() => new(DecisionsLogRenumberOutcome.NoActionNeeded, null, null, 0);
    }

    private readonly record struct TailScan(
        int TailLine,
        string TailToken,
        bool TailIsPlaceholder,
        int MaxRealNumber,
        int DividerLine,
        Dictionary<int, List<int>> RealEntryLinesByNumber);

    private static (int Start, int End) FindSection(string[] lines)
    {
        int sectionStart = Array.FindIndex(lines, line => line.StartsWith(SectionStartHeading, StringComparison.Ordinal));
        if (sectionStart < 0)
        {
            return (-1, -1);
        }

        int sectionEnd = Array.FindIndex(
            lines, sectionStart + 1, line => line.StartsWith(SectionEndHeadingPrefix, StringComparison.Ordinal));
        return sectionEnd > sectionStart ? (sectionStart, sectionEnd) : (-1, -1);
    }

    private static TailScan ScanTail(string[] lines, int sectionStart, int sectionEnd)
    {
        int tailLine = -1;
        string tailToken = "";
        bool tailIsPlaceholder = false;
        int maxRealNumber = 0;
        Dictionary<int, List<int>> realEntryLinesByNumber = [];

        for (int i = sectionStart + 1; i < sectionEnd; i++)
        {
            Match placeholderMatch = DecisionsLogPlaceholder.EntryHeadingPattern.Match(lines[i]);
            if (placeholderMatch.Success)
            {
                tailLine = i;
                tailToken = placeholderMatch.Groups[1].Value;
                tailIsPlaceholder = true;
                continue;
            }

            Match realMatch = RealEntryHeadingPattern.Match(lines[i]);
            if (!realMatch.Success)
            {
                continue;
            }

            int number = int.Parse(realMatch.Groups[1].Value);
            maxRealNumber = Math.Max(maxRealNumber, number);
            if (!realEntryLinesByNumber.TryGetValue(number, out List<int>? entryLines))
            {
                entryLines = [];
                realEntryLinesByNumber[number] = entryLines;
            }

            entryLines.Add(i);
            tailLine = i;
            tailToken = number.ToString();
            tailIsPlaceholder = false;
        }

        int dividerLine = -1;
        if (tailLine >= 0)
        {
            for (int i = tailLine + 1; i < sectionEnd; i++)
            {
                if (lines[i].Trim() == SectionDivider)
                {
                    dividerLine = i;
                    break;
                }
            }
        }

        return new TailScan(tailLine, tailToken, tailIsPlaceholder, maxRealNumber, dividerLine, realEntryLinesByNumber);
    }

    private static bool ContainsRealEntryNumber(string planMarkdown, int number)
    {
        string[] lines = NormalizeLineEndings(planMarkdown).Split('\n');
        (int sectionStart, int sectionEnd) = FindSection(lines);
        if (sectionStart < 0)
        {
            return false;
        }

        for (int i = sectionStart + 1; i < sectionEnd; i++)
        {
            Match match = RealEntryHeadingPattern.Match(lines[i]);
            if (match.Success && int.Parse(match.Groups[1].Value) == number)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Rewrites just the tail entry's own heading (its leading token to the new number) and
    /// returns, alongside the rewritten lines, the line index where
    /// <see cref="InsertPlacementNote"/> should later insert the freshly generated note. Unlike a
    /// full rebuild that assumes a one-line entry, everything between the heading and the section
    /// boundary is carried forward untouched, because a Decisions Log entry routinely runs to
    /// several blank-line-separated paragraphs (e.g. #59) and discarding them would silently
    /// destroy authored decision text — including any placement note already sitting in that span,
    /// hand-authored or left by an earlier run of this same mechanical step: nothing in that span
    /// is ever dropped, only appended to, by <see cref="InsertPlacementNote"/>.
    /// </summary>
    private static (string[] Lines, int InsertionIndex) RewriteHeadingPreservingBody(
        string[] lines, TailScan scan, int sectionEnd, int newNumber, string taskShortId, string oldCitationToken)
    {
        // The heading regex anchors at the start of the line (^), so the token to drop is always
        // the line's own leading substring — never searched for, since a real number can appear
        // as a substring elsewhere in a long entry's own prose.
        string oldHeadingToken = scan.TailIsPlaceholder ? DecisionsLogPlaceholder.TokenFor(taskShortId) : oldCitationToken;
        string rewrittenHeading = newNumber.ToString() + lines[scan.TailLine][oldHeadingToken.Length..];

        // Everything from the divider onward (or, lacking one, from the section-closing heading
        // onward) is preserved verbatim.
        int boundaryLine = scan.DividerLine >= 0 ? scan.DividerLine : sectionEnd;

        int bodyStart = scan.TailLine + 1;
        int bodyEnd = boundaryLine;
        while (bodyEnd > bodyStart && lines[bodyEnd - 1].Trim().Length == 0)
        {
            bodyEnd--;
        }

        var rebuilt = new List<string>(lines.Length);
        rebuilt.AddRange(lines[..scan.TailLine]);
        rebuilt.Add(rewrittenHeading);
        rebuilt.AddRange(lines[bodyStart..bodyEnd]);
        int insertionIndex = rebuilt.Count;
        rebuilt.AddRange(lines[boundaryLine..]);

        return ([.. rebuilt], insertionIndex);
    }

    /// <summary>
    /// Inserts the freshly generated placement note at <paramref name="insertionIndex"/> — always
    /// called AFTER <see cref="RewriteCitationsAsync"/> has already run and rewritten this same
    /// file on disk, so the note's own mention of the old number (transition shape only) is never
    /// itself mistaken for a citation to rewrite.
    /// </summary>
    private static string[] InsertPlacementNote(
        string[] lines, int insertionIndex, int newNumber, string taskShortId, bool tailIsPlaceholder, string oldCitationToken)
    {
        string[] placementNote = tailIsPlaceholder
            ?
            [
                $"> Renumbering placement note: this entry was appended under placeholder",
                $"> `PLACEHOLDER-{taskShortId}` and assigned **#{newNumber}** by the mechanical pre-final-pass",
                "> rebase step — the log's next free number once this branch was rebased onto its base.",
                "> Every citation of the placeholder elsewhere in this repository was rewritten to",
                $"> `#{newNumber}` in the same commit.",
            ]
            :
            [
                $"> Renumbering placement note: this entry carried #{oldCitationToken}, which collided with",
                "> an entry that reached the base after this branch's own fork point. The mechanical",
                $"> pre-final-pass rebase step reassigned it to **#{newNumber}**, the log's next free number,",
                $"> and rewrote every citation of #{oldCitationToken} this branch itself had added since its",
                $"> fork point to `#{newNumber}` in the same commit.",
            ];

        var rebuilt = new List<string>(lines.Length + placementNote.Length + 2);
        rebuilt.AddRange(lines[..insertionIndex]);
        rebuilt.Add("");
        rebuilt.AddRange(placementNote);
        rebuilt.Add("");
        rebuilt.AddRange(lines[insertionIndex..]);

        return [.. rebuilt];
    }

    private static async Task<List<string>> RewriteCitationsAsync(
        ProcessRunner git,
        string worktreePath,
        string baseTipSha,
        string taskShortId,
        string oldCitationToken,
        int newNumber,
        bool tailIsPlaceholder,
        CancellationToken cancellationToken)
    {
        Regex citationPattern = tailIsPlaceholder
            ? new Regex(Regex.Escape(DecisionsLogPlaceholder.CitationFor(taskShortId)) + "\\b")
            : new Regex($"(?<!\\d)#{Regex.Escape(oldCitationToken)}(?!\\d)");
        string newCitation = $"#{newNumber}";

        List<string> filesRewritten = [];
        foreach (string path in EnumerateTextFiles(worktreePath))
        {
            // PLAN.md is deliberately NOT skipped: its own tail entry heading was already
            // rewritten and re-written to disk above, so re-scanning it here is safe (the
            // heading carries no leading '#', so the citation pattern never matches it), and it
            // is exactly what catches another entry's own prose citing this one by number.
            string relativePath = Path.GetRelativePath(worktreePath, path);

            string content;
            try
            {
                content = await File.ReadAllTextAsync(path, cancellationToken);
            }
            catch (IOException)
            {
                continue;
            }

            if (!citationPattern.IsMatch(content))
            {
                continue;
            }

            bool changed;
            string rewritten;
            if (tailIsPlaceholder)
            {
                // The placeholder token is unique to this branch's own task, so every citation of
                // it anywhere in the repository is this branch's own — no fork-point filter needed.
                rewritten = citationPattern.Replace(content, newCitation);
                changed = rewritten != content;
            }
            else
            {
                // A real number's citations are ambiguous by construction (that is the collision
                // itself) — only a citation line this branch itself added is this branch's own to
                // rewrite. That line's OWN branch is what "added" means here, so ownership is
                // decided against baseTipSha — the base's tip once this branch is current with it
                // — never forkPointSha: content the base added between the fork point and now is
                // the base's own, even though, like this branch's own additions, it is equally
                // absent from the fork point. Reading against the fork point here would treat the
                // base's own newly-landed citations for the SAME colliding number as this
                // branch's, and rewrite them to point at this branch's decision instead
                // (independent pre-PR review, cycle 1, conformance lens).
                (string[] contentLines, string contentNewline, bool contentTrailingNewline) =
                    SplitPreservingLineEnding(content);
                string baseTipContent = await ReadFileAtRevisionAsync(
                    git, worktreePath, baseTipSha, relativePath, cancellationToken);
                HashSet<string> baseTipLines = [.. SplitPreservingLineEnding(baseTipContent).Lines];
                bool anyLineChanged = false;
                for (int i = 0; i < contentLines.Length; i++)
                {
                    if (citationPattern.IsMatch(contentLines[i]) && !baseTipLines.Contains(contentLines[i]))
                    {
                        contentLines[i] = citationPattern.Replace(contentLines[i], newCitation);
                        anyLineChanged = true;
                    }
                }

                rewritten = JoinPreservingLineEnding(contentLines, contentNewline, contentTrailingNewline);
                changed = anyLineChanged;
            }

            if (changed)
            {
                await File.WriteAllTextAsync(path, rewritten, cancellationToken);
                filesRewritten.Add(relativePath);
            }
        }

        return filesRewritten;
    }

    private static IEnumerable<string> EnumerateTextFiles(string root)
    {
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, path);
            string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(segment => ExcludedDirectoryNames.Contains(segment)))
            {
                continue;
            }

            yield return path;
        }
    }

    private static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n");

    /// <summary>Splits text into lines without losing what its own line ending or trailing newline were, so a rewrite can restore them rather than forcing every file to LF.</summary>
    private static (string[] Lines, string Newline, bool TrailingNewline) SplitPreservingLineEnding(string text)
    {
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string normalized = NormalizeLineEndings(text);
        bool trailingNewline = normalized.Length > 0 && normalized[^1] == '\n';
        string body = trailingNewline ? normalized[..^1] : normalized;
        string[] lines = body.Length == 0 ? [] : body.Split('\n');
        return (lines, newline, trailingNewline);
    }

    private static string JoinPreservingLineEnding(IReadOnlyList<string> lines, string newline, bool trailingNewline)
    {
        string content = string.Join(newline, lines);
        return trailingNewline ? content + newline : content;
    }

    /// <summary>
    /// Reads <paramref name="relativePath"/> as it stood at <paramref name="revision"/>. Git's
    /// <c>&lt;rev&gt;:&lt;path&gt;</c> syntax walks the tree on forward slashes only, so
    /// <paramref name="relativePath"/> is normalized to them regardless of the OS this runs on —
    /// on Windows, an OS-separated path here would make every <c>git show</c> call fail and, left
    /// undistinguished from genuine absence, silently treat every line in the file as newly added
    /// (independent pre-PR review, cycle 1, both lenses). A path genuinely absent at that
    /// revision — this branch (or the base) added the file itself since — is the one failure this
    /// method is entitled to swallow as empty; any other git failure is surfaced rather than
    /// coerced into the same "nothing was there" reading, since the caller's ownership test
    /// depends on that distinction being honest.
    /// </summary>
    private static async Task<string> ReadFileAtRevisionAsync(
        ProcessRunner git, string worktreePath, string revision, string relativePath, CancellationToken cancellationToken)
    {
        string treePath = relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        ProcessResult result = await git("git", ["show", $"{revision}:{treePath}"], worktreePath, cancellationToken);
        if (result.ExitCode == 0)
        {
            return result.StandardOutput;
        }

        if (result.StandardError.Contains("does not exist in", StringComparison.Ordinal)
            || result.StandardError.Contains("exists on disk, but not in", StringComparison.Ordinal))
        {
            return "";
        }

        throw new InvalidOperationException(
            $"git show {revision}:{treePath} failed in {worktreePath}: {result.StandardError}");
    }

    private static async Task RunGitAsync(
        ProcessRunner git, string worktreePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ProcessResult result = await git("git", arguments, worktreePath, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed in {worktreePath}: {result.StandardError}");
        }
    }
}
