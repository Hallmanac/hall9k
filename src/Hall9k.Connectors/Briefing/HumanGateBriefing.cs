using System.Globalization;
using System.Text;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Connectors.Briefing;

/// <summary>One commit on the branch, as the briefing lists it.</summary>
/// <param name="Sha">The full commit id.</param>
/// <param name="Subject">The commit's first line.</param>
public sealed record BriefingCommit(string Sha, string Subject);

/// <summary>Everything the briefing prints, gathered by the command so the page itself is a pure function of it.</summary>
/// <param name="PullRequestSummaryFile">The text of the run's <c>pr-summary.md</c>, or null when it does not exist.</param>
/// <param name="SummaryWrittenAt">When that file was last written, or null when unknown.</param>
/// <param name="TipAuthoredAt">The author date of the branch's tip commit, or null when unreadable.</param>
/// <param name="SummaryPath">Where the full summary lives, for the pointer a cut summary ends with.</param>
/// <param name="Settlement">How review ended, in the settlement word (Clean, Settled, Overridden), or empty when unrecorded.</param>
/// <param name="FilesChanged">Files the whole change touches, or null when git could not be asked.</param>
/// <param name="LinesChanged">Lines added plus deleted across the whole change, or null when git could not be asked.</param>
/// <param name="HasRunSkill">Whether the project records how its product is started.</param>
/// <param name="Levers">Which choice set the stop offers, from the one shared vocabulary.</param>
/// <param name="StoppedFor">What stopped the run: the human gate, or interactive mode's boundary.</param>
public sealed record HumanGateBriefingFacts(
    Guid TaskId,
    string ProjectName,
    string Objective,
    IReadOnlyList<string> AcceptanceCriteria,
    string? PullRequestSummaryFile,
    DateTimeOffset? SummaryWrittenAt,
    DateTimeOffset? TipAuthoredAt,
    string SummaryPath,
    string Settlement,
    int ReviewCycles,
    int UnfixedFindings,
    string MergedReportPath,
    IReadOnlyList<BriefingCommit> Commits,
    int? FilesChanged,
    int? LinesChanged,
    string WorktreePath,
    string Branch,
    string BaseCommit,
    string HeadCommit,
    bool HasRunSkill,
    InteractiveBoundaryLevers Levers,
    HumanGateBriefing.Stop StoppedFor);

/// <summary>
/// The plain briefing <c>h9k review brief</c> prints at the stop before a pull request opens (the human
/// gate, or interactive mode's gates-to-pull-request boundary). A pure function of observed facts: the
/// command reads git and the run directory, this lays them out, and a test pins the page without a branch
/// or a daemon.
/// <para>
/// The order is fixed because a reader decides in that order: what changed in plain words, what was asked
/// for, how automated review ended, what the commits are, and then the levers (compare, run, step in,
/// outcomes). It starts no session and makes no network call; the summary is the opening paragraph of the
/// run's own <c>pr-summary.md</c>, never a new model call.
/// </para>
/// </summary>
public static class HumanGateBriefing
{
    /// <summary>The most words the summary paragraph prints before it is cut with a pointer to the full file.</summary>
    public const int MaxSummaryWords = 150;

    /// <summary>The most commits listed one by one; past this the list collapses to a count.</summary>
    public const int MaxListedCommits = 20;

    /// <summary>What the run stopped for.</summary>
    public enum Stop
    {
        /// <summary>The task's human gate (<c>--human-gate</c>).</summary>
        HumanGate,

        /// <summary>Interactive mode's gates-to-pull-request boundary.</summary>
        InteractiveMode,
    }

