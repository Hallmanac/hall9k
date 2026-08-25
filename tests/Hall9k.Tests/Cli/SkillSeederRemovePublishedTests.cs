using FluentAssertions;
using Hall9k.Cli.ProjectHomes;
using Hall9k.Domain.Infrastructure.Storage;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <c>h9k uninstall</c>'s use of the canonical skill set: it must remove exactly what
/// <see cref="SkillSeeder.PublishCanonical"/> published and nothing else, the same
/// name-and-content-hash discipline that method's own retiring pass already applies (see its
/// origin incident, 2026-08-23). Before this existed, uninstall deleted
/// <c>~/.hall9k/skills</c> wholesale, taking an operator's hand-written skills with it.
/// </summary>
// Redirects the process-wide HALL9K_HOME (the canonical skill set hangs off it), so it shares
// the collection with the other tests that do.
[Collection("Hall9kHome")]
public sealed class SkillSeederRemovePublishedTests : IDisposable
{
    private readonly string _platformHome = Path.Combine(Path.GetTempPath(), $"h9k-remove-published-{Guid.NewGuid():N}");
    private readonly string _source = Path.Combine(Path.GetTempPath(), $"h9k-remove-published-source-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    public SkillSeederRemovePublishedTests()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", _platformHome);
        Directory.CreateDirectory(_source);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", _previousHome);
        if (Directory.Exists(_platformHome))
        {
            Directory.Delete(_platformHome, recursive: true);
        }

        Directory.Delete(_source, recursive: true);
    }

    [Fact]
    public void A_published_skill_is_removed_and_a_hand_written_one_survives()
    {
        WriteSourceSkill("pr-summary");
        SkillSeeder.PublishCanonical(_source);
        string handWritten = Path.Combine(SkillLibraryPaths.CanonicalDirectory, "my-team-conventions");
        Directory.CreateDirectory(handWritten);
        File.WriteAllText(Path.Combine(handWritten, "SKILL.md"), "# my-team-conventions\n");

        List<string> stillPresent = [];
        IReadOnlyList<string> removed = SkillSeeder.RemovePublished(stillPresent);

        removed.Should().ContainSingle().Which.Should().Be("pr-summary");
        stillPresent.Should().BeEmpty();
        Directory.Exists(SkillLibraryPaths.Skill("pr-summary")).Should().BeFalse();
        Directory.Exists(handWritten).Should().BeTrue(
            "a skill this install never published is never uninstall's to delete either");
        File.Exists(SkillLibraryPaths.PublishedManifest).Should().BeFalse("its own bookkeeping file goes with it");
    }

    [Fact]
    public void A_published_skill_edited_since_publish_is_left_alone()
    {
        // The same gap PublishCanonical's own hash check exists to close: a name recorded as
        // published is safe to touch only while its content still matches what was recorded,
        // not forever just because the name was once published.
        WriteSourceSkill("pr-summary");
        SkillSeeder.PublishCanonical(_source);
        File.WriteAllText(Path.Combine(SkillLibraryPaths.Skill("pr-summary"), "SKILL.md"), "# edited by hand\n");

        List<string> stillPresent = [];
        IReadOnlyList<string> removed = SkillSeeder.RemovePublished(stillPresent);

        removed.Should().BeEmpty();
        Directory.Exists(SkillLibraryPaths.Skill("pr-summary")).Should().BeTrue(
            "an operator's edit to a published skill is their own work, not install's to delete");
    }

    [Fact]
    public void Nothing_published_leaves_the_canonical_directory_untouched()
    {
        List<string> stillPresent = [];

        IReadOnlyList<string> removed = SkillSeeder.RemovePublished(stillPresent);

        removed.Should().BeEmpty();
        stillPresent.Should().BeEmpty();
    }

    [Fact]
    public void A_locked_skill_keeps_its_manifest_entry_for_a_retry()
    {
        // Before this fix, the manifest was deleted unconditionally at the end of
        // RemovePublished regardless of stillPresent, so a skill whose directory could not be
        // deleted (locked) lost its manifest entry along with every skill that did succeed. A
        // later retry — or the next install's own PublishCanonical pass — could then no longer
        // tell the locked skill apart from an operator's own override once the lock cleared.
        WriteSourceSkill("pr-summary");
        SkillSeeder.PublishCanonical(_source);
        string skillDirectory = SkillLibraryPaths.Skill("pr-summary");

        if (OperatingSystem.IsWindows())
        {
            using FileStream lockHandle = new(
                Path.Combine(skillDirectory, "SKILL.md"), FileMode.Open, FileAccess.Read, FileShare.Read);

            AssertManifestEntrySurvives(skillDirectory);
        }
        else
        {
            if (!MadeUnwritable(skillDirectory))
            {
                return;
            }

            try
            {
                AssertManifestEntrySurvives(skillDirectory);
            }
            finally
            {
                File.SetUnixFileMode(
                    skillDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    private static void AssertManifestEntrySurvives(string skillDirectory)
    {
        List<string> stillPresent = [];
        IReadOnlyList<string> removed = SkillSeeder.RemovePublished(stillPresent);

        removed.Should().BeEmpty();
        stillPresent.Should().ContainSingle().Which.Should().Be(skillDirectory);
        File.Exists(SkillLibraryPaths.PublishedManifest).Should().BeTrue(
            "the locked skill's manifest entry must survive for a retry");
        File.ReadAllText(SkillLibraryPaths.PublishedManifest).Should().Contain("pr-summary");
    }

    private static bool MadeUnwritable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        string probe = Path.Combine(path, "probe");
        try
        {
            File.WriteAllText(probe, "probe\n");
            File.Delete(probe);
            return false;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    private void WriteSourceSkill(string name)
    {
        string skill = Path.Combine(_source, name);
        Directory.CreateDirectory(skill);
        File.WriteAllText(Path.Combine(skill, "SKILL.md"), $"# {name}\n");
    }
}
