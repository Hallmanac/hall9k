using FluentAssertions;
using Hall9k.Daemon.Review;
using Xunit;

namespace Hall9k.Tests.Daemon.Review;

/// <summary>
/// The whole shape test of <see cref="DecisionsLogTailConflictResolver"/>, driven over its own
/// input — an unmerged-file list and a conflicted PLAN.md — with no git repository, no branch and
/// no daemon anywhere in sight, which is what the resolver being pure text-in/text-out buys
/// (task: the mechanical pre-final-pass rebase resolves a Decisions Log tail-append conflict on
/// its own). The exact shape is one case; every near miss that must still park is its own, because
/// the whole value of this step is that it declines everything it is not certain of: a wrong park
/// costs a recovery session, a wrong resolution silently rewrites authored decision text.
/// </summary>
public sealed class DecisionsLogTailConflictResolverTests
{
    private const string TaskShortId = "64ba195a";
    private const string OtherTaskShortId = "98484f36";
    private static readonly string[] OnlyPlanMarkdown = ["PLAN.md"];

    [Fact]
    public void The_exact_shape_keeps_the_bases_entry_first_and_this_branchs_placeholder_after_it()
    {
        string conflicted = PlanWithDecisionsLog(
            [
                .. EarlierEntry,
                .. ConflictHunk(BaseEntries(246), ThisBranchsPlaceholderEntry(TaskShortId)),
            ]);

        DecisionsLogTailConflictResolution resolution =
            DecisionsLogTailConflictResolver.Recognize(OnlyPlanMarkdown, conflicted, TaskShortId);

        resolution.IsTailAppendShape.Should().BeTrue();
        resolution.KeptBaseEntryNumbers.Should().Equal(246);
        resolution.PlaceholderToken.Should().Be("PLACEHOLDER-64ba195a");
        resolution.Explanation.Should().Contain("#246").And.Contain("PLACEHOLDER-64ba195a");

        string resolved = resolution.ResolvedPlanMarkdown!;
        resolved.Should().NotContain("<<<<<<<").And.NotContain("=======").And.NotContain(">>>>>>>");
        resolved.Should().Contain("245. **").And.Contain("246. **");
        resolved.IndexOf("246. **", StringComparison.Ordinal).Should().BeLessThan(
            resolved.IndexOf("PLACEHOLDER-64ba195a. **", StringComparison.Ordinal),
            "the base's entry keeps its place and this branch's placeholder goes after it");

        // The renumbering step that runs moments later has to recognise what this left behind, and
        // it asks this exact question — so asserting it here is asserting the two steps agree.
        DecisionsLogRenumberer.TailEntryIsThisTasksUnresolvedPlaceholder(resolved, TaskShortId)
            .Should().BeTrue();
    }

    [Fact]
    public void A_two_entry_base_side_keeps_both_in_the_order_the_base_had_them()
    {
        string conflicted = PlanWithDecisionsLog(
            [
                .. EarlierEntry,
                .. ConflictHunk(BaseEntries(246, 247), ThisBranchsPlaceholderEntry(TaskShortId)),
            ]);

        DecisionsLogTailConflictResolution resolution =
            DecisionsLogTailConflictResolver.Recognize(OnlyPlanMarkdown, conflicted, TaskShortId);

        resolution.IsTailAppendShape.Should().BeTrue();
        resolution.KeptBaseEntryNumbers.Should().Equal(246, 247);

        string resolved = resolution.ResolvedPlanMarkdown!;
        resolved.IndexOf("246. **", StringComparison.Ordinal).Should().BeLessThan(
            resolved.IndexOf("247. **", StringComparison.Ordinal));
        resolved.IndexOf("247. **", StringComparison.Ordinal).Should().BeLessThan(
            resolved.IndexOf("PLACEHOLDER-64ba195a. **", StringComparison.Ordinal));
        DecisionsLogRenumberer.TailEntryIsThisTasksUnresolvedPlaceholder(resolved, TaskShortId)
            .Should().BeTrue();
    }

