using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.Exceptions;
using Spectre.Console.Cli;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The CLI-facing half of the assign/queue split that needs no store: what assign says once it has
/// recorded an assignee, that publish's retired <c>--assign</c> refuses and points at <c>--queue</c>, and
/// that a dispatched session may run neither of the new verbs.
/// </summary>
public sealed class TaskQueueVerbsTests
{
    [Fact]
    public void Assigning_a_published_task_says_it_is_not_queued_and_names_the_queue_command()
    {
        IReadOnlyList<string> mine = TaskAssignCommand.HoldOutcomeLines(
            TaskState.Published, "28b19893", "Brian", heldByActor: true, unchanged: false);
        IReadOnlyList<string> handedOff = TaskAssignCommand.HoldOutcomeLines(
            TaskState.Published, "28b19893", "Ryan", heldByActor: false, unchanged: false);

        string.Join('\n', mine).Should().Contain("not queued").And.Contain("h9k task queue 28b19893");
        string.Join('\n', handedOff).Should().Contain("not queued").And.Contain("h9k task queue 28b19893");
    }

    [Fact]
    public void Assigning_a_draft_says_it_stays_a_draft_and_points_at_publish_rather_than_queue()
    {
        string lines = string.Join('\n', TaskAssignCommand.HoldOutcomeLines(
            TaskState.Draft, "28b19893", "Brian", heldByActor: true, unchanged: false));

        lines.Should().Contain("still Draft").And.Contain("h9k task publish 28b19893").And.NotContain("h9k task queue");
    }

    [Fact]
    public void Publish_assign_refuses_with_a_pointer_to_queue_and_is_never_a_silent_alias()
    {
        TaskPublishCommand.Settings settings = new() { Id = "28b19893", Assign = new() { IsSet = true } };

        Action publish = () => TaskPublishCommand.RefuseRetiredAssignFlag(settings);

        publish.Should().Throw<DomainValidationException>()
            .Which.Message.Should().Contain("--queue").And.Contain("h9k task assign <id> <member>");
        TaskPublishCommand.RefuseRetiredAssignFlag(new TaskPublishCommand.Settings { Id = "28b19893", Queue = true });
    }

    [Theory]
    [InlineData(typeof(TaskQueueCommand.Settings), "task queue")]
    [InlineData(typeof(TaskDequeueCommand.Settings), "task dequeue")]
    [InlineData(typeof(TaskAssignCommand.Settings), "task assign")]
    [InlineData(typeof(TaskUnassignCommand.Settings), "task unassign")]
    public void A_dispatched_session_is_refused_every_verb_that_moves_the_go_or_the_hold(Type settings, string verb)
    {
        DispatchedSessionCommandClassification.BySettingsType[settings]
            .Should().Be((DispatchedSessionAccess.Refused, verb));
    }
}
