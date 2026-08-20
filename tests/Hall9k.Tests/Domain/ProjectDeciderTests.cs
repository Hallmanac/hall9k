using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

public sealed class ProjectDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Register_produces_event_with_main_as_default_base_branch()
    {
        ProjectRegistered @event = ProjectDecider.Register(
            DomainId.New(), DomainId.New(), DomainId.New(),
            name: "hall9k", repositoryPath: "/repos/hall9k.git", repositoryUrl: null,
            baseBranch: null, registeredAt: Now);

        @event.BaseBranch.Should().Be("main");
        @event.Name.Should().Be("hall9k");
    }

    [Fact]
    public void Register_without_name_fails_validation()
    {
        Action act = () => ProjectDecider.Register(
            DomainId.New(), DomainId.New(), DomainId.New(),
            name: " ", repositoryPath: "/repos/x", repositoryUrl: null,
            baseBranch: "main", registeredAt: Now);

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Register_without_connection_fails_validation()
    {
        Action act = () => ProjectDecider.Register(
            DomainId.New(), DomainId.New(), Guid.Empty,
            name: "x", repositoryPath: "/repos/x", repositoryUrl: null,
            baseBranch: "main", registeredAt: Now);

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void ChangeSettings_with_zero_parallel_agents_fails_validation()
    {
        ProjectAggregate project = RegisteredProject();

        Action act = () => ProjectDecider.ChangeSettings(
            project,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: Optional<bool>.None,
            maxParallelAgents: 0,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            changedAt: Now, changedByOwnerId: DomainId.New());

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void ChangeSettings_rejects_a_commit_style_outside_the_vocabulary()
    {
        ProjectAggregate project = RegisteredProject();

        Action act = () => ProjectDecider.ChangeSettings(
            project,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: Optional<bool>.None,
            maxParallelAgents: Optional<int>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            changedAt: Now, changedByOwnerId: DomainId.New(),
            commitStyle: Optional<CommitStyle>.Of("Squash"));

        act.Should().Throw<DomainValidationException>().WithMessage("*Narrative*Append*");
    }

    [Fact]
    public void ChangeSettings_accepts_unknown_commit_style_as_clearing_the_override()
    {
        ProjectAggregate project = RegisteredProject();
        project.Apply(new ProjectSettingsChanged(
            project.Id,
            VerifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            SkipPermissions: Optional<bool>.None,
            MaxParallelAgents: Optional<int>.None,
            ContextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            ChangedAt: Now, ChangedByOwnerId: DomainId.New(),
            CommitStyle: CommitStyle.Append));
        project.CommitStyle.Should().Be(CommitStyle.Append);

        ProjectSettingsChanged cleared = ProjectDecider.ChangeSettings(
            project,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: Optional<bool>.None,
            maxParallelAgents: Optional<int>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            changedAt: Now, changedByOwnerId: DomainId.New(),
            commitStyle: Optional<CommitStyle>.Of(CommitStyle.Unknown));
        project.Apply(cleared);

        project.CommitStyle.Should().Be(CommitStyle.Unknown, "the platform default applies again");
    }

    [Fact]
    public void Aggregate_applies_settings_only_where_optionals_are_present()
    {
        ProjectAggregate project = RegisteredProject();
        project.Apply(new ProjectSettingsChanged(
            project.Id,
            VerifyCommands: new List<VerifyCommand> { new("test", "dotnet test") },
            SkipPermissions: true,
            MaxParallelAgents: Optional<int>.None,
            ContextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            ChangedAt: Now, ChangedByOwnerId: DomainId.New()));

        project.VerifyCommands.Should().ContainSingle(c => c.Name == "test");
        project.SkipPermissions.Should().BeTrue();
        project.MaxParallelAgents.Should().Be(3, "absent optionals leave settings unchanged");
    }

    private static ProjectAggregate RegisteredProject()
    {
        ProjectAggregate project = new();
        project.Apply(ProjectDecider.Register(
            DomainId.New(), DomainId.New(), DomainId.New(),
            name: "hall9k", repositoryPath: "/repos/hall9k.git", repositoryUrl: null,
            baseBranch: null, registeredAt: Now));
        return project;
    }

    /// <summary>
    /// A project's model default is the third link in the chain (Decisions Log #33), and
    /// Unknown is a legal explicit value: it clears the override so the node's per-role and
    /// platform defaults decide again, exactly how CommitStyle behaves.
    /// </summary>
    [Fact]
    public void Change_settings_carries_a_model_default_and_lets_unknown_clear_it()
    {
        ProjectAggregate project = Registered();

        ProjectSettingsChanged set = ProjectDecider.ChangeSettings(
            project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<int>.None, Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New(),
            model: Optional<AgentModel>.Of(AgentModel.FromInput("claude-sonnet-5")));
        project.Apply(set);
        project.Model.Value.Should().Be("claude-sonnet-5");

        ProjectSettingsChanged cleared = ProjectDecider.ChangeSettings(
            project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<int>.None, Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New(),
            model: Optional<AgentModel>.Of(AgentModel.FromInput("default")));
        project.Apply(cleared);
        project.Model.Should().Be(AgentModel.Unknown, "'default' hands the decision back to the chain");

        ProjectSettingsChanged untouched = ProjectDecider.ChangeSettings(
            project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<int>.None, Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New());
        untouched.Model.HasValue.Should().BeFalse("an option not passed leaves the setting alone");
    }

    [Fact]
    public void Change_settings_rejects_a_model_that_could_not_be_handed_to_the_executors_shell()
    {
        Action act = () => ProjectDecider.ChangeSettings(
            Registered(), Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<int>.None, Optional<IReadOnlyList<ContextLink>>.None, Now, DomainId.New(),
            model: Optional<AgentModel>.Of(AgentModel.FromInput("$(id)")));

        act.Should().Throw<DomainValidationException>().WithMessage("*not a usable model name*");
    }

    private static ProjectAggregate Registered()
    {
        ProjectAggregate project = new();
        project.Apply(ProjectDecider.Register(
            DomainId.New(), DomainId.New(), DomainId.New(),
            name: "hall9k", repositoryPath: "/repos/hall9k.git", repositoryUrl: null,
            baseBranch: "main", registeredAt: Now));
        return project;
    }
}
