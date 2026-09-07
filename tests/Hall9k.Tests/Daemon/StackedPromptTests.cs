using FluentAssertions;
using Hall9k.Connectors.Prompts;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// A stacked child's own delta is what its sessions read (task: a stacked pull-request edge exists
/// as an explicit opt-in dependency). Every prompt that names a base branch has to name the
/// PARENT's, not the project's — otherwise a reviewer reads the parent's already-reviewed work as
/// part of this branch, and, worse, the build session's own recompose resets to a fork point below
/// the parent's commits and recomposes them as if they were this task's history.
/// </summary>
public sealed class StackedPromptTests
{
    private const string ParentBranch = "task/parent-slice-one";
    private const string ForkPoint = "0f1e2d3c4b5a69788796a5b4c3d2e1f009182736";

    [Fact]
    public void A_build_session_on_a_stacked_child_hunts_and_recomposes_from_its_recorded_fork_point()
    {
        string prompt = AgentPromptBuilder.Build(
            SomeTask(), SomeProject(), "task/child-slice-two", worktreePath: "/tmp/wt",
            baseBranch: ParentBranch, baseCommit: ForkPoint);

        prompt.Should().Contain($"git diff {ForkPoint}...HEAD",
            "the self-review hunt must read this branch's own delta from a commit the parent cannot move");
        prompt.Should().Contain($"git rev-parse --verify \"{ForkPoint}^{{commit}}\"",
            "the recompose resets to the recorded fork point, verified rather than computed");
        prompt.Should().NotContain($"git merge-base origin/{ParentBranch} HEAD",
            "a merge base against a force-pushed parent collapses below the real fork point, and the "
            + "mixed reset would then recompose the parent's commits as this branch's own history");
        prompt.Should().NotContain("origin/main...HEAD");
    }

    [Fact]
    public void A_stacked_child_with_no_recorded_fork_point_falls_back_to_the_parent_branch()
    {
        string prompt = AgentPromptBuilder.Build(
            SomeTask(), SomeProject(), "task/child-slice-two", worktreePath: "/tmp/wt",
            baseBranch: ParentBranch);

        prompt.Should().Contain($"git diff origin/{ParentBranch}...HEAD",
            "with no observed commit to name, the parent branch is still a better answer than the project's");
        prompt.Should().Contain($"git merge-base origin/{ParentBranch} HEAD",
            "and a merge base against it is the best available fork point rather than a guessed commit");
        prompt.Should().NotContain("origin/main...HEAD");
    }

    [Fact]
    public void An_unstacked_build_session_ignores_a_recorded_fork_point()
    {
        string prompt = AgentPromptBuilder.Build(
            SomeTask(), SomeProject(), "task/ordinary", worktreePath: "/tmp/wt",
            baseBranch: "main", baseCommit: ForkPoint);

        prompt.Should().Contain("git diff origin/main...HEAD",
            "the project's own base branch does not get rewritten under a run, so the ref is stable");
        prompt.Should().Contain("git merge-base origin/main HEAD");
        prompt.Should().NotContain(ForkPoint);
    }

    [Fact]
    public void An_unstacked_build_session_still_reads_the_project_base_branch()
    {
        string prompt = AgentPromptBuilder.Build(
            SomeTask(), SomeProject(), "task/ordinary", worktreePath: "/tmp/wt");

        prompt.Should().Contain("git diff origin/main...HEAD");
        prompt.Should().Contain("git merge-base origin/main HEAD");
    }

    [Fact]
    public void A_review_pass_on_a_stacked_child_reads_the_diff_against_the_parent_branch()
    {
        string prompt = AgentPromptBuilder.BuildReview(
            SomeTask(), SomeProject(), "task/child-slice-two", cycle: 1, ReviewLens.Adversarial,
            mechanicsOverride: new AgentPromptBuilder.ReviewMechanicsOverride(ParentBranch));

        prompt.Should().Contain($"git diff origin/{ParentBranch}...HEAD",
            "so its reviewers see only the child's own delta");
        prompt.Should().Contain("You are in the implementation's git worktree",
            "a stacked child really is on its own branch in its own worktree — only its base differs");
    }

    /// <summary>
    /// The base-only override must not drag the foreign-pull-request behaviour along with it: a
    /// stacked child's own gates really did run, and its acceptance criteria really are the standard
    /// its diff is judged against.
    /// </summary>
    [Fact]
    public void The_base_only_mechanics_override_changes_nothing_but_the_base()
    {
        AgentPromptBuilder.ReviewMechanicsOverride stacked = new(ParentBranch);

        stacked.CheckoutDescription.Should().BeNull("the ordinary on-branch wording is right here");
        stacked.GatesObserved.Should().BeTrue("this run's own gates did run");
        stacked.DiffIsForeignPullRequest.Should().BeFalse("the diff is this task's own work");
    }

