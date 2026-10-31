using System.ComponentModel;
using System.Reflection;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.TestSupport;
using Spectre.Console;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// What the linear backlog policy says on the surfaces an operator reads: the project row, the task
/// row that catches an issue that was never linked, the note about routing text that does nothing,
/// and the help for the options the policy touches.
/// </summary>
public sealed class LinearBacklogSurfaceTests
{
    private static ProjectDetails Project(string? routing = null, BacklogPolicy? policy = null) =>
        new()
        {
            Id = DomainId.New(),
            Name = "hall9k",
            BacklogPolicy = policy ?? BacklogPolicy.Linear,
            BacklogRoutingGuidance = routing,
        };

    private static string Plain(string markup)
    {
        using ScopedAnsiConsoleCapture capture = ScopedAnsiConsoleCapture.Begin();
        AnsiConsole.MarkupLine(markup);
        return capture.Text;
    }

    [Fact]
    public void Project_show_renders_the_linear_policy_as_its_own_row_rather_than_as_none()
    {
        string row = Plain(ProjectShowCommand.BacklogPolicyRow(Project(), recorded: true));

        row.Should().StartWith("linear").And.Contain("creates the issue in the project's Linear team")
            .And.NotContain("none");
    }

    [Fact]
    public void Project_show_marks_routing_text_as_ignored_under_the_linear_policy()
    {
        Plain(ProjectShowCommand.BacklogPolicyRow(Project(routing: "bugs, urgent"), recorded: true))
            .Should().Contain("routing ignored: bugs, urgent");
    }

    [Fact]
    public void Project_show_names_linear_among_the_policies_a_project_with_none_can_set()
    {
        Plain(ProjectShowCommand.BacklogPolicyRow(Project(policy: BacklogPolicy.None), recorded: true))
            .Should().Contain("--backlog github-issues|jira|linear");
    }

    [Fact]
    public void Project_set_says_routing_is_ignored_when_it_touched_a_setting_and_left_the_policy_linear_with_text()
    {
        string? note = ProjectSetCommand.LinearRoutingNote(true, Project("labels: bug"));

        note.Should().NotBeNull();
        Plain(note!).Should().Contain("--backlog-routing is ignored under the backlog policy linear")
            .And.Contain("h9k project set hall9k --backlog-routing \"\"");
    }

    [Fact]
    public void Project_set_stays_quiet_when_it_touched_neither_setting()
    {
        ProjectSetCommand.LinearRoutingNote(false, Project("labels: bug")).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Project_set_stays_quiet_when_there_is_no_routing_text(string? routing)
    {
        ProjectSetCommand.LinearRoutingNote(true, Project(routing)).Should().BeNull();
    }

    [Fact]
    public void Project_set_stays_quiet_under_a_policy_that_reads_the_routing_text()
    {
        ProjectSetCommand.LinearRoutingNote(true, Project("labels: bug", BacklogPolicy.GitHubIssues)).Should().BeNull();
    }

    private static TaskDetails Task(
        TaskState state, string? externalReference = null, bool untracked = false) =>
        new()
        {
            Id = DomainId.New(),
            State = state,
            ExternalReference = externalReference ?? string.Empty,
            UntrackedAttested = untracked,
        };

    [Fact]
    public void Task_show_says_no_Linear_issue_is_linked_and_names_link_linear_for_a_published_task_with_none()
    {
        TaskDetails details = Task(TaskState.Published);

        string? row = TaskShowCommand.UnlinkedLinearRow(details, Project());

        row.Should().NotBeNull();
        Plain(row!).Should().Contain("no Linear issue is linked")
            .And.Contain($"h9k task link-linear {TaskListCommand.ShortId(details.Id)} <identifier>");
    }

    [Fact]
    public void Task_show_says_nothing_for_a_draft() =>
        TaskShowCommand.UnlinkedLinearRow(Task(TaskState.Draft), Project()).Should().BeNull();

    [Fact]
    public void Task_show_says_nothing_for_an_abandoned_task_because_it_cannot_be_linked() =>
        TaskShowCommand.UnlinkedLinearRow(Task(TaskState.Abandoned), Project()).Should().BeNull();

    [Fact]
    public void Task_show_still_says_no_Linear_issue_is_linked_for_a_done_task_because_a_link_is_still_accepted() =>
        TaskShowCommand.UnlinkedLinearRow(Task(TaskState.Done), Project()).Should().NotBeNull();

    [Fact]
    public void Task_show_says_nothing_for_a_task_that_carries_an_issue() =>
        TaskShowCommand.UnlinkedLinearRow(Task(TaskState.Published, "linear:ENG-1"), Project()).Should().BeNull();

    [Fact]
    public void Task_show_says_nothing_for_a_task_published_untracked() =>
        TaskShowCommand.UnlinkedLinearRow(Task(TaskState.Queued, untracked: true), Project()).Should().BeNull();

    [Fact]
    public void Task_show_says_nothing_under_a_policy_other_than_linear()
    {
        TaskShowCommand.UnlinkedLinearRow(Task(TaskState.Published), Project(policy: BacklogPolicy.GitHubIssues))
            .Should().BeNull();
        TaskShowCommand.UnlinkedLinearRow(Task(TaskState.Published), project: null).Should().BeNull();
    }

    private static string Describe(Type settings, string property) =>
        settings.GetProperty(property)!.GetCustomAttribute<DescriptionAttribute>()!.Description;

    [Fact]
    public void The_backlog_option_describes_the_linear_policy_and_what_it_needs()
    {
        Describe(typeof(ProjectSetCommand.Settings), nameof(ProjectSetCommand.Settings.Backlog))
            .Should().Contain("'linear'").And.Contain("--linear").And.Contain("--backlog-routing is ignored")
            .And.Contain("no agent session");
    }

    [Fact]
    public void The_routing_option_says_it_is_ignored_under_linear()
    {
        Describe(typeof(ProjectSetCommand.Settings), nameof(ProjectSetCommand.Settings.BacklogRouting))
            .Should().Contain("ignored under 'linear'");
    }

    [Fact]
    public void The_publish_options_describe_what_linear_changes()
    {
        Describe(typeof(TaskPublishCommand.Settings), nameof(TaskPublishCommand.Settings.NoExistingItem))
            .Should().Contain("linear").And.Contain("h9k task link-linear").And.Contain("still a Draft")
            .And.Contain("--untracked");
        Describe(typeof(TaskPublishCommand.Settings), nameof(TaskPublishCommand.Settings.Queue))
            .Should().Contain("Linear issue").And.Contain("after the issue is linked")
            .And.Contain("h9k task link-linear");
    }
}
