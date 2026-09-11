using FluentAssertions;
using Hall9k.Domain.Infrastructure.Storage;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <see cref="PromptTemplates"/>'s own contract: substitution, exact line reconstruction from a
/// template file's own newlines (never <see cref="Environment.NewLine"/> read raw off disk), and
/// resolving the install's canonical copy — the only place these fixtures ever write a template —
/// once a checkout's own <c>.claude/templates</c> does not carry the relative path being asked for.
/// </summary>
// Redirects the process-wide HALL9K_HOME (the canonical directory PromptTemplates falls back to),
// so it shares the collection with every other test that does.
[Collection("Hall9kHome")]
public sealed class PromptTemplatesTests : IDisposable
{
    private readonly string _platformHome = Path.Combine(Path.GetTempPath(), $"h9k-prompt-templates-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    public PromptTemplatesTests()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", _platformHome);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", _previousHome);
        if (Directory.Exists(_platformHome))
        {
            Directory.Delete(_platformHome, recursive: true);
        }
    }

    [Fact]
    public void Load_substitutes_every_token_and_leaves_everything_else_untouched()
    {
        string directory = Path.Combine(TemplateLibraryPaths.CanonicalDirectory, "sample");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "greeting.md"), "Hello, {{Name}}. Today is {{Day}}.");

        string text = PromptTemplates.Load(
            "sample/greeting.md",
            parameters: new Dictionary<string, string> { ["Name"] = "Ada", ["Day"] = "Tuesday" });

        text.Should().Be("Hello, Ada. Today is Tuesday.");
    }

    [Fact]
    public void A_missing_template_reports_both_locations_it_checked()
    {
        Action act = () => PromptTemplates.Load("sample/does-not-exist.md");

        act.Should().Throw<FileNotFoundException>()
            .Which.Message.Should().Contain(TemplateLibraryPaths.CanonicalDirectory)
            .And.Contain(".claude/templates");
    }

    [Fact]
    public void Load_reads_a_named_fragment_out_of_a_multi_fragment_file()
    {
        string directory = Path.Combine(TemplateLibraryPaths.CanonicalDirectory, "sample");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "bundle.md"),
            "===opening===\nNone moved\n===unchanged-truncated===\n — all {{Count}} unchanged.\n===none===\n — they opened none.\n");

        PromptTemplates.Load("sample/bundle.md", fragment: "opening").Should().Be("None moved");
        PromptTemplates.Load(
            "sample/bundle.md", fragment: "unchanged-truncated",
            parameters: new Dictionary<string, string> { ["Count"] = "3 threads" })
            .Should().Be(" — all 3 threads unchanged.");
        PromptTemplates.Load("sample/bundle.md", fragment: "none").Should().Be(" — they opened none.");
    }

    /// <summary>
    /// A bare Markdown setext heading underline (just <c>===</c>, or any longer run of only
    /// <c>=</c>) is content a fragment's own body is free to carry, and must not be mistaken for
    /// the next fragment's own <c>===name===</c> marker — the two used to collide because the
    /// terminator search only checked that a line started and ended with <c>===</c>, which a bare
    /// underline satisfies trivially (independent pre-PR review, cycle 1).
    /// </summary>
    [Fact]
    public void A_bare_markdown_underline_inside_a_fragment_does_not_end_it_early()
    {
        string directory = Path.Combine(TemplateLibraryPaths.CanonicalDirectory, "sample");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "underline.md"),
            "===opening===\nHeading\n===\nMore prose under the underline.\n===closing===\nDone.\n");

        PromptTemplates.Load("sample/underline.md", fragment: "opening")
            .Should().Be("Heading\n===\nMore prose under the underline.");
        PromptTemplates.Load("sample/underline.md", fragment: "closing").Should().Be("Done.");
    }

    [Fact]
    public void A_missing_fragment_name_reports_the_file_it_looked_in()
    {
        string directory = Path.Combine(TemplateLibraryPaths.CanonicalDirectory, "sample");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "bundle.md"), "===opening===\nNone moved\n");

        Action act = () => PromptTemplates.Load("sample/bundle.md", fragment: "does-not-exist");

        act.Should().Throw<FileNotFoundException>().Which.Message.Should().Contain("does-not-exist");
    }

    [Fact]
    public void AppendTemplate_reproduces_the_files_own_lines_with_no_trailing_blank_line()
    {
        string directory = Path.Combine(TemplateLibraryPaths.CanonicalDirectory, "sample");
        Directory.CreateDirectory(directory);
        // A file saved with a trailing newline — the ordinary shape a text editor writes — must
        // not read as one extra blank AppendLine call beyond its own three visible lines.
        File.WriteAllText(Path.Combine(directory, "three-lines.md"), "First\n\nThird\n");

        System.Text.StringBuilder builder = new();
        PromptTemplates.AppendTemplate(builder, "sample/three-lines.md");
        builder.Append("Fourth, appended after");

        builder.ToString().Should().Be("First" + Environment.NewLine + Environment.NewLine + "Third"
            + Environment.NewLine + "Fourth, appended after");
    }
}