    /// <summary>
    /// An insertion in the middle of the log: the base's own side is not the tail, because a later
    /// entry follows the conflict. Resolving that as "base first, branch after" would put this
    /// branch's placeholder in the middle of the log rather than at its end, which is the one place
    /// the numbering convention says it belongs.
    /// </summary>
    [Fact]
    public void A_conflict_in_the_middle_of_the_log_parks()
    {
        string conflicted = PlanWithDecisionsLog(
            [
                .. EarlierEntry,
                .. ConflictHunk(BaseEntries(246), ThisBranchsPlaceholderEntry(TaskShortId)),
                "",
                "248. **An entry that already sits after the conflict.** Why: it does.",
            ]);

        AssertParks(
            DecisionsLogTailConflictResolver.Recognize(OnlyPlanMarkdown, conflicted, TaskShortId),
            "content follows the conflict");
    }

    /// <summary>The same refusal for a conflict that is not in the Decisions Log at all.</summary>
    [Fact]
    public void A_conflict_elsewhere_in_PLAN_md_parks()
    {
        string conflicted = PlanWithConflictBeforeTheDecisionsLog(
            ConflictHunk(["- the base's own bullet"], ["- this branch's own bullet"]));

        AssertParks(
            DecisionsLogTailConflictResolver.Recognize(OnlyPlanMarkdown, conflicted, TaskShortId),
            "not inside the Decisions Log section");
    }

    [Fact]
    public void A_second_conflicted_file_parks_however_right_PLAN_mds_own_hunk_looks()
    {
        string conflicted = PlanWithDecisionsLog(
            [
                .. EarlierEntry,
                .. ConflictHunk(BaseEntries(246), ThisBranchsPlaceholderEntry(TaskShortId)),
            ]);

        AssertParks(
            DecisionsLogTailConflictResolver.Recognize(
                ["PLAN.md", "src/Hall9k.Daemon/Review/ReviewEngine.cs"], conflicted, TaskShortId),
            "2 files conflicted");
    }

    [Fact]
    public void A_second_conflict_hunk_in_PLAN_md_parks()
    {
        string conflicted = PlanWithDecisionsLog(
            [
                .. ConflictHunk(["245. **The base rewrote this one.** Why: it did."], ["245. **So did this branch.** Why: it did."]),
                "",
                .. ConflictHunk(BaseEntries(246), ThisBranchsPlaceholderEntry(TaskShortId)),
            ]);

        AssertParks(
            DecisionsLogTailConflictResolver.Recognize(OnlyPlanMarkdown, conflicted, TaskShortId),
            "exactly one well-formed conflict hunk");
    }

    /// <summary>
    /// Another task's placeholder is that task's branch's business, never this one's — the
    /// question is put to <see cref="DecisionsLogRenumberer"/>'s own helper, so this is the same
    /// refusal the renumbering step would make of the same entry.
    /// </summary>
    [Fact]
    public void A_placeholder_that_is_not_this_tasks_own_parks()
    {
        string conflicted = PlanWithDecisionsLog(
            [
                .. EarlierEntry,
                .. ConflictHunk(BaseEntries(246), ThisBranchsPlaceholderEntry(OtherTaskShortId)),
            ]);

        AssertParks(
            DecisionsLogTailConflictResolver.Recognize(OnlyPlanMarkdown, conflicted, TaskShortId),
            "does not leave this task's own unresolved placeholder");
    }

    [Theory]
    [InlineData("the rest of a paragraph whose own entry heading is above the conflict marker.", "incomplete or unnumbered")]
    [InlineData("**A decision nobody numbered.** Why: it was written by hand.", "incomplete or unnumbered")]
    public void A_base_side_that_is_not_whole_numbered_entries_parks(string baseSideLine, string expectedReason)
    {
        string conflicted = PlanWithDecisionsLog(
            [
                .. EarlierEntry,
                .. ConflictHunk([baseSideLine], ThisBranchsPlaceholderEntry(TaskShortId)),
            ]);

        AssertParks(
            DecisionsLogTailConflictResolver.Recognize(OnlyPlanMarkdown, conflicted, TaskShortId),
            expectedReason);
    }

    [Fact]
    public void A_placeholder_entry_with_no_placement_note_parks()
    {
        string[] noteless = ["PLACEHOLDER-64ba195a. **This branch's own decision.** Why: this task."];
        string conflicted = PlanWithDecisionsLog(
            [.. EarlierEntry, .. ConflictHunk(BaseEntries(246), noteless)]);

        AssertParks(
            DecisionsLogTailConflictResolver.Recognize(OnlyPlanMarkdown, conflicted, TaskShortId),
            "carries no placement note");
    }

