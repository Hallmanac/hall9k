using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Task: a delivered diff that touches no buildable or testable source skips the build and test
/// gates. <see cref="NonExecutablePathClassifier"/> is a pure function over a path list and a
/// rule list — every case here is a plain in-memory call, no git, no filesystem, no store.
/// </summary>
public sealed class NonExecutablePathClassifierTests
{
    private static readonly IReadOnlyList<string> CompiledDefaults = NonExecutablePathDefaults.Rules;

    [Fact]
    public void A_diff_that_is_entirely_markdown_and_docs_skips()
    {
        NonExecutablePathClassifier.ClassificationResult result = NonExecutablePathClassifier.Classify(
            ["README.md", "docs/scope.md", ".claude/skills/foo/SKILL.md", ".claude/commands/bar.md"],
            CompiledDefaults);

        result.AllMatched.Should().BeTrue();
        result.Paths.Should().AllSatisfy(path => path.MatchedRule.Should().NotBeNull());
    }

    [Fact]
    public void A_mixed_diff_with_one_source_file_runs_the_gates_in_full()
    {
        NonExecutablePathClassifier.ClassificationResult result = NonExecutablePathClassifier.Classify(
            ["docs/scope.md", "src/Hall9k.Domain/Widget.cs"],
            CompiledDefaults);

        result.AllMatched.Should().BeFalse("one changed path is real source, so the whole diff is not content-only");
        result.Paths.Should().Contain(path => path.Path == "src/Hall9k.Domain/Widget.cs" && path.MatchedRule == null);
        // Matches "*.md" (the filename rule), which comes ahead of "docs/" in the compiled
        // default order — either rule would call it content, so which one wins is incidental,
        // but the assertion has to name the one that actually does.
        result.Paths.Should().Contain(path => path.Path == "docs/scope.md" && path.MatchedRule == "*.md");
    }

    [Fact]
    public void A_path_outside_the_default_set_never_skips_even_alone()
    {
        NonExecutablePathClassifier.ClassificationResult result = NonExecutablePathClassifier.Classify(
            ["Directory.Packages.props"], CompiledDefaults);

        result.AllMatched.Should().BeFalse("a project file outside the compiled default set is never guessed as safe");
    }

    [Fact]
    public void An_empty_changed_path_list_is_never_read_as_vacuously_content_only()
    {
        NonExecutablePathClassifier.ClassificationResult result =
            NonExecutablePathClassifier.Classify([], CompiledDefaults);

        result.AllMatched.Should().BeFalse("nothing changed is never treated as 'everything changed matched'");
        result.Paths.Should().BeEmpty();
    }

    [Fact]
    public void A_project_added_glob_is_honored_alongside_the_compiled_defaults()
    {
        IReadOnlyList<string> rules = [.. CompiledDefaults, "assets/**/*.png"];

        NonExecutablePathClassifier.ClassificationResult result = NonExecutablePathClassifier.Classify(
            ["docs/scope.md", "assets/icons/logo.png"], rules);

        result.AllMatched.Should().BeTrue();
        result.Paths.Single(path => path.Path == "assets/icons/logo.png").MatchedRule.Should().Be("assets/**/*.png");
    }

    [Fact]
    public void A_project_added_glob_does_not_relax_the_compiled_defaults_for_other_paths()
    {
        IReadOnlyList<string> rules = [.. CompiledDefaults, "assets/**/*.png"];

        NonExecutablePathClassifier.ClassificationResult result = NonExecutablePathClassifier.Classify(
            ["src/Hall9k.Domain/Widget.cs"], rules);

        result.AllMatched.Should().BeFalse("an addition only ever widens the set, never narrows what still runs the gates");
    }

    /// <summary>
    /// A deleted path is still classified against its old name — the shape `git diff --name-status`
    /// reports it in — so deleting a real source file is never read as content-only just because
    /// nothing new was added under it.
    /// </summary>
    [Fact]
    public void A_deleted_source_path_still_runs_the_gates()
    {
        NonExecutablePathClassifier.ClassificationResult result = NonExecutablePathClassifier.Classify(
            ["src/Hall9k.Domain/Retired.cs"], CompiledDefaults);

        result.AllMatched.Should().BeFalse();
    }

    /// <summary>
    /// A rename is recorded as two separate paths (old name, new name) — both have to match for
    /// the rename to count as content-only, so a rename that moves a file OUT of a non-executable
    /// directory into src/ still forces the gates to run.
    /// </summary>
    [Fact]
    public void Both_sides_of_a_rename_must_match_for_the_rename_to_be_content_only()
    {
        NonExecutablePathClassifier.ClassificationResult bothSidesMatch = NonExecutablePathClassifier.Classify(
            ["docs/old-name.md", "docs/new-name.md"], CompiledDefaults);
        bothSidesMatch.AllMatched.Should().BeTrue();

        NonExecutablePathClassifier.ClassificationResult onlyOldSideMatches = NonExecutablePathClassifier.Classify(
            ["docs/old-name.md", "src/Hall9k.Domain/NewName.cs"], CompiledDefaults);
        onlyOldSideMatches.AllMatched.Should().BeFalse();
    }

    [Theory]
    [InlineData("AGENTS.md")]
    [InlineData("docs/scope.md")]
    [InlineData("src/Hall9k.Domain/nested/deep/Readme.md")]
    public void The_markdown_default_rule_matches_at_any_depth(string path)
    {
        NonExecutablePathClassifier.Matches(path, "*.md").Should().BeTrue();
    }

    [Theory]
    [InlineData("docs/scope.md")]
    [InlineData("docs/sub/dir/file.txt")]
    public void The_directory_default_rules_match_any_depth_under_them(string path)
    {
        NonExecutablePathClassifier.Matches(path, "docs/").Should().BeTrue();
    }

    [Fact]
    public void A_directory_rule_never_matches_a_sibling_path_with_the_same_prefix_as_a_substring()
    {
        // "docs/" must anchor on the path SEGMENT, not merely as a string prefix — a sibling
        // directory like "docs-legacy/" sharing the same leading characters must never match.
        NonExecutablePathClassifier.Matches("docs-legacy/notes.md", "docs/").Should().BeFalse();
    }

    [Fact]
    public void A_full_path_glob_with_a_slash_is_evaluated_against_the_whole_path_not_just_the_file_name()
    {
        NonExecutablePathClassifier.Matches("assets/icons/logo.png", "assets/**/*.png").Should().BeTrue();
        NonExecutablePathClassifier.Matches("other/icons/logo.png", "assets/**/*.png").Should().BeFalse();
    }
}
