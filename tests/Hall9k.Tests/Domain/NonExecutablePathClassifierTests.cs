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
    /// This repository's own <c>.claude/templates/</c> is rendered prompt source with its own
    /// golden tests, not docs, so the compiled <c>!.claude/templates/</c> exclusion has to win over
    /// the bare <c>*.md</c> rule even though <c>*.md</c> is checked first in the compiled list
    /// (independent pre-PR review, cycle 1, adversarial lens, high).
    /// </summary>
    [Fact]
    public void The_templates_exclusion_wins_over_the_markdown_default_even_though_markdown_is_checked_first()
    {
        NonExecutablePathClassifier.ClassificationResult result = NonExecutablePathClassifier.Classify(
            [".claude/templates/agent-prompt-builder/review-fix.md"], CompiledDefaults);

        result.AllMatched.Should().BeFalse("templates are tested prompt source, not docs, even though they end in .md");
        result.Paths.Single().MatchedRule.Should().BeNull();
    }

    [Fact]
    public void An_exclusion_rule_wins_regardless_of_where_it_sits_in_the_rule_list()
    {
        NonExecutablePathClassifier.ClassificationResult exclusionFirst = NonExecutablePathClassifier.Classify(
            [".claude/templates/foo.md"], ["!.claude/templates/", "*.md"]);
        NonExecutablePathClassifier.ClassificationResult exclusionLast = NonExecutablePathClassifier.Classify(
            [".claude/templates/foo.md"], ["*.md", "!.claude/templates/"]);

        exclusionFirst.Paths.Single().MatchedRule.Should().BeNull();
        exclusionLast.Paths.Single().MatchedRule.Should().BeNull();
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
