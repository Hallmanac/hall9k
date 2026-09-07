using FluentAssertions;
using Hall9k.Cli.Orchestrator;
using Hall9k.Cli.ProjectHomes;
using Hall9k.Domain.Infrastructure.Storage;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The orchestrator-recipe-generator skill is the one skill this platform ships into
/// <c>recipes/</c> instead of the ordinary <c>skills/</c> set (task: an operator starts a lean
/// node or project orchestrator window), through the identical hash-manifest publish/shadow/retire
/// discipline the ordinary skill set already uses.
/// </summary>
// Redirects the process-wide HALL9K_HOME, the same collection every other test touching it joins.
[Collection("Hall9kHome")]
public sealed class RecipeSkillPublisherTests : IDisposable
{
    private readonly string _platformHome = Path.Combine(Path.GetTempPath(), $"hall9k-recipe-skill-{Guid.NewGuid():N}");
    private readonly string _source = Path.Combine(Path.GetTempPath(), $"hall9k-recipe-skill-source-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    public RecipeSkillPublisherTests() => Environment.SetEnvironmentVariable("HALL9K_HOME", _platformHome);

    [Fact]
    public void Publishing_writes_the_skill_and_the_manifest()
    {
        WriteSkill("First version.");

        SkillPublication result = RecipeSkillPublisher.PublishCanonical(_source);

        result.Published.Should().Equal([RecipeSkillPublisher.GeneratorSkillName]);
        result.Retired.Should().BeEmpty();
        result.LeftAlone.Should().BeEmpty();
        File.ReadAllText(Path.Combine(
                RecipeLibraryPaths.CanonicalDirectory, RecipeSkillPublisher.GeneratorSkillName, "SKILL.md"))
            .Should().Contain("First version.");
        File.Exists(RecipeLibraryPaths.PublishedManifest).Should().BeTrue();
    }

    [Fact]
    public void Republishing_unmodified_content_updates_it_again_rather_than_shadowing()
    {
        WriteSkill("First version.");
        RecipeSkillPublisher.PublishCanonical(_source);

        WriteSkill("Second version.");
        SkillPublication result = RecipeSkillPublisher.PublishCanonical(_source);

        result.Published.Should().Equal([RecipeSkillPublisher.GeneratorSkillName]);
        File.ReadAllText(Path.Combine(
                RecipeLibraryPaths.CanonicalDirectory, RecipeSkillPublisher.GeneratorSkillName, "SKILL.md"))
            .Should().Contain("Second version.");
    }

    [Fact]
    public void An_edited_published_copy_is_left_alone_rather_than_overwritten()
    {
        WriteSkill("Shipped by the platform.");
        RecipeSkillPublisher.PublishCanonical(_source);

        string published = Path.Combine(RecipeLibraryPaths.CanonicalDirectory, RecipeSkillPublisher.GeneratorSkillName, "SKILL.md");
        File.WriteAllText(published, "The operator's own edit.");

        SkillPublication result = RecipeSkillPublisher.PublishCanonical(_source);

        result.Published.Should().BeEmpty();
        result.LeftAlone.Should().Equal([RecipeSkillPublisher.GeneratorSkillName]);
        File.ReadAllText(published).Should().Be("The operator's own edit.");
    }

    [Fact]
    public void A_skill_the_source_stops_shipping_is_retired_when_still_unmodified()
    {
        WriteSkill("Shipped by the platform.");
        RecipeSkillPublisher.PublishCanonical(_source);

        Directory.Delete(Path.Combine(_source, RecipeSkillPublisher.GeneratorSkillName), recursive: true);
        SkillPublication result = RecipeSkillPublisher.PublishCanonical(_source);

        result.Retired.Should().Equal([RecipeSkillPublisher.GeneratorSkillName]);
        Directory.Exists(Path.Combine(RecipeLibraryPaths.CanonicalDirectory, RecipeSkillPublisher.GeneratorSkillName))
            .Should().BeFalse();
    }

    [Fact]
    public void Seeding_a_home_without_a_published_copy_is_skipped_honestly()
    {
        string home = Path.Combine(_platformHome, "projects", "hall9k");

        IReadOnlyList<ProjectHomeStep> steps = RecipeSkillPublisher.Seed(home);

        steps.Should().ContainSingle(step => step.Outcome == ProjectHomeOutcome.Skipped);
    }

    [Fact]
    public void Seeding_a_home_links_the_skill_beside_the_anchor_and_into_the_claude_adapter()
    {
        WriteSkill("Shipped by the platform.");
        RecipeSkillPublisher.PublishCanonical(_source);
        string home = Path.Combine(_platformHome, "projects", "hall9k");
        Directory.CreateDirectory(home);

        IReadOnlyList<ProjectHomeStep> steps = RecipeSkillPublisher.Seed(home);

        steps.Should().ContainSingle(step => step.Outcome == ProjectHomeOutcome.Created);
        File.Exists(Path.Combine(
                ProjectHomePaths.RecipeSkillDirectory(home, RecipeSkillPublisher.GeneratorSkillName), "SKILL.md"))
            .Should().BeTrue();
        File.Exists(Path.Combine(
                ProjectHomePaths.ClaudeSkillsDirectory(home), RecipeSkillPublisher.GeneratorSkillName, "SKILL.md"))
            .Should().BeTrue();
    }

    private void WriteSkill(string body)
    {
        string directory = Path.Combine(_source, RecipeSkillPublisher.GeneratorSkillName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), $"---\nname: {RecipeSkillPublisher.GeneratorSkillName}\n---\n\n{body}\n");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", _previousHome);
        if (Directory.Exists(_platformHome))
        {
            Directory.Delete(_platformHome, recursive: true);
        }

        if (Directory.Exists(_source))
        {
            Directory.Delete(_source, recursive: true);
        }
    }
}
