using System.Text.RegularExpressions;
using FluentAssertions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// PLAN.md's §16 v0 Decisions Log numbers every decision, and every citation of one across the
/// repo — AGENTS.md, TASK-MODEL.md, docs/, and source and test comments alike — cites it by that
/// number alone. A duplicate silently breaks the discipline the citations depend on: the
/// 2026-09-01 architecture review found three collisions live at once — #99 three ways (the
/// pr-review task type, the out-of-scope sweep consolidation, and the install
/// connection-string write), #113 three ways (the fix-session self-check, FinalFullPass's
/// narrowed fix bar, and the periodic token-spend budget), and #114 two ways (Jira writes off
/// twg, and the project branch template) — and this test is what turns the next one into a red
/// build instead of a merge-time surprise — origin: two in-flight branches both claimed #109 as
/// of 2026-09-01, and whichever rebased second would have renumbered under conflict pressure with
/// no guard to catch a miss.
/// <para>
/// #37 is deliberately never assigned (its own placeholder entry says so) and is, by construction,
/// the only number in the log with a single entry that documents its own absence rather than a
/// decision — this guard does not special-case it, because a lone entry under any number,
/// #37 included, is not a duplicate and needs no exemption to pass.
/// </para>
/// <para>
/// A SECOND origin incident (2026-09-07, task 6df5f975): three Windows branches and two Mac
/// branches all chose #148 the same afternoon, because a branch picked its own number at write
/// time by reading main's own tail — a race no single guard run could see coming, since each
/// branch's own build was green in isolation. <see cref="DecisionsLogPlaceholder"/> is the fix:
/// a branch writes its own entry under a placeholder derived from its task's own short id rather
/// than guessing a real number, and the mechanical pre-final-pass rebase step
/// (<c>DecisionsLogRenumberer</c>) assigns the true number once the branch is current with main —
/// no agent session, and no number two branches could ever race for, since a task's short id is
/// unique by construction. This guard recognizes exactly one such placeholder at the log's own
/// tail as correctly waiting for its number, not a defect; two placeholders, or one buried
/// somewhere other than the tail, are exactly the sort of authoring mistake this guard exists to
/// catch, so both still fail it.
/// </para>
/// </summary>
public sealed class DecisionsLogNumberingGuardTests
{
    private const string SectionStartHeading = "## 16. v0 Decisions Log";
    private const string SectionEndHeadingPrefix = "## 17.";

    private static readonly Regex DecisionEntryPattern = new(@"^(\d+)\. \*\*", RegexOptions.Compiled);
    private static readonly Regex UnboldedEntryLookingLinePattern = new(@"^(\d+)\. (?!\*\*)", RegexOptions.Compiled);

    private const string PlaceholderConventionExplanation =
        "A branch that instead appended its own entry under a placeholder derived from its task's " +
        "own short id (PLACEHOLDER-<shortid>, at the log's own tail) would never have collided here " +
        "— the mechanical pre-final-pass rebase step assigns the real number once the branch is " +
        "current with main, and no agent session ever picks one by hand.";

    [Fact]
    public void Every_decision_number_in_the_v0_Decisions_Log_is_unique()
    {
        string planPath = PlanMarkdownPath();
        File.Exists(planPath).Should().BeTrue($"PLAN.md should exist at '{planPath}'");

        string[] lines = File.ReadAllLines(planPath);
        DecisionsLogScan scan = Scan(lines);

        scan.SectionStart.Should().BeGreaterThanOrEqualTo(0,
            $"PLAN.md should still carry a '{SectionStartHeading}' heading — this guard scans between it and the '{SectionEndHeadingPrefix}' heading that closes it");
        scan.SectionEnd.Should().BeGreaterThan(scan.SectionStart,
            $"PLAN.md should still carry a '{SectionEndHeadingPrefix}' heading closing the Decisions Log");

        AssertWellFormed(scan);
    }

    [Fact]
    public void A_single_placeholder_entry_at_the_log_s_tail_is_accepted()
    {
        string[] lines = FixtureSection(
            RealEntry(1, "First."),
            RealEntry(2, "Second."),
            PlaceholderEntry("6df5f975", "In flight."));

        DecisionsLogScan scan = Scan(lines);
        AssertWellFormed(scan);
        scan.Placeholders.Should().ContainSingle().Which.Token.Should().Be("6df5f975");
    }

    [Fact]
    public void Two_placeholder_entries_fail_even_when_the_tasks_differ()
    {
        string[] lines = FixtureSection(
            RealEntry(1, "First."),
            PlaceholderEntry("6df5f975", "First in-flight branch."),
            PlaceholderEntry("aaaaaaaa", "Second in-flight branch."));

        DecisionsLogScan scan = Scan(lines);
        scan.Placeholders.Should().HaveCount(2);

        Action act = () => AssertWellFormed(scan);

        act.Should().Throw<Exception>().WithMessage("*at most one placeholder*");
    }

    [Fact]
    public void A_placeholder_entry_not_at_the_log_s_tail_fails()
    {
        string[] lines = FixtureSection(
            RealEntry(1, "First."),
            PlaceholderEntry("6df5f975", "Buried placeholder."),
            RealEntry(2, "Landed after the placeholder was written."));

        DecisionsLogScan scan = Scan(lines);
        scan.Placeholders.Should().ContainSingle();
        scan.Placeholders[0].Line.Should().NotBe(scan.LastEntryLine);

        Action act = () => AssertWellFormed(scan);

        act.Should().Throw<Exception>().WithMessage("*log's own tail*");
    }

