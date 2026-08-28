using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Daemon;
using Hall9k.Daemon.Dispatch;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Tests.Fakes;
using JasperFx;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The migration every task-projection shape change needs. The projections are Inline, so a
/// task that stopped receiving events before a change still carries the older document shape,
/// and each reader of the new field mistakes absent-because-old for nothing-was-recorded: the
/// lifecycle split (Decisions Log #34) left pre-split documents with no assignedOwnerId key,
/// which the claim filter reads as nobody's work, and the dead-blocker recovery (Decisions Log
/// #61) left pre-recovery documents with no deadDependencyReasons map, which the surviving-hold
/// fallback reads as no blocker having said anything. An older document is simulated the way it
/// actually exists: the current projection writes the document, then the key is stripped back
/// off it in the database.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class TaskProjectionBackfillTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_task_projected_before_the_lifecycle_split_is_re_projected_and_claimable_again()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = new();
        await node.InitializeAsync(store, cts.Token);
        DispatchEngine engine = NewEngine(store, node);

        Guid taskId = DomainId.New();
        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<TaskAggregate>(taskId, TaskSeed.Dispatchable(
                Add(taskId, "Queued long before the split"), node.OwnerId, Now));
            await seed.SaveChangesAsync(cts.Token);
        }

        await StripAssignedOwnerAsync(taskId, cts.Token);

        (await engine.ClaimEligibleAsync(cts.Token)).Should().BeEmpty(
            "this is the defect: a document without the key never matches the claim filter, "
            + "and an unclaimable task looks exactly like an idle queue");

        IReadOnlyList<Guid> rebuilt = await TaskLifecycleProjectionBackfill.RunAsync(store, cts.Token);

        rebuilt.Should().Equal(taskId);
        await using (IQuerySession query = store.QuerySession())
        {
            TaskListItem row = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
            row.AssignedOwnerId.Should().Be(node.OwnerId, "the events always said whose work it is");
            row.State.Should().Be(TaskState.Queued, "replaying the stream reproduces the rest of the document too");

            TaskDetails details = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
            details.AssignedOwnerId.Should().Be(node.OwnerId, "h9k task show reads this one");
        }

        (await engine.ClaimEligibleAsync(cts.Token)).Select(work => work.TaskId).Should().Equal(taskId);

        await using (IQuerySession query = store.QuerySession())
        {
            TaskListItem row = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
            row.State.Should().Be(
                TaskState.Claimed,
                "the repair must leave the document where ordinary Inline projection left it: a "
                + "rebuild that stored it a version ahead of its stream would drop the very next "
                + "event, and the claim it just accepted would never reach the board");
        }
    }

    [Fact]
    public async Task A_task_that_genuinely_has_no_owner_is_not_re_projected_on_every_start()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();

        Guid draft = DomainId.New();
        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<TaskAggregate>(draft, Add(draft, "Still being written"));
            await seed.SaveChangesAsync(cts.Token);
        }

        (await TaskLifecycleProjectionBackfill.RunAsync(store, cts.Token)).Should().BeEmpty(
            "an unassigned draft stores assignedOwnerId as an explicit null, which is what makes "
            + "an absent key a sound marker for the pre-split shape — and the backfill self-terminating");
    }

    [Fact]
    public async Task A_dependent_projected_before_the_recovery_keeps_the_hold_its_stream_still_records()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();

        Guid first = DomainId.New();
        Guid second = DomainId.New();
        Guid dependent = DomainId.New();
        await SeedDependentHeldByTwoDeadBlockersAsync(store, dependent, first, second, cts.Token);

        // What a document written before Decisions Log #61 looks like: dead blockers listed and
        // a reason displayed, but no per-blocker map for the surviving-hold fallback to read.
        await StripKeyAsync(dependent, "deadDependencyReasons", cts.Token);

        IReadOnlyList<Guid> rebuilt = await TaskLifecycleProjectionBackfill.RunAsync(store, cts.Token);
        rebuilt.Should().Equal(dependent);

        // The event the repair exists for, and the one a rebuild that left the document a
        // version ahead of its stream would silently drop.
        await using (IDocumentSession recover = store.LightweightSession())
        {
            TaskAggregate held = (await recover.Events.AggregateStreamAsync<TaskAggregate>(
                dependent, token: cts.Token))!;
            recover.Events.Append(dependent, TaskDecider.DependencyRecovered(
                held, first, "it is back in the pipeline", Now.AddHours(1)));
            await recover.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession query = store.QuerySession();
        TaskListItem row = (await query.LoadAsync<TaskListItem>(dependent, cts.Token))!;
        row.DeadDependencies.Should().Equal([second], "only the recovered blocker stops counting");
        row.DependencyFailureReason.Should().Be(
            "the second blocker will never close out",
            "the surviving hold is what the stream records, not silence about a blocker still dead");

        TaskDetails details = (await query.LoadAsync<TaskDetails>(dependent, cts.Token))!;
        details.DependencyFailureReason.Should().Be(
            row.DependencyFailureReason, "h9k task show reads the same surviving hold");
    }

    [Fact]
    public async Task A_dependent_already_carrying_its_recorded_reasons_is_not_re_projected_on_every_start()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();

        Guid first = DomainId.New();
        Guid second = DomainId.New();
        Guid dependent = DomainId.New();
        await SeedDependentHeldByTwoDeadBlockersAsync(store, dependent, first, second, cts.Token);

        (await TaskLifecycleProjectionBackfill.RunAsync(store, cts.Token)).Should().BeEmpty(
            "the current projections always write deadDependencyReasons — populated here, and an "
            + "explicit empty map on a task with nothing dead — which is what makes an absent key "
            + "a sound marker for the pre-recovery shape, and the backfill self-terminating");
    }

    /// <summary>
    /// The backfill runs at daemon start, and a launch failure is exactly what someone inspects
    /// with no daemon up — so the window where the lean row has no failureReason key is a window
    /// a human reads h9k task show in. The screen holds the detail document, which recorded the
    /// reason, and must not report an absence its own record contradicts (pre-PR review,
    /// 2026-08-22).
    /// </summary>
    [Fact]
    public async Task A_failure_projected_before_the_status_redesign_still_says_why_before_the_backfill_runs()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();

        Guid taskId = DomainId.New();
        // The launch-failure path fails a task whose run stream was never started (RunLauncher),
        // so there is no run document to read the reason off either.
        Guid runId = DomainId.New();
        await using (IDocumentSession seed = store.LightweightSession())
        {
            TaskAdded added = Add(taskId, "Failed on the way out of the launcher");
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(added, DomainId.New(), Now);
            seed.Events.StartStream<TaskAggregate>(taskId, [
                .. lifecycle,
                TaskDecider.Fail(task, runId, "Launch failed: the worktree checkout was refused", Now),
            ]);
            await seed.SaveChangesAsync(cts.Token);
        }

        await StripKeyAsync(taskId, "failureReason", ["mt_doc_tasklistitem"], cts.Token);

        await using (IQuerySession query = store.QuerySession())
        {
            TaskDetails details = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
            TaskStatusRow row = (await TaskStatusComposer.ComposeOneAsync(query, details, Now, cts.Token))!;

            row.State.Should().Be(LifecycleState.Failed);
            row.Attention.Cause.Should().Be(
                "Launch failed: the worktree checkout was refused",
                "the stream recorded why, and the detail document still carries it");
        }

        (await TaskLifecycleProjectionBackfill.RunAsync(store, cts.Token)).Should().Equal(
            [taskId], "the missing key is a staleness marker, so the window closes at the next daemon start");

        await using (IQuerySession query = store.QuerySession())
        {
            (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.FailureReason.Should().Be(
                "Launch failed: the worktree checkout was refused", "and the board reads it from the row again");
        }
    }

    /// <summary>
    /// A document written before the tasks/_archive/ archiving rule (backlog 51) carries
    /// ResolvedReason but no ResolvedRunId, which the render sweep's <c>IsArchived</c> compares
    /// against the task's current run to tell "this resolve belongs to the run standing right
    /// now" from a stale note left over from a superseded run. With the key absent, that
    /// comparison reads null against a real run id and never matches, so the task's directory
    /// would sit at the top level of <c>tasks/</c> forever — the same class of defect the
    /// failureReason marker below already covers for <c>FailedRunId</c>.
    /// </summary>
    [Fact]
    public async Task A_resolve_projected_before_the_archiving_rule_still_carries_its_run_id_after_the_backfill_runs()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();

        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid runId = DomainId.New();
        await using (IDocumentSession seed = store.LightweightSession())
        {
            TaskAdded added = Add(taskId, "Resolved on an attestation, projected before the archiving rule");
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(added, ownerId, Now);
            TaskClaimed claimed = TaskDecider.Claim(task, DomainId.New(), ownerId, runId, Now);
            task.Apply(claimed);
            TaskFailed failed = TaskDecider.Fail(task, runId, "the run failed", Now.AddMinutes(1));
            task.Apply(failed);
            TaskResolved resolved = TaskDecider.Resolve(
                task, "merged by hand", null, Now.AddMinutes(2), ownerId);
            seed.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed, failed, resolved]);
            await seed.SaveChangesAsync(cts.Token);
        }

        await StripKeyAsync(taskId, "resolvedRunId", ["mt_doc_taskdetails"], cts.Token);

        await using (IQuerySession query = store.QuerySession())
        {
            TaskDetails stale = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
            stale.ResolvedRunId.Should().BeNull(
                "the pre-archiving-rule document never wrote this key at all");
        }

        (await TaskLifecycleProjectionBackfill.RunAsync(store, cts.Token)).Should().Equal(
            [taskId], "the missing key is a staleness marker, so the window closes at the next daemon start");

        await using (IQuerySession query = store.QuerySession())
        {
            TaskDetails details = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
            details.ResolvedRunId.Should().Be(
                runId, "the stream always said which run this attestation belongs to");
            details.CurrentRunId.Should().Be(
                runId, "and it is this task's current run, which is what IsArchived actually compares against");
        }
    }

    /// <summary>
    /// <see cref="TaskDetails.UntrackedAttested"/> is a non-nullable bool, always serialized, so
    /// an absent key is the marker for a document written before the field landed (backlog: a
    /// task can be published deliberately untracked under a tracking backlog policy) — the same
    /// class of defect the failureReason and resolvedRunId markers above already cover.
    /// </summary>
    [Fact]
    public async Task An_untracked_attestation_projected_before_the_marker_landed_is_restored_after_the_backfill_runs()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();

        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();
        await using (IDocumentSession seed = store.LightweightSession())
        {
            TaskAdded added = Add(taskId, "Published untracked, projected before the marker landed");
            TaskAggregate task = new();
            task.Apply(added);
            TaskPublished published = TaskDecider.Publish(
                task, TaskDependencyGraph.Empty, Now, ownerId, BacklogPolicy.GitHubIssues, untracked: true);
            seed.Events.StartStream<TaskAggregate>(taskId, [added, published]);
            await seed.SaveChangesAsync(cts.Token);
        }

        await StripKeyAsync(taskId, "untrackedAttested", ["mt_doc_taskdetails"], cts.Token);

        await using (IQuerySession query = store.QuerySession())
        {
            TaskDetails stale = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
            stale.UntrackedAttested.Should().BeFalse(
                "the pre-marker document never wrote this key at all");
        }

        (await TaskLifecycleProjectionBackfill.RunAsync(store, cts.Token)).Should().Equal(
            [taskId], "the missing key is a staleness marker, so the window closes at the next daemon start");

        await using (IQuerySession query = store.QuerySession())
        {
            TaskDetails details = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
            details.UntrackedAttested.Should().BeTrue(
                "the stream always recorded the attestation; the rebuild restores it");
        }
    }

    /// <summary>
    /// A task Blocked on two blockers, both recorded dead, written through the same deciders the
    /// resolver uses. The blockers themselves need no streams here: what is under test is the
    /// dependent's document, and the records that hold it live on the dependent's own stream.
    /// </summary>
    private static async Task SeedDependentHeldByTwoDeadBlockersAsync(
        IDocumentStore store, Guid dependent, Guid first, Guid second, CancellationToken cancellationToken)
    {
        TaskDependencyGraph graph = new([
            Blocker(first, "The first blocker"),
            Blocker(second, "The second blocker"),
        ]);

        TaskAdded added = TaskDecider.Add(
            dependent, DomainId.New(), "Waits on two", ["it is done"], TaskType.Chore,
            null, null, null, Now, DomainId.New(), blockedBy: [first, second]);
        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(added, DomainId.New(), Now, graph);

        TaskDependencyFailed firstDied = TaskDecider.DependencyFailed(
            task, first, "the first blocker will never close out", Now);
        task.Apply(firstDied);
        TaskDependencyFailed secondDied = TaskDecider.DependencyFailed(
            task, second, "the second blocker will never close out", Now);

        await using IDocumentSession seed = store.LightweightSession();
        seed.Events.StartStream<TaskAggregate>(dependent, [.. lifecycle, firstDied, secondDied]);
        await seed.SaveChangesAsync(cancellationToken);
    }

    private static TaskDependency Blocker(Guid id, string objective) => new(
        id, objective, TaskState.Queued, IsClosedOut: false, CurrentRunState: null,
        PullRequestUrl: null, TaskType.Chore, []);

    /// <summary>Turns the stored documents back into the shape an older projection wrote.</summary>
    private async Task StripAssignedOwnerAsync(Guid taskId, CancellationToken cancellationToken) =>
        await StripKeyAsync(taskId, "assignedOwnerId", cancellationToken);

    private Task StripKeyAsync(Guid taskId, string key, CancellationToken cancellationToken) =>
        StripKeyAsync(taskId, key, ["mt_doc_tasklistitem", "mt_doc_taskdetails"], cancellationToken);

    /// <summary>
    /// The same, for a key only one of the two documents lost. The projections learned their
    /// fields at different times, so a field the detail document has carried for months can be
    /// absent from the lean row beside it.
    /// </summary>
    private async Task StripKeyAsync(
        Guid taskId, string key, IReadOnlyList<string> tables, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (string table in tables)
        {
            await using NpgsqlCommand command = new(
                $"update {table} set data = data - '{key}' where id = @id", connection);
            command.Parameters.AddWithValue("id", taskId);
            (await command.ExecuteNonQueryAsync(cancellationToken)).Should().Be(1);
        }
    }

    private static TaskAdded Add(Guid id, string objective) => TaskDecider.Add(
        id, DomainId.New(), objective, ["it is done"], TaskType.Chore,
        null, null, null, Now, DomainId.New());

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    private DispatchEngine NewEngine(DocumentStore store, NodeContext node) =>
        new(store, node, new DaemonConnection(postgres.ConnectionString), new FakeProcessManager(),
            Options.Create(new DaemonOptions { MaxConcurrentAgentSessions = 100, LeaseTimeout = TimeSpan.FromSeconds(60) }),
            NullLogger<DispatchEngine>.Instance);
}
