using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <see cref="SecurityReviewNonExecutablePathRules"/> is what <c>RunLauncher</c>'s Security
/// docs-only skip actually classifies against, never <c>ProjectDetails.EffectiveNonExecutablePaths</c>
/// directly (independent pre-PR review, cycle 1, both lenses): that set may be widened freely by a
/// project's own <c>--non-executable-path</c> additions to skip the build and test gates, and
/// reusing it here let such an addition (<c>.github/</c>, <c>*.yml</c>) silently skip the one
/// review meant to catch a CI or release workflow change, and let its own compiled defaults
/// (<c>.claude/skills/</c>, <c>.claude/commands/</c>, the bare <c>*.md</c> rule) wave through
/// agent-executed content this persona exists to read.
/// </summary>
public sealed class SecurityReviewNonExecutablePathRulesTests
{
    private static bool AllMatched(IReadOnlyList<string> paths, IReadOnlyList<string> projectNonExecutablePaths) =>
        NonExecutablePathClassifier.Classify(
            paths, SecurityReviewNonExecutablePathRules.Resolve(projectNonExecutablePaths)).AllMatched;

    [Fact]
    public void An_ordinary_docs_only_diff_still_skips()
    {
        AllMatched(["README.md", "docs/scope.md"], NonExecutablePathDefaults.Rules)
            .Should().BeTrue();
    }

    [Fact]
    public void A_ci_workflow_file_never_skips_even_when_a_project_added_dot_github()
    {
        IReadOnlyList<string> projectRules = [.. NonExecutablePathDefaults.Rules, ".github/"];

        AllMatched([".github/workflows/release.yml"], projectRules).Should().BeFalse(
            "a CI or release workflow file must always run the Security review, whatever a project added "
            + "to its own non-executable-path set for gate-skipping purposes");
    }

    [Fact]
    public void A_ci_workflow_file_never_skips_even_when_a_project_added_a_bare_yml_glob()
    {
        IReadOnlyList<string> projectRules = [.. NonExecutablePathDefaults.Rules, "*.yml"];

        AllMatched([".github/workflows/release.yml"], projectRules).Should().BeFalse();
    }

    [Fact]
    public void A_repository_skill_file_never_skips_even_though_the_build_test_defaults_treat_it_as_docs()
    {
        AllMatched([".claude/skills/commit-plan/SKILL.md"], NonExecutablePathDefaults.Rules).Should().BeFalse(
            "a repository skill is agent-executed content this install publishes host-wide, not docs");
    }

    [Fact]
    public void A_repository_skill_script_never_skips()
    {
        AllMatched([".claude/skills/foo/scripts/run.sh"], NonExecutablePathDefaults.Rules).Should().BeFalse();
    }

    [Fact]
    public void A_repository_command_file_never_skips()
    {
        AllMatched([".claude/commands/foo.md"], NonExecutablePathDefaults.Rules).Should().BeFalse();
    }

    [Fact]
    public void A_claude_md_edit_never_skips_even_though_the_build_test_defaults_treat_it_as_ordinary_markdown()
    {
        AllMatched(["CLAUDE.md"], NonExecutablePathDefaults.Rules).Should().BeFalse(
            "CLAUDE.md is agent-loaded instructions every future session in this repository reads");
    }

    [Fact]
    public void A_nested_claude_md_never_skips()
    {
        AllMatched(["packages/widget/CLAUDE.md"], NonExecutablePathDefaults.Rules).Should().BeFalse();
    }
}
