using FluentAssertions;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Xunit;

namespace Hall9k.Tests.Daemon;

public sealed class AgentPromptBuilderTests : IDisposable
{
    private readonly string _worktreePath =
        Path.Combine(Path.GetTempPath(), $"hall9k-prompt-{Guid.NewGuid():N}");

    public AgentPromptBuilderTests() => Directory.CreateDirectory(_worktreePath);

    public void Dispose() => Directory.Delete(_worktreePath, recursive: true);

    [Fact]
    public void Working_rules_name_each_repo_skill_with_its_when_to_use_guidance()
    {
        WriteSkill("commit-plan", "Organize changes into cohesive commits. Use before committing multi-part work.");
        WriteSkill("pr-summary", "Generate a PR title and description. Use when the branch's work is finished.");

        string prompt = AgentPromptBuilder.Build(SomeTask(), SomeProject(), "task/1-slug", _worktreePath);

        int workingRulesAt = prompt.IndexOf("## Working rules", StringComparison.Ordinal);
        workingRulesAt.Should().BeGreaterThan(-1);
        prompt.IndexOf("`commit-plan`", StringComparison.Ordinal).Should().BeGreaterThan(
            workingRulesAt, "skills belong to the working-rules section");

        prompt.Should().Contain(
            "  - `commit-plan` — Organize changes into cohesive commits. Use before committing multi-part work.");
        prompt.Should().Contain(
            "  - `pr-summary` — Generate a PR title and description. Use when the branch's work is finished.");
        prompt.Should().Contain("invoke the matching one");
    }

    [Fact]
    public void Skills_are_omitted_when_the_worktree_has_no_skills_directory()
    {
        string prompt = AgentPromptBuilder.Build(SomeTask(), SomeProject(), "task/1-slug", _worktreePath);

        prompt.Should().NotContainEquivalentOf("skill");
        prompt.Should().Contain("## Working rules");
        prompt.Should().Contain("- End with a short summary: what you did, decisions made, assumptions, open questions.");
    }

    [Fact]
    public void Skills_are_omitted_when_the_skills_directory_holds_no_manifests()
    {
        Directory.CreateDirectory(Path.Combine(_worktreePath, ".claude", "skills", "empty-skill"));

        string prompt = AgentPromptBuilder.Build(SomeTask(), SomeProject(), "task/1-slug", _worktreePath);

        prompt.Should().NotContainEquivalentOf("skill");
    }

