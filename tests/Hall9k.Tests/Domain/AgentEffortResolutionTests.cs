using System.Text.Json;
using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The effort chain as a pure function over vendor-neutral values, and the two places a level is stored
/// beside a model: the task (which travels) and the project (which stays on its node). Nothing here
/// mentions the Claude Code settings key; that mapping belongs to the executor and the settings writer.
/// </summary>
public sealed class AgentEffortResolutionTests
{
    private static readonly Guid Owner = DomainId.New();

    [Fact]
    public void A_task_value_wins_over_a_project_value()
    {
        AgentEffort.Resolve(AgentEffort.Low, AgentEffort.High, AgentEffort.Medium, AgentEffort.ExtraHigh)
            .Should().Be(AgentEffort.Low);
    }

    [Fact]
    public void A_project_value_wins_over_the_nodes_role_value()
    {
        AgentEffort.Resolve(AgentEffort.Unknown, AgentEffort.High, AgentEffort.Medium, AgentEffort.ExtraHigh)
            .Should().Be(AgentEffort.High);
    }

    [Fact]
    public void A_role_value_wins_over_the_node_wide_value()
    {
        AgentEffort.Resolve(AgentEffort.Unknown, AgentEffort.Unknown, AgentEffort.Medium, AgentEffort.ExtraHigh)
            .Should().Be(AgentEffort.Medium);
    }

    [Fact]
    public void The_node_wide_value_decides_when_nothing_above_it_is_set()
    {
        AgentEffort.Resolve(null, null, null, AgentEffort.ExtraHigh).Should().Be(AgentEffort.ExtraHigh);
    }

    [Fact]
    public void A_session_with_nothing_set_at_any_level_resolves_to_unknown_so_the_model_default_decides()
    {
        AgentEffort resolved = AgentEffort.Resolve(null, AgentEffort.Unknown, null, AgentEffort.Unknown);

        resolved.Should().Be(AgentEffort.Unknown);
        resolved.IsWellFormed.Should().BeFalse("the executor leaves its settings key out for a value like this");
    }

    [Fact]
    public void First_set_skips_unset_candidates_so_a_pass_can_fall_through_to_its_role()
    {
        AgentEffort.FirstSet(AgentEffort.Unknown, AgentEffort.High).Should().Be(AgentEffort.High);
        AgentEffort.FirstSet(AgentEffort.Low, AgentEffort.High).Should().Be(AgentEffort.Low);
        AgentEffort.FirstSet().Should().Be(AgentEffort.Unknown);
    }

    [Theory]
    [InlineData("high", "high")]
    [InlineData(" XHIGH ", "xhigh")]
    [InlineData("max", "")]
    [InlineData("default", "")]
    public void An_effort_survives_a_json_round_trip_through_its_canonical_form(string stored, string expected)
    {
        AgentEffort read = JsonSerializer.Deserialize<AgentEffort>(JsonSerializer.Serialize(stored))!;

        read.Value.Should().Be(expected, "a stored document is an input like any other and never carries a word the settings file must not receive");
        JsonSerializer.Serialize(read).Should().Be($"\"{expected}\"");
    }

    [Fact]
    public void A_task_revision_records_an_effort_and_the_aggregate_and_projection_apply_it()
    {
        (TaskAggregate task, TaskDetails details) = DraftTask();

        TaskRevised revised = Revise(task, Optional<AgentEffort>.Of(AgentEffort.High));
        task.Apply(revised);
        new TaskDetailsProjection().Apply(new FakeEvent<TaskRevised>(revised), details);

        task.Effort.Should().Be(AgentEffort.High);
        details.Effort.Should().Be(AgentEffort.High);
    }

    [Fact]
    public void The_word_default_clears_a_task_effort_and_an_untouched_revision_leaves_it_alone()
    {
        (TaskAggregate task, _) = DraftTask();
        task.Apply(Revise(task, Optional<AgentEffort>.Of(AgentEffort.Low)));

        task.Apply(Revise(task, Optional<AgentEffort>.None, objective: Optional<string>.Of("A new objective")));
        task.Effort.Should().Be(AgentEffort.Low, "a revision that never named effort leaves it as it was");

        task.Apply(Revise(task, Optional<AgentEffort>.Of(AgentEffort.Unknown)));
        task.Effort.Should().Be(AgentEffort.Unknown);
    }

    [Fact]
    public void An_effort_alone_is_a_real_revision_and_is_draft_only_like_the_model()
    {
        (TaskAggregate task, _) = DraftTask();
        task.Apply(TaskDecider.Publish(task, TaskDependencyGraph.Empty, DateTimeOffset.UtcNow, Owner));

        Action revise = () => Revise(task, Optional<AgentEffort>.Of(AgentEffort.High));

        revise.Should().Throw<DomainConflictException>().WithMessage("*only a draft can be revised*");
    }