    /// <summary>
    /// The judgment-session rebase a stacked child reaches when closeout could not observe its
    /// parent that sweep (<c>StackedParentVerdict.Unobservable</c>) and GitHub's own conflict read
    /// dispatched a <c>FollowUpKind.Rebase</c> follow-up instead of the mechanical replay
    /// (independent pre-PR review, cycle 2, adversarial lens): a plain merge-base rebase onto the
    /// parent's branch is the same provably wrong operation the pre-final-pass gate refuses
    /// outright, so this prompt has to teach the replay instead.
    /// </summary>
    [Fact]
    public void A_rebase_follow_up_on_a_stacked_child_replays_from_its_recorded_fork_point()
    {
        string prompt = AgentPromptBuilder.BuildRebase(
            SomeTask(), NarrativeProject(), "task/child-slice-two", "https://github.com/x/y/pull/8",
            CommitStyle.Narrative, baseBranch: ParentBranch, baseCommit: ForkPoint);

        prompt.Should().Contain(
            $"git rebase --onto origin/{ParentBranch} {ForkPoint} task/child-slice-two",
            "the boundary is the recorded fork point, which is what keeps the parent's own commits out of the replay");
        prompt.Should().NotContain($"`git rebase origin/{ParentBranch}`, resolving each conflict",
            "a merge-base rebase onto a force-pushed parent replays this branch's copies of the parent's old commits");
        prompt.Should().Contain("git rev-parse origin/task/parent-slice-one",
            "the commit the replay lands on is this branch's boundary afterwards, so it has to be recorded");
        prompt.Should().Contain("git rebase -i --autosquash <the commit you recorded before the replay>",
            "a gate fix folds back to where the replay landed — not to the ref, and not to the pre-replay "
            + "fork point, which the replay has just moved this branch off");
        prompt.Should().NotContain($"--autosquash origin/{ParentBranch}");
        prompt.Should().NotContain($"--autosquash {ForkPoint}");
        prompt.Should().Contain("the branch this task is stacked on",
            "the opening line must not claim other work merged into the project's base");
    }

    [Fact]
    public void A_rebase_follow_up_on_a_stacked_child_with_no_fork_point_disputes_rather_than_guessing()
    {
        string prompt = AgentPromptBuilder.BuildRebase(
            SomeTask(), NarrativeProject(), "task/child-slice-two", "https://github.com/x/y/pull/8",
            CommitStyle.Narrative, baseBranch: ParentBranch);

        prompt.Should().Contain("do not rebase this branch",
            "with no recorded boundary there is nothing to replay from that is not a guess");
        prompt.Should().NotContain($"`git rebase origin/{ParentBranch}`, resolving each conflict");
        prompt.Should().NotContain($"git rebase --onto origin/{ParentBranch}",
            "and no --onto either, since the boundary argument would have to be invented");
        prompt.Should().Contain(AgentPromptBuilder.DisputeMarker,
            "the dispute path is where an unobserved boundary belongs");
    }

    /// <summary>
    /// The stacked treatment is additive: an ordinary run's rebase prompt is byte-identical whether
    /// or not a fork point was recorded for it, which is what keeps this feature off every
    /// unstacked run.
    /// </summary>
    [Fact]
    public void An_unstacked_rebase_follow_up_is_unchanged_by_a_recorded_fork_point()
    {
        // One task instance for both, since the prompt embeds the task's own id.
        TaskDetails task = SomeTask();
        string withCommit = AgentPromptBuilder.BuildRebase(
            task, NarrativeProject(), "task/ordinary", "https://github.com/x/y/pull/8",
            CommitStyle.Narrative, baseBranch: "main", baseCommit: ForkPoint);
        string without = AgentPromptBuilder.BuildRebase(
            task, NarrativeProject(), "task/ordinary", "https://github.com/x/y/pull/8",
            CommitStyle.Narrative);

        withCommit.Should().Be(without);
        withCommit.Should().Contain("`git rebase origin/main`, resolving each conflict");
        withCommit.Should().Contain("git rebase -i --autosquash origin/main");
        withCommit.Should().NotContain(ForkPoint);
    }

    [Fact]
    public void A_review_feedback_follow_up_on_a_stacked_child_folds_onto_its_recorded_fork_point()
    {
        string prompt = AgentPromptBuilder.BuildFollowUp(
            SomeTask(), NarrativeProject(), "task/child-slice-two", "https://github.com/x/y/pull/8",
            CommitStyle.Narrative, baseBranch: ParentBranch, baseCommit: ForkPoint);

        prompt.Should().Contain($"git rebase -i --autosquash {ForkPoint}",
            "an autosquash onto a force-pushed parent branch folds the parent's work into this branch's history");
        prompt.Should().NotContain($"--autosquash origin/{ParentBranch}");
    }