    [Fact]
    public void Two_real_entries_sharing_a_number_fail_with_a_message_naming_the_placeholder_convention()
    {
        string[] lines = FixtureSection(
            RealEntry(1, "First."),
            RealEntry(2, "Second, independently numbered #2 by a different branch."),
            RealEntry(2, "Third, also numbered #2 — the collision."));

        DecisionsLogScan scan = Scan(lines);

        Action act = () => AssertWellFormed(scan);

        act.Should().Throw<Exception>().WithMessage($"*{PlaceholderConventionExplanation}*");
    }

    private static void AssertWellFormed(DecisionsLogScan scan)
    {
        scan.UnboldedEntryLookingLines.Should().BeEmpty(
            "a line shaped like a decision entry ('<number>. ') but missing the bold headline is invisible " +
            "to this guard's duplicate check — a duplicate authored this way would be silently skipped rather " +
            "than reported; bold the headline (or confirm this line is not meant to be a decision entry) " +
            "before re-running");

        scan.LineNumbersByDecisionNumber.Should().NotBeEmpty(
            "the scan should find real decision entries between the two headings — an empty result " +
            "means the entry pattern or the section bounds have drifted from PLAN.md's actual shape");

        List<string> duplicates =
        [
            .. scan.LineNumbersByDecisionNumber
                .Where(pair => pair.Value.Count > 1)
                .OrderBy(pair => pair.Key)
                .Select(pair => $"#{pair.Key} at lines {string.Join(", ", pair.Value)}")
        ];

        duplicates.Should().BeEmpty(
            "every Decisions Log entry number must be unique: a collision means two decisions are " +
            "citable under the same number, and every existing reference to either one is ambiguous " +
            "until one is renumbered and every citation is updated by meaning. " +
            PlaceholderConventionExplanation);

        scan.Placeholders.Should().HaveCountLessThanOrEqualTo(1,
            "a branch carries at most one placeholder Decisions Log entry — derived from its own " +
            "task's short id (PLACEHOLDER-<shortid>) — so the mechanical rebase step never has two " +
            "candidates to choose between when it assigns the real next number");

        if (scan.Placeholders.Count == 1)
        {
            scan.Placeholders[0].Line.Should().Be(scan.LastEntryLine,
                "a placeholder entry must sit at the log's own tail — the mechanical rebase step " +
                "assigns it the next free number by reading the tail, and a placeholder buried " +
                "earlier in the log would be assigned a number that collides with whatever real " +
                "entry actually follows it");
        }
    }

    private readonly record struct PlaceholderEntryLocation(string Token, int Line);

    private readonly record struct DecisionsLogScan(
        int SectionStart,
        int SectionEnd,
        Dictionary<int, List<int>> LineNumbersByDecisionNumber,
        List<string> UnboldedEntryLookingLines,
        List<PlaceholderEntryLocation> Placeholders,
        int LastEntryLine);

    private static DecisionsLogScan Scan(string[] lines)
    {
        int sectionStart = Array.FindIndex(lines, line => line.StartsWith(SectionStartHeading, StringComparison.Ordinal));
        int sectionEnd = sectionStart < 0
            ? -1
            : Array.FindIndex(lines, sectionStart + 1, line => line.StartsWith(SectionEndHeadingPrefix, StringComparison.Ordinal));

        Dictionary<int, List<int>> lineNumbersByDecisionNumber = [];
        List<string> unboldedEntryLookingLines = [];
        List<PlaceholderEntryLocation> placeholders = [];
        int lastEntryLine = -1;

        if (sectionStart >= 0 && sectionEnd > sectionStart)
        {
            for (int i = sectionStart + 1; i < sectionEnd; i++)
            {
                Match placeholderMatch = DecisionsLogPlaceholder.EntryHeadingPattern.Match(lines[i]);
                if (placeholderMatch.Success)
                {
                    placeholders.Add(new PlaceholderEntryLocation(placeholderMatch.Groups[1].Value, i + 1));
                    lastEntryLine = i + 1;
                    continue;
                }

                Match match = DecisionEntryPattern.Match(lines[i]);
                if (!match.Success)
                {
                    if (UnboldedEntryLookingLinePattern.IsMatch(lines[i]))
                    {
                        unboldedEntryLookingLines.Add($"line {i + 1}: \"{lines[i]}\"");
                    }

                    continue;
                }

                int decisionNumber = int.Parse(match.Groups[1].Value);
                if (!lineNumbersByDecisionNumber.TryGetValue(decisionNumber, out List<int>? entryLineNumbers))
                {
                    entryLineNumbers = [];
                    lineNumbersByDecisionNumber[decisionNumber] = entryLineNumbers;
                }

                entryLineNumbers.Add(i + 1);
                lastEntryLine = i + 1;
            }
        }

        return new DecisionsLogScan(
            sectionStart, sectionEnd, lineNumbersByDecisionNumber, unboldedEntryLookingLines, placeholders, lastEntryLine);
    }

    private static string[] FixtureSection(params string[] entries) =>
    [
        SectionStartHeading,
        "",
        .. entries,
        "",
        "---",
        "",
        "## 17. Reference Materials",
    ];

    private static string RealEntry(int number, string text) => $"{number}. **{text}**";

    private static string PlaceholderEntry(string taskShortId, string text) =>
        $"{DecisionsLogPlaceholder.TokenFor(taskShortId)}. **{text}**";

    private static string PlanMarkdownPath()
    {
        string sourceDirectory = TestSourceTree.SourceDirectory();
        string? repositoryRoot = Path.GetDirectoryName(sourceDirectory);
        if (repositoryRoot is null)
        {
            throw new InvalidOperationException($"'{sourceDirectory}' has no parent directory to resolve the repository root from");
        }

        return Path.Combine(repositoryRoot, "PLAN.md");
    }
}
