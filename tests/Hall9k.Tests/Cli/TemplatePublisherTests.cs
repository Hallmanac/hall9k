using FluentAssertions;
using Hall9k.Cli.ProjectHomes;
using Hall9k.Cli.Prompts;
using Hall9k.Domain.Infrastructure.Storage;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="TemplatePublisher"/>'s publish/retire/override-by-name discipline — the identical
/// contract <c>SkillSeederRemovePublishedTests</c> already proves for the ordinary skill set,
/// scoped to the canonical prompt-template set instead (task: agent prompt prose lives in shipped
/// markdown templates rather than hard-coded C# strings).
/// </summary>
// Redirects the process-wide HALL9K_HOME (the canonical template set hangs off it), so it shares
// the collection with every other test that does.
[Collection("Hall9kHome")]
public sealed class TemplatePublisherTests : IDisposable
{
    private readonly string _platformHome = Path.Combine(Path.GetTempPath(), $"h9k-templates-{Guid.NewGuid():N}");
    private readonly string _source = Path.Combine(Path.GetTempPath(), $"h9k-templates-source-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    public TemplatePublisherTests()
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
    public void A_source_package_is_published_into_the_canonical_directory()
    {
        WriteSourcePackage("review-lap-prompt-builder", "objective.md", "# Stated objective\n");

        SkillPublication publication = TemplatePublisher.PublishCanonical(_source);

        publication.Published.Should().ContainSingle().Which.Should().Be("review-lap-prompt-builder");
        publication.ManifestUnconfirmed.Should().BeFalse();
        File.ReadAllText(Path.Combine(TemplateLibraryPaths.CanonicalDirectory, "review-lap-prompt-builder", "objective.md"))
            .Should().Be("# Stated objective\n");
        File.Exists(TemplateLibraryPaths.PublishedManifest).Should().BeTrue();
    }

    [Fact]
    public void A_missing_source_directory_publishes_nothing_and_is_not_an_error()
    {
        // Unlike PublishSkills, a checkout that has not moved any prose into templates yet (or
        // predates this task) has no .claude/templates at all — that is not a problem worth
        // reporting, just an empty publish.
        SkillPublication publication = TemplatePublisher.PublishCanonical(
            Path.Combine(_source, "does-not-exist"));

        publication.Published.Should().BeEmpty();
        publication.Retired.Should().BeEmpty();
        publication.LeftAlone.Should().BeEmpty();
        publication.ManifestUnconfirmed.Should().BeFalse();
    }

    [Fact]
    public void An_operators_edit_to_a_published_template_is_left_alone_on_republish()
    {
        WriteSourcePackage("review-lap-prompt-builder", "objective.md", "# Stated objective\n");
        TemplatePublisher.PublishCanonical(_source);
        File.WriteAllText(
            Path.Combine(TemplateLibraryPaths.CanonicalDirectory, "review-lap-prompt-builder", "objective.md"),
            "# edited by hand\n");

        SkillPublication publication = TemplatePublisher.PublishCanonical(_source);

        publication.LeftAlone.Should().ContainSingle().Which.Should().Be("review-lap-prompt-builder");
        publication.Published.Should().BeEmpty();
        File.ReadAllText(Path.Combine(TemplateLibraryPaths.CanonicalDirectory, "review-lap-prompt-builder", "objective.md"))
            .Should().Be("# edited by hand\n", "an operator's own edit is theirs, not install's to overwrite");
    }

    [Fact]
    public void A_package_the_source_no_longer_ships_is_retired()
    {
        WriteSourcePackage("review-lap-prompt-builder", "objective.md", "# Stated objective\n");
        TemplatePublisher.PublishCanonical(_source);
        Directory.Delete(Path.Combine(_source, "review-lap-prompt-builder"), recursive: true);

        SkillPublication publication = TemplatePublisher.PublishCanonical(_source);

        publication.Retired.Should().ContainSingle().Which.Should().Be("review-lap-prompt-builder");
        Directory.Exists(Path.Combine(TemplateLibraryPaths.CanonicalDirectory, "review-lap-prompt-builder"))
            .Should().BeFalse();
    }

    [Fact]
    public void RemovePublished_removes_a_published_package_and_leaves_a_hand_written_one()
    {
        WriteSourcePackage("review-lap-prompt-builder", "objective.md", "# Stated objective\n");
        TemplatePublisher.PublishCanonical(_source);
        string handWritten = Path.Combine(TemplateLibraryPaths.CanonicalDirectory, "my-own-templates");
        Directory.CreateDirectory(handWritten);
        File.WriteAllText(Path.Combine(handWritten, "note.md"), "mine\n");

        List<string> stillPresent = [];
        (IReadOnlyList<string> removed, bool manifestConfirmed) = TemplatePublisher.RemovePublished(stillPresent);

        removed.Should().ContainSingle().Which.Should().Be("review-lap-prompt-builder");
        manifestConfirmed.Should().BeTrue();
        stillPresent.Should().BeEmpty();
        Directory.Exists(Path.Combine(TemplateLibraryPaths.CanonicalDirectory, "review-lap-prompt-builder"))
            .Should().BeFalse();
        Directory.Exists(handWritten).Should().BeTrue(
            "a template package this install never published is never uninstall's to delete either");
    }

    [Fact]
    public void Nothing_published_leaves_RemovePublished_a_no_op()
    {
        List<string> stillPresent = [];

        (IReadOnlyList<string> removed, bool manifestConfirmed) = TemplatePublisher.RemovePublished(stillPresent);

        removed.Should().BeEmpty();
        manifestConfirmed.Should().BeTrue();
        stillPresent.Should().BeEmpty();
    }

    private void WriteSourcePackage(string packageName, string fileName, string content)
    {
        string package = Path.Combine(_source, packageName);
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, fileName), content);
    }
}
