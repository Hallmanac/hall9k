using FluentAssertions;
using Hall9k.Cli.ProjectHomes;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The recipe that creates a project home: deterministic platform code, idempotent throughout,
/// and answerable for every directory it makes. No git here — the repo/ half has its own tests
/// against a real repository; these cover the shape, the skill seeding and the render.
/// </summary>
// Redirects the process-wide HALL9K_HOME (the canonical skill set hangs off it), so it shares
// the collection with the other tests that do.
[Collection("Hall9kHome")]
public sealed class ProjectHomeRecipeTests : IDisposable
{
    private readonly string _platformHome = Path.Combine(Path.GetTempPath(), $"hall9k-recipe-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    // The recipe shells out to git, so a hung child never becomes a hung suite.
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromMinutes(3));

    public ProjectHomeRecipeTests() => Environment.SetEnvironmentVariable("HALL9K_HOME", _platformHome);

    [Fact]
    public async Task The_shape_is_created_whole_and_is_the_same_on_a_second_run()
    {
        string home = ProjectHomePaths.DefaultFor("hall9k");
        ProjectDetails project = SomeProject();

        IReadOnlyList<ProjectHomeStep> first = await ProjectHomeRecipe.BuildAsync(home, project, _cancellation.Token);

        first.Should().NotContain(step => step.Outcome == ProjectHomeOutcome.Failed);
        Directory.Exists(Path.Combine(home, "repo")).Should().BeTrue();
        Directory.Exists(Path.Combine(home, "ideas")).Should().BeTrue();
        Directory.Exists(Path.Combine(home, "tasks")).Should().BeTrue();
        Directory.Exists(Path.Combine(home, "skills")).Should().BeTrue();
        Directory.Exists(Path.Combine(home, ".claude", "skills")).Should().BeTrue();
        File.Exists(Path.Combine(home, "AGENTS.md")).Should().BeTrue();

        IReadOnlyList<ProjectHomeStep> second = await ProjectHomeRecipe.BuildAsync(home, project, _cancellation.Token);

        second.Should().Contain(
            step => step.Outcome == ProjectHomeOutcome.AlreadyThere && step.Message.Contains("already existed"),
            "rerunning reports and completes in place rather than starting over");
        second.Should().Contain(
            step => step.Outcome == ProjectHomeOutcome.AlreadyThere && step.Message.Contains("AGENTS.md already matches"));
    }

    /// <summary>
    /// <c>h9k project add --repo</c> registers against a repository that already exists here, and
    /// its help text promises the home's repo/ is left unmaterialised. A clone made anyway would
    /// be a second copy nothing ever cuts a worktree from — the decoration project init exists to
    /// avoid leaving behind. Everything else in the shape is still built.
    /// </summary>
    [Fact]
    public async Task A_project_that_dispatches_from_elsewhere_gets_a_home_but_no_second_clone()
    {
        string home = ProjectHomePaths.DefaultFor("hall9k");
        ProjectDetails project = SomeProject();
        project.RepositoryPath = Path.Combine(_platformHome, "somewhere-else", "hall9k.git");

        IReadOnlyList<ProjectHomeStep> steps = await ProjectHomeRecipe.BuildAsync(
            home, project, _cancellation.Token, materialiseRepository: false);

        steps.Should().Contain(step =>
            step.Outcome == ProjectHomeOutcome.Skipped && step.Message.Contains("left unmaterialised"));
        steps.Should().NotContain(step => step.Outcome == ProjectHomeOutcome.Failed);
        Directory.EnumerateFileSystemEntries(ProjectHomePaths.RepoDirectory(home)).Should().BeEmpty();
        File.Exists(ProjectHomePaths.AgentsFile(home)).Should().BeTrue("the rest of the shape is still a home");
        Directory.Exists(ProjectHomePaths.TasksDirectory(home)).Should().BeTrue();

        string rendered = File.ReadAllText(ProjectHomePaths.AgentsFile(home));
        rendered.Should().Contain(project.RepositoryPath, "the render sends a session where the code actually is");
        rendered.Should().NotContain(
            ProjectHomePaths.DevWorktree(home), "there is no dev worktree here, so nothing may point at one");
    }

