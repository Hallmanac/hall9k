using FluentAssertions;
using Hall9k.Cli.Orchestrator;
using Hall9k.Cli.ProjectHomes;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Rendering;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The recipe behind <c>h9k solution init</c>: the thin home's layout in a temporary directory,
/// idempotence, the refusal of a foreign AGENTS.md before anything is written, and the reuse of
/// the platform writers for the anchor and settings. No store, no git, no GitHub.
/// </summary>
public sealed class SolutionHomeRecipeTests : IDisposable
{
    private const string SolutionKey = "01JSSSSSSSSSSSSSSSSSSSSSSS";

    private readonly ScopedTestHome _scopedHome = new();

    private static SolutionMembership Membership(string name = "storefront") =>
        SolutionDecider.Create(
            SolutionKey,
            name,
            [new SolutionMember("01JAAAAAAAAAAAAAAAAAAAAAAA", "shop-api", new Uri("https://github.com/acme/shop-api.git"))]);

    private static Task<IReadOnlyList<ProjectHomeStep>> Build(string home, SolutionMembership membership) =>
        SolutionHomeRecipe.BuildAsync(home, membership, [], _ => null, CancellationToken.None);

    [Fact]
    public async Task The_home_defaults_to_the_solutions_directory_named_after_the_solution()
    {
        SolutionHomePaths.DefaultFor("Store Front").Should().Be(Path.Combine(_scopedHome.Home, "solutions", "store-front"));

        string home = SolutionHomePaths.DefaultFor("storefront");
        IReadOnlyList<ProjectHomeStep> steps = await Build(home, Membership());

        steps.Should().NotContain(step => step.Outcome == ProjectHomeOutcome.Failed);
        SolutionHomePaths.Existing().Should().Equal([home]);
    }

    [Fact]
    public async Task The_thin_shape_is_made_with_no_repo_ideas_or_tasks()
    {
        string home = SolutionHomePaths.DefaultFor("storefront");

        await Build(home, Membership());

        File.Exists(ProjectHomePaths.AgentsFile(home)).Should().BeTrue();
        Directory.Exists(ProjectHomePaths.SkillsDirectory(home)).Should().BeTrue();
        Directory.Exists(ProjectHomePaths.ClaudeSkillsDirectory(home)).Should().BeTrue();
        Directory.Exists(ProjectHomePaths.RecipesDirectory(home)).Should().BeTrue();
        Directory.Exists(ProjectHomePaths.RepoDirectory(home)).Should().BeFalse();
        Directory.Exists(ProjectHomePaths.IdeasDirectory(home)).Should().BeFalse();
        Directory.Exists(ProjectHomePaths.TasksDirectory(home)).Should().BeFalse();
    }

    [Fact]
    public async Task The_anchor_and_settings_are_the_platform_writers_output_resolved_through_the_node()
    {
        string home = SolutionHomePaths.DefaultFor("storefront");

        await Build(home, Membership());

        File.ReadAllText(ProjectHomePaths.LaunchAnchorFile(home)).Should().Be(LaunchAnchorDocument.Render());
        File.ReadAllText(ProjectHomePaths.RecipeSettingsFile(home)).Should()
            .Be(RecipeSettingsDocument.Render(
                OrchestratorModel.ForNode(new OperatingSettings()), OrchestratorEffort.ForNode(new OperatingSettings())));
    }

    [Fact]
    public async Task The_recipe_skill_and_canonical_skills_are_seeded_from_what_the_install_published()
    {
        WriteSkill(SkillLibraryPaths.CanonicalDirectory, "challenge");
        WriteSkill(RecipeLibraryPaths.CanonicalDirectory, RecipeSkillPublisher.GeneratorSkillName);
        string home = SolutionHomePaths.DefaultFor("storefront");

        await Build(home, Membership());

        File.Exists(Path.Combine(ProjectHomePaths.SkillsDirectory(home), "challenge", "SKILL.md")).Should().BeTrue();
        File.Exists(Path.Combine(
            ProjectHomePaths.RecipeSkillDirectory(home, RecipeSkillPublisher.GeneratorSkillName), "SKILL.md")).Should().BeTrue();
        File.Exists(Path.Combine(
            ProjectHomePaths.ClaudeSkillsDirectory(home), RecipeSkillPublisher.GeneratorSkillName, "SKILL.md")).Should().BeTrue();
    }