    [Fact]
    public void A_failing_checks_follow_up_on_a_stacked_child_folds_onto_its_recorded_fork_point()
    {
        string prompt = AgentPromptBuilder.BuildFixChecks(
            SomeTask(), NarrativeProject(), "task/child-slice-two", "https://github.com/x/y/pull/8",
            CommitStyle.Narrative, baseBranch: ParentBranch, baseCommit: ForkPoint);

        prompt.Should().Contain($"git rebase -i --autosquash {ForkPoint}");
        prompt.Should().NotContain($"--autosquash origin/{ParentBranch}");
    }

    [Fact]
    public void An_unstacked_follow_up_still_folds_onto_the_project_base_branch()
    {
        string prompt = AgentPromptBuilder.BuildFollowUp(
            SomeTask(), NarrativeProject(), "task/ordinary", "https://github.com/x/y/pull/8",
            CommitStyle.Narrative, baseCommit: ForkPoint);

        prompt.Should().Contain("git rebase -i --autosquash origin/main");
        prompt.Should().NotContain(ForkPoint);
    }

    /// <summary>
    /// A replay's own gate fix folds onto the commit the replay just landed on, never onto the
    /// parent's branch ref — the same reason the replay itself is keyed to a commit.
    /// </summary>
    [Fact]
    public void A_replay_onto_a_moved_parent_head_folds_a_gate_fix_onto_that_head()
    {
        string prompt = AgentPromptBuilder.BuildStackReplay(
            SomeTask(), NarrativeProject(), "task/child-slice-two", "https://github.com/x/y/pull/8",
            baseBranch: ParentBranch, upstreamCommit: "deadbee", ontoCommit: "cafe111");

        prompt.Should().Contain("git rebase -i --autosquash cafe111");
        prompt.Should().NotContain($"--autosquash origin/{ParentBranch}");
    }

    [Fact]
    public void A_replay_after_a_merged_parent_folds_a_gate_fix_onto_the_project_base_branch()
    {
        string prompt = AgentPromptBuilder.BuildStackReplay(
            SomeTask(), NarrativeProject(), "task/child-slice-two", "https://github.com/x/y/pull/8",
            baseBranch: "main", upstreamCommit: "deadbee", ontoCommit: "cafe111");

        prompt.Should().Contain("git rebase -i --autosquash origin/main",
            "the pull request is retargeted onto the project's own base by then, and that ref only moves forward");
    }

    [Fact]
    public void A_replay_prompt_names_the_boundary_commit_and_forbids_new_intent()
    {
        string prompt = AgentPromptBuilder.BuildStackReplay(
            SomeTask(), SomeProject(), "task/child-slice-two", "https://github.com/x/y/pull/8",
            baseBranch: "main", upstreamCommit: "abc1234", ontoCommit: "ffff999");

        prompt.Should().Contain("git rebase --onto ffff999 abc1234 task/child-slice-two",
            "the boundary is what drops the parent's commits instead of replaying them onto a base that holds them");
        prompt.Should().Contain("git log --oneline abc1234..HEAD",
            "so the session can check nothing of its own was lost");
        prompt.Should().NotContain("--onto origin/main",
            "a ref would land the replay wherever it had drifted to, not where closeout actually looked");
        prompt.Should().Contain("no new intent here and you must not add any",
            "nothing reviews a replay, so the prompt cannot leave room for judgment");
        prompt.Should().Contain("Do NOT push", "the daemon pushes, never the agent");
        prompt.Should().NotContain("open a new pull request");
    }

    /// <summary>
    /// A replay onto a moved parent head, rather than onto the base after a merge: same operation,
    /// different <c>--onto</c>, which is the whole reason both triggers share one follow-up kind.
    /// </summary>
    [Fact]
    public void A_replay_onto_a_moved_parent_head_targets_the_parent_branch()
    {
        string prompt = AgentPromptBuilder.BuildStackReplay(
            SomeTask(), SomeProject(), "task/child-slice-two", "https://github.com/x/y/pull/8",
            baseBranch: ParentBranch, upstreamCommit: "deadbee", ontoCommit: "cafe111");

        prompt.Should().Contain("git rebase --onto cafe111 deadbee task/child-slice-two");
        prompt.Should().Contain(ParentBranch, "the branch is still named, so the session knows where it is landing");
    }