    /// <summary>
    /// <c>h9k project init --keep-repo-path</c> still materialises repo/ (ProjectHomeRecipe always
    /// does), it just declines to re-point the project at it. A render driven only by whether
    /// RepositoryPath equals the bare clone would describe a repo/ that physically has a bare
    /// clone and a dev/ worktree in it as empty — which is what a pre-PR review of this branch
    /// caught (cycle 4, adversarial lens): ProjectCheckout.ForReading reads dev/ once it exists,
    /// regardless of RepositoryPath, so the briefing has to agree with what reading sessions do.
    /// </summary>
    [Fact]
    public void Repo_materialised_but_not_repointed_is_not_described_as_empty()
    {
        string home = ProjectHomePaths.DefaultFor("hall9k");
        Directory.CreateDirectory(ProjectHomePaths.DevWorktree(home));
        ProjectDetails project = SomeProject();
        project.RepositoryPath = Path.Combine(_platformHome, "somewhere-else", "hall9k.git");

        string rendered = ProjectAgentsDocument.Render(home, project);

        rendered.Should().NotContain("repo/       empty", "the bare clone and dev/ worktree are really there");
        rendered.Should().Contain(
            Path.Combine(ProjectHomePaths.DevWorktree(home), "AGENTS.md"),
            "a reading session lands in dev/, so that is the deep layer this file should point at");
        rendered.Should().Contain(
            project.RepositoryPath, "task worktrees still come from the recorded path, and that stays honest");
    }

    [Fact]
    public async Task Seeded_skills_link_into_the_install_set_and_project_skills_survive_reseeding()
    {
        PublishCanonicalSkill("commit-plan", "Organize changes into cohesive commits.");
        PublishCanonicalSkill("pr-summary", "Generate a PR title and description.");
        string home = ProjectHomePaths.DefaultFor("hall9k");

        await ProjectHomeRecipe.BuildAsync(home, SomeProject(), _cancellation.Token);

        string seeded = Path.Combine(home, "skills", "commit-plan");
        new DirectoryInfo(seeded).LinkTarget.Should().Be(
            SkillLibraryPaths.Skill("commit-plan"),
            "a link is what makes h9k install update every project's platform skills in one move");
        new DirectoryInfo(Path.Combine(home, ".claude", "skills", "commit-plan")).LinkTarget.Should().Be(
            Path.Combine(home, "skills", "commit-plan"),
            "the vendor adapter points into skills/, never at a second copy of the content");

        // A project-specific skill: a real directory, beside the seeded links.
        string mine = Path.Combine(home, "skills", "board-rules");
        Directory.CreateDirectory(mine);
        File.WriteAllText(Path.Combine(mine, "SKILL.md"), "---\nname: board-rules\n---\n");

        await ProjectHomeRecipe.BuildAsync(home, SomeProject(), _cancellation.Token);

        File.Exists(Path.Combine(mine, "SKILL.md")).Should().BeTrue("re-seeding never touches a real directory");
        new DirectoryInfo(mine).LinkTarget.Should().BeNull();
        new DirectoryInfo(Path.Combine(home, ".claude", "skills", "board-rules")).LinkTarget.Should().Be(
            mine, "a project skill gets its adapter on exactly the same footing as a seeded one");
    }

    /// <summary>
    /// A copy fallback (<see cref="SkillSeeder.CopyMarkerFile"/>) is this recipe's own real
    /// directory, not a project override, so it is refreshed on the next pass exactly like a
    /// symlink is rather than frozen at whatever the install shipped the day the symlink first
    /// failed. Origin: adversarial pre-PR review of this branch, cycle 1.
    /// </summary>
    [Fact]
    public async Task A_copy_made_when_symlinks_were_refused_is_refreshed_on_the_next_pass()
    {
        PublishCanonicalSkill("commit-plan", "Organize changes into cohesive commits.");
        string home = ProjectHomePaths.DefaultFor("hall9k");
        Directory.CreateDirectory(Path.Combine(home, "skills"));
        Directory.CreateDirectory(Path.Combine(home, ".claude", "skills"));

        string copied = Path.Combine(home, "skills", "commit-plan");
        Directory.CreateDirectory(copied);
        File.WriteAllText(Path.Combine(copied, "SKILL.md"), "stale content from the earlier install");
        File.WriteAllText(Path.Combine(copied, SkillSeeder.CopyMarkerFile), string.Empty);

        await ProjectHomeRecipe.BuildAsync(home, SomeProject(), _cancellation.Token);

        new DirectoryInfo(copied).LinkTarget.Should().Be(
            SkillLibraryPaths.Skill("commit-plan"),
            "a marked copy is this recipe's own, so it is refreshed to a link rather than left frozen");
    }

