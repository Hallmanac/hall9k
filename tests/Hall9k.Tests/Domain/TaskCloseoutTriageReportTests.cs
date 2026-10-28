using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The unread triage report is held for the lap the owner's go dispatches, so it is held only while the
/// interactive flag is on: a triage that finishes after the task has been handed back must not park a
/// report that a later, unrelated lap would then be seeded with.
/// </summary>
public sealed class TaskCloseoutTriageReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private const string SetKey = "PRRT_a,PRRT_b";
    private const string ReportPath = "/runs/closeout-triage-1.md";

    [Fact]
    public void An_assessed_triage_is_held_as_unread_while_the_flag_is_on()
    {
        (TaskAggregate task, _) = DrivenTaskWithTriageRunning(out Guid sessionId);

        task.Apply(Assessed(task.Id, sessionId));

        task.InteractiveModeEnabled.Should().BeTrue();
        task.UnreadTriageReportPath.Should().Be(ReportPath);
    }

    [Fact]
    public void An_assessed_triage_that_lands_after_the_flag_cleared_is_recorded_but_never_held_as_unread()
    {
        (TaskAggregate task, Guid ownerId) = DrivenTaskWithTriageRunning(out Guid sessionId);
        task.Apply(PullRequestResolveCommand.ClearInteractiveMode(task, Now, ownerId));

        task.Apply(Assessed(task.Id, sessionId));

        task.CloseoutTriages[SetKey].Status.Should().Be(FeedbackEvaluationStatus.Assessed);
        task.CloseoutTriages[SetKey].ReportPath.Should().Be(ReportPath);
        task.UnreadTriageReportPath.Should().BeNull("the go already dispatched its lap, and a later lap is not about these threads");
    }

    private static (TaskAggregate Task, Guid OwnerId) DrivenTaskWithTriageRunning(out Guid sessionId)
    {
        Guid ownerId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Bound the limiter", ["the limiter resets per window"], TaskType.Feature,
            null, null, null, Now, ownerId);
        (TaskAggregate task, _) = TaskSeed.Start(added, ownerId, Now);
        TaskClaimed claimed = TaskDecider.Claim(task, DomainId.New(), ownerId, DomainId.New(), Now) with
        {
            InteractiveMode = true,
        };
        task.Apply(claimed);
        task.Apply(TaskDecider.Complete(task, claimed.RunId, "https://github.com/x/y/pull/7", Now));
        sessionId = DomainId.New();
        task.Apply(new CloseoutTriageDispatched(task.Id, SetKey, sessionId, ReportPath, 2, Now));
        return (task, ownerId);
    }

    private static CloseoutTriageRecorded Assessed(Guid taskId, Guid sessionId) =>
        new(taskId, SetKey, FeedbackEvaluationStatus.Assessed, sessionId, 1, 1, 0, null, Now, ReportPath);
}
