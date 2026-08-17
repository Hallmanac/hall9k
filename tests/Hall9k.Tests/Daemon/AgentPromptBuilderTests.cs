using FluentAssertions;
using Hall9k.Daemon.Execution;
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
    };
}