    [Fact]
    public async Task A_skill_the_install_stopped_publishing_leaves_no_dangling_pointer()
    {
        PublishCanonicalSkill("retired-skill", "Something the platform used to ship.");
        string home = ProjectHomePaths.DefaultFor("hall9k");
        await ProjectHomeRecipe.BuildAsync(home, SomeProject(), _cancellation.Token);

        Directory.Delete(SkillLibraryPaths.Skill("retired-skill"), recursive: true);
        await ProjectHomeRecipe.BuildAsync(home, SomeProject(), _cancellation.Token);

        Directory.EnumerateFileSystemEntries(Path.Combine(home, "skills")).Should().BeEmpty();
        Directory.EnumerateFileSystemEntries(Path.Combine(home, ".claude", "skills")).Should().BeEmpty();
    }

    /// <summary>
    /// The other half of that rule, and the boundary it turns on. A link somebody made by hand to
    /// a directory that merely sits beside the canonical set (<c>~/.hall9k/skills-local/…</c> next
    /// to <c>~/.hall9k/skills</c>) is not a seed this owns, so a dangling one of those is theirs
    /// to explain rather than ours to collect. Origin incident (2026-08-23): the third cycle of
    /// this branch's pre-PR review read the prefix test as written and found the sibling
    /// directory it admits.
    /// </summary>
    [Fact]
    public async Task A_hand_made_link_beside_the_canonical_directory_is_left_alone_when_it_dangles()
    {
        PublishCanonicalSkill("commit-plan", "Organize changes into cohesive commits.");
        string home = ProjectHomePaths.DefaultFor("hall9k");
        await ProjectHomeRecipe.BuildAsync(home, SomeProject(), _cancellation.Token);

        // A sibling of the canonical directory, so its path starts with the canonical one
        // character for character while being nobody's seed.
        string sibling = Path.Combine($"{SkillLibraryPaths.CanonicalDirectory}-local", "board-rules");
        string link = Path.Combine(home, "skills", "board-rules");
        Directory.CreateDirectory(sibling);
        Directory.CreateSymbolicLink(link, sibling);
        Directory.Delete(sibling, recursive: true);

        await ProjectHomeRecipe.BuildAsync(home, SomeProject(), _cancellation.Token);

        new DirectoryInfo(link).LinkTarget.Should().Be(
            sibling, "a link out of the canonical set is somebody's own, dangling or not");
    }

    [Fact]
    public async Task An_install_with_no_published_skills_says_so_rather_than_failing()
    {
        string home = ProjectHomePaths.DefaultFor("hall9k");

        IReadOnlyList<ProjectHomeStep> steps = await ProjectHomeRecipe.BuildAsync(home, SomeProject(), _cancellation.Token);

        steps.Should().Contain(step =>
            step.Outcome == ProjectHomeOutcome.Skipped && step.Message.Contains("h9k install"));
        steps.Should().NotContain(step => step.Outcome == ProjectHomeOutcome.Failed);
    }

    [Fact]
    public async Task Install_publishes_the_skill_set_and_a_home_seeded_from_it_follows_a_republish()
    {
        string source = Path.Combine(_platformHome, "release-skills");
        WriteSkill(source, "commit-plan", "The first version.");
        SkillSeeder.PublishCanonical(source).Published.Should().Equal(["commit-plan"]);

        string home = ProjectHomePaths.DefaultFor("hall9k");
        await ProjectHomeRecipe.BuildAsync(home, SomeProject(), _cancellation.Token);

        // The install republishes with the skill's content changed — the whole point of seeding
        // by symlink is that every project home is already on the new content.
        WriteSkill(source, "commit-plan", "The second version.");
        SkillSeeder.PublishCanonical(source);

        File.ReadAllText(Path.Combine(home, "skills", "commit-plan", "SKILL.md"))
            .Should().Contain("The second version.", "no project had to be re-seeded for that");
    }

