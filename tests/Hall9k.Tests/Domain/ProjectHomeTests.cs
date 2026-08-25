using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The home as the domain sees it: a value object that refuses a relative path, a setting that
/// rides both the registration and the settings event, and a layout that is stated once so the
/// recipe and the render cannot disagree about it.
/// </summary>
// Redirects the process-wide HALL9K_HOME, so it shares the collection with the other tests
// that do: serialized, never yanking a home out from under a running one.
[Collection("Hall9kHome")]
public sealed class ProjectHomeTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly string _home = Path.Combine(Path.GetTempPath(), $"hall9k-home-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    public ProjectHomeTests() => Environment.SetEnvironmentVariable("HALL9K_HOME", _home);

    [Fact]
    public void A_home_is_absolute_or_it_is_refused()
    {
        ProjectHome.Parse(Path.Combine(Path.GetTempPath(), "somewhere")).HasValue.Should().BeTrue();

        Action relative = () => ProjectHome.Parse("./projects/hall9k");

        relative.Should().Throw<DomainValidationException>()
            .WithMessage("*absolute*", "a home is read back by a daemon that is in no particular directory");
    }

    [Fact]
    public void Blank_is_the_honest_absence_rather_than_an_error()
    {
        ProjectHome.Parse(null).Should().Be(ProjectHome.None);
        ProjectHome.Parse("   ").Should().Be(ProjectHome.None);
        ProjectHome.None.HasValue.Should().BeFalse();
    }

    [Fact]
    public void The_default_location_follows_the_platform_home_and_slugs_the_project_name()
    {
        ProjectHomePaths.DefaultFor("hall9k").Should().Be(Path.Combine(_home, "projects", "hall9k"));
        ProjectHomePaths.DefaultFor("Hall9k Platform").Should().Be(Path.Combine(_home, "projects", "hall9k-platform"));
    }

    [Fact]
    public void The_shape_is_seven_entries_and_the_layout_states_all_of_them()
    {
        string home = ProjectHomePaths.DefaultFor("hall9k");

        // tasks/_archive/ (backlog 51) is created up front alongside tasks/ itself, even though
        // nothing lands there until a task first goes terminal: the render advertises it as part
        // of the always-there layout, and Directories() is the one list the recipe and the render
        // both read from, so the two can never drift apart on whether it exists (conformance review).
        ProjectHomePaths.Directories(home).Should().Equal(
            home,
            Path.Combine(home, "repo"),
            Path.Combine(home, "ideas"),
            Path.Combine(home, "tasks"),
            Path.Combine(home, "tasks", "_archive"),
            Path.Combine(home, "skills"),
            Path.Combine(home, ".claude"),
            Path.Combine(home, ".claude", "skills"));

        ProjectHomePaths.AgentsFile(home).Should().Be(Path.Combine(home, "AGENTS.md"));
        ProjectHomePaths.BareRepository(home, "hall9k").Should().Be(Path.Combine(home, "repo", "hall9k.git"));
        ProjectHomePaths.DevWorktree(home).Should().Be(Path.Combine(home, "repo", "dev"));
    }

    [Fact]
    public void Worktrees_land_beside_the_bare_clone_inside_the_home()
    {
        string home = ProjectHomePaths.DefaultFor("hall9k");
        string bare = ProjectHomePaths.BareRepository(home, "hall9k");

        // GitWorktreeManager creates worktrees as siblings of the repository path, so the
        // recorded repository path being the bare clone inside repo/ is what puts every wt-*
        // under the home rather than somewhere else on the disk.
        Path.GetDirectoryName(bare).Should().Be(ProjectHomePaths.RepoDirectory(home));
        Path.GetDirectoryName(ProjectHomePaths.DevWorktree(home)).Should().Be(ProjectHomePaths.RepoDirectory(home));
    }

    [Fact]
    public void Registration_carries_the_home_and_a_stream_written_before_homes_existed_reads_as_none()
    {
        ProjectHome home = ProjectHome.Parse(ProjectHomePaths.DefaultFor("hall9k"));

        ProjectRegistered withHome = ProjectDecider.Register(
            DomainId.New(), DomainId.New(), DomainId.New(),
            name: "hall9k", repositoryPath: "/repos/hall9k.git", repositoryUrl: null,
            baseBranch: "main", registeredAt: Now, homeDirectory: home);
        ProjectRegistered without = ProjectDecider.Register(
            DomainId.New(), DomainId.New(), DomainId.New(),
            name: "hall9k", repositoryPath: "/repos/hall9k.git", repositoryUrl: null,
            baseBranch: "main", registeredAt: Now);

        withHome.HomeDirectory.Should().Be(home);
        without.HomeDirectory.Should().Be(ProjectHome.None, "a project with no home says so rather than guessing one");

        ProjectAggregate replayed = new();
        replayed.Apply(without);
        replayed.HomeDirectory.Should().Be(ProjectHome.None);
    }

    [Fact]
    public void Setting_the_home_moves_the_repository_path_with_it_on_both_aggregate_and_projection()
    {
        Guid id = DomainId.New();
        ProjectRegistered registered = ProjectDecider.Register(
            id, DomainId.New(), DomainId.New(),
            name: "hall9k", repositoryPath: "/old/hall9k.git", repositoryUrl: null,
            baseBranch: "main", registeredAt: Now);

        ProjectAggregate project = new();
        project.Apply(registered);

        string home = ProjectHomePaths.DefaultFor("hall9k");
        ProjectSettingsChanged changed = ProjectDecider.ChangeSettings(
            project,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: Optional<bool>.None,
            maxParallelAgents: Optional<int>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            Now,
            DomainId.New(),
            homeDirectory: Optional<ProjectHome>.Of(ProjectHome.Parse(home)),
            repositoryPath: Optional<string>.Of(ProjectHomePaths.BareRepository(home, "hall9k")));

        project.Apply(changed);
        project.HomeDirectory.Value.Should().Be(home);
        project.RepositoryPath.Should().Be(ProjectHomePaths.BareRepository(home, "hall9k"));

        ProjectDetails view = new ProjectDetailsProjection().Create(new FakeEvent<ProjectRegistered>(registered));
        new ProjectDetailsProjection().Apply(new FakeEvent<ProjectSettingsChanged>(changed), view);
        view.HomeDirectory.Value.Should().Be(home);
        view.RepositoryPath.Should().Be(ProjectHomePaths.BareRepository(home, "hall9k"));
    }

    [Fact]
    public void A_settings_change_that_says_nothing_about_the_home_leaves_it_alone()
    {
        Guid id = DomainId.New();
        ProjectHome home = ProjectHome.Parse(ProjectHomePaths.DefaultFor("hall9k"));
        ProjectAggregate project = new();
        project.Apply(ProjectDecider.Register(
            id, DomainId.New(), DomainId.New(),
            name: "hall9k", repositoryPath: "/repos/hall9k.git", repositoryUrl: null,
            baseBranch: "main", registeredAt: Now, homeDirectory: home));

        project.Apply(ProjectDecider.ChangeSettings(
            project,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: Optional<bool>.None,
            maxParallelAgents: 5,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            Now,
            DomainId.New()));

        project.HomeDirectory.Should().Be(home);
        project.RepositoryPath.Should().Be("/repos/hall9k.git");
    }

    [Fact]
    public void The_repository_path_can_be_repointed_but_never_cleared()
    {
        ProjectAggregate project = new();
        project.Apply(ProjectDecider.Register(
            DomainId.New(), DomainId.New(), DomainId.New(),
            name: "hall9k", repositoryPath: "/repos/hall9k.git", repositoryUrl: null,
            baseBranch: "main", registeredAt: Now));

        Action clear = () => ProjectDecider.ChangeSettings(
            project,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: Optional<bool>.None,
            maxParallelAgents: Optional<int>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            Now,
            DomainId.New(),
            repositoryPath: Optional<string>.Of("  "));

        clear.Should().Throw<DomainValidationException>()
            .WithMessage("*always has a local repository path*");
    }

    /// <summary>
    /// The repository path carries the home's own rule. Origin incident (2026-08-23): the pre-PR
    /// review of this branch found <c>project add --no-home --repo-url …</c> composing the path
    /// from an empty home and recording <c>repo/&lt;name&gt;.git</c>, which the daemon would have
    /// resolved against its own working directory. The CLI refuses that combination now; this is
    /// the rule underneath it.
    /// </summary>
    [Fact]
    public void A_repository_path_is_absolute_or_it_is_refused()
    {
        Action registerRelative = () => ProjectDecider.Register(
            DomainId.New(), DomainId.New(), DomainId.New(),
            name: "hall9k", repositoryPath: Path.Combine("repo", "hall9k.git"), repositoryUrl: null,
            baseBranch: "main", registeredAt: Now);

        registerRelative.Should().Throw<DomainValidationException>()
            .WithMessage("*absolute*", "a relative path names a different repository for every caller");

        ProjectAggregate project = new();
        project.Apply(ProjectDecider.Register(
            DomainId.New(), DomainId.New(), DomainId.New(),
            name: "hall9k", repositoryPath: "/repos/hall9k.git", repositoryUrl: null,
            baseBranch: "main", registeredAt: Now));

        Action repointRelative = () => ProjectDecider.ChangeSettings(
            project,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: Optional<bool>.None,
            maxParallelAgents: Optional<int>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            Now,
            DomainId.New(),
            repositoryPath: Optional<string>.Of(Path.Combine("repo", "hall9k.git")));

        repointRelative.Should().Throw<DomainValidationException>().WithMessage("*absolute*");
    }

    /// <summary>
    /// A session that has to read a project's files needs a working tree, and the repository path
    /// of a project with a home names the bare clone, which has none. Origin incident
    /// (2026-08-23): the pre-PR review of this branch found h9k task push-to-jira spawning its
    /// card-authoring session inside the bare clone.
    /// </summary>
    [Fact]
    public void A_reading_session_is_given_the_dev_worktree_and_never_the_bare_clone()
    {
        string home = ProjectHomePaths.DefaultFor("hall9k");
        ProjectDetails project = new()
        {
            Id = DomainId.New(),
            Name = "hall9k",
            BaseBranch = "main",
            HomeDirectory = ProjectHome.Parse(home),
            RepositoryPath = ProjectHomePaths.BareRepository(home, "hall9k"),
        };

        // Nothing on disk yet: the bare clone is all a half-made home has, and it is not a place
        // to read code in — so the answer is honest about there being no worktree.
        Directory.CreateDirectory(Path.Combine(project.RepositoryPath, "objects"));
        Directory.CreateDirectory(Path.Combine(project.RepositoryPath, "refs"));
        ProjectCheckout.ForReading(project).Should().Be(project.RepositoryPath);
        ProjectCheckout.IsBare(project.RepositoryPath).Should().BeTrue();

        Directory.CreateDirectory(ProjectHomePaths.DevWorktree(home));
        ProjectCheckout.ForReading(project).Should().Be(ProjectHomePaths.DevWorktree(home));

        // A project registered before homes existed points at an ordinary clone, and that stays
        // the answer rather than being redirected at a home it does not have.
        ProjectDetails legacy = new()
        {
            Id = DomainId.New(),
            Name = "hall9k",
            BaseBranch = "main",
            RepositoryPath = "/repos/hall9k",
        };
        ProjectCheckout.ForReading(legacy).Should().Be("/repos/hall9k");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", _previousHome);
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }
}
