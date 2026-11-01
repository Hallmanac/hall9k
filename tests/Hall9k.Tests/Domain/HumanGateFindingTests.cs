using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The human gate's moved-tip decision, as the pure function over observed facts the daemon calls. No
/// branch, no worktree, and no GitHub is involved: a proceed typed on a machine that holds no worktree is
/// judged by the daemon on the machine that does, from exactly these four facts.
/// </summary>
public sealed class HumanGateFindingTests
{
    private const string Reviewed = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Later = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void A_branch_still_at_the_reviewed_tip_with_a_clean_worktree_is_clear()
    {
        HumanGateFinding.Decide(Reviewed, Reviewed, Reviewed, [], requireUnchangedTip: true).IsClear.Should().BeTrue();
    }

    [Fact]
    public void A_moved_tip_is_a_finding_for_a_proceed_and_names_both_tips()
    {
        HumanGateFinding finding = HumanGateFinding.Decide(Reviewed, Later, Later, [], requireUnchangedTip: true);

        finding.Kind.Should().Be(HumanGateFindingKind.TipMoved);
        finding.Detail.Should().Contain("aaaaaaaaaaaa").And.Contain("bbbbbbbbbbbb");
    }

    [Fact]
    public void A_moved_tip_is_not_a_finding_for_a_merge_ready_resolve_which_owns_it()
    {
        HumanGateFinding.Decide(Reviewed, Later, Later, [], requireUnchangedTip: false).IsClear.Should().BeTrue();
    }

    [Fact]
    public void A_worktree_not_on_the_branch_tip_is_a_finding_either_way_because_the_gates_would_judge_another_commit()
    {
        foreach (bool requireUnchangedTip in new[] { true, false })
        {
            HumanGateFinding finding = HumanGateFinding.Decide(Reviewed, Later, Reviewed, [], requireUnchangedTip);

            finding.Kind.Should().Be(HumanGateFindingKind.HeadOffBranch);
            finding.Detail.Should().Contain("aaaaaaaaaaaa").And.Contain("bbbbbbbbbbbb");
        }

        HumanGateFinding.Decide(Reviewed, Later, null, [], requireUnchangedTip: false).Kind
            .Should().Be(HumanGateFindingKind.TipUnobservable);
    }

    [Fact]
    public void Uncommitted_work_is_a_finding_either_way_and_is_named()
    {
        foreach (bool requireUnchangedTip in new[] { true, false })
        {
            HumanGateFinding finding = HumanGateFinding.Decide(
                Reviewed, Reviewed, Reviewed, ["src/A.cs", "tests/ATests.cs"], requireUnchangedTip);

            finding.Kind.Should().Be(HumanGateFindingKind.UncommittedWork);
            finding.Detail.Should().Contain("src/A.cs").And.Contain("tests/ATests.cs");
        }
    }

    [Fact]
    public void A_long_list_of_uncommitted_paths_is_named_up_to_a_cap_and_then_counted()
    {
        string[] files = [.. Enumerable.Range(0, 25).Select(index => $"src/F{index}.cs")];

        HumanGateFinding finding = HumanGateFinding.Decide(Reviewed, Reviewed, Reviewed, files, requireUnchangedTip: true);

        finding.Detail.Should().Contain("src/F19.cs").And.NotContain("src/F20.cs").And.Contain("and 5 more");
    }

    [Fact]
    public void An_unreadable_tip_or_status_counts_against_the_gate_never_for_it()
    {
        HumanGateFinding.Decide(Reviewed, null, null, [], requireUnchangedTip: true).Kind
            .Should().Be(HumanGateFindingKind.TipUnobservable);
        HumanGateFinding.Decide(null, Reviewed, Reviewed, [], requireUnchangedTip: true).Kind
            .Should().Be(HumanGateFindingKind.TipUnobservable);
        HumanGateFinding.Decide(Reviewed, Reviewed, Reviewed, null, requireUnchangedTip: true).Kind
            .Should().Be(HumanGateFindingKind.StatusUnobservable);
        HumanGateFinding.Decide(Reviewed, Reviewed, Reviewed, null, requireUnchangedTip: false).IsClear
            .Should().BeFalse("an unreadable status is not a clean one, even when the tip may move");
    }

    [Fact]
    public void The_repark_reason_names_what_was_found_and_the_two_levers_that_answer_it()
    {
        Guid taskId = Guid.Parse("01a19510-0000-7000-8000-0000000000aa");

        foreach (HumanGateFinding finding in new[]
        {
            HumanGateFinding.Decide(Reviewed, Later, Later, [], requireUnchangedTip: true),
            HumanGateFinding.Decide(Reviewed, Reviewed, Reviewed, ["src/A.cs"], requireUnchangedTip: true),
        })
        {
            string reason = finding.ReparkReason(taskId);

            reason.Should().Contain(finding.Detail)
                .And.Contain($"h9k review fixed {taskId}")
                .And.Contain($"h9k review resolve {taskId} --merge-ready");
        }
    }

    [Fact]
    public void The_repark_reason_offers_each_finding_only_the_levers_that_fit_it()
    {
        Guid taskId = Guid.Parse("01a19510-0000-7000-8000-0000000000aa");

        string uncommitted = HumanGateFinding.Decide(Reviewed, Reviewed, Reviewed, ["src/A.cs"], requireUnchangedTip: true).ReparkReason(taskId);
        uncommitted.Should().Contain("If you discarded it").And.Contain($"h9k review proceed {taskId}");

        string noStatus = HumanGateFinding.Decide(Reviewed, Reviewed, Reviewed, null, requireUnchangedTip: true).ReparkReason(taskId);
        noStatus.Should().Contain("Check the worktree yourself").And.NotContain("Commit the work or discard it");

        string noTip = HumanGateFinding.Decide(Reviewed, null, null, [], requireUnchangedTip: true).ReparkReason(taskId);
        noTip.Should().Contain("Check the branch yourself")
            .And.NotContain("review proceed", "a proceed cannot compare a tip that could not be read")
            .And.NotContain("Hand the commits to review");

        string moved = HumanGateFinding.Decide(Reviewed, Later, Later, [], requireUnchangedTip: true).ReparkReason(taskId);
        moved.Should().Contain("Hand the commits to review").And.NotContain("review proceed");
    }
}