    [Fact]
    public void Publishing_drops_a_skill_the_install_no_longer_ships()
    {
        string source = Path.Combine(_platformHome, "release-skills");
        WriteSkill(source, "commit-plan", "Kept.");
        WriteSkill(source, "retired-skill", "Dropped.");
        SkillSeeder.PublishCanonical(source);

        Directory.Delete(Path.Combine(source, "retired-skill"), recursive: true);

        SkillPublication second = SkillSeeder.PublishCanonical(source);

        second.Published.Should().Equal(["commit-plan"]);
        second.Retired.Should().Equal(["retired-skill"], "removing content is named, never merely done");
        SkillLibraryPaths.Published().Should().Equal(["commit-plan"]);
    }

    /// <summary>
    /// The install owns what the install published, and nothing else. A skill somebody wrote
    /// straight into the canonical directory is theirs: it is not a stale copy of anything, so it
    /// is not the publishing pass's to recursively delete.
    /// </summary>
    [Fact]
    public void Publishing_never_removes_a_skill_the_install_did_not_put_there()
    {
        string source = Path.Combine(_platformHome, "release-skills");
        WriteSkill(source, "commit-plan", "Shipped by the platform.");
        SkillSeeder.PublishCanonical(source);

        PublishCanonicalSkill("my-team-conventions", "Written by hand on this machine.");

        SkillPublication second = SkillSeeder.PublishCanonical(source);

        second.Retired.Should().BeEmpty();
        File.Exists(Path.Combine(SkillLibraryPaths.Skill("my-team-conventions"), "SKILL.md"))
            .Should().BeTrue("an ordinary h9k install must not delete what it never shipped");
    }

    /// <summary>
    /// The same rule where it is easiest to miss: a hand-written skill whose name collides with a
    /// shipped one. The retiring half never sees it (it is not in the manifest), so the publishing
    /// half is the only thing standing between the operator's content and a recursive delete.
    /// Origin incident (2026-08-23): the second cycle of the project-home branch's pre-PR review
    /// found the retiring half fixed and this one still wiping a colliding name with no prompt, no
    /// backup, and nothing in the install's output naming what went.
    /// </summary>
    [Fact]
    public void Publishing_never_overwrites_a_skill_the_install_did_not_put_there()
    {
        string source = Path.Combine(_platformHome, "release-skills");
        WriteSkill(source, "pr-summary", "Shipped by the platform.");
        PublishCanonicalSkill("pr-summary", "Written by hand on this machine.");

        SkillPublication publication = SkillSeeder.PublishCanonical(source);

        File.ReadAllText(Path.Combine(SkillLibraryPaths.Skill("pr-summary"), "SKILL.md"))
            .Should().Contain("Written by hand on this machine.", "an override is a thing somebody meant");
        publication.LeftAlone.Should().Equal(["pr-summary"], "a shadow nobody is told about is found out later");
        publication.Published.Should().BeEmpty("what was not written is not reported as published");

        // And the name stays out of the manifest, so the retiring loop on the next install does
        // not read it as the install's own and delete what publishing just declined to overwrite.
        SkillPublication next = SkillSeeder.PublishCanonical(source);
        next.Retired.Should().BeEmpty();
        next.LeftAlone.Should().Equal(["pr-summary"]);
        File.Exists(Path.Combine(SkillLibraryPaths.Skill("pr-summary"), "SKILL.md")).Should().BeTrue();
    }

