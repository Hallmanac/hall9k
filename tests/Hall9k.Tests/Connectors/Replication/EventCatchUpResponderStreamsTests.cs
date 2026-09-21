using FluentAssertions;
using Hall9k.Connectors.Replication;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Connectors.Replication;

/// <summary>
/// Task 9eb5b245, observed 2026-09-21: the Mac pulled task 3727884f and it landed Delivered with
/// laps 0 and sessions 0, because the answer carried the task stream and not the run streams
/// hanging off it — while the answering node had that same task Done. A run's own stream id is not
/// derivable from its task's, so an ask for a task has to widen to the runs the answering node
/// holds for it.
/// </summary>
public sealed class EventCatchUpResponderStreamsTests
{
    [Fact]
    public void An_ask_for_a_task_serves_the_task_stream_first_and_then_its_runs()
    {
        Guid taskId = DomainId.New();
        Guid firstRun = DomainId.New();
        Guid secondRun = DomainId.New();

        EventCatchUpResponder.StreamIdsToServe(taskId, [firstRun, secondRun])
            .Should().Equal([taskId, firstRun, secondRun]);
    }

    [Fact]
    public void An_ask_for_a_run_stream_serves_that_run_alone()
    {
        Guid runId = DomainId.New();

        EventCatchUpResponder.StreamIdsToServe(runId, [])
            .Should().Equal([runId], "no run's TaskId is a run id, so the join finds nothing and the ask is itself");
    }

    [Fact]
    public void A_stream_never_travels_twice_in_one_answer()
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();

        EventCatchUpResponder.StreamIdsToServe(taskId, [runId, runId, taskId])
            .Should().Equal([taskId, runId]);
    }
}
