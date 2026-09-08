using System.Text.RegularExpressions;
using Hall9k.Connectors.Processes;
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
/// (task: a Decisions Log entry gets its number at merge time, not at write time; the idea's
/// origin is Windows's 24940ccd of 2026-09-07, drafted here after three Windows branches and two
/// Mac branches all wrote #148 the same afternoon). A branch writes its own PLAN.md §16 entry
/// under a placeholder derived from its task's short id
/// (<see cref="DecisionsLogPlaceholder"/>) rather than guessing the log's true next number and
/// racing every other branch reading the same tail. Once <c>ReviewEngine.EnsureRebasedBeforeFinalPassAsync</c>
/// has rebased the branch cleanly onto its current base, it calls
/// <see cref="RenumberIfNeededAsync"/> right there, before the mandatory gate re-runs — no agent
/// session ever picks the number or edits a citation by hand.
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
    /// transition shape's "reached the base after this branch's fork point" test reads against.
    /// <paramref name="taskShortId"/> is this run's own task's short id
    /// (<c>DomainId.Short(context.TaskId)</c>) — the only placeholder this call may ever assign a
    /// number to, since a placeholder belonging to some OTHER task is that task's own branch's
    /// job, not this one's.
    /// </summary>
    public static async Task<DecisionsLogRenumberResult> RenumberIfNeededAsync(
        ProcessRunner git,
        string worktreePath,
        string forkPointSha,
        string taskShortId,
        CancellationToken cancellationToken)
    {
        string planPath = Path.Combine(worktreePath, PlanMarkdownFileName);
        if (!File.Exists(planPath))
        {
            return NoAction();
        }

        string[] lines = await File.ReadAllLinesAsync(planPath, cancellationToken);
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
        string[] rewrittenLines = RewritePlanMarkdown(lines, scan, sectionEnd, newNumber, taskShortId, oldCitationToken);
        await File.WriteAllLinesAsync(planPath, rewrittenLines, cancellationToken);

        int filesRewritten = await RewriteCitationsAsync(
            git, worktreePath, forkPointSha, taskShortId, oldCitationToken, newNumber, scan.TailIsPlaceholder, cancellationToken);

        string commitMessage = scan.TailIsPlaceholder
            ? $"chore: assign Decisions Log #{newNumber} to placeholder PLACEHOLDER-{taskShortId}"
            : $"chore: renumber Decisions Log #{oldCitationToken} to #{newNumber}";
        await RunGitAsync(git, worktreePath, ["add", "-A"], cancellationToken);
        await RunGitAsync(git, worktreePath, ["commit", "-m", commitMessage], cancellationToken);

        return new DecisionsLogRenumberResult(DecisionsLogRenumberOutcome.Renumbered, oldCitationToken, newNumber, filesRewritten);

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

    private static string[] RewritePlanMarkdown(
        string[] lines, TailScan scan, int sectionEnd, int newNumber, string taskShortId, string oldCitationToken)
    {
        // The heading regex anchors at the start of the line (^), so the token to drop is always
        // the line's own leading substring — never searched for, since a real number can appear
        // as a substring elsewhere in a long entry's own prose.
        string oldHeadingToken = scan.TailIsPlaceholder ? DecisionsLogPlaceholder.TokenFor(taskShortId) : oldCitationToken;
        string rewrittenHeading = newNumber.ToString() + lines[scan.TailLine][oldHeadingToken.Length..];

        string[] placementNote = scan.TailIsPlaceholder
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

        // Everything from the divider onward (or, lacking one, from the section-closing heading
        // onward) is preserved verbatim — only the region between the entry itself and that
        // boundary (blank lines plus whatever placement note, stale or absent, used to sit there)
        // is replaced.
        int boundaryLine = scan.DividerLine >= 0 ? scan.DividerLine : sectionEnd;

        var rebuilt = new List<string>(lines.Length + placementNote.Length);
        rebuilt.AddRange(lines[..scan.TailLine]);
        rebuilt.Add(rewrittenHeading);
        rebuilt.Add("");
        rebuilt.AddRange(placementNote);
        rebuilt.Add("");
        rebuilt.AddRange(lines[boundaryLine..]);

        return [.. rebuilt];
    }

    private static async Task<int> RewriteCitationsAsync(
        ProcessRunner git,
        string worktreePath,
        string forkPointSha,
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

        int filesRewritten = 0;
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
                // itself) — only a citation line this branch itself added since its fork point,
                // never one already present there, is this branch's own to rewrite.
                string forkPointContent = await ReadFileAtRevisionAsync(
                    git, worktreePath, forkPointSha, relativePath, cancellationToken);
                HashSet<string> forkPointLines = [.. NormalizeLineEndings(forkPointContent).Split('\n')];
                bool anyLineChanged = false;
                string[] contentLines = NormalizeLineEndings(content).Split('\n');
                for (int i = 0; i < contentLines.Length; i++)
                {
                    if (citationPattern.IsMatch(contentLines[i]) && !forkPointLines.Contains(contentLines[i]))
                    {
                        contentLines[i] = citationPattern.Replace(contentLines[i], newCitation);
                        anyLineChanged = true;
                    }
                }

                rewritten = string.Join('\n', contentLines);
                changed = anyLineChanged;
            }

            if (changed)
            {
                await File.WriteAllTextAsync(path, rewritten, cancellationToken);
                filesRewritten++;
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

    private static async Task<string> ReadFileAtRevisionAsync(
        ProcessRunner git, string worktreePath, string revision, string relativePath, CancellationToken cancellationToken)
    {
        ProcessResult result = await git(
            "git", ["show", $"{revision}:{relativePath}"], worktreePath, cancellationToken);
        return result.ExitCode == 0 ? result.StandardOutput : "";
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
