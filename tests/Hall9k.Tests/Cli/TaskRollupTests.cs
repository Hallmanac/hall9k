using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The rollups every project row and every status header is counted from. They read the
/// same composed state h9k status does; the counting is what is theirs.
/// </summary>
public sealed class TaskRollupTests
{
    private static readonly DateTimeOffset Now = StatusFixtures.Now;

    [Fact]
    public void Every_task_lands_in_exactly_one_rollup_bucket_so_a_project_row_adds_up()
    {
        Guid runId = DomainId.New();
        Guid stalledRunId = DomainId.New();
        Guid deliveredRunId = DomainId.New();
        TaskStatusRow[] rows =
        [
            StatusFixtures.Compose(StatusFixtures.Task(TaskState.NeedsHuman)),
            StatusFixtures.Compose(StatusFixtures.Task(TaskState.Failed)),
            StatusFixtures.Compose(
                StatusFixtures.Task(TaskState.Claimed, runId),
                StatusFixtures.Run(runId, RunState.Running),
                silentSince: Now.AddMinutes(-2)),
            StatusFixtures.Compose(
                StatusFixtures.Task(TaskState.Claimed, stalledRunId),
                StatusFixtures.Run(stalledRunId, RunState.Running),
                silentSince: Now.AddHours(-2)),
            StatusFixtures.Compose(
                StatusFixtures.Task(TaskState.Done, deliveredRunId, "https://github.com/x/y/pull/7"),
                StatusFixtures.Run(
                    deliveredRunId, RunState.AwaitingReview, sessionProcessId: null, pullRequestNumber: 7)),
            StatusFixtures.Compose(StatusFixtures.Task(TaskState.Queued)),
            StatusFixtures.Compose(StatusFixtures.Task(TaskState.Done)),
            StatusFixtures.Compose(StatusFixtures.Task(TaskState.Abandoned)),
        ];

        TaskRollup rollup = TaskRollup.From(rows);

        rollup.NeedsYou.Should().Be(2, "NeedsHuman and Failed both wait on a human (log #27)");
        rollup.Stalled.Should().Be(1);
        rollup.Working.Should().Be(1, "the stalled run is counted as stalled, not twice");
        rollup.Delivered.Should().Be(1,
            "an open pull request whose only ask is the merge stays in its lifecycle group");
        rollup.Queued.Should().Be(1);
        rollup.Done.Should().Be(1);
        rollup.Closed.Should().Be(1);
        rollup.Total.Should().Be(rows.Length, "the buckets are single-assignment");
    }

    [Fact]
    public void A_delivered_row_that_has_stopped_is_counted_where_a_reader_looks_for_it()
    {
        // Only the awaiting-the-merge case stays in the Delivered group. A parked closeout, a
        // review park and an agent's question have all stopped, and counting them as Delivered
        // left the header saying nothing needed you, h9k project list withholding its hint, and
        // --state needs-you returning an empty set for work that was definitively blocked on a
        // human.
        Guid parkedRunId = DomainId.New();
        Guid askedRunId = DomainId.New();
        string pullRequest = "https://github.com/x/y/pull/7";

        TaskStatusRow parked = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Done, parkedRunId, pullRequest),
            StatusFixtures.Run(parkedRunId, RunState.CloseoutParked, sessionProcessId: null, pullRequestNumber: 7));
        TaskStatusRow asked = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.NeedsHuman, askedRunId, pullRequest),
            StatusFixtures.Run(askedRunId, RunState.Running, sessionProcessId: null, pullRequestNumber: 7));

        parked.State.Should().Be(LifecycleState.Delivered);
        asked.State.Should().Be(LifecycleState.Delivered);
        parked.Group.Should().Be(AttentionBucket.NeedsYou);
        asked.Group.Should().Be(AttentionBucket.NeedsYou);

        TaskRollup rollup = TaskRollup.From([parked, asked]);
        rollup.NeedsYou.Should().Be(2);
        rollup.Delivered.Should().Be(0);
        TaskStateFilter.Matches(parked, "needs-you").Should().BeTrue();
    }

    [Fact]
    public void The_rollup_summary_names_only_the_buckets_that_have_something_in_them()
    {
        string summary = TaskRollup.From(
        [
            StatusFixtures.Compose(StatusFixtures.Task(TaskState.NeedsHuman)),
            StatusFixtures.Compose(StatusFixtures.Task(TaskState.Queued)),
        ]).Summary();

        summary.Should().Contain("1 need you").And.Contain("1 queued");
        summary.Should().NotContain("done", "a zero bucket is noise on a glanceable line");
    }

    [Fact]
    public void The_rollup_columns_use_the_same_words_the_status_column_does()
    {
        TaskRollup.Columns.Should().Contain("Working").And.Contain("Delivered");
        TaskRollup.Columns.Should().NotContain("Active").And.NotContain("In review");
        TaskRollup.Columns.Should().HaveCount(TaskRollup.Empty.Cells.Length);
    }
}