    [Fact]
    public void A_replay_prompt_still_requires_the_projects_own_gates()
    {
        ProjectDetails project = SomeProject();
        project.VerifyCommands = [new VerifyCommand("build", "dotnet build")];

        string prompt = AgentPromptBuilder.BuildStackReplay(
            SomeTask(), project, "task/child-slice-two", "https://github.com/x/y/pull/8",
            baseBranch: "main", upstreamCommit: "abc1234", ontoCommit: "ffff999");

        prompt.Should().Contain("re-run the project's verification gates",
            "the replay runs the gates even though no review cycle reads it");
        prompt.Should().Contain("dotnet build");
    }

    /// <summary>
    /// <c>RunDetails.BaseBranch</c>'s blank-means-the-project's-own invariant is what lets the CLI's
    /// composers tell a stacked run from an ordinary one with no project in hand, so it is asserted
    /// directly rather than only through the surfaces that depend on it.
    /// </summary>
    [Fact]
    public void A_run_with_no_recorded_base_reads_as_the_projects_own()
    {
        RunDetails run = new();

        run.BaseBranch.Should().BeEmpty();
        run.BaseBranchOr("main").Should().Be("main");
        run.StackedOnBranch.Should().BeNull();
        run.AwaitsStackedRetarget("main").Should().BeFalse();
    }

    [Fact]
    public void A_stacked_run_reports_its_parent_branch_and_awaits_a_retarget()
    {
        RunDetails run = new() { BaseBranch = ParentBranch };

        run.BaseBranchOr("main").Should().Be(ParentBranch);
        run.StackedOnBranch.Should().Be(ParentBranch);
        run.AwaitsStackedRetarget("main").Should().BeTrue(
            "an un-retargeted stacked pull request is not at the merge bar");
    }

    /// <summary>
    /// A recorded base that happens to equal the project's own is not a stack — the belt-and-braces
    /// arm for a pull request a human retargeted by hand, or a project base changed under a live run.
    /// </summary>
    [Fact]
    public void A_run_whose_recorded_base_is_the_projects_own_awaits_nothing()
    {
        RunDetails run = new() { BaseBranch = "main" };

        run.AwaitsStackedRetarget("main").Should().BeFalse();
    }

    [Fact]
    public void A_successful_retarget_clears_the_recorded_base_back_to_the_projects_own()
    {
        RunDetailsProjection projection = new();
        RunDetails view = projection.Create(new FakeEvent<RunDispatched>(Dispatched(ParentBranch)));
        view.StackedOnBranch.Should().Be(ParentBranch);

        projection.Apply(
            new FakeEvent<StackedPullRequestRetargeted>(new StackedPullRequestRetargeted(
                view.Id, ParentBranch, "main", "abc1234", Succeeded: true, "retargeted", DateTimeOffset.UtcNow)),
            view);

        view.StackedOnBranch.Should().BeNull(
            "blank is what BaseBranch means by the project's own base, and the invariant has to hold");
        view.LastStackedRetargetSucceeded.Should().BeTrue();
    }

    [Fact]
    public void A_failed_retarget_leaves_the_recorded_base_where_it_was()
    {
        RunDetailsProjection projection = new();
        RunDetails view = projection.Create(new FakeEvent<RunDispatched>(Dispatched(ParentBranch)));

        projection.Apply(
            new FakeEvent<StackedPullRequestRetargeted>(new StackedPullRequestRetargeted(
                view.Id, ParentBranch, "main", "abc1234", Succeeded: false, "gh pr edit failed",
                DateTimeOffset.UtcNow)),
            view);

        view.StackedOnBranch.Should().Be(ParentBranch,
            "the pull request is still aimed at the parent, so it is still off the merge bar");
        view.LastStackedRetargetSucceeded.Should().BeFalse();
        view.LastStackedRetargetDetail.Should().Contain("gh pr edit failed");
    }

    private static RunDispatched Dispatched(string baseBranch) => new(
        DomainId.New(), DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
        "/tmp/wt", "task/child-slice-two", ExecutorMode.Subscription, DateTimeOffset.UtcNow,
        BaseBranch: baseBranch);

    private static TaskDetails SomeTask() => new()
    {
        Id = DomainId.New(),
        Objective = "Slice two of one idea",
        AcceptanceCriteria = ["It works"],
    };

    private static ProjectDetails SomeProject() => new()
    {
        Name = "hall9k",
        BaseBranch = "main",
    };

    /// <summary>
    /// The narrative commit style's fixup-fold instruction only appears when the project has gates
    /// to re-run after a rebase, so the fold assertions above need a project that configures one.
    /// </summary>
    private static ProjectDetails NarrativeProject() => new()
    {
        Name = "hall9k",
        BaseBranch = "main",
        CommitStyle = CommitStyle.Narrative,
        VerifyCommands = [new VerifyCommand("build", "dotnet build")],
    };
}
