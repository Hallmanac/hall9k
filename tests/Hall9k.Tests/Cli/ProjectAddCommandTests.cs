using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The pure collision-resolution helpers <c>h9k project add</c> uses when <c>--name</c> already
/// names an archived project (task: a project can be archived, listed as archived, reactivated,
/// and renamed). Database-free, the same convention <c>ProjectRemoveCommandTests</c> already uses
/// for a command's own pure helper logic.
/// <para>
/// What these guard: renaming an archived project frees its <em>name</em> for the uniqueness
/// check, but a rename never moves the archived project's recorded home off disk
/// (<c>ProjectRenameCommand</c>'s own contract) — so a fresh registration that would otherwise
/// default to that exact path still collides with it, and the operator has to say <c>--home</c>
/// explicitly. Before this fix, that collision surfaced only downstream, after the rename had
/// already been reported as done and discarded when the registration then failed (independent
/// pre-PR review, cycle 1: conformance and adversarial lenses, both high, "no test covers this
/// path").
/// </para>
/// </summary>
// Redirects the process-wide HALL9K_HOME (ProjectHomePaths.DefaultFor reads it), so it shares
// the collection with the other tests that do: serialized, never racing a concurrent test that
// points it somewhere else mid-assertion.
[Collection("Hall9kHome")]
public sealed class ProjectAddCommandTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly string _home = Path.Combine(Path.GetTempPath(), $"hall9k-project-add-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    public ProjectAddCommandTests() => Environment.SetEnvironmentVariable("HALL9K_HOME", _home);

    public void Dispose() => Environment.SetEnvironmentVariable("HALL9K_HOME", _previousHome);

    [Fact]
    public void Default_home_collision_is_detected_when_the_archived_project_still_holds_it()
    {
        ProjectDetails archived = RegisteredAt("smoke", ProjectHomePaths.DefaultFor("smoke"));

        ProjectAddCommand.DefaultHomeCollidesWithArchived(archived, "smoke").Should().BeTrue(
            "the archived project's own recorded home is exactly where a fresh 'smoke' "
            + "registration would default to as well");
    }

    [Fact]
    public void No_collision_when_the_archived_project_used_a_custom_home()
    {
        ProjectDetails archived = RegisteredAt("smoke", "/somewhere/else/entirely");

        ProjectAddCommand.DefaultHomeCollidesWithArchived(archived, "smoke").Should().BeFalse(
            "the archived project never claimed the default path, so a fresh registration "
            + "defaulting to it collides with nothing");
    }

    [Fact]
    public void No_collision_when_the_archived_project_never_had_a_home()
    {
        ProjectDetails archived = RegisteredAt("smoke", homeDirectory: null);

        ProjectAddCommand.DefaultHomeCollidesWithArchived(archived, "smoke").Should().BeFalse();
    }

    [Fact]
    public void Renaming_into_the_collision_without_a_home_is_refused_up_front()
    {
        ProjectDetails archived = RegisteredAt("smoke", ProjectHomePaths.DefaultFor("smoke"));
        ProjectAddCommand.Settings settings = new()
        {
            Name = "smoke",
            RenameArchivedTo = "smoke-old",
        };

        Action act = () => ProjectAddCommand.RequireHomeAwayFromArchivedDefault(archived, settings);

        act.Should().Throw<DomainValidationException>().WithMessage("*--home*");
    }

    [Fact]
    public void An_explicit_home_clears_the_collision()
    {
        ProjectDetails archived = RegisteredAt("smoke", ProjectHomePaths.DefaultFor("smoke"));
        ProjectAddCommand.Settings settings = new()
        {
            Name = "smoke",
            RenameArchivedTo = "smoke-old",
            Home = "/a/home/of/its/own",
        };

        Action act = () => ProjectAddCommand.RequireHomeAwayFromArchivedDefault(archived, settings);

        act.Should().NotThrow(
            "an operator who already said where the new project lives never hits the collision "
            + "at all");
    }

    [Fact]
    public void No_home_at_all_clears_the_collision()
    {
        ProjectDetails archived = RegisteredAt("smoke", ProjectHomePaths.DefaultFor("smoke"));
        ProjectAddCommand.Settings settings = new()
        {
            Name = "smoke",
            RenameArchivedTo = "smoke-old",
            NoHome = true,
        };

        Action act = () => ProjectAddCommand.RequireHomeAwayFromArchivedDefault(archived, settings);

        act.Should().NotThrow("--no-home means the new registration never claims a home path at all");
    }

    [Fact]
    public void A_custom_archived_home_never_collides_with_the_new_default()
    {
        ProjectDetails archived = RegisteredAt("smoke", "/somewhere/else/entirely");
        ProjectAddCommand.Settings settings = new()
        {
            Name = "smoke",
            RenameArchivedTo = "smoke-old",
        };

        Action act = () => ProjectAddCommand.RequireHomeAwayFromArchivedDefault(archived, settings);

        act.Should().NotThrow();
    }

    [Fact]
    public void Reactivate_and_rename_archived_together_are_refused()
    {
        ProjectAddCommand.Settings settings = new()
        {
            Name = "smoke",
            ReactivateArchived = true,
            RenameArchivedTo = "smoke-old",
        };

        Action act = () => ProjectAddCommand.RequireExclusiveArchivedCollisionFlags(settings);

        act.Should().Throw<DomainValidationException>().WithMessage("*--reactivate-archived*--rename-archived-to*");
    }

    [Fact]
    public void Reactivate_archived_alone_is_not_refused()
    {
        ProjectAddCommand.Settings settings = new() { Name = "smoke", ReactivateArchived = true };

        Action act = () => ProjectAddCommand.RequireExclusiveArchivedCollisionFlags(settings);

        act.Should().NotThrow();
    }

    [Fact]
    public void Rename_archived_alone_is_not_refused()
    {
        ProjectAddCommand.Settings settings = new() { Name = "smoke", RenameArchivedTo = "smoke-old" };

        Action act = () => ProjectAddCommand.RequireExclusiveArchivedCollisionFlags(settings);

        act.Should().NotThrow();
    }

    private static ProjectDetails RegisteredAt(string name, string? homeDirectory)
    {
        ProjectDetailsProjection projection = new();
        return projection.Create(new FakeEvent<ProjectRegistered>(new ProjectRegistered(
            DomainId.New(),
            DomainId.New(),
            DomainId.New(),
            name,
            "/repos/whatever.git",
            RepositoryUrl: null,
            "main",
            Now,
            HomeDirectory: homeDirectory is null ? ProjectHome.None : ProjectHome.Parse(homeDirectory))));
    }
}
