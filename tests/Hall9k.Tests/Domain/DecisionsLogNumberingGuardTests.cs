using System.Text.RegularExpressions;
using FluentAssertions;
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
/// </summary>
public sealed class DecisionsLogNumberingGuardTests
{
    private const string SectionStartHeading = "## 16. v0 Decisions Log";
    private const string SectionEndHeadingPrefix = "## 17.";

    private static readonly Regex DecisionEntryPattern = new(@"^(\d+)\. \*\*", RegexOptions.Compiled);
    private static readonly Regex UnboldedEntryLookingLinePattern = new(@"^(\d+)\. (?!\*\*)", RegexOptions.Compiled);

    [Fact]
    public void Every_decision_number_in_the_v0_Decisions_Log_is_unique()
    {
        string planPath = PlanMarkdownPath();
        File.Exists(planPath).Should().BeTrue($"PLAN.md should exist at '{planPath}'");

        string[] lines = File.ReadAllLines(planPath);

        int sectionStart = Array.FindIndex(lines, line => line.StartsWith(SectionStartHeading, StringComparison.Ordinal));
        sectionStart.Should().BeGreaterThanOrEqualTo(0,
            $"PLAN.md should still carry a '{SectionStartHeading}' heading — this guard scans between it and the '{SectionEndHeadingPrefix}' heading that closes it");

        int sectionEnd = Array.FindIndex(lines, sectionStart + 1, line => line.StartsWith(SectionEndHeadingPrefix, StringComparison.Ordinal));
        sectionEnd.Should().BeGreaterThan(sectionStart,
            $"PLAN.md should still carry a '{SectionEndHeadingPrefix}' heading closing the Decisions Log");

        Dictionary<int, List<int>> lineNumbersByDecisionNumber = [];
        List<string> unboldedEntryLookingLines = [];
        for (int i = sectionStart + 1; i < sectionEnd; i++)
        {
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
        }

        unboldedEntryLookingLines.Should().BeEmpty(
            "a line shaped like a decision entry ('<number>. ') but missing the bold headline is invisible " +
            "to this guard's duplicate check — a duplicate authored this way would be silently skipped rather " +
            "than reported; bold the headline (or confirm this line is not meant to be a decision entry) " +
            "before re-running");

        lineNumbersByDecisionNumber.Should().NotBeEmpty(
            "the scan should find real decision entries between the two headings — an empty result " +
            "means the entry pattern or the section bounds have drifted from PLAN.md's actual shape");

        List<string> duplicates =
        [
            .. lineNumbersByDecisionNumber
                .Where(pair => pair.Value.Count > 1)
                .OrderBy(pair => pair.Key)
                .Select(pair => $"#{pair.Key} at lines {string.Join(", ", pair.Value)}")
        ];

        duplicates.Should().BeEmpty(
            "every Decisions Log entry number must be unique: a collision means two decisions are " +
            "citable under the same number, and every existing reference to either one is ambiguous " +
            "until one is renumbered and every citation is updated by meaning");
    }

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
