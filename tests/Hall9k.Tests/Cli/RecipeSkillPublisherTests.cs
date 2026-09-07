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

    [Fact]
    public void Seeding_a_home_twice_does_not_throw()
    {
        // Regression for the Windows directory-reparse-point bug (independent pre-PR review,
        // cycle 1, adversarial lens): Point used to unlink a live symlink with a bare File.Delete,
        // which is fine on this platform but threw UnauthorizedAccessException on a Windows
        // reparse point, falling into a copy fallback that copied a directory onto itself through
        // the still-live symlink. A second Seed against an already-seeded home is exactly the
        // shape that hit it.
        WriteSkill("Shipped by the platform.");
        RecipeSkillPublisher.PublishCanonical(_source);
        string home = Path.Combine(_platformHome, "projects", "hall9k");
        Directory.CreateDirectory(home);

        RecipeSkillPublisher.Seed(home);
        IReadOnlyList<ProjectHomeStep> steps = RecipeSkillPublisher.Seed(home);

        steps.Should().ContainSingle(step => step.Outcome == ProjectHomeOutcome.Created);
        File.Exists(Path.Combine(
                ProjectHomePaths.RecipeSkillDirectory(home, RecipeSkillPublisher.GeneratorSkillName), "SKILL.md"))
            .Should().BeTrue();
    }

    [Fact]
    public void Seeding_a_home_with_an_operators_own_real_directory_leaves_it_alone_and_says_so()
    {
        WriteSkill("Shipped by the platform.");
        RecipeSkillPublisher.PublishCanonical(_source);
        string home = Path.Combine(_platformHome, "projects", "hall9k");
        string link = ProjectHomePaths.RecipeSkillDirectory(home, RecipeSkillPublisher.GeneratorSkillName);
        Directory.CreateDirectory(link);
        File.WriteAllText(Path.Combine(link, "SKILL.md"), "the operator's own skill, not seeded");

        IReadOnlyList<ProjectHomeStep> steps = RecipeSkillPublisher.Seed(home);

        steps.Should().ContainSingle(step => step.Outcome == ProjectHomeOutcome.Skipped);
        File.ReadAllText(Path.Combine(link, "SKILL.md")).Should().Be("the operator's own skill, not seeded");
    }

    [Fact]
    public void Removing_the_published_skill_takes_it_off_and_reports_it()
    {
        WriteSkill("Shipped by the platform.");
        RecipeSkillPublisher.PublishCanonical(_source);
        List<string> stillPresent = [];

        (IReadOnlyList<string> removed, bool manifestConfirmed) = RecipeSkillPublisher.RemovePublished(stillPresent);

        manifestConfirmed.Should().BeTrue();
        removed.Should().Equal([RecipeSkillPublisher.GeneratorSkillName]);
        stillPresent.Should().BeEmpty();
        Directory.Exists(Path.Combine(RecipeLibraryPaths.CanonicalDirectory, RecipeSkillPublisher.GeneratorSkillName))
            .Should().BeFalse();
    }

    [Fact]
    public void Removing_the_published_skill_leaves_an_operators_edit_alone()
    {
        WriteSkill("Shipped by the platform.");
        RecipeSkillPublisher.PublishCanonical(_source);
        string published = Path.Combine(RecipeLibraryPaths.CanonicalDirectory, RecipeSkillPublisher.GeneratorSkillName, "SKILL.md");
        File.WriteAllText(published, "the operator's own edit");
        List<string> stillPresent = [];

        (IReadOnlyList<string> removed, bool manifestConfirmed) = RecipeSkillPublisher.RemovePublished(stillPresent);

        manifestConfirmed.Should().BeTrue();
        removed.Should().BeEmpty();
        File.ReadAllText(published).Should().Be("the operator's own edit");
    }

    [Fact]
    public void Removing_with_nothing_ever_published_reports_nothing_removed_and_nothing_still_present()
    {
        List<string> stillPresent = [];

        (IReadOnlyList<string> removed, bool manifestConfirmed) = RecipeSkillPublisher.RemovePublished(stillPresent);

        manifestConfirmed.Should().BeTrue();
        removed.Should().BeEmpty();
        stillPresent.Should().BeEmpty();
    }

    [Fact]
    public void Removing_the_node_adapter_unlinks_the_seeded_symlink()
    {
        WriteSkill("Shipped by the platform.");
        RecipeSkillPublisher.PublishCanonical(_source);
        RecipeSkillPublisher.SeedNode();
        string adapter = Path.Combine(RecipeLibraryPaths.ClaudeSkillsDirectory, RecipeSkillPublisher.GeneratorSkillName);
        Directory.Exists(adapter).Should().BeTrue("SeedNode should have linked it first");
        List<string> stillPresent = [];

        RecipeSkillPublisher.RemoveNodeAdapter(stillPresent);

        stillPresent.Should().BeEmpty();
        Directory.Exists(adapter).Should().BeFalse();
    }

    [Fact]
    public void Removing_the_node_adapter_also_removes_a_symlink_refusing_filesystems_copy_fallback()
    {
        // On Windows without Developer Mode, SkillSeeder.Point (which RecipeSkillPublisher's own
        // Point mirrors) falls back to a real copy plus a CopyMarkerFile when CreateSymbolicLink
        // is denied. Before this, RemoveNodeAdapter only ever checked for a symlink, so that
        // marker-bearing copy — plainly platform-authored, not an operator's own — survived
        // uninstall forever (independent pre-PR review, cycle 3, adversarial lens).
        WriteSkill("Shipped by the platform.");
        RecipeSkillPublisher.PublishCanonical(_source);
        string adapter = Path.Combine(RecipeLibraryPaths.ClaudeSkillsDirectory, RecipeSkillPublisher.GeneratorSkillName);
        Directory.CreateDirectory(adapter);
        File.WriteAllText(Path.Combine(adapter, "SKILL.md"), "Shipped by the platform.");
        File.WriteAllText(Path.Combine(adapter, SkillSeeder.CopyMarkerFile), string.Empty);
        List<string> stillPresent = [];

        RecipeSkillPublisher.RemoveNodeAdapter(stillPresent);

        stillPresent.Should().BeEmpty();
        Directory.Exists(adapter).Should().BeFalse();
    }

    [Fact]
    public void Removing_the_node_adapter_leaves_an_operators_own_real_directory_alone()
    {
        // A real directory with no CopyMarkerFile could only be an operator's own — never
        // something Point itself wrote — and must never be swept just because it sits at the
        // adapter's own path.
        string adapter = Path.Combine(RecipeLibraryPaths.ClaudeSkillsDirectory, RecipeSkillPublisher.GeneratorSkillName);
        Directory.CreateDirectory(adapter);
        File.WriteAllText(Path.Combine(adapter, "SKILL.md"), "the operator's own skill");
        List<string> stillPresent = [];

        RecipeSkillPublisher.RemoveNodeAdapter(stillPresent);

        stillPresent.Should().BeEmpty();
        Directory.Exists(adapter).Should().BeTrue();
        File.ReadAllText(Path.Combine(adapter, "SKILL.md")).Should().Be("the operator's own skill");
    }

    [Fact]
    public void A_directory_that_cannot_be_deleted_keeps_a_manifest_entry_matching_what_survives()
    {
        // Before this fix, a failed Directory.Delete left the manifest holding the pre-deletion
        // hash unconditionally, even though Directory.Delete(recursive: true) can remove files
        // before hitting the one that is locked — a later pass would then see the surviving
        // remainder no longer match that stale hash and misread it as an operator's own edit,
        // leaving it behind forever (independent pre-PR review, cycle 3, adversarial lens).
        WriteSkill("Shipped by the platform.");
        RecipeSkillPublisher.PublishCanonical(_source);
        string directory = Path.Combine(RecipeLibraryPaths.CanonicalDirectory, RecipeSkillPublisher.GeneratorSkillName);
        List<string> stillPresent = [];

        if (OperatingSystem.IsWindows())
        {
            using FileStream lockHandle = new(
                Path.Combine(directory, "SKILL.md"), FileMode.Open, FileAccess.Read, FileShare.Read);

            RecipeSkillPublisher.RemovePublished(stillPresent);
        }
        else
        {
            UnixFileMode original = File.GetUnixFileMode(directory);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                RecipeSkillPublisher.RemovePublished(stillPresent);
            }
            finally
            {
                File.SetUnixFileMode(directory, original);
            }
        }

        stillPresent.Should().Contain(directory, "the delete failed — it must not be reported as removed");
        File.Exists(RecipeLibraryPaths.PublishedManifest).Should().BeTrue(
            "a manifest entry must survive so a retry can still tell this apart from an operator's own edit");
        string[] manifestParts = File.ReadAllText(RecipeLibraryPaths.PublishedManifest).Split('\t', 2);
        manifestParts[0].Should().Be(RecipeSkillPublisher.GeneratorSkillName);
        Directory.Exists(directory).Should().BeTrue("the delete failed, so the directory itself must still be there");
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