    [Fact]
    public void A_revision_naming_nothing_at_all_still_says_effort_is_one_of_the_things_to_pass()
    {
        (TaskAggregate task, _) = DraftTask();

        Action revise = () => Revise(task, Optional<AgentEffort>.None);

        revise.Should().Throw<DomainValidationException>().WithMessage("*--effort*");
    }

    [Fact]
    public void A_task_revised_payload_written_before_effort_existed_reads_with_no_effort()
    {
        (TaskAggregate task, _) = DraftTask();
        TaskRevised withEffort = Revise(task, Optional<AgentEffort>.Of(AgentEffort.High), objective: Optional<string>.Of("x"));

        string json = JsonSerializer.Serialize(withEffort);
        TaskRevised roundTripped = JsonSerializer.Deserialize<TaskRevised>(json)!;
        roundTripped.Effort.HasValue.Should().BeTrue();
        roundTripped.Effort.Value.Should().Be(AgentEffort.High);

        using JsonDocument document = JsonDocument.Parse(json);
        Dictionary<string, JsonElement> legacy = document.RootElement.EnumerateObject()
            .Where(property => !property.NameEquals("Effort"))
            .ToDictionary(property => property.Name, property => property.Value.Clone());
        TaskRevised old = JsonSerializer.Deserialize<TaskRevised>(JsonSerializer.Serialize(legacy))!;

        old.Effort.HasValue.Should().BeFalse("a stream written before this field existed replays as it always did");
    }

    [Fact]
    public void A_project_settings_change_records_an_effort_beside_the_model_and_apply_it_to_the_aggregate_and_view()
    {
        ProjectAggregate project = RegisteredProject();

        ProjectSettingsChanged changed = ProjectDecider.ChangeSettings(
            project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, DateTimeOffset.UtcNow, Owner,
            model: Optional<AgentModel>.Of(AgentModel.Sonnet), effort: Optional<AgentEffort>.Of(AgentEffort.ExtraHigh));
        project.Apply(changed);
        ProjectDetails view = new();
        new ProjectDetailsProjection().Apply(new FakeEvent<ProjectSettingsChanged>(changed), view);

        project.Effort.Should().Be(AgentEffort.ExtraHigh);
        project.Model.Should().Be(AgentModel.Sonnet, "the effort travels beside the model without disturbing it");
        view.Effort.Should().Be(AgentEffort.ExtraHigh);
    }

    [Fact]
    public void A_project_effort_is_cleared_by_unknown_and_left_alone_by_a_change_that_never_names_it()
    {
        ProjectAggregate project = RegisteredProject();
        project.Apply(ProjectDecider.ChangeSettings(
            project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, DateTimeOffset.UtcNow, Owner,
            effort: Optional<AgentEffort>.Of(AgentEffort.Low)));

        project.Apply(ProjectDecider.ChangeSettings(
            project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, DateTimeOffset.UtcNow, Owner,
            model: Optional<AgentModel>.Of(AgentModel.Opus)));
        project.Effort.Should().Be(AgentEffort.Low);

        project.Apply(ProjectDecider.ChangeSettings(
            project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, DateTimeOffset.UtcNow, Owner,
            effort: Optional<AgentEffort>.Of(AgentEffort.Unknown)));
        project.Effort.Should().Be(AgentEffort.Unknown);
    }

    [Fact]
    public void A_project_effort_never_leaves_the_node_it_was_set_on()
    {
        ProjectAggregate project = RegisteredProject();
        ProjectSettingsChanged changed = ProjectDecider.ChangeSettings(
            project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None, DateTimeOffset.UtcNow, Owner,
            effort: Optional<AgentEffort>.Of(AgentEffort.High));

        EventScopeRegistry.ClassificationOf(typeof(ProjectSettingsChanged)).Should().Be(EventScope.NodeScoped);
        ProjectTeamSettingsChanged.From(changed).Should().BeNull(
            "an effort-only change has no team field, so no replicated companion event is appended");
    }

    private static (TaskAggregate Task, TaskDetails Details) DraftTask()
    {
        TaskAdded added = TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Objective", ["It works"], TaskType.Feature, null, null, null,
            DateTimeOffset.UtcNow, Owner);
        TaskAggregate task = new();
        task.Apply(added);
        return (task, new TaskDetails());
    }

    private static TaskRevised Revise(
        TaskAggregate task, Optional<AgentEffort> effort, Optional<string> objective = default) =>
        TaskDecider.Revise(
            task, objective, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None,
            DateTimeOffset.UtcNow, Owner, effort: effort);

    private static ProjectAggregate RegisteredProject()
    {
        ProjectAggregate project = new();
        project.Apply(ProjectDecider.Register(
            DomainId.New(), Owner, DomainId.New(), "hall9k", "/repos/hall9k.git", null, null, DateTimeOffset.UtcNow));
        return project;
    }
}