    /// <summary>
    /// One conflicted file, and it is not PLAN.md — the shape a rebase's later stop takes when this
    /// branch and its base both changed the same source file. Distinct from the two-file refusal
    /// above, which never reaches the "which file is it" question at all.
    /// </summary>
    [Fact]
    public void A_single_conflicted_file_that_is_not_PLAN_md_parks()
    {
        AssertParks(
            DecisionsLogTailConflictResolver.Recognize(["src/Hall9k.Daemon/Review/ReviewEngine.cs"], "", TaskShortId),
            "not PLAN.md");
    }

    /// <summary>
    /// A base commit that also trimmed the tail of the entry ABOVE leaves that entry's orphaned
    /// prose at the head of the branch's side of the same hunk. Every other question still answers
    /// yes — the base's side is whole numbered entries at the tail, the branch's side does end on
    /// this task's own placeholder with its note — so only "does the branch's side BEGIN on that
    /// heading" catches it, and resolving it would graft a line the base deleted back in under the
    /// wrong entry while calling the stop mechanically resolved.
    /// </summary>
    [Fact]
    public void A_branch_side_carrying_the_previous_entrys_orphaned_prose_before_the_placeholder_parks()
    {
        string conflicted = PlanWithDecisionsLog(
            [
                .. EarlierEntry,
                .. ConflictHunk(
                    BaseEntries(246),
                    [
                        "A trailing sentence of #245 that the base's own commit deleted.",
                        "",
                        .. ThisBranchsPlaceholderEntry(TaskShortId),
                    ]),
            ]);

        AssertParks(
            DecisionsLogTailConflictResolver.Recognize(OnlyPlanMarkdown, conflicted, TaskShortId),
            "not exactly one entry beginning on this task's own PLACEHOLDER-64ba195a heading");
    }

    private static void AssertParks(DecisionsLogTailConflictResolution resolution, string expectedReasonFragment)
    {
        resolution.IsTailAppendShape.Should().BeFalse();
        resolution.ResolvedPlanMarkdown.Should().BeNull();
        resolution.KeptBaseEntryNumbers.Should().BeEmpty();
        resolution.Explanation.Should().Contain(
            expectedReasonFragment,
            "a park's own log line is the only place a human learns why the machine declined");
    }

    private static readonly string[] EarlierEntry =
    [
        "245. **An earlier decision both sides already had.** Why: it was here before either branch.",
        "",
    ];

    private static string[] BaseEntries(params int[] numbers) =>
    [
        .. numbers.SelectMany<int, string>((number, index) => index == 0
            ? [$"{number}. **A decision that reached the base first.** Why: it merged before this branch was rebased."]
            : ["", $"{number}. **A decision that reached the base first.** Why: it merged before this branch was rebased."]),
    ];

    private static string[] ThisBranchsPlaceholderEntry(string taskShortId) =>
    [
        $"PLACEHOLDER-{taskShortId}. **This branch's own decision.** Why: this task asked for it.",
        "",
        "> Renumbering placement note: this entry was appended under placeholder",
        $"> `PLACEHOLDER-{taskShortId}` and will be assigned its real number by the mechanical",
        "> pre-final-pass rebase step, the log's next free number once this branch is rebased.",
    ];

    private static string[] ConflictHunk(IReadOnlyList<string> baseSide, IReadOnlyList<string> branchSide) =>
    [
        "<<<<<<< HEAD",
        .. baseSide,
        "=======",
        .. branchSide,
        ">>>>>>> 1a2b3c4d (task: log this branch's own decision)",
    ];

    private static string PlanWithDecisionsLog(IReadOnlyList<string> sectionSixteenLines) =>
        string.Join(
            "\n",
            [
                "# Hall9k — Plan",
                "",
                "## 15. Something Before It",
                "",
                "- a bullet the Decisions Log does not care about",
                "",
                "## 16. v0 Decisions Log",
                "",
                .. sectionSixteenLines,
                "",
                "---",
                "",
                "## 17. Reference Materials",
                "",
                "- a reference",
                "",
            ]);

    private static string PlanWithConflictBeforeTheDecisionsLog(IReadOnlyList<string> hunk) =>
        string.Join(
            "\n",
            [
                "# Hall9k — Plan",
                "",
                "## 15. Something Before It",
                "",
                .. hunk,
                "",
                "## 16. v0 Decisions Log",
                "",
                .. EarlierEntry,
                "---",
                "",
                "## 17. Reference Materials",
                "",
                "- a reference",
                "",
            ]);
}
