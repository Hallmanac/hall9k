using System.Text.RegularExpressions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Daemon.Review;

/// <summary>
/// What <see cref="DecisionsLogTailConflictResolver.Recognize"/> made of one stop of a rebase.
/// </summary>
/// <param name="ResolvedPlanMarkdown">
/// PLAN.md with the conflict resolved — the base's own entries first, this branch's placeholder
/// entry after them — or null when the conflict was not the tail-append shape, which is the one
/// thing a caller must test before acting.
/// </param>
/// <param name="KeptBaseEntryNumbers">
/// The numbers of the base-side entries this resolution kept ahead of the branch's own, in the
/// order they appear; empty when this was not the shape. Named in the caller's own log line, so a
/// human reading the run can see exactly which entries the machine decided came first.
/// </param>
/// <param name="PlaceholderToken">
/// The branch-side placeholder this resolution placed last (<c>PLACEHOLDER-&lt;short id&gt;</c>),
/// or null when this was not the shape.
/// </param>
/// <param name="Explanation">
/// Why this conflict is not the shape, or — when it is — what the shape was, in the wording the
/// caller logs. Always populated: a park's log line is the only place a human learns why the
/// machine declined to resolve, and "conflicted" alone is not an account.
/// </param>
public sealed record DecisionsLogTailConflictResolution(
    string? ResolvedPlanMarkdown,
    IReadOnlyList<int> KeptBaseEntryNumbers,
    string? PlaceholderToken,
    string Explanation)
{
    /// <summary>Whether this conflict was the tail-append shape and so is resolved, not parked.</summary>
    public bool IsTailAppendShape => ResolvedPlanMarkdown is not null;
}

/// <summary>
/// The single git conflict shape the mechanical pre-final-pass rebase resolves on its own, and the
/// only exception to Brian's 2026-09-04 ruling that a conflict is itself the evidence judgment is
/// required (<c>ReviewEngine.EnsureRebasedBeforeFinalPassAsync</c>'s own doc): the base and this
/// branch each appended an entry at the end of PLAN.md's §16 Decisions Log and nothing else
/// disagrees. There is no judgment in that conflict — the placeholder-numbering convention
/// (Decisions Log #162) already decided the answer, which is that the base's entries keep their
/// places and this branch's placeholder goes after them to be assigned the next free number by
/// <see cref="DecisionsLogRenumberer"/> moments later.
/// <para>
/// <b>The shape, and nothing wider.</b> Exactly one conflicted file, and it is PLAN.md; exactly one
/// conflict hunk in it; the base's side is one or more complete numbered entries sitting at the very
/// tail of §16; the branch's side is this task's own placeholder entry, with its placement note,
/// and nothing else. Every near miss — an insertion in the middle of the log, a second hunk, a
/// second file, another task's placeholder, a base-side hunk cut off mid-entry — is not the shape
/// and parks exactly as it did before this existed. The conservative direction is deliberate: a
/// wrong park costs a recovery session, a wrong resolution silently rewrites authored decision text.
/// </para>
/// <para>
/// <b>Pure.</b> Text in, text out — no git call, no file read, no worktree. Its caller hands it the
/// unmerged-file list and the conflicted PLAN.md, which is what lets the whole shape be tested
/// without a repository (origin: 2026-09-20, the courier 504c9c3b parked three times on this exact
/// shape and 98484f36 spent two recovery sessions on it).
/// </para>
/// </summary>
public static class DecisionsLogTailConflictResolver
{
    /// <summary>The one file this shape may ever touch, repository-relative.</summary>
    public const string PlanMarkdownFileName = "PLAN.md";

    private const string OursMarkerPrefix = "<<<<<<<";
    private const string BaseMarkerPrefix = "|||||||";
    private const string SeparatorMarker = "=======";
    private const string TheirsMarkerPrefix = ">>>>>>>";
    private const string SectionDivider = "---";

