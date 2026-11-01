using FluentAssertions;
using Hall9k.Connectors.Briefing;
using Hall9k.Domain.Features.Run;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The briefing <c>h9k review brief</c> prints, pinned as a pure function of the facts the command gathers:
/// the summary rule, the fixed order, the commit list and its collapse, the run-skill branch, and the
/// older-than-the-branch line.
/// </summary>
public sealed class HumanGateBriefingTests
{
    private static readonly Guid TaskId = Guid.Parse("01a19510-0000-7000-8000-0000000000bb");
    private static readonly DateTimeOffset Written = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static string Words(int count) => string.Join(' ', Enumerable.Range(1, count).Select(index => $"w{index}"));

    private static HumanGateBriefingFacts Facts(
        string? summary = "Title: A title\n\nThe status pane now lists parked runs first.\n\nMore detail follows here.",
        int commits = 3, bool runSkill = true, DateTimeOffset? tipAuthoredAt = null,
        HumanGateBriefing.Stop stop = HumanGateBriefing.Stop.HumanGate,
        InteractiveBoundaryLevers levers = InteractiveBoundaryLevers.HumanGate) =>
        new(
            TaskId, "hall9k", "Rework the status pane", ["Parked runs come first", "The count is shown"],
            summary, Written, tipAuthoredAt, "/runs/pr-summary.md", "Settled", 2, 1, "/runs/review-2-findings.md",
            [.. Enumerable.Range(1, commits).Select(index => new BriefingCommit($"{index:x2}".PadRight(40, '0'), $"commit number {index}"))],
            7, 123, "/wt/status pane", "task/status", "base0000000000000000000000000000000000000", "head0000000000000000000000000000000000000",
            runSkill, levers, stop);

    /// <summary>
    /// The objective, criteria, summary and commit subjects come from an issue anyone could file or a build
    /// session under injection, so none may reach the terminal with an escape, a lone carriage return, or a
    /// newline that would print a line of its own choosing.
    /// </summary>
    [Fact]
    public void Free_text_is_printed_without_control_characters_or_extra_lines()
    {
        HumanGateBriefingFacts facts = Facts(summary: "Title: T\n\nHides\u001b[2K\rthe truth.") with
        {
            Objective = "Real objective\u001b]0;pwned\u0007\rOverwritten\nFake line: 0 findings left unfixed",
            AcceptanceCriteria = ["Criterion\u001b[31m one\nsecond line"],
            Commits = [new BriefingCommit("aa".PadRight(40, '0'), "subject\u001b[0m\rhidden\nfake commit line")],
        };

        string page = HumanGateBriefing.Render(facts);

        page.Should().NotContain("\u001b").And.NotContain("\u0007").And.NotContain("\r");
        page.Should().Contain("Objective: Real objective")
            .And.NotContain("\n  Fake line: 0 findings left unfixed", "a newline in the objective must not start a line of its own")
            .And.NotContain("\nfake commit line");
        page.Split('\n').Should().NotContain(line => line.StartsWith("Fake line", StringComparison.Ordinal));
    }

    [Fact]
    public void The_summary_is_the_first_paragraph_of_the_body_that_is_not_a_heading()
    {
        string? paragraph = HumanGateBriefing.FirstParagraph(
            "Title: A title\n\n## What this does\n\nThe status pane now lists parked runs first.\n\nSecond paragraph.");

        paragraph.Should().Be("The status pane now lists parked runs first.");
    }

    [Fact]
    public void A_paragraph_that_opens_under_a_heading_on_the_next_line_still_counts_without_its_heading()
    {
        HumanGateBriefing.FirstParagraph("Title: T\n\n## Summary\nThe pane lists parked runs first.\nIt also counts them.\n\nMore.")
            .Should().Be("The pane lists parked runs first. It also counts them.");
    }

    [Fact]
    public void A_line_that_starts_with_a_hash_but_is_not_a_heading_is_prose_and_opens_the_paragraph()
    {
        HumanGateBriefing.FirstParagraph("Title: T\n\n#412 now lists parked runs first.\n\nSecond.")
            .Should().Be("#412 now lists parked runs first.");
    }

    [Fact]
    public void A_summary_with_only_a_title_or_headings_has_no_paragraph()
    {
        HumanGateBriefing.FirstParagraph("Title: T").Should().BeNull();
        HumanGateBriefing.FirstParagraph("Title: T\n\n# Heading only").Should().BeNull();
        HumanGateBriefing.FirstParagraph(null).Should().BeNull();
        HumanGateBriefing.FirstParagraph("   ").Should().BeNull();
    }

    [Fact]
    public void A_summary_over_150_words_is_cut_at_150_with_a_pointer_to_the_full_file()
    {
        string page = HumanGateBriefing.Render(Facts(summary: $"Title: T\n\n{Words(200)}\n\nTail."));

        page.Should().Contain("w150 ...").And.NotContain("w151");
        page.Should().Contain("Cut at 150 words").And.Contain("/runs/pr-summary.md");
    }

    [Fact]
    public void A_summary_of_exactly_150_words_is_printed_whole_with_no_pointer()
    {
        string page = HumanGateBriefing.Render(Facts(summary: $"Title: T\n\n{Words(150)}"));

        page.Should().Contain("w150").And.NotContain("Cut at");
    }