    [Fact]
    public async Task A_second_run_changes_nothing_and_says_so()
    {
        string home = SolutionHomePaths.DefaultFor("storefront");
        await Build(home, Membership());

        IReadOnlyList<ProjectHomeStep> second = await Build(home, Membership());

        second.Should().NotContain(step => step.Outcome == ProjectHomeOutcome.Failed);
        second.Should().Contain(step => step.Outcome == ProjectHomeOutcome.AlreadyThere && step.Message.Contains("already existed"));
        second.Should().Contain(step => step.Outcome == ProjectHomeOutcome.AlreadyThere && step.Message.Contains("AGENTS.md already matches"));
        second.Should().Contain(step => step.Outcome == ProjectHomeOutcome.AlreadyThere && step.Message.Contains("launch-anchor.md already current"));
        second.Should().Contain(step => step.Outcome == ProjectHomeOutcome.AlreadyThere && step.Message.Contains("settings.json already current"));
    }

    [Fact]
    public async Task A_stale_anchor_in_a_repaired_home_is_rewritten()
    {
        string home = SolutionHomePaths.DefaultFor("storefront");
        await Build(home, Membership());
        File.WriteAllText(ProjectHomePaths.LaunchAnchorFile(home), "stale\n");

        await Build(home, Membership());

        File.ReadAllText(ProjectHomePaths.LaunchAnchorFile(home)).Should().Be(LaunchAnchorDocument.Render());
    }

    [Fact]
    public async Task A_foreign_agents_file_is_refused_before_anything_else_is_written()
    {
        string home = SolutionHomePaths.DefaultFor("storefront");
        Directory.CreateDirectory(home);
        File.WriteAllText(ProjectHomePaths.AgentsFile(home), "# Somebody's own AGENTS.md\n");

        IReadOnlyList<ProjectHomeStep> steps = await Build(home, Membership());

        steps.Should().ContainSingle().Which.Outcome.Should().Be(ProjectHomeOutcome.Failed);
        steps[0].Message.Should().Contain("was left alone").And.Contain("h9k solution init \"storefront\"");
        File.ReadAllText(ProjectHomePaths.AgentsFile(home)).Should().Be("# Somebody's own AGENTS.md\n");
        Directory.GetFileSystemEntries(home).Should().ContainSingle("nothing but the foreign file may be in the home");
    }

    [Fact]
    public async Task The_first_agents_copy_names_the_members_and_a_run_after_a_rename_rewrites_it_in_place()
    {
        string home = SolutionHomePaths.DefaultFor("storefront");
        await Build(home, Membership());
        File.ReadAllText(ProjectHomePaths.AgentsFile(home)).Should().Contain("shop-api").And.Contain(SolutionAgentsDocument.NotJoinedStatement);

        SolutionMembership renamed = SolutionDecider.Rename(Membership(), "marketplace");
        ProjectHomeStep step = SolutionHomeRecipe.WriteAgentsStep(home, renamed, [], _ => null);

        step.Outcome.Should().Be(ProjectHomeOutcome.Created);
        step.Message.Should().Contain("re-rendered");
        File.ReadAllText(ProjectHomePaths.AgentsFile(home)).Should().Contain("# marketplace: solution home");
    }

    private static void WriteSkill(string parent, string name)
    {
        string directory = Path.Combine(parent, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), $"---\nname: {name}\ndescription: a skill\n---\n\n# {name}\n");
    }

    public void Dispose() => _scopedHome.Dispose();
}
