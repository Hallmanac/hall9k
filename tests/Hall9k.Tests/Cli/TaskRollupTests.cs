using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Documents;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The rollups every project row and every status header is counted from. They read the
/// same composed state h9k status does; the counting is what is theirs.
/// </summary>
public sealed class TaskRollupTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Every_task_lands_in_exactly_one_rollup_bucket_so_a_project_row_adds_up()
    {
        Guid runId = DomainId.New();
        TaskStatusRow[] rows =
        [
            Row(Task(TaskState.NeedsHuman)),
            Row(Task(TaskState.Failed)),
            Row(Task(TaskState.Claimed, runId), new RunListItem { Id = runId, State = RunState.Running }),
            Row(Task(TaskState.Claimed, runId), new RunListItem { Id = runId, State = RunState.Running },
                silentSince: Now.AddHours(-2)),
            Row(Task(TaskState.Done, pullRequest: "https://github.com/x/y/pull/7")),
            Row(Task(TaskState.Queued)),
            Row(Task(TaskState.Done)),
            Row(Task(TaskState.Abandoned)),
        ];

        TaskRollup rollup = TaskRollup.From(rows);

        rollup.NeedsYou.Should().Be(2, "NeedsHuman and Failed both wait on a human (log #27)");
        rollup.Stalled.Should().Be(1);
        rollup.Active.Should().Be(1, "the stalled run is counted as stalled, not twice");
        rollup.InReview.Should().Be(1, "an open pull request is in review, whatever its checks say");
        rollup.Queued.Should().Be(1);
        rollup.Done.Should().Be(1);
        rollup.Closed.Should().Be(1);
        rollup.Total.Should().Be(rows.Length, "the buckets are single-assignment");
    }

    [Fact]
    public void The_rollup_summary_names_only_the_buckets_that_have_something_in_them()
    {
        string summary = TaskRollup.From([Row(Task(TaskState.NeedsHuman)), Row(Task(TaskState.Queued))]).Summary();

        summary.Should().Contain("1 need you").And.Contain("1 queued");
        summary.Should().NotContain("done", "a zero bucket is noise on a glanceable line");
    }

    private static TaskListItem Task(TaskState state, Guid? runId = null, string? pullRequest = null) => new()
    {
        Id = DomainId.New(),
        ProjectId = DomainId.New(),
        Objective = "x",
        State = state,
        CurrentRunId = runId,
        PullRequestUrl = pullRequest,
        AddedAt = Now,
    };

    private static TaskStatusRow Row(TaskListItem task, RunListItem? run = null, DateTimeOffset? silentSince = null) =>
        TaskStatusComposer.Compose(
            task,
            run is null ? new Dictionary<Guid, RunListItem>() : new Dictionary<Guid, RunListItem> { [run.Id] = run },
            run is null || silentSince is null
                ? new Dictionary<Guid, RunActivity>()
                : new Dictionary<Guid, RunActivity> { [run.Id] = new() { Id = run.Id, LastActivityAt = silentSince.Value } },
            new Dictionary<Guid, string>(),
            new Dictionary<Guid, string>(),
            Now);
}