    [Fact]
    public void A_run_with_no_summary_says_so_and_still_prints_everything_else()
    {
        string page = HumanGateBriefing.Render(Facts(summary: null));

        page.Should().Contain("No pull request summary was written");
        page.Should().Contain("What was asked for").And.Contain("How automated review settled")
            .And.Contain("The commits, oldest first").And.Contain("Your choices");
    }

    [Fact]
    public void The_sections_come_in_the_fixed_order()
    {
        string page = HumanGateBriefing.Render(Facts());

        string[] headings =
        [
            "What changed", "What was asked for", "How automated review settled", "The commits, oldest first",
            "Where it is", "Compare,", "Run it", "Step in", "Your choices",
        ];
        int[] positions = [.. headings.Select(heading => page.IndexOf(heading, StringComparison.Ordinal))];
        positions.Should().OnlyContain(position => position >= 0);
        positions.Should().BeInAscendingOrder();
    }

    [Fact]
    public void Review_is_described_by_the_settlement_word_the_cycle_count_and_the_unfixed_findings_with_the_report()
    {
        string page = HumanGateBriefing.Render(Facts());

        page.Should().Contain("settled, after 2 review cycles, with 1 finding left unfixed.")
            .And.Contain("The merged report is /runs/review-2-findings.md.");
    }

    [Fact]
    public void The_merged_report_is_pointed_at_whenever_a_cycle_ran_and_not_when_none_did()
    {
        HumanGateBriefingFacts none = Facts() with { UnfixedFindings = 0 };
        HumanGateBriefing.Render(none).Should().Contain("with no findings left unfixed.")
            .And.Contain("The merged report is /runs/review-2-findings.md.");

        HumanGateBriefing.Render(none with { ReviewCycles = 0 }).Should().Contain("after no review cycles")
            .And.NotContain("merged report");
    }

    [Fact]
    public void Commits_are_listed_oldest_first_with_a_total_and_collapse_to_a_count_past_twenty()
    {
        string listed = HumanGateBriefing.Render(Facts(commits: 20));
        listed.IndexOf("commit number 1", StringComparison.Ordinal).Should().BeLessThan(
            listed.IndexOf("commit number 20", StringComparison.Ordinal));
        listed.Should().Contain("In total 7 files and 123 lines changed.");

        string collapsed = HumanGateBriefing.Render(Facts(commits: 21));
        collapsed.Should().Contain("21 commits, too many to list here").And.NotContain("commit number 1\n");
    }

    [Fact]
    public void The_page_names_the_worktree_branch_base_and_head_and_gives_difftool_lines_for_the_whole_change_and_each_commit()
    {
        string page = HumanGateBriefing.Render(Facts(commits: 2));

        page.Should().Contain("Worktree: /wt/status pane").And.Contain("Branch: task/status")
            .And.Contain("Base commit: base0").And.Contain("Head commit: head0");
        page.Should().Contain("git -C \"/wt/status pane\" difftool --dir-diff base0000000000000000000000000000000000000 head0000000000000000000000000000000000000");
        page.Should().Contain("010000000000^ 010000000000").And.Contain("020000000000^ 020000000000");
        page.Should().NotContain("--tool=", "hall9k records no editor or tool setting of its own");
    }

    [Fact]
    public void With_a_run_skill_the_page_names_run_local_and_without_one_it_says_so_and_names_the_command_that_asks()
    {
        HumanGateBriefing.Render(Facts(runSkill: true)).Should().Contain($"h9k task run-local {TaskId}");

        string without = HumanGateBriefing.Render(Facts(runSkill: false));
        without.Should().Contain("has no run skill").And.Contain("h9k project set hall9k --discover-run-skill")
            .And.Contain("Every outcome below works without it").And.NotContain("h9k task run-local");
    }

    [Fact]
    public void The_summary_is_older_than_the_branch_only_when_the_tip_was_authored_after_it_was_written()
    {
        HumanGateBriefing.Render(Facts(tipAuthoredAt: Written.AddMinutes(5))).Should().Contain("older than the branch");
        HumanGateBriefing.Render(Facts(tipAuthoredAt: Written.AddMinutes(-5))).Should().NotContain("older than the branch");
        HumanGateBriefing.Render(Facts(tipAuthoredAt: null)).Should().NotContain("older than the branch");
        HumanGateBriefing.SummaryIsOlderThanBranch(null, Written).Should().BeFalse();
    }

    [Fact]
    public void The_gate_page_never_names_interactive_mode_and_offers_the_discard_commands()
    {
        string page = HumanGateBriefing.Render(Facts());

        page.Should().NotContain("--clear-interactive-mode").And.NotContainEquivalentOf("interactive mode");
        page.Should().Contain($"h9k task abandon {TaskId}").And.Contain($"h9k task add --evolved-from {TaskId}")
            .And.Contain($"h9k review proceed {TaskId}").And.Contain($"h9k review fixed {TaskId}")
            .And.Contain($"h9k review resolve {TaskId} --needs-fixes")
            .And.Contain($"h9k review resolve {TaskId} --merge-ready --reason");
    }

    [Fact]
    public void An_interactive_mode_stop_is_named_as_such_and_offers_its_own_choices()
    {
        string page = HumanGateBriefing.Render(Facts(
            stop: HumanGateBriefing.Stop.InteractiveMode, levers: InteractiveBoundaryLevers.GatesToPullRequest));

        page.Should().Contain("interactive mode's gates-to-pull-request boundary");
        page.Should().Contain($"h9k task revise {TaskId} --clear-interactive-mode");
    }
}
