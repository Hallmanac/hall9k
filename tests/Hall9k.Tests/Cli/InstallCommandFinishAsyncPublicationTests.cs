using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Infrastructure.Storage;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="InstallCommand.FinishAsync"/>'s own skills/templates sibling derivation (Copilot
/// review, PR #318): every other test either calls <c>TemplatePublisher</c>/<c>SkillSeeder</c>
/// directly, or runs <c>FinishAsync</c> with <c>skillsSource: null</c>
/// (<c>InstallCommandConnectionStringTests</c>), so a regression in
/// <c>Path.GetDirectoryName(skillsSource)</c>'s own sibling derivation, or in the
/// <c>PublishSkills</c>/<c>PublishTemplates</c> call sequence itself, had nothing here to catch it.
/// </summary>
// Redirects the process-wide HALL9K_HOME (both canonical directories hang off it), so it shares
// the collection with every other test that does.
[Collection("Hall9kHome")]
public sealed class InstallCommandFinishAsyncPublicationTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), $"h9k-install-finish-{Path.GetRandomFileName()}");
    private readonly string staging = Path.Combine(Path.GetTempPath(), $"h9k-install-finish-staging-{Path.GetRandomFileName()}");
    private readonly string repo = Path.Combine(Path.GetTempPath(), $"h9k-install-finish-repo-{Path.GetRandomFileName()}");
    private readonly string? previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    public InstallCommandFinishAsyncPublicationTests()
    {
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(staging);
        Environment.SetEnvironmentVariable("HALL9K_HOME", home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", previousHome);
        InstallCommand.TryDelete(home);
        InstallCommand.TryDelete(staging);
        InstallCommand.TryDelete(repo);
    }

    [Fact]
    public async Task A_real_sibling_skills_and_templates_source_publishes_both_canonical_sets()
    {
        string skillsSource = Path.Combine(repo, ".claude", "skills");
        string templatesSource = Path.Combine(repo, ".claude", "templates");
        Directory.CreateDirectory(Path.Combine(skillsSource, "a-skill"));
        File.WriteAllText(Path.Combine(skillsSource, "a-skill", "SKILL.md"), "# a-skill\n");
        Directory.CreateDirectory(Path.Combine(templatesSource, "review-lap-prompt-builder"));
        File.WriteAllText(Path.Combine(templatesSource, "review-lap-prompt-builder", "objective.md"), "# objective\n");

        int exitCode = await InstallCommand.FinishAsync(
            staging,
            skillsSource,
            version: "0.0.0-test",
            restart: false,
            noRestart: false,
            linkOntoPath: false,
            cancellationToken: CancellationToken.None);

        exitCode.Should().Be(0);
        File.Exists(Path.Combine(SkillLibraryPaths.CanonicalDirectory, "a-skill", "SKILL.md")).Should().BeTrue(
            "PublishSkills ran off the skillsSource FinishAsync was given");
        File.Exists(Path.Combine(TemplateLibraryPaths.CanonicalDirectory, "review-lap-prompt-builder", "objective.md")).Should().BeTrue(
            "PublishTemplates derives templates/ as a sibling of whichever skillsSource this same run resolved — "
            + "the identical seam PublishSkills itself already reads off skillsSource");
    }
}
