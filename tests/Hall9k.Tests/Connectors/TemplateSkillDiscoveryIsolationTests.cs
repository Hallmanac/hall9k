using FluentAssertions;
using Hall9k.Cli.ProjectHomes;
using Hall9k.Cli.Prompts;
using Hall9k.Connectors.Prompts;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// This task's own turn-one-cost rule: templates live in a sibling directory to the canonical
/// skills, never inside it, never carry a <c>SKILL.md</c>, and never appear in the skill list a
/// work prompt renders. <see cref="WorkPromptBuilder.DiscoverRepoSkills"/> is the read this repeats
/// for — proved here by enumerating a worktree's skills before and after a sibling
/// <c>.claude/templates</c> tree (carrying no <c>SKILL.md</c> of its own) is populated, and
/// asserting the two enumerations name exactly the same skills.
/// <para>
/// <see cref="WorkPromptBuilder.DiscoverHomeSkills"/> and <see cref="SkillSeeder.Seed"/> read the
/// identical rule one tier up, off <c>~/.hall9k/skills</c> and a project home's own <c>skills/</c>
/// rather than a worktree's <c>.claude/skills</c> — proved the same way, with
/// <c>~/.hall9k/templates</c> published beside <c>~/.hall9k/skills</c> this time (independent
/// pre-PR review, cycle 1: the worktree-level guard above had no home-level counterpart).
/// </para>
/// </summary>
// Redirects the process-wide HALL9K_HOME (SkillLibraryPaths.CanonicalDirectory and
// TemplateLibraryPaths.CanonicalDirectory both hang off it), so it shares the collection with
// every other test that does.
[Collection("Hall9kHome")]
public sealed class TemplateSkillDiscoveryIsolationTests : IDisposable
{
    private readonly string _worktreePath = Path.Combine(Path.GetTempPath(), $"h9k-template-isolation-{Guid.NewGuid():N}");
    private readonly string _platformHome = Path.Combine(Path.GetTempPath(), $"h9k-template-isolation-home-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    public TemplateSkillDiscoveryIsolationTests()
    {
        Directory.CreateDirectory(_worktreePath);
        Environment.SetEnvironmentVariable("HALL9K_HOME", _platformHome);
    }

    public void Dispose()
    {
        Directory.Delete(_worktreePath, recursive: true);
        Environment.SetEnvironmentVariable("HALL9K_HOME", _previousHome);
        if (Directory.Exists(_platformHome))
        {
            Directory.Delete(_platformHome, recursive: true);
        }
    }

    [Fact]
    public void Seeding_a_project_home_and_discovering_its_skills_ignore_templates_published_beside_them()
    {
        Directory.CreateDirectory(SkillLibraryPaths.CanonicalDirectory);
        WriteCanonicalSkill("commit-plan", "Organize changes into cohesive commits.");
        string templateSource = Path.Combine(_platformHome, "template-source");
        Directory.CreateDirectory(Path.Combine(templateSource, "review-lap-prompt-builder"));
        File.WriteAllText(
            Path.Combine(templateSource, "review-lap-prompt-builder", "objective.md"), "===heading===\n## Stated objective\n");
        TemplatePublisher.PublishCanonical(templateSource).Published.Should().Equal(["review-lap-prompt-builder"]);

        string home = ProjectHomePaths.DefaultFor("hall9k");
        SkillSeeder.Seed(home);

        Directory.EnumerateDirectories(ProjectHomePaths.SkillsDirectory(home))
            .Select(Path.GetFileName)
            .Should().BeEquivalentTo(["commit-plan"],
                "the canonical prompt-template set published beside the canonical skill set must never seed "
                + "into a project home's own skills/");

        ProjectDetails project = new()
        {
            Id = DomainId.New(),
            Name = "hall9k",
            BaseBranch = "main",
            HomeDirectory = ProjectHome.Parse(home),
            RepositoryPath = ProjectHomePaths.BareRepository(home, "hall9k"),
        };

        WorkPromptBuilder.DiscoverHomeSkills(project).Select(skill => skill.Name)
            .Should().BeEquivalentTo(["commit-plan"],
                "a template package carries no SKILL.md and must never be read as a home skill");
    }

    private static void WriteCanonicalSkill(string name, string description)
    {
        string directory = Path.Combine(SkillLibraryPaths.CanonicalDirectory, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "SKILL.md"), $"---\nname: {name}\ndescription: {description}\n---\n\n# {name}\n");
    }

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
