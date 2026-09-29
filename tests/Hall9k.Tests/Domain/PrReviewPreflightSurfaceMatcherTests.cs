using FluentAssertions;
using Hall9k.Domain.Features.PrReviewPreflight;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <see cref="PrReviewPreflightSurfaceMatcher"/> is a pure function over a changed-file list
/// (idea 6be68ee2, finding 1, phase one's own acceptance criterion) — no store, no I/O, run
/// exactly like <c>NonExecutablePathClassifierTests</c> for the identical reason.
/// </summary>
public sealed class PrReviewPreflightSurfaceMatcherTests
{
    [Fact]
    public void An_empty_changed_file_list_matches_nothing()
    {
        PrReviewPreflightSurfaceMatcher.Match([]).Should().BeEmpty();
    }

    [Fact]
    public void A_change_outside_every_surface_matches_nothing()
    {
        PrReviewPreflightSurfaceMatcher.Match(["src/Hall9k.Domain/Features/Run/AgentRole.cs"])
            .Should().BeEmpty("an ordinary source file is not one of the fixed executable surfaces");
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData("src/Hall9k.Cli/Hall9k.Cli.csproj")]
    [InlineData("Directory.Build.props")]
    [InlineData("Directory.Packages.props")]
    [InlineData("global.json")]
    [InlineData("nuget.config")]
    [InlineData("package.json")]
    [InlineData("package-lock.json")]
    [InlineData("yarn.lock")]
    [InlineData("pnpm-lock.yaml")]
    [InlineData("npm-shrinkwrap.json")]
    [InlineData("scripts/deploy.sh")]
    [InlineData("install.sh")]
    [InlineData(".githooks/pre-commit")]
    [InlineData("Makefile")]
    [InlineData("Dockerfile")]
    [InlineData("Dockerfile.dev")]
    [InlineData("docker-compose.yml")]
    [InlineData("compose.yaml")]
    [InlineData(".claude/skills/foo/SKILL.md")]
    [InlineData("CLAUDE.md")]
    [InlineData("AGENTS.md")]
    [InlineData(".gitattributes")]
    [InlineData(".gitmodules")]
    [InlineData("build/Directory.Build.targets")]
    public void Every_listed_surface_pattern_matches_its_own_example(string changedFile)
    {
        PrReviewPreflightSurfaceMatcher.Match([changedFile]).Should().ContainSingle().Which.Should().Be(changedFile);
    }

    [Fact]
    public void A_nested_project_file_still_matches_by_basename_at_any_depth()
    {
        PrReviewPreflightSurfaceMatcher.Match(["src/deep/nested/tree/package.json"])
            .Should().ContainSingle("a basename-style pattern matches at any depth, not only at the repository root");
    }

    [Fact]
    public void Only_the_matching_files_are_returned_in_the_given_order()
    {
        IReadOnlyList<string> changedFiles =
        [
            "src/Hall9k.Domain/Features/Run/AgentRole.cs",
            "package.json",
            "docs/scope.md",
            ".github/workflows/ci.yml",
        ];

        PrReviewPreflightSurfaceMatcher.Match(changedFiles).Should().Equal("package.json", ".github/workflows/ci.yml");
    }

    [Fact]
    public void A_sibling_directory_sharing_a_string_prefix_does_not_match_a_directory_anchored_pattern()
    {
        PrReviewPreflightSurfaceMatcher.Match(["scripts-legacy/deploy.sh"])
            .Should().BeEmpty("scripts/** must not match a differently-named sibling directory that merely shares a prefix");
    }

    [Fact]
    public void A_windows_style_path_separator_still_matches_a_directory_anchored_pattern()
    {
        PrReviewPreflightSurfaceMatcher.Match(["scripts\\deploy.ps1"]).Should().ContainSingle();
    }
}
