using FluentAssertions;
using Hall9k.Connectors.Prompts;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// This task's own turn-one-cost rule: templates live in a sibling directory to the canonical
/// skills, never inside it, never carry a <c>SKILL.md</c>, and never appear in the skill list a
/// work prompt renders. <see cref="WorkPromptBuilder.DiscoverRepoSkills"/> is the read this repeats
/// for — proved here by enumerating a worktree's skills before and after a sibling
/// <c>.claude/templates</c> tree (carrying no <c>SKILL.md</c> of its own) is populated, and
/// asserting the two enumerations name exactly the same skills.
/// </summary>
public sealed class TemplateSkillDiscoveryIsolationTests : IDisposable
{
    private readonly string _worktreePath = Path.Combine(Path.GetTempPath(), $"h9k-template-isolation-{Guid.NewGuid():N}");

    public TemplateSkillDiscoveryIsolationTests()
    {
        Directory.CreateDirectory(_worktreePath);
    }

    public void Dispose() => Directory.Delete(_worktreePath, recursive: true);

    [Fact]
    public void Publishing_templates_beside_claude_skills_does_not_change_what_the_work_prompt_discovers()
    {
        WriteSkill("commit-plan", "Organize changes into cohesive commits.");
        WriteSkill("pr-summary", "Generate a PR title and description.");

        IReadOnlyList<RepoSkill> before = WorkPromptBuilder.DiscoverRepoSkills(_worktreePath);

        string templates = Path.Combine(_worktreePath, ".claude", "templates", "review-lap-prompt-builder");
        Directory.CreateDirectory(templates);
        File.WriteAllText(Path.Combine(templates, "objective.md"), "===heading===\n## Stated objective\n");

        IReadOnlyList<RepoSkill> after = WorkPromptBuilder.DiscoverRepoSkills(_worktreePath);

        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering(),
            "a sibling .claude/templates tree carries no SKILL.md and must never be read as a skill");
        after.Select(skill => skill.Name).Should().BeEquivalentTo(["commit-plan", "pr-summary"]);
    }

    private void WriteSkill(string name, string description)
    {
        string skillDirectory = Path.Combine(_worktreePath, ".claude", "skills", name);
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), $"---\nname: {name}\ndescription: {description}\n---\n");
    }
}