    /// <summary>
    /// The first paragraph of the summary's body that is not a heading, or null when there is none. The file
    /// is a <c>Title:</c> line, a blank line, then the body, which is what
    /// <see cref="Prompts.PrSummaryParser.ParseBlock"/> reads back; a heading line at the head of a paragraph
    /// is dropped so a paragraph that opens under one still counts.
    /// </summary>
    public static string? FirstParagraph(string? summaryFile)
    {
        Prompts.PrSummaryParser.PrSummary? parsed = Prompts.PrSummaryParser.ParseBlock(summaryFile);
        if (parsed is null)
        {
            return null;
        }

        foreach (string block in parsed.Body.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n"))
        {
            string[] lines = [.. block.Split('\n').SkipWhile(line => line.TrimStart().StartsWith('#') || line.IsBlank())];
            if (lines.Length == 0)
            {
                continue;
            }

            return string.Join(' ', lines.Select(line => line.Trim()).Where(line => line.Length > 0));
        }

        return null;
    }

    /// <summary>The paragraph limited to <see cref="MaxSummaryWords"/> words, and whether it had to be cut.</summary>
    public static (string Text, bool Cut) Limit(string paragraph)
    {
        string[] words = paragraph.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= MaxSummaryWords
            ? (string.Join(' ', words), false)
            : (string.Join(' ', words.Take(MaxSummaryWords)) + " ...", true);
    }

    /// <summary>
    /// Whether the branch has moved on past the summary: the tip commit was authored after the summary was
    /// written. Compares the author date, never the committer date, because the pre-final-pass rebase
    /// rewrites commit dates without changing what anyone wrote, and a rebase alone must never cause this
    /// line. Unknown on either side is not older.
    /// </summary>
    public static bool SummaryIsOlderThanBranch(DateTimeOffset? summaryWrittenAt, DateTimeOffset? tipAuthoredAt) =>
        summaryWrittenAt is { } written && tipAuthoredAt is { } authored && authored > written;

    /// <summary>Lays out the page.</summary>
    public static string Render(HumanGateBriefingFacts facts)
    {
        StringBuilder page = new();
        string id = facts.TaskId.ToString("D", CultureInfo.InvariantCulture);
        page.AppendLine(facts.StoppedFor == Stop.HumanGate
            ? $"Task {id} is stopped at its human gate. Automated review has settled and no GitHub pull request exists yet."
            : $"Task {id} is stopped at interactive mode's gates-to-pull-request boundary. Review has settled and no GitHub pull request exists yet.");
        page.AppendLine();

        AppendSummary(page, facts);
        AppendAsked(page, facts);
        AppendReview(page, facts);
        AppendCommits(page, facts);
        AppendLevers(page, facts, id);

        // AppendLine writes the platform's newline, which is a carriage return and a line feed on Windows. The
        // free text was folded onto one line above, so any carriage return left here is one this method wrote.
        return page.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";
    }

    private static void AppendSummary(StringBuilder page, HumanGateBriefingFacts facts)
    {
        page.AppendLine("What changed");
        string? paragraph = FirstParagraph(facts.PullRequestSummaryFile);
        if (paragraph is null)
        {
            page.AppendLine(
                "  No pull request summary was written for this run, so there is no plain description here. "
                + "Everything below is still accurate.");
        }
        else
        {
            (string text, bool cut) = Limit(paragraph);
            page.AppendLine($"  {text}");
            if (cut)
            {
                page.AppendLine($"  (Cut at {MaxSummaryWords} words. The full summary is {facts.SummaryPath}.)");
            }

            if (SummaryIsOlderThanBranch(facts.SummaryWrittenAt, facts.TipAuthoredAt))
            {
                page.AppendLine(
                    "  The summary is older than the branch: the tip commit was authored after the summary was "
                    + "written, so it may not describe the latest changes.");
            }
        }

        page.AppendLine();
    }

    private static void AppendAsked(StringBuilder page, HumanGateBriefingFacts facts)
    {
        page.AppendLine("What was asked for");
        page.AppendLine($"  Objective: {facts.Objective}");
        if (facts.AcceptanceCriteria.Count == 0)
        {
            page.AppendLine("  Acceptance criteria: none recorded.");
        }
        else
        {
            page.AppendLine("  Acceptance criteria:");
            for (int index = 0; index < facts.AcceptanceCriteria.Count; index++)
            {
                page.AppendLine($"    {index + 1}. {facts.AcceptanceCriteria[index]}");
            }
        }

        page.AppendLine();
    }

    private static void AppendReview(StringBuilder page, HumanGateBriefingFacts facts)
    {
        page.AppendLine("How automated review settled");
        string settlement = facts.Settlement.Length == 0 ? "settlement not recorded" : facts.Settlement.ToLowerInvariant();
        string cycles = facts.ReviewCycles switch
        {
            0 => "no review cycles",
            1 => "1 review cycle",
            var count => $"{count} review cycles",
        };
        string unfixed = facts.UnfixedFindings switch
        {
            0 => "no findings left unfixed",
            1 => "1 finding left unfixed",
            var count => $"{count} findings left unfixed",
        };
        page.AppendLine($"  {settlement}, after {cycles}, with {unfixed}.");
        // Always pointed at when a cycle ran, so even a count of none can be checked against the report
        // itself; composition none runs no cycle and so has no report to point at.
        if (facts.ReviewCycles > 0)
        {
            page.AppendLine($"  The merged report is {facts.MergedReportPath}.");
        }

        page.AppendLine();
    }

    private static void AppendCommits(StringBuilder page, HumanGateBriefingFacts facts)
    {
        page.AppendLine("The commits, oldest first");
        if (facts.Commits.Count == 0)
        {
            page.AppendLine("  No commits found on the branch beyond its base.");
        }
        else if (facts.Commits.Count <= MaxListedCommits)
        {
            foreach (BriefingCommit commit in facts.Commits)
            {
                page.AppendLine($"  {Short(commit.Sha)} {commit.Subject}");
            }
        }
        else
        {
            page.AppendLine(
                $"  {facts.Commits.Count} commits, too many to list here: git log --reverse --oneline {Short(facts.BaseCommit)}..{Short(facts.HeadCommit)}");
        }

        page.AppendLine(facts.FilesChanged is { } files && facts.LinesChanged is { } lines
            ? $"  In total {Plural(files, "file")} and {Plural(lines, "line")} changed."
            : "  The total of files and lines changed could not be read.");
        page.AppendLine();
    }

    private static void AppendLevers(StringBuilder page, HumanGateBriefingFacts facts, string id)
    {
        string worktree = Quote(facts.WorktreePath);
        page.AppendLine("Where it is");
        page.AppendLine($"  Worktree: {facts.WorktreePath}");
        page.AppendLine($"  Branch: {facts.Branch}");
        page.AppendLine($"  Base commit: {facts.BaseCommit}");
        page.AppendLine($"  Head commit: {facts.HeadCommit}");
        page.AppendLine();

        page.AppendLine("Compare, in the compare tool git is configured to use");
        page.AppendLine($"  The whole change: git -C {worktree} difftool --dir-diff {facts.BaseCommit} {facts.HeadCommit}");
        if (facts.Commits.Count is > 0 and <= MaxListedCommits)
        {
            foreach (BriefingCommit commit in facts.Commits)
            {
                page.AppendLine(
                    $"  {Short(commit.Sha)}: git -C {worktree} difftool --dir-diff {Short(commit.Sha)}^ {Short(commit.Sha)}");
            }
        }
        else if (facts.Commits.Count > MaxListedCommits)
        {
            page.AppendLine(
                $"  One commit: git -C {worktree} difftool --dir-diff <sha>^ <sha>, with a sha from the log line above.");
        }

        page.AppendLine();

        page.AppendLine("Run it");
        page.AppendLine(facts.HasRunSkill
            ? $"  h9k task run-local {id} stands the branch up in the worktree from this project's run skill."
            : $"  Project {facts.ProjectName} has no run skill, so nothing records how its product is started. Ask for one with "
                + $"h9k project set {facts.ProjectName} --discover-run-skill. Every outcome below works without it.");
        page.AppendLine();

        page.AppendLine("Step in");
        page.AppendLine(
            $"  h9k task drive {id} prints a line that starts a Claude Code session in the worktree, for questions and "
            + "changes. It registers nothing and changes neither the claim nor this stop.");
        page.AppendLine();

        page.AppendLine("Your choices");
        foreach (string choice in InteractiveBoundaryChoices.Lines(facts.Levers, facts.TaskId))
        {
            page.AppendLine($"  - {choice}");
        }
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static string Short(string sha) => sha.Length > 12 ? sha[..12] : sha;

    private static string Quote(string path) => path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;
}
