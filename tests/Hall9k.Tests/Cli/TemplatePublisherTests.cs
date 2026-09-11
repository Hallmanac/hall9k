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

    /// <summary>
    /// An operator's override must not strand a builder: a package they overrode before the source
    /// shipped a new file must still end up with that file once it is published, closing the gap a
    /// builder's own PromptTemplates.Load would otherwise throw FileNotFoundException on
    /// (independent pre-PR review, cycle 1).
    /// </summary>
    [Fact]
    public void An_overridden_package_missing_a_new_source_file_is_filled_in_without_touching_the_edit()
    {
        WriteSourcePackage("review-lap-prompt-builder", "objective.md", "# Stated objective\n");
        TemplatePublisher.PublishCanonical(_source);
        File.WriteAllText(
            Path.Combine(TemplateLibraryPaths.CanonicalDirectory, "review-lap-prompt-builder", "objective.md"),
            "# edited by hand\n");
        File.WriteAllText(Path.Combine(_source, "review-lap-prompt-builder", "closing.md"), "# Closing\n");

        SkillPublication publication = TemplatePublisher.PublishCanonical(_source);

        publication.LeftAlone.Should().ContainSingle().Which.Should().Be("review-lap-prompt-builder");
        File.ReadAllText(Path.Combine(TemplateLibraryPaths.CanonicalDirectory, "review-lap-prompt-builder", "objective.md"))
            .Should().Be("# edited by hand\n", "an operator's own edit is theirs, not install's to overwrite");
        File.ReadAllText(Path.Combine(TemplateLibraryPaths.CanonicalDirectory, "review-lap-prompt-builder", "closing.md"))
            .Should().Be("# Closing\n", "a file the operator's own snapshot never had is not their edit to preserve as absent");
    }

    /// <summary>
    /// The other half of the same gap: an operator's override keeps a file the canonical source
    /// still ships, but a later revision adds a new named fragment inside it. Filling in only the
    /// missing fragment, leaving the operator's own edit and existing fragments untouched, closes
    /// the case cycle 1's fix left open — a builder's PromptTemplates.Load(file, "new-fragment")
    /// would otherwise throw FileNotFoundException against the operator's stale copy (independent
    /// pre-PR review, cycle 2).
    /// </summary>
    [Fact]
    public void An_overridden_files_new_fragment_is_appended_without_touching_the_edit_or_other_fragments()
    {
        WriteSourcePackage("review-lap-prompt-builder", "rules.md", "===first===\n# First\n");
        TemplatePublisher.PublishCanonical(_source);
        File.WriteAllText(
            Path.Combine(TemplateLibraryPaths.CanonicalDirectory, "review-lap-prompt-builder", "rules.md"),
            "===first===\n# edited by hand\n");
        File.WriteAllText(
            Path.Combine(_source, "review-lap-prompt-builder", "rules.md"),
            "===first===\n# First\n===second===\n# Second\n");

        SkillPublication publication = TemplatePublisher.PublishCanonical(_source);

        publication.LeftAlone.Should().ContainSingle().Which.Should().Be("review-lap-prompt-builder");
        string content = File.ReadAllText(
            Path.Combine(TemplateLibraryPaths.CanonicalDirectory, "review-lap-prompt-builder", "rules.md"));
        content.Should().Contain("# edited by hand\n", "an operator's own edit is theirs, not install's to overwrite");
        PromptTemplates.FragmentNames(content).Should().Equal("first", "second");
        content.Should().Contain("===second===\n# Second\n", "a fragment the operator's own snapshot never had is not their edit to preserve as absent");
        // Pins the file to a single trailing newline: appending the source's own LAST fragment
        // (its own trailing newline already folded into the block ExtractFragmentBlock returns)
        // must not leave the file with two, which would make PromptTemplates.Load(file, "second")
        // return "# Second\n" here against "# Second" from an untouched canonical copy — a byte
        // difference this test asserts on directly, since PromptTemplates.Load itself would
        // resolve against this checkout's own real rules.md rather than the fake canonical
        // directory this test publishes into (independent pre-PR review, cycle 2).
        content.Should().Be("===first===\n# edited by hand\n\n===second===\n# Second\n");
    }

    /// <summary>
    /// A file browser can drop OS metadata (Finder's .DS_Store, say) into the canonical directory
    /// just by somebody opening it — included in the content hash, that would flip an untouched
    /// package into "edited" and stop it receiving further publishes for no reason an operator ever
    /// intended.
    /// </summary>
    [Fact]
    public void A_stray_DS_Store_in_the_published_directory_does_not_shadow_the_package()
    {
        WriteSourcePackage("review-lap-prompt-builder", "objective.md", "# Stated objective\n");
        TemplatePublisher.PublishCanonical(_source);
        File.WriteAllText(
            Path.Combine(TemplateLibraryPaths.CanonicalDirectory, "review-lap-prompt-builder", ".DS_Store"), "junk");
        WriteSourcePackage("review-lap-prompt-builder", "objective.md", "# Updated objective\n");

        SkillPublication publication = TemplatePublisher.PublishCanonical(_source);

        publication.Published.Should().ContainSingle().Which.Should().Be("review-lap-prompt-builder");
        publication.LeftAlone.Should().BeEmpty();
        File.ReadAllText(Path.Combine(TemplateLibraryPaths.CanonicalDirectory, "review-lap-prompt-builder", "objective.md"))
            .Should().Be("# Updated objective\n");
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