    [Fact]
    public void A_skill_without_a_frontmatter_description_is_still_named()
    {
        string skillDirectory = Path.Combine(_worktreePath, ".claude", "skills", "bare-skill");
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), "# Bare skill\n\nNo frontmatter here.\n");

        string prompt = AgentPromptBuilder.Build(SomeTask(), SomeProject(), "task/1-slug", _worktreePath);

        prompt.Should().Contain("  - `bare-skill`");
        prompt.Should().NotContain("`bare-skill` —");
    }

    [Fact]
    public void Fix_checks_prompt_targets_the_failing_ci_not_the_review_skill()
    {
        TaskDetails task = SomeTask();
        task.FollowUpReason = "CI checks failing on the pull request: build (windows-latest).";

        string prompt = AgentPromptBuilder.BuildFixChecks(
            task, SomeProject(), "task/1-slug", "https://github.com/x/y/pull/7", CommitStyle.Append);

        prompt.Should().Contain("fix the failing CI checks");
        prompt.Should().Contain("https://github.com/x/y/pull/7");
        prompt.Should().Contain("branch `task/1-slug`");
        prompt.Should().Contain("gh pr checks");
        prompt.Should().Contain("CI checks failing on the pull request: build (windows-latest).");
        prompt.Should().Contain("Do NOT push");
        prompt.Should().NotContain("resolve-copilot-reviews", "review resolution is the other follow-up kind");
    }

    [Fact]
    public void Follow_up_prompt_carries_the_dispatch_reason_when_one_was_recorded()
    {
        TaskDetails task = SomeTask();
        task.FollowUpReason = "2 unresolved Copilot review thread(s) on the pull request.";

        string prompt = AgentPromptBuilder.BuildFollowUp(
            task, SomeProject(), "task/1-slug", "https://github.com/x/y/pull/7", CommitStyle.Append);

        prompt.Should().Contain("2 unresolved Copilot review thread(s) on the pull request.");
        prompt.Should().Contain("resolve-copilot-reviews");
    }

    [Fact]
    public void Narrative_follow_up_demands_fixups_by_file_ownership_and_the_tree_identity_check()
    {
        string prompt = AgentPromptBuilder.BuildFollowUp(
            SomeTask(), SomeProject(), "task/1-slug", "https://github.com/x/y/pull/7", CommitStyle.Narrative);

        prompt.Should().Contain("git commit --fixup=<owning-commit>");
        prompt.Should().Contain("most recent branch commit that touches the same file",
            "the fixup mapping rule is mechanical and lives in the prompt");
        prompt.Should().Contain("one fixup per owning commit", "a fix spanning owners splits");
        prompt.Should().Contain("never \"review fixes\"");
        prompt.Should().Contain("GIT_SEQUENCE_EDITOR=: git rebase -i --autosquash origin/main");
        prompt.Should().Contain("git diff <old-tip> HEAD",
            "tree identity must be verified so gate results carry over to the force-push");
        prompt.Should().Contain("must print nothing");
        prompt.Should().Contain("--force-with-lease", "the agent must know the platform pushes the rewrite");
        prompt.Should().Contain("Do NOT push", "the platform owns the push, narrative or not");
        prompt.Should().Contain("absorb-review-fixes");
    }

    [Fact]
    public void Narrative_fix_checks_prompt_carries_the_same_commit_mechanics()
    {
        string prompt = AgentPromptBuilder.BuildFixChecks(
            SomeTask(), SomeProject(), "task/1-slug", "https://github.com/x/y/pull/7", CommitStyle.Narrative);

        prompt.Should().Contain("git commit --fixup=<owning-commit>");
        prompt.Should().Contain("GIT_SEQUENCE_EDITOR=: git rebase -i --autosquash origin/main");
        prompt.Should().Contain("git diff <old-tip> HEAD");
        prompt.Should().Contain("Do NOT push");
    }

    [Fact]
    public void Append_follow_up_keeps_the_stack_on_top_instructions_without_rebase_mechanics()
    {
        string prompt = AgentPromptBuilder.BuildFollowUp(
            SomeTask(), SomeProject(), "task/1-slug", "https://github.com/x/y/pull/7", CommitStyle.Append);

        prompt.Should().Contain("append commit style");
        prompt.Should().Contain("on top of the existing");
        prompt.Should().Contain("Do NOT push");
        prompt.Should().NotContain("--fixup", "append mode never rewrites history");
        prompt.Should().NotContain("--autosquash");
        prompt.Should().NotContain("--force-with-lease");
    }

    [Fact]
    public void Review_prompt_demands_verified_findings_with_locations_scenarios_and_a_verdict()
    {
        ProjectDetails project = SomeProject();
        project.BaseBranch = "main";

        string prompt = AgentPromptBuilder.BuildReview(SomeTask(), project, "task/1-slug", cycle: 2);

        prompt.Should().Contain("independent reviewer with fresh context");
        prompt.Should().Contain("git diff main...HEAD");
        prompt.Should().Contain("verified findings only", Exactly.Once());
        prompt.Should().Contain("read the surrounding");
        prompt.Should().Contain("discard anything you cannot confirm");
        prompt.Should().Contain("file and line");
        prompt.Should().Contain("concrete failure scenario");
        prompt.Should().Contain("VERDICT: merge-ready");
        prompt.Should().Contain("VERDICT: needs-fixes");
        prompt.Should().Contain("Do NOT modify files");
        prompt.Should().Contain("review cycle 2");
        prompt.Should().Contain("Add rate limiting to auth endpoints", "the reviewer needs the intent to judge the diff");
    }

    [Fact]
    public void Retry_prompt_warns_that_the_previous_attempts_work_may_already_be_present()
    {
        string prompt = AgentPromptBuilder.Build(
            SomeTask(), SomeProject(), "task/1-slug", _worktreePath, resumesPreviousWork: true);

        prompt.Should().Contain("A previous attempt worked here first");
        prompt.Should().Contain("uncommitted");
        prompt.Should().Contain("git status");
        prompt.Should().Contain("Do not start over when usable work");
    }

    [Fact]
    public void Fresh_run_prompt_carries_no_previous_attempt_warning()
    {
        string prompt = AgentPromptBuilder.Build(SomeTask(), SomeProject(), "task/1-slug", _worktreePath);

        prompt.Should().NotContain("previous attempt", "a fresh worktree has no history to review");
    }

    [Fact]
    public void Follow_up_prompts_warn_about_stranded_work_in_the_retained_worktree()
    {
        string followUp = AgentPromptBuilder.BuildFollowUp(
            SomeTask(), SomeProject(), "task/1-slug", "https://github.com/x/y/pull/7", CommitStyle.Append);
        string fixChecks = AgentPromptBuilder.BuildFixChecks(
            SomeTask(), SomeProject(), "task/1-slug", "https://github.com/x/y/pull/7", CommitStyle.Append);

        foreach (string prompt in new[] { followUp, fixChecks })
        {
            prompt.Should().Contain("retained from a previous run");
            prompt.Should().Contain("UNCOMMITTED", "the retained-worktree resume carries stranded work by design");
            prompt.Should().Contain("build on");
        }
    }

    [Fact]
    public void Review_fix_prompt_carries_the_findings_and_the_dispute_escape_hatch()
    {
        string findings = "1. `Auth.cs:42` — limiter never resets. Scenario: second request always 429s.";

        string prompt = AgentPromptBuilder.BuildReviewFix(SomeTask(), "task/1-slug", findings, cycle: 1);

        prompt.Should().Contain(findings);
        prompt.Should().Contain("branch `task/1-slug`");
        prompt.Should().Contain("Do NOT push");
        prompt.Should().Contain("RESOLUTION: fixed");
        prompt.Should().Contain("RESOLUTION: disputed");
        prompt.Should().Contain("not a defect");
        prompt.Should().Contain("human");
    }

    private void WriteSkill(string name, string description)
    {
        string skillDirectory = Path.Combine(_worktreePath, ".claude", "skills", name);
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(
            Path.Combine(skillDirectory, "SKILL.md"),
            $"---\nname: {name}\ndescription: {description}\n---\n\n# {name}\n\nFull skill body — must never be pasted into the prompt.\n");
    }

    private static TaskDetails SomeTask() => new()
    {
        Objective = "Add rate limiting to auth endpoints",
        AcceptanceCriteria = ["Requests over the limit get 429"],
    };

    private static ProjectDetails SomeProject() => new()
    {
        Name = "hall9k",
        BaseBranch = "main",
    };
}