    /// <summary>
    /// Decides whether one stop of a rebase is the tail-append shape and, if it is, produces the
    /// resolved PLAN.md. <paramref name="conflictedFiles"/> is the repository-relative unmerged
    /// file list (<c>git diff --name-only --diff-filter=U</c>); <paramref name="conflictedPlanMarkdown"/>
    /// is PLAN.md as git left it, conflict markers and all, and is read only once the file list has
    /// already narrowed to PLAN.md alone. <paramref name="taskShortId"/> is the run's own task's
    /// short id: the only placeholder this resolver may ever place, since another task's is that
    /// task's branch's business.
    /// </summary>
    public static DecisionsLogTailConflictResolution Recognize(
        IReadOnlyList<string> conflictedFiles, string conflictedPlanMarkdown, string taskShortId)
    {
        if (!DecisionsLogPlaceholder.ShortIdPattern.IsMatch(taskShortId))
        {
            return NotTheShape($"'{taskShortId}' is not a task short id, so no placeholder can be recognised from it");
        }

        if (conflictedFiles.Count == 0)
        {
            return NotTheShape("the rebase stopped with no unmerged file at all, which is not a conflict this step can read");
        }

        if (conflictedFiles.Count > 1)
        {
            return NotTheShape(
                $"{conflictedFiles.Count} files conflicted ({string.Join(", ", conflictedFiles)}) and this shape is {PlanMarkdownFileName} alone");
        }

        string onlyFile = conflictedFiles[0].Replace('\\', '/').Trim();
        if (!string.Equals(onlyFile, PlanMarkdownFileName, StringComparison.Ordinal))
        {
            return NotTheShape($"the one conflicted file is '{onlyFile}', not {PlanMarkdownFileName}");
        }

        (string[] lines, string newline, bool trailingNewline) =
            DecisionsLogRenumberer.SplitPreservingLineEnding(conflictedPlanMarkdown);
        if (FindSingleHunk(lines) is not { } hunk)
        {
            return NotTheShape(
                $"{PlanMarkdownFileName} does not hold exactly one well-formed conflict hunk, so which side is whose cannot be read");
        }

        string[] baseSide = lines[(hunk.OursMarker + 1)..(hunk.BaseMarker >= 0 ? hunk.BaseMarker : hunk.Separator)];
        string[] branchSide = lines[(hunk.Separator + 1)..hunk.TheirsMarker];
        string[] prefix = lines[..hunk.OursMarker];
        string[] suffix = lines[(hunk.TheirsMarker + 1)..];

        string[] baseResolved = [.. prefix, .. baseSide, .. suffix];
        if (CheckBaseSideIsTheLogsTail(baseResolved, baseSide, prefix.Length) is { } baseRefusal)
        {
            return NotTheShape(baseRefusal);
        }

        string[] branchResolved = [.. prefix, .. branchSide, .. suffix];
        if (CheckBranchSideIsThisTasksPlaceholder(branchResolved, branchSide, newline, trailingNewline, taskShortId)
            is { } branchRefusal)
        {
            return NotTheShape(branchRefusal);
        }

        // The base's side first, then this branch's, which is the whole decision: the base's
        // entries already hold their numbers and this branch's placeholder has not been assigned
        // one yet, so it belongs after them (Decisions Log #162). A blank line goes between only
        // when neither side already carries one, so a resolution never runs two entries together
        // and never doubles a separator either side already wrote.
        bool needsSeparatingBlankLine =
            baseSide.Length > 0 && baseSide[^1].Trim().Length > 0
            && branchSide.Length > 0 && branchSide[0].Trim().Length > 0;
        string[] resolved = needsSeparatingBlankLine
            ? [.. prefix, .. baseSide, "", .. branchSide, .. suffix]
            : [.. prefix, .. baseSide, .. branchSide, .. suffix];

        if (RealEntryNumbersIn(baseSide) is not { } keptBaseEntryNumbers)
        {
            return NotTheShape(
                "an entry heading on the base's side carries a number this step cannot read as one, so which "
                + "entries it would be keeping is not a fact this step has");
        }

        string placeholderToken = DecisionsLogPlaceholder.TokenFor(taskShortId);
        return new DecisionsLogTailConflictResolution(
            DecisionsLogRenumberer.JoinPreservingLineEnding(resolved, newline, trailingNewline),
            keptBaseEntryNumbers,
            placeholderToken,
            DescribeShape(keptBaseEntryNumbers, placeholderToken));
    }

    /// <summary>
    /// The wording a caller logs for a conflict this resolver did resolve — the shape's own name
    /// and the entry numbers it kept, so a human reading the run never has to open the diff to see
    /// what the machine decided came first.
    /// </summary>
    public static string DescribeShape(IReadOnlyList<int> keptBaseEntryNumbers, string placeholderToken) =>
        $"the Decisions Log tail-append shape in {PlanMarkdownFileName} section 16 "
        + $"(kept the base's own {DescribeEntryNumbers(keptBaseEntryNumbers)} at the tail and this branch's own "
        + $"{placeholderToken} entry after them)";

