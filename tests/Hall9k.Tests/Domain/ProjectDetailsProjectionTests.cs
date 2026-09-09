using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

public sealed class ProjectDetailsProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_then_apply_settings_builds_the_read_model_without_a_database()
    {
        ProjectDetailsProjection projection = new();
        Guid id = DomainId.New();

        ProjectDetails view = projection.Create(new FakeEvent<ProjectRegistered>(new ProjectRegistered(
            id, DomainId.New(), DomainId.New(), "hall9k", "/repos/hall9k.git",
            new Uri("https://github.com/Hallmanac/hall9k"), "main", Now)));

        projection.Apply(new FakeEvent<ProjectSettingsChanged>(new ProjectSettingsChanged(
            id,
            VerifyCommands: new List<VerifyCommand> { new("build", "dotnet build"), new("test", "dotnet test") },
            SkipPermissions: true,
            MaxParallelAgents: 2,
            ContextLinks: new List<ContextLink> { new("jira", new Uri("https://example.atlassian.net")) },
            ChangedAt: Now.AddMinutes(5), ChangedByOwnerId: DomainId.New(),
            CommitStyle: CommitStyle.Append)), view);

        view.Name.Should().Be("hall9k");
        view.BaseBranch.Should().Be("main");
        view.VerifyCommands.Should().HaveCount(2);
        view.SkipPermissions.Should().BeTrue();
        view.MaxParallelAgents.Should().Be(2);
        view.ContextLinks.Should().ContainSingle(l => l.Name == "jira");
        view.CommitStyle.Should().Be(CommitStyle.Append);
        view.SettingsChangedAt.Should().Be(Now.AddMinutes(5));
    }

    /// <summary>
    /// The writing conventions read the platform default on a document nobody set one on, and an
    /// operator's own text survives a later change that leaves the field absent (task 412afe6c) —
    /// the same absent-means-left-alone contract every other setting on this event holds.
    /// </summary>
    [Fact]
    public void Writing_conventions_default_to_the_platform_text_and_survive_updates_that_leave_them_absent()
    {
        ProjectDetailsProjection projection = new();
        Guid id = DomainId.New();

        ProjectDetails view = projection.Create(new FakeEvent<ProjectRegistered>(new ProjectRegistered(
            id, DomainId.New(), DomainId.New(), "hall9k", "/repos/hall9k.git", null, "main", Now)));
        view.WritingConventions.Should().Be(
            WritingConventions.Default, "a project nobody configured still has a house style");

        projection.Apply(new FakeEvent<ProjectSettingsChanged>(new ProjectSettingsChanged(
            id,
            VerifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            SkipPermissions: Optional<bool>.None,
            MaxParallelAgents: Optional<int>.None,
            ContextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            ChangedAt: Now.AddMinutes(1), ChangedByOwnerId: DomainId.New(),
            WritingConventions: Optional<WritingConventions>.Of(
                WritingConventions.Parse("Terse. British spelling.")))), view);
        view.WritingConventions.Value.Should().Be("Terse. British spelling.");

        projection.Apply(new FakeEvent<ProjectSettingsChanged>(new ProjectSettingsChanged(
            id,
            VerifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            SkipPermissions: true,
            MaxParallelAgents: Optional<int>.None,
            ContextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            ChangedAt: Now.AddMinutes(2), ChangedByOwnerId: DomainId.New())), view);
        view.WritingConventions.Value.Should().Be(
            "Terse. British spelling.", "a change that says nothing about them leaves them alone");
    }

    [Fact]
    public void Commit_style_defaults_to_unknown_and_survives_updates_that_leave_it_absent()
    {
        ProjectDetailsProjection projection = new();
        Guid id = DomainId.New();

        ProjectDetails view = projection.Create(new FakeEvent<ProjectRegistered>(new ProjectRegistered(
            id, DomainId.New(), DomainId.New(), "hall9k", "/repos/hall9k.git", null, "main", Now)));
        view.CommitStyle.Should().Be(CommitStyle.Unknown, "an unset project uses the platform default");

        projection.Apply(new FakeEvent<ProjectSettingsChanged>(new ProjectSettingsChanged(
            id,
            VerifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            SkipPermissions: Optional<bool>.None,
            MaxParallelAgents: Optional<int>.None,
            ContextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            ChangedAt: Now.AddMinutes(1), ChangedByOwnerId: DomainId.New(),
            CommitStyle: CommitStyle.Narrative)), view);
        view.CommitStyle.Should().Be(CommitStyle.Narrative);

        projection.Apply(new FakeEvent<ProjectSettingsChanged>(new ProjectSettingsChanged(
            id,
            VerifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            SkipPermissions: true,
            MaxParallelAgents: Optional<int>.None,
            ContextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            ChangedAt: Now.AddMinutes(2), ChangedByOwnerId: DomainId.New())), view);
        view.CommitStyle.Should().Be(CommitStyle.Narrative, "an absent optional leaves the setting unchanged");
    }

    /// <summary>
    /// The per-project run ceiling the dispatcher reads (Decisions Log #140): absent leaves it
    /// alone, 0 is the pause the projection must carry as a real value rather than lose to a
    /// falsy reading, and present-with-null clears it back to uncapped.
    /// </summary>
    [Fact]
    public void The_parallel_task_cap_carries_zero_as_a_pause_and_a_cleared_value_as_uncapped()
    {
        ProjectDetailsProjection projection = new();
        Guid id = DomainId.New();

        ProjectDetails view = projection.Create(new FakeEvent<ProjectRegistered>(new ProjectRegistered(
            id, DomainId.New(), DomainId.New(), "hall9k", "/repos/hall9k.git", null, "main", Now)));
        view.MaxParallelTasks.Should().BeNull("an untouched project is uncapped");

        projection.Apply(new FakeEvent<ProjectSettingsChanged>(new ProjectSettingsChanged(
            id,
            VerifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            SkipPermissions: Optional<bool>.None,
            MaxParallelAgents: Optional<int>.None,
            ContextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            ChangedAt: Now.AddMinutes(1), ChangedByOwnerId: DomainId.New(),
            MaxParallelTasks: Optional<int?>.Of(0))), view);
        view.MaxParallelTasks.Should().Be(0);

        projection.Apply(new FakeEvent<ProjectSettingsChanged>(new ProjectSettingsChanged(
            id,
            VerifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            SkipPermissions: true,
            MaxParallelAgents: Optional<int>.None,
            ContextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            ChangedAt: Now.AddMinutes(2), ChangedByOwnerId: DomainId.New())), view);
        view.MaxParallelTasks.Should().Be(0, "an absent optional leaves the pause standing");

        projection.Apply(new FakeEvent<ProjectSettingsChanged>(new ProjectSettingsChanged(
            id,
            VerifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            SkipPermissions: Optional<bool>.None,
            MaxParallelAgents: Optional<int>.None,
            ContextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            ChangedAt: Now.AddMinutes(3), ChangedByOwnerId: DomainId.New(),
            MaxParallelTasks: Optional<int?>.Of(null))), view);
        view.MaxParallelTasks.Should().BeNull("present-with-null clears the cap so the node ceiling decides");
    }

    /// <summary>
    /// A stream that recorded the retired session-denominated ceiling keeps exactly what it
    /// recorded, and it does not become a run cap: that is the whole retirement (Decisions Log
    /// #140) — the old number was never enforced, so it is named rather than converted.
    /// </summary>
    [Fact]
    public void The_retired_session_denominated_ceiling_replays_without_becoming_a_run_cap()
    {
        ProjectDetailsProjection projection = new();
        Guid id = DomainId.New();

        ProjectDetails view = projection.Create(new FakeEvent<ProjectRegistered>(new ProjectRegistered(
            id, DomainId.New(), DomainId.New(), "hall9k", "/repos/hall9k.git", null, "main", Now)));

        projection.Apply(new FakeEvent<ProjectSettingsChanged>(new ProjectSettingsChanged(
            id,
            VerifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            SkipPermissions: Optional<bool>.None,
            MaxParallelAgents: 6,
            ContextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            ChangedAt: Now.AddMinutes(1), ChangedByOwnerId: DomainId.New())), view);

        view.MaxParallelAgents.Should().Be(6);
        view.MaxParallelTasks.Should().BeNull("the retired value is not carried into the enforced ceiling");
    }
}
