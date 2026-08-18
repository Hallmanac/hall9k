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
}