    private static string DescribeEntryNumbers(IReadOnlyList<int> numbers) =>
        numbers.Count switch
        {
            0 => "entries",
            1 => $"entry #{numbers[0]}",
            _ => "entries " + string.Join(", ", numbers.Select(number => $"#{number}")),
        };

    private static DecisionsLogTailConflictResolution NotTheShape(string reason) => new(null, [], null, reason);

    private readonly record struct ConflictHunk(int OursMarker, int BaseMarker, int Separator, int TheirsMarker);

    /// <summary>
    /// The one conflict hunk in <paramref name="lines"/>, or null when there is not exactly one
    /// well-formed hunk. A <c>|||||||</c> section is accepted and ignored — a node configured for
    /// the diff3 or zdiff3 conflict style writes one, and the merge base it shows changes nothing
    /// about which side is whose — but a second hunk anywhere is refused outright rather than
    /// resolved one hunk at a time: two disagreements in one file are not this shape, whatever the
    /// first one looks like.
    /// <para>
    /// The separator is matched as the whole line, exactly <c>=======</c>, rather than by prefix: a
    /// repository that widened its conflict markers (git's own <c>conflict-marker-size</c>
    /// attribute) simply parks here instead of being misread, and a markdown rule of the same seven
    /// characters somewhere in PLAN.md's own prose parks it too. Both are the conservative
    /// direction this whole step takes.
    /// </para>
    /// </summary>
    private static ConflictHunk? FindSingleHunk(string[] lines)
    {
        int oursMarker = -1;
        int baseMarker = -1;
        int separator = -1;
        int theirsMarker = -1;
        int oursMarkers = 0;
        int separators = 0;
        int theirsMarkers = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.StartsWith(OursMarkerPrefix, StringComparison.Ordinal))
            {
                oursMarkers++;
                oursMarker = oursMarker < 0 ? i : oursMarker;
            }
            else if (line.StartsWith(TheirsMarkerPrefix, StringComparison.Ordinal))
            {
                theirsMarkers++;
                theirsMarker = theirsMarker < 0 ? i : theirsMarker;
            }
            else if (string.Equals(line, SeparatorMarker, StringComparison.Ordinal))
            {
                separators++;
                separator = separator < 0 ? i : separator;
            }
            else if (line.StartsWith(BaseMarkerPrefix, StringComparison.Ordinal) && baseMarker < 0)
            {
                baseMarker = i;
            }
        }

        if (oursMarkers != 1 || separators != 1 || theirsMarkers != 1)
        {
            return null;
        }

        if (oursMarker > separator || separator > theirsMarker)
        {
            return null;
        }

        // A diff3 base section belongs between the two sides; anywhere else it is not this hunk's
        // and the file is not something this resolver understands.
        return baseMarker >= 0 && (baseMarker < oursMarker || baseMarker > separator)
            ? null
            : new ConflictHunk(oursMarker, baseMarker, separator, theirsMarker);
    }

    /// <summary>
    /// Why the base's side of the hunk is not "one or more complete numbered entries at the tail of
    /// section 16", or null when it is. Read against the file as it would stand if the conflict were
    /// resolved to the base's side alone, so the questions are asked of a real PLAN.md rather than of
    /// a fragment: the hunk has to sit inside §16, start on an entry heading rather than part-way
    /// through someone's prose, carry no placeholder of its own, and have nothing but blank lines and
    /// the section's closing divider between it and the end of the section.
    /// </summary>
    private static string? CheckBaseSideIsTheLogsTail(string[] baseResolved, string[] baseSide, int hunkStart)
    {
        (int sectionStart, int sectionEnd) = DecisionsLogRenumberer.FindSection(baseResolved);
        if (sectionStart < 0 || sectionEnd < 0)
        {
            return $"{PlanMarkdownFileName}'s own Decisions Log section could not be located on the base's side of the conflict";
        }

        int hunkEnd = hunkStart + baseSide.Length;
        if (hunkStart <= sectionStart || hunkEnd > sectionEnd)
        {
            return "the conflict is not inside the Decisions Log section at all";
        }

        string? firstContentLine = baseSide.FirstOrDefault(line => line.Trim().Length > 0);
        if (firstContentLine is null || !DecisionsLogRenumberer.RealEntryHeadingPattern.IsMatch(firstContentLine))
        {
            return "the base's side of the conflict does not begin with a numbered Decisions Log entry, "
                + "so it is an incomplete or unnumbered entry rather than whole ones";
        }

        if (baseSide.Any(line => DecisionsLogPlaceholder.EntryHeadingPattern.IsMatch(line)))
        {
            return "the base's side of the conflict carries a placeholder entry of its own, which is never this shape";
        }

        for (int i = hunkEnd; i < sectionEnd; i++)
        {
            string line = baseResolved[i];
            if (line.Trim().Length > 0 && !string.Equals(line.Trim(), SectionDivider, StringComparison.Ordinal))
            {
                return "Decisions Log content follows the conflict, so the base's side is an insertion in the middle "
                    + "of the log or an entry cut off part-way rather than the log's own tail";
            }
        }

        return null;
    }

    /// <summary>
    /// Why the branch's side of the hunk is not this task's own placeholder entry with its placement
    /// note, or null when it is. The tail question is put to <see cref="DecisionsLogRenumberer"/>
    /// itself — the same helper the renumbering step that runs moments later reads — so the two steps
    /// can never disagree about whose placeholder is at the tail; this method adds only what is
    /// specific to a conflict, which is that the hunk itself begins on that entry's own heading and
    /// holds nothing else.
    /// </summary>
    private static string? CheckBranchSideIsThisTasksPlaceholder(
        string[] branchResolved, string[] branchSide, string newline, bool trailingNewline, string taskShortId)
    {
        string branchResolvedText =
            DecisionsLogRenumberer.JoinPreservingLineEnding(branchResolved, newline, trailingNewline);
        if (!DecisionsLogRenumberer.TailEntryIsThisTasksUnresolvedPlaceholder(branchResolvedText, taskShortId))
        {
            return "the branch's side of the conflict does not leave this task's own unresolved placeholder "
                + "at the Decisions Log's tail";
        }

        if (!DecisionsLogRenumberer.TailEntryCarriesPlacementNoteFor(branchResolvedText, taskShortId))
        {
            return "this task's own placeholder entry carries no placement note naming it, which every entry "
                + "written under the placeholder convention does";
        }

        // The hunk's branch side has to BEGIN on this task's own placeholder heading, not merely
        // carry it somewhere: containing it leaves room for whatever sits above it inside the same
        // hunk. A base commit that trimmed the tail of the entry ABOVE puts that entry's orphaned
        // prose at the head of this side, and "base's entries first, this side after" would then
        // graft a line the base deleted back in, under the wrong entry, while reporting the whole
        // stop resolved mechanically (independent pre-PR review, cycle 1, conformance lens). The
        // count check below is the same question asked of what follows the heading.
        string? firstContentLine = branchSide.FirstOrDefault(line => line.Trim().Length > 0);
        bool beginsOnThisTasksPlaceholder =
            firstContentLine is not null
            && DecisionsLogPlaceholder.EntryHeadingPattern.Match(firstContentLine) is { Success: true } heading
            && heading.Groups[1].Value == taskShortId;
        int branchHeadings = branchSide.Count(line =>
            DecisionsLogPlaceholder.EntryHeadingPattern.IsMatch(line)
            || DecisionsLogRenumberer.RealEntryHeadingPattern.IsMatch(line));
        if (!beginsOnThisTasksPlaceholder || branchHeadings != 1)
        {
            return "the branch's side of the conflict is not exactly one entry beginning on this task's own "
                + DecisionsLogPlaceholder.TokenFor(taskShortId) + " heading";
        }

        return null;
    }

    /// <summary>
    /// Every entry number the base's side carries, in order — or null when one of its headings
    /// matches the entry shape but its digits will not fit an <c>int</c>. Parsed rather than
    /// asserted, because this resolver is the one step that never throws on what it reads: an
    /// exception out of here escapes the caller's own catches (which cover a stuck git pipe and a
    /// deadline, not a malformed file) and would leave the worktree mid-rebase with nothing
    /// recorded. Refusing the shape parks the run instead, which is the answer for anything this
    /// step cannot read.
    /// </summary>
    private static List<int>? RealEntryNumbersIn(IReadOnlyList<string> lines)
    {
        List<int> numbers = [];
        foreach (string line in lines)
        {
            Match match = DecisionsLogRenumberer.RealEntryHeadingPattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            if (!int.TryParse(match.Groups[1].Value, out int number))
            {
                return null;
            }

            numbers.Add(number);
        }

        return numbers;
    }
}
