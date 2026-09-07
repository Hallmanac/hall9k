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

    [Fact]
    public void A_build_session_on_a_stacked_child_hunts_and_recomposes_against_the_parent_branch()
    {
        string prompt = AgentPromptBuilder.Build(
            SomeTask(), SomeProject(), "task/child-slice-two", worktreePath: "/tmp/wt",
            baseBranch: ParentBranch);

        prompt.Should().Contain($"git diff origin/{ParentBranch}...HEAD",
            "the self-review hunt must read this branch's own delta, not the parent's work with it");
        prompt.Should().Contain($"git merge-base origin/{ParentBranch} HEAD",
            "the recompose's fork point is the parent's tip — resetting past it would rewrite the parent's commits");
        prompt.Should().NotContain("origin/main...HEAD");
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
}