    /// <summary>
    /// Before this fix, an unreadable manifest was read exactly like an absent one, so
    /// <c>commit-plan</c>'s already-existing destination failed the ownership check and landed
    /// in <c>leftAlone</c> — falsely reporting an install-owned skill as an operator override
    /// that was never observed, and (for a name new to this source) leaving a copy on disk whose
    /// hash was never recorded anywhere, since the unconditional manifest rewrite at the end of
    /// <see cref="SkillSeeder.PublishCanonical"/> was skipped for the same reason. Both are the
    /// same root cause: proceeding on an empty reading of a manifest that is not actually empty.
    /// The fix is to do nothing at all this pass — see <see cref="SkillPublication.ManifestUnconfirmed"/>.
    /// </summary>
    [Fact]
    public void Publishing_does_not_erase_the_manifest_when_it_cannot_be_read()
    {
        string source = Path.Combine(_platformHome, "release-skills");
        WriteSkill(source, "commit-plan", "Shipped by the platform.");
        SkillSeeder.PublishCanonical(source);
        string manifestBeforeFailure = File.ReadAllText(SkillLibraryPaths.PublishedManifest);

        SkillPublication publication;
        if (OperatingSystem.IsWindows())
        {
            using FileStream lockHandle = new(
                SkillLibraryPaths.PublishedManifest, FileMode.Open, FileAccess.Read, FileShare.None);

            publication = SkillSeeder.PublishCanonical(source);
        }
        else
        {
            if (!MadeUnreadable(SkillLibraryPaths.PublishedManifest))
            {
                return;
            }

            try
            {
                publication = SkillSeeder.PublishCanonical(source);
            }
            finally
            {
                File.SetUnixFileMode(
                    SkillLibraryPaths.PublishedManifest, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        publication.ManifestUnconfirmed.Should().BeTrue(
            "the manifest could not be read this pass, so nothing can safely be classified");
        publication.Published.Should().BeEmpty();
        publication.Retired.Should().BeEmpty();
        publication.LeftAlone.Should().BeEmpty(
            "commit-plan was never observed to be an operator override — reporting it as one "
            + "would be a claim this pass never confirmed");
        File.ReadAllText(SkillLibraryPaths.PublishedManifest).Should().Be(manifestBeforeFailure,
            "an unreadable manifest must never be overwritten with an empty one — that would "
            + "permanently forget every previously published skill");
    }

    private static bool MadeUnreadable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            File.ReadAllText(path);
            return false;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    [Fact]
    public void The_render_names_the_layout_the_deep_layer_and_the_tools_the_bindings_imply()
    {
        string home = ProjectHomePaths.DefaultFor("hall9k");
        Directory.CreateDirectory(ProjectHomePaths.DevWorktree(home));

        string rendered = ProjectAgentsDocument.Render(home, GitHubProject());

        rendered.Should().Contain("Generated by `h9k`", "a generated file that does not announce itself gets hand-edited");
        rendered.Should().Contain("repo/");
        rendered.Should().Contain("ideas/");
        rendered.Should().Contain("tasks/");
        rendered.Should().Contain("skills/");
        rendered.Should().Contain(Path.Combine(ProjectHomePaths.DevWorktree(home), "AGENTS.md"),
            "the repository's own guidance is the deep layer this file points at");
        rendered.Should().Contain("`h9k`");
        rendered.Should().Contain("`gh`", "the remote is GitHub");
        rendered.Should().NotContain("Atlassian CLI", "no Jira board is bound");
    }

    [Fact]
    public void Binding_a_jira_board_adds_the_atlassian_cli_and_nothing_else()
    {
        ProjectDetails project = GitHubProject();
        project.JiraProjectKey = JiraProjectKey.Parse("PROJ");

        IReadOnlyList<string> dependencies = ProjectAgentsDocument.ToolDependencies(project);

        dependencies.Should().ContainSingle(dependency => dependency.Contains("Atlassian CLI"));
        dependencies.Should().ContainSingle(dependency => dependency.Contains("`gh`"));
    }

    [Fact]
    public void A_project_with_no_remote_needs_neither_gh_nor_an_atlassian_cli()
    {
        ProjectDetails project = SomeProject();

        IReadOnlyList<string> dependencies = ProjectAgentsDocument.ToolDependencies(project);

        dependencies.Should().HaveCount(2);
        dependencies.Should().ContainSingle(dependency => dependency.Contains("`h9k`"));
        dependencies.Should().ContainSingle(dependency => dependency.Contains("`git`"));
    }

    /// <summary>
    /// The rule <see cref="SkillSeeder"/> already applies to the canonical skill set, extended to
    /// AGENTS.md: only what the platform rendered is the platform's to overwrite. Pointing --home
    /// at a directory that already holds an unrelated AGENTS.md (a checkout's own, for instance)
    /// must not silently destroy it. Origin incident (2026-08-23): cycle 5 of this branch's pre-PR
    /// review found the recipe writing over exactly this file with no prompt and no backup. The
    /// refusal is checked before anything else in <c>BuildAsync</c> runs, so it costs nothing: the
    /// conformance pass of cycle 2 found it checked last instead, after the clone, the skill
    /// seeding and the rest of the shape had already been written into the directory it then
    /// refused to finish.
    /// </summary>
    [Fact]
    public async Task A_pre_existing_AGENTS_md_that_was_never_rendered_by_h9k_is_left_alone()
    {
        string home = ProjectHomePaths.DefaultFor("hall9k");
        Directory.CreateDirectory(home);
        string path = ProjectHomePaths.AgentsFile(home);
        File.WriteAllText(path, "# hall9k\n\nHand-written repository guidance, not generated by h9k.\n");

        IReadOnlyList<ProjectHomeStep> steps = await ProjectHomeRecipe.BuildAsync(home, SomeProject(), _cancellation.Token);

        steps.Should().Contain(step =>
            step.Outcome == ProjectHomeOutcome.Failed && step.Message.Contains(path));
        File.ReadAllText(path).Should().Contain(
            "Hand-written repository guidance", "content this recipe never rendered is not its to destroy");
        steps.Should().ContainSingle("the refusal is the whole report: nothing else in the recipe ran");
        Directory.Exists(Path.Combine(home, "repo")).Should().BeFalse(
            "the refusal is checked before the clone, not after it");
        Directory.Exists(Path.Combine(home, "skills")).Should().BeFalse(
            "the refusal is checked before skill seeding, not after it");
    }

    [Fact]
    public async Task The_render_is_rewritten_when_the_facts_change()
    {
        string home = ProjectHomePaths.DefaultFor("hall9k");
        ProjectDetails project = SomeProject();
        await ProjectHomeRecipe.BuildAsync(home, project, _cancellation.Token);

        File.ReadAllText(ProjectHomePaths.AgentsFile(home)).Should().NotContain("PROJ");

        project.JiraProjectKey = JiraProjectKey.Parse("PROJ");
        ProjectHomeStep step = ProjectAgentsDocument.Write(home, project);

        step.Outcome.Should().Be(ProjectHomeOutcome.Created);
        step.Message.Should().Contain("re-rendered");
        File.ReadAllText(ProjectHomePaths.AgentsFile(home)).Should().Contain("PROJ");
    }

    private static void PublishCanonicalSkill(string name, string description) =>
        WriteSkill(SkillLibraryPaths.CanonicalDirectory, name, description);

    private static void WriteSkill(string parent, string name, string description)
    {
        string directory = Path.Combine(parent, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "SKILL.md"), $"---\nname: {name}\ndescription: {description}\n---\n\n# {name}\n");
    }

    /// <summary>
    /// No remote on purpose: these tests run the recipe for real, and a project with a remote
    /// would have it reach for the network. Materialisation against a real repository is
    /// <see cref="RepoMaterialiserTests"/>'s job, against a repository on this disk.
    /// </summary>
    private static ProjectDetails SomeProject() => new()
    {
        Id = DomainId.New(),
        Name = "hall9k",
        BaseBranch = "main",
        HomeDirectory = ProjectHome.Parse(ProjectHomePaths.DefaultFor("hall9k")),
        RepositoryPath = ProjectHomePaths.BareRepository(ProjectHomePaths.DefaultFor("hall9k"), "hall9k"),
    };

    private static ProjectDetails GitHubProject()
    {
        ProjectDetails project = SomeProject();
        project.RepositoryUrl = new Uri("https://github.com/Hallmanac/hall9k");
        return project;
    }

    public void Dispose()
    {
        _cancellation.Dispose();
        Environment.SetEnvironmentVariable("HALL9K_HOME", _previousHome);
        if (Directory.Exists(_platformHome))
        {
            Directory.Delete(_platformHome, recursive: true);
        }
    }
}
