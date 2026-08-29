using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Rendering;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// task.md (backlog 48): the render must stay parseable by <see cref="TaskFileParser"/>, the same
/// format <c>h9k task add/revise --file</c> reads, and its body must carry nothing but the agent
/// context — anything else there would round-trip into that field on the next revise.
/// </summary>
public sealed class TaskDocumentRendererTests
{
    [Fact]
    public void Render_round_trips_the_readiness_contract_through_the_file_parser()
    {
        TaskDetails task = SomeTask();
        task.AcceptanceCriteria.AddRange(["Builds", "Tests pass"]);
        task.BlockedBy.Add(DomainId.New());
        task.Model = AgentModel.Sonnet;

        string rendered = TaskDocumentRenderer.Render(task, "hall9k");
        TaskFileContent parsed = TaskFileParser.Parse(rendered);

        parsed.Project.Should().Be("hall9k");
        parsed.Objective.Should().Be(task.Objective);
        parsed.Criteria.Should().Equal(task.AcceptanceCriteria);
        parsed.Type.Should().Be(task.Type.Value);
        parsed.Model.Should().Be("sonnet");
        parsed.AgentContext.Should().Be(task.AgentContext);
        parsed.BlockedBy.Should().ContainSingle().Which.Should().Be(DomainId.Short(task.BlockedBy[0]));
    }

    [Fact]
    public void The_body_carries_only_the_agent_context_never_status_information()
    {
        TaskDetails task = SomeTask();
        task.AgentContext = "Read AGENTS.md first.";
        task.PullRequestUrl = "https://github.com/example/example/pull/1";
        task.FailureReason = "flaky test";

        string rendered = TaskDocumentRenderer.Render(task, "hall9k");
        TaskFileContent parsed = TaskFileParser.Parse(rendered);

        parsed.AgentContext.Should().Be("Read AGENTS.md first.");
        rendered.Should().NotContain(task.PullRequestUrl);
        rendered.Should().NotContain(task.FailureReason);
    }

    [Fact]
    public void An_empty_agent_context_leaves_the_body_empty_rather_than_inventing_placeholder_text()
    {
        TaskDetails task = SomeTask();
        task.AgentContext = null;

        string rendered = TaskDocumentRenderer.Render(task, "hall9k");
        TaskFileContent parsed = TaskFileParser.Parse(rendered);

        parsed.AgentContext.Should().BeNull();
    }

    [Fact]
    public void Every_render_carries_the_generated_marker()
    {
        string rendered = TaskDocumentRenderer.Render(SomeTask(), "hall9k");

        rendered.Should().Contain(TaskDocumentRenderer.GeneratedMarker);
        rendered.Should().Contain("h9k task revise");
    }

    [Fact]
    public void Draft_and_published_tasks_render_the_same_shape_only_the_state_line_differs()
    {
        TaskDetails draft = SomeTask();
        draft.State = TaskState.Draft;
        TaskDetails published = SomeTask();
        published.Id = draft.Id;
        published.Objective = draft.Objective;
        published.State = TaskState.Published;

        string draftRendered = TaskDocumentRenderer.Render(draft, "hall9k");
        string publishedRendered = TaskDocumentRenderer.Render(published, "hall9k");

        draftRendered.Replace("state: Draft", "state: X").Should()
            .Be(publishedRendered.Replace("state: Published", "state: X"));
    }

    [Fact]
    public void An_unknown_type_renders_as_the_stored_empty_value_never_a_guessed_default()
    {
        TaskDetails task = SomeTask();
        task.Type = TaskType.Unknown;

        string rendered = TaskDocumentRenderer.Render(task, "hall9k");
        TaskFileContent parsed = TaskFileParser.Parse(rendered);

        rendered.ReplaceLineEndings("\n").Split('\n').Should().Contain("type: ");
        parsed.Type.Should().BeEmpty();
    }

    [Fact]
    public void A_task_in_an_epic_renders_the_epic_and_round_trips_it_through_the_file_parser()
    {
        TaskDetails task = SomeTask();
        Guid epicId = DomainId.New();
        task.EpicId = epicId;

        string rendered = TaskDocumentRenderer.Render(task, "hall9k");
        TaskFileContent parsed = TaskFileParser.Parse(rendered);

        rendered.Should().Contain($"epic: {DomainId.Short(epicId)}");
        parsed.Epic.Should().Be(DomainId.Short(epicId));
    }

    [Fact]
    public void An_epics_jira_reference_renders_as_a_link_out_but_the_parser_ignores_it()
    {
        TaskDetails task = SomeTask();
        task.EpicId = DomainId.New();

        string rendered = TaskDocumentRenderer.Render(
            task, "hall9k", epicJiraReference: "https://example.atlassian.net/browse/PROJ-45");
        TaskFileContent parsed = TaskFileParser.Parse(rendered);

        rendered.Should().Contain("epic-jira: https://example.atlassian.net/browse/PROJ-45");
        parsed.Epic.Should().Be(DomainId.Short(task.EpicId!.Value));
    }

    [Fact]
    public void No_epic_jira_line_renders_when_the_task_carries_no_epic()
    {
        string rendered = TaskDocumentRenderer.Render(
            SomeTask(), "hall9k", epicJiraReference: "https://example.atlassian.net/browse/PROJ-45");

        rendered.Should().NotContain("epic-jira");
    }

    [Fact]
    public void Directory_name_is_short_id_plus_a_slug_of_the_objective()
    {
        TaskDetails task = SomeTask();
        task.Objective = "Tasks and ideas render as markdown files!";

        string name = TaskDocumentRenderer.DirectoryName(task);

        name.Should().Be($"{DomainId.Short(task.Id)}-tasks-and-ideas-render-as-markdown-files");
    }

    private static TaskDetails SomeTask() => new()
    {
        Id = DomainId.New(),
        ProjectId = DomainId.New(),
        Objective = "Tasks and ideas render as markdown files",
        Type = TaskType.Feature,
        State = TaskState.Draft,
        AgentContext = "Some context.",
        AddedAt = DateTimeOffset.UtcNow,
        AddedByOwnerId = DomainId.New(),
    };
}
