using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Daemon;
using Hall9k.Daemon.Dispatch;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Documents;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// What the database and the operating system arbitrate about a claim and its lease, sharing one
/// container across the three seams that ask it. Each was its own class and so its own container
/// for two to five tests; every assertion here names the stream, task, or lease it seeded, so a
/// sibling seam's rows are as invisible as a sibling test's already were —
/// <see cref="PostgresFixture"/> has always shared one database across a class. The dispatch
/// ceiling's own coverage stays out (<c>DispatchEngineTests</c>, <c>DispatchCeilingTests</c>,
/// <c>ProjectRunCeilingDispatchTests</c>): those count what a node or project is carrying rather
/// than reading one row by id, and their own doc comments say a sibling's leftover lease would be
/// counted as one of them.
/// <para>
/// Racing claims from Queued — two racing claims produce exactly one winner and one generation, an
/// unassign that races a claim loses at the database, a link that races an abandon loses there
/// too, and heartbeat telemetry upserts without ever touching a stream.
/// </para>
/// <para>
/// Racing claims from Published — <c>h9k task work</c>'s atomic entry (task 688a1ccf-h9k): the
/// assignment and the interactive claim land as one event append, so the database's own optimistic
/// concurrency is what arbitrates a genuine collision — two operators, or an operator racing
/// another owner's plain <c>h9k task assign</c> — to exactly one winner.
/// <see cref="TaskWorkCommand.PrepareInteractiveClaimFromPublished"/> is what each racing side
/// calls to build its own (Assigned, Claimed) pair; the loser's own honest-race-loss message
/// (<see cref="TaskWorkCommand.DescribeAssignAndClaimRaceLossAsync"/>) names who actually won.
/// </para>
/// <para>
/// Lease liveness — the sleep-masquerading-as-death defenses (origin incident, 2026-08-18: a
/// lid-close turned two active runs into a five-generation storm). A lease this node holds asks
/// the OS before the timestamp is believed; a wall-clock jump between sweeps refreshes local
/// heartbeats before expiry is evaluated; a claim is refused while the previous generation's agent
/// still runs here. Remote leases keep the plain timeout.
/// </para>
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class ClaimAndLeaseArbitrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Two_racing_claims_produce_exactly_one_winner_and_one_generation()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();
        await using (IDocumentSession setup = store.LightweightSession())
        {
            setup.Events.StartStream<TaskAggregate>(taskId, TaskSeed.Dispatchable(
                TaskDecider.Add(
                    taskId, DomainId.New(), "Prove the claim is the lock",
                    ["exactly one claim survives"], TaskType.Chore,
                    null, null, null, Now, ownerId),
                ownerId, Now));
            await setup.SaveChangesAsync(cts.Token);
        }

        // Two daemons read the same stream state, then both try to append a claim at the
        // same expected version — the database, not timing, decides the winner.
        await using IDocumentSession first = store.LightweightSession();
        await using IDocumentSession second = store.LightweightSession();

        TaskAggregate view1 = (await first.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        TaskAggregate view2 = (await second.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;

        TaskClaimed claim1 = TaskDecider.Claim(view1, DomainId.New(), ownerId, DomainId.New(), Now);
        TaskClaimed claim2 = TaskDecider.Claim(view2, DomainId.New(), ownerId, DomainId.New(), Now);

        // The seed wrote the lifecycle events, so the claim lands one past them.
        first.Events.Append(taskId, expectedVersion: TaskSeed.EventCount + 1, claim1);
        second.Events.Append(taskId, expectedVersion: TaskSeed.EventCount + 1, claim2);

        await first.SaveChangesAsync(cts.Token);
        Func<Task> losing = () => second.SaveChangesAsync(cts.Token);
        await losing.Should().ThrowAsync<EventStreamUnexpectedMaxEventIdException>(
            "the second claim must lose at the database, not by luck");

        await using IDocumentSession verify = store.LightweightSession();
        TaskAggregate final = (await verify.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        final.State.Should().Be(TaskState.Claimed);
        final.LeaseGeneration.Should().Be(1, "exactly one claim landed");
        final.CurrentRunId.Should().Be(claim1.RunId);
        final.RunIds.Should().ContainSingle();
    }

    [Fact]
    public async Task An_unassign_that_races_a_claim_loses_at_the_database()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();
        await using (IDocumentSession setup = store.LightweightSession())
        {
            setup.Events.StartStream<TaskAggregate>(taskId, TaskSeed.Dispatchable(
                TaskDecider.Add(
                    taskId, DomainId.New(), "Prove unassign cannot outrun a claim",
                    ["a claimed task stays claimed"], TaskType.Chore,
                    null, null, null, Now, ownerId),
                ownerId, Now));
            await setup.SaveChangesAsync(cts.Token);
        }

        // h9k task unassign fences the stream and reads the lease — no node holds one yet, so
        // the decider allows it. The dispatch loop then claims the task inside that window.
        await using IDocumentSession unassigning = store.LightweightSession();
        StreamState fence = (await unassigning.Events.FetchStreamStateAsync(taskId, cts.Token))!;
        TaskAggregate view = (await unassigning.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, version: fence.Version, token: cts.Token))!;
        bool leaseHeld = await unassigning.LoadAsync<TaskLease>(taskId, cts.Token) is not null;
        leaseHeld.Should().BeFalse("no node has claimed the task at the moment the CLI reads it");

        await using (IDocumentSession claiming = store.LightweightSession())
        {
            TaskAggregate claimView = (await claiming.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, token: cts.Token))!;
            claiming.Events.Append(taskId, expectedVersion: TaskSeed.EventCount + 1,
                TaskDecider.Claim(claimView, DomainId.New(), ownerId, DomainId.New(), Now));
            await claiming.SaveChangesAsync(cts.Token);
        }

        unassigning.Events.Append(taskId, expectedVersion: fence.Version + 1, TaskDecider.Unassign(
            view, "taking it back", leaseHeld, Now, ownerId));
        Func<Task> losing = () => unassigning.SaveChangesAsync(cts.Token);
        await losing.Should().ThrowAsync<EventStreamUnexpectedMaxEventIdException>(
            "an unfenced unassign would land on top of the claim and orphan a running agent");

        await using IDocumentSession verify = store.LightweightSession();
        TaskAggregate final = (await verify.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        final.State.Should().Be(TaskState.Claimed);
        final.AssignedOwnerId.Should().Be(ownerId, "the contract stayed under the running agent");
    }

    [Fact]
    public async Task Telemetry_documents_upsert_without_touching_any_stream()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
                taskId, DomainId.New(), "Telemetry stays out of streams",
                ["lease + activity upsert freely"], TaskType.Chore,
                null, null, null, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        // Heartbeats and tail-cursor updates: pure document upserts, many times over.
        for (int beat = 0; beat < 3; beat++)
        {
            await using IDocumentSession session = store.LightweightSession();
            session.Store(new TaskLease
            {
                Id = taskId, NodeId = DomainId.New(), LeaseGeneration = 1,
                HeartbeatAt = Now.AddSeconds(beat * 15),
            });
            session.Store(new RunActivity
            {
                Id = runId, LastActivityAt = Now.AddSeconds(beat * 15), StreamBytesRead = beat * 4096,
            });
            await session.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession query = store.QuerySession();
        TaskLease? lease = await query.LoadAsync<TaskLease>(taskId, cts.Token);
        RunActivity? activity = await query.LoadAsync<RunActivity>(runId, cts.Token);
        lease!.HeartbeatAt.Should().Be(Now.AddSeconds(30));
        activity!.StreamBytesRead.Should().Be(8192);

        long eventCount = (await query.Events.FetchStreamAsync(taskId, token: cts.Token)).Count;
        eventCount.Should().Be(1, "heartbeats are telemetry, never events (log #7/#11)");
    }

    /// <summary>
    /// h9k task link-jira reads the card back from Jira before it writes anything, and that read
    /// is a request to somebody else's tenant with a 30-second deadline on it. Everything the link
    /// is allowed to assume about the task was read before that call, so the append has to be
    /// fenced on the version it read — the guard that refuses to link an abandoned task is worth
    /// nothing if an abandon can land inside the window and be silently written over.
    /// <para>
    /// The design expects agents to retry this command after a card is created, which is what
    /// makes the window ordinary rather than exotic. Origin incident (2026-08-21): the pre-PR
    /// review of the Jira branch found the append unfenced while every sibling task-mutating
    /// command fenced.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_link_that_races_an_abandon_loses_at_the_database()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();
        await using (IDocumentSession setup = store.LightweightSession())
        {
            setup.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
                taskId, DomainId.New(), "Prove a link cannot land on an abandoned task",
                ["the fence refuses it"], TaskType.Chore, null, null, null, Now, ownerId));
            await setup.SaveChangesAsync(cts.Token);
        }

        // What the command does before it calls Jira: fence, then read the task at that version.
        await using IDocumentSession linking = store.LightweightSession();
        StreamState fence = (await linking.Events.FetchStreamStateAsync(taskId, cts.Token))!;
        TaskAggregate view = (await linking.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, version: fence.Version, token: cts.Token))!;
        view.State.Should().NotBe(TaskState.Abandoned, "the guard sees a live task at the moment it reads");

        // And what a human does while the tenant is answering.
        await using (IDocumentSession abandoning = store.LightweightSession())
        {
            TaskAggregate abandonView = (await abandoning.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, token: cts.Token))!;
            abandoning.Events.Append(taskId, TaskDecider.Abandon(abandonView, "not doing it", Now, ownerId));
            await abandoning.SaveChangesAsync(cts.Token);
        }

        linking.Events.Append(taskId, expectedVersion: fence.Version + 1, TaskDecider.LinkWorkItem(
            view, new ExternalReference(WorkItemProvider.Jira, "PROJ-123"),
            "Prove a link cannot land on an abandoned task", "To Do (open)", Now, Now, ownerId));
        Func<Task> losing = () => linking.SaveChangesAsync(cts.Token);
        await losing.Should().ThrowAsync<EventStreamUnexpectedMaxEventIdException>(
            "an unfenced link would attach a live card to a task nobody is doing");

        await using IDocumentSession verify = store.LightweightSession();
        TaskAggregate final = (await verify.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        final.State.Should().Be(TaskState.Abandoned);
        final.ExternalReference.Should().BeNull("nothing was linked, so nothing has to be unlinked");
    }

    /// <summary>
    /// The same fence on the other Jira command. h9k task push-to-jira decides against a task it
    /// read, and the write that most plausibly lands in between is h9k task link-jira, which an
    /// agent may be running at that moment — the command's own reads take long enough to matter,
    /// since node bootstrap can shell out to git and gh before the append.
    /// <para>
    /// Unfenced, both sides see a task with no reference: the request appends after the link, the
    /// task reads as linked and pending at once, and the daemon dispatches a session to write a
    /// card for work that already carries one. Origin incident (2026-08-21): the pre-PR review of
    /// the Jira branch found exactly that window.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_publication_request_that_races_a_link_loses_at_the_database()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();
        await using (IDocumentSession setup = store.LightweightSession())
        {
            setup.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
                taskId, DomainId.New(), "Prove one task cannot ask for a second card",
                ["the fence refuses it"], TaskType.Chore, null, null, null, Now, ownerId));
            await setup.SaveChangesAsync(cts.Token);
        }

        // What the command does before it appends: fence, then read the task at that version.
        await using IDocumentSession requesting = store.LightweightSession();
        StreamState fence = (await requesting.Events.FetchStreamStateAsync(taskId, cts.Token))!;
        TaskAggregate view = (await requesting.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, version: fence.Version, token: cts.Token))!;
        view.ExternalReference.Should().BeNull("the guard sees an unlinked task at the moment it reads");

        // And what an agent finishing an earlier publication does in the meantime.
        await using (IDocumentSession linking = store.LightweightSession())
        {
            TaskAggregate linkView = (await linking.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, token: cts.Token))!;
            linking.Events.Append(taskId, TaskDecider.LinkWorkItem(
                linkView, new ExternalReference(WorkItemProvider.Jira, "PROJ-123"),
                "Prove one task cannot ask for a second card", "To Do (open)", Now, Now, ownerId));
            await linking.SaveChangesAsync(cts.Token);
        }

        requesting.Events.Append(taskId, expectedVersion: fence.Version + 1,
            TaskDecider.RequestWorkItemPublication(
                view, WorkItemProvider.Jira, JiraProjectKey.Parse("PROJ"), Now, ownerId));
        Func<Task> losing = () => requesting.SaveChangesAsync(cts.Token);
        await losing.Should().ThrowAsync<EventStreamUnexpectedMaxEventIdException>(
            "an unfenced request would leave the task linked and pending, and the daemon files card two");

        await using IDocumentSession verify = store.LightweightSession();
        TaskAggregate final = (await verify.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        final.ExternalReference.Should().NotBeNull("the card it already has is what survived");
        final.PendingPublicationProvider.Should().BeNull("nothing is outstanding, so nothing dispatches");
    }

    // ── racing claims from Published ──
    private static readonly DateTimeOffset InteractiveClaimNow = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Two_racing_interactive_claims_from_published_produce_exactly_one_winner()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();
        await SeedOwnerAsync(store, ownerId, "Operator One", cts.Token);
        await SeedPublishedTaskAsync(store, taskId, cts.Token);

        // Two operators (or one operator in two terminals) read the same Published task at the
        // same fence version, then both race to claim it interactively — exactly the collision
        // the old two-step assign-then-claim flow could lose to the dispatcher, now arbitrated by
        // one append per side instead.
        await using IDocumentSession first = store.LightweightSession();
        await using IDocumentSession second = store.LightweightSession();

        StreamState fence = (await first.Events.FetchStreamStateAsync(taskId, cts.Token))!;
        TaskAggregate view1 = (await first.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, version: fence.Version, token: cts.Token))!;
        TaskAggregate view2 = (await second.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, version: fence.Version, token: cts.Token))!;

        Guid runId1 = DomainId.New();
        Guid runId2 = DomainId.New();
        (TaskAssigned assigned1, TaskClaimed claimed1, _) = TaskWorkCommand.PrepareInteractiveClaimFromPublished(
            view1, ownerId, [], runId1, InteractiveClaimNow, acknowledgeUnmetDependencies: false);
        (TaskAssigned assigned2, TaskClaimed claimed2, _) = TaskWorkCommand.PrepareInteractiveClaimFromPublished(
            view2, ownerId, [], runId2, InteractiveClaimNow, acknowledgeUnmetDependencies: false);

        long expectedVersion = fence.Version + 2;
        first.Events.Append(taskId, expectedVersion: expectedVersion, assigned1, claimed1);
        second.Events.Append(taskId, expectedVersion: expectedVersion, assigned2, claimed2);

        await first.SaveChangesAsync(cts.Token);
        Func<Task> losing = () => second.SaveChangesAsync(cts.Token);
        await losing.Should().ThrowAsync<EventStreamUnexpectedMaxEventIdException>(
            "the second atomic claim must lose at the database, not by luck");

        await using IQuerySession verify = store.QuerySession();
        TaskAggregate final = (await verify.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        final.State.Should().Be(TaskState.Claimed);
        final.IsInteractiveClaim.Should().BeTrue();
        final.LeaseGeneration.Should().Be(1, "exactly one claim landed, from a fresh task");
        final.CurrentRunId.Should().Be(runId1, "the first append committed and the second never landed");
        final.RunIds.Should().ContainSingle();

        // The loser is told honestly who won, not just that something changed — read back from
        // what actually committed rather than guessed at.
        DomainConflictException raceLoss = await TaskWorkCommand.DescribeAssignAndClaimRaceLossAsync(
            store, taskId, cts.Token);
        raceLoss.Message.Should().Contain("Operator One");
        raceLoss.Message.Should().Contain("claimed it interactively first");
    }

    /// <summary>
    /// The other shape a collision can take: a plain <c>h9k task assign</c> (no claim) commits
    /// first, so the loser's atomic append fails against a task that only ever reached Queued.
    /// <see cref="TaskWorkCommand.DescribeAssignAndClaimRaceLossAsync"/> has nothing to name a
    /// winner by — a bare assignment claims nothing — so it says exactly that rather than
    /// inventing a claimant.
    /// </summary>
    [Fact]
    public async Task A_claim_that_races_a_plain_assign_is_told_the_task_moved_on_without_a_claimant()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();
        await SeedPublishedTaskAsync(store, taskId, cts.Token);

        await using IDocumentSession claiming = store.LightweightSession();
        await using IDocumentSession assigning = store.LightweightSession();

        StreamState fence = (await claiming.Events.FetchStreamStateAsync(taskId, cts.Token))!;
        TaskAggregate claimView = (await claiming.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, version: fence.Version, token: cts.Token))!;
        TaskAggregate assignView = (await assigning.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, version: fence.Version, token: cts.Token))!;

        (TaskAssigned assigned, TaskClaimed claimed, _) = TaskWorkCommand.PrepareInteractiveClaimFromPublished(
            claimView, ownerId, [], DomainId.New(), InteractiveClaimNow, acknowledgeUnmetDependencies: false);

        // The plain h9k task assign path: TaskDecider.Assign alone, no claim.
        TaskAssigned plainAssign = TaskDecider.Assign(assignView, ownerId, [], InteractiveClaimNow, ownerId);
        assigning.Events.Append(taskId, expectedVersion: fence.Version + 1, plainAssign);
        await assigning.SaveChangesAsync(cts.Token);

        claiming.Events.Append(taskId, expectedVersion: fence.Version + 2, assigned, claimed);
        Func<Task> losing = () => claiming.SaveChangesAsync(cts.Token);
        await losing.Should().ThrowAsync<EventStreamUnexpectedMaxEventIdException>(
            "the plain assign landed first, so the atomic claim's own expected version is already stale");

        await using IQuerySession verify = store.QuerySession();
        TaskAggregate final = (await verify.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        final.State.Should().Be(TaskState.Queued, "only the plain assign landed — nothing claimed it");

        DomainConflictException raceLoss = await TaskWorkCommand.DescribeAssignAndClaimRaceLossAsync(
            store, taskId, cts.Token);
        raceLoss.Message.Should().Contain("Queued", "nothing claimed it, so there is no winner to name");
        raceLoss.Message.Should().NotContain("claimed by", "the plain assign never claimed the task, so nothing claims that it did");
    }

    private static async Task SeedPublishedTaskAsync(DocumentStore store, Guid taskId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        TaskAdded added = TaskDecider.Add(
            taskId, DomainId.New(), "Prove the atomic claim's collision is arbitrated by the store",
            ["exactly one claim survives a genuine race"], TaskType.Chore,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: InteractiveClaimNow, addedByOwnerId: DomainId.New());
        TaskAggregate task = new();
        task.Apply(added);
        TaskPublished published = TaskDecider.Publish(task, TaskDependencyGraph.Empty, InteractiveClaimNow, DomainId.New());

        session.Events.StartStream<TaskAggregate>(taskId, added, published);
        await session.SaveChangesAsync(cancellationToken);
    }

    private static async Task SeedOwnerAsync(
        DocumentStore store, Guid ownerId, string name, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<OwnerAggregate>(ownerId, OwnerDecider.Register(ownerId, name, null, InteractiveClaimNow));
        await session.SaveChangesAsync(cancellationToken);
    }

    // ── lease liveness ──
    private static readonly DateTimeOffset LeaseNow = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_stale_heartbeat_with_a_live_local_process_is_refreshed_never_requeued()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        FakeProcessManager processes = new();
        DispatchEngine engine = NewEngine(store, node, processes);

        // The wake scenario: the laptop slept 50 minutes mid-run, so the heartbeat is
        // long past the timeout — but the agent process is alive and about to resume.
        (Guid taskId, _) = await SeedClaimedTaskWithRunAsync(
            store, node.NodeId, node.OwnerId, processId: 61234, heartbeatAt: LeaseNow.AddMinutes(-50), cts.Token);
        processes.MarkAlive(61234);

        await engine.SweepExpiredLeasesAsync(LeaseNow, cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Value.Should().Be("Claimed", "a live local process means a live lease, whatever the heartbeat says");

        TaskLease lease = (await query.LoadAsync<TaskLease>(taskId, cts.Token))!;
        lease.HeartbeatAt.Should().Be(LeaseNow, "the sweep refreshes the lease it declined to expire");
    }

    [Fact]
    public async Task A_stale_heartbeat_with_a_dead_local_process_still_requeues_on_the_timeout()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        FakeProcessManager processes = new();
        DispatchEngine engine = NewEngine(store, node, processes);

        (Guid taskId, _) = await SeedClaimedTaskWithRunAsync(
            store, node.NodeId, node.OwnerId, processId: 61235, heartbeatAt: LeaseNow.AddMinutes(-50), cts.Token);
        // 61235 is never marked alive: the OS was asked and said no.

        await engine.SweepExpiredLeasesAsync(LeaseNow, cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Value.Should().Be("Queued", "a dead process plus a stale heartbeat is honest abandonment");
        processes.LivenessQueries.Should().Contain(q => q.ProcessId == 61235, "the OS was consulted, not bypassed");
    }

    [Fact]
    public async Task A_silent_remote_lease_expires_on_the_timeout_without_asking_this_nodes_os()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        FakeProcessManager processes = new();
        DispatchEngine engine = NewEngine(store, node, processes);

        // Another node's lease went silent. Even a pid our own OS would call alive is
        // meaningless for a remote lease — pids only make sense on the node that owns them.
        Guid remoteNodeId = DomainId.New();
        (Guid taskId, _) = await SeedClaimedTaskWithRunAsync(
            store, remoteNodeId, node.OwnerId, processId: 61236, heartbeatAt: LeaseNow.AddMinutes(-50), cts.Token);
        processes.MarkAlive(61236);

        await engine.SweepExpiredLeasesAsync(LeaseNow, cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Value.Should().Be("Queued", "sleep detection only applies to leases this node holds");
        processes.LivenessQueries.Should().BeEmpty("this node's OS knows nothing about a remote node's pids");
    }

    [Fact]
    public async Task A_wall_clock_jump_refreshes_local_heartbeats_before_expiry_while_remote_leases_still_expire()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        DispatchEngine engine = NewEngine(store, node, new FakeProcessManager());

        // Neither task records a pid, so the wake refresh is the only thing that can
        // save the local one — this pins the refresh itself, not the process check.
        Guid localTaskId = await SeedClaimedTaskAsync(store, node.NodeId, node.OwnerId, heartbeatAt: LeaseNow.AddSeconds(-30), cts.Token);
        Guid remoteTaskId = await SeedClaimedTaskAsync(store, DomainId.New(), node.OwnerId, heartbeatAt: LeaseNow.AddSeconds(-30), cts.Token);

        // Baseline sweep, then the machine sleeps 45 minutes: the next sweep's wall
        // clock has jumped far beyond the expected cadence.
        await engine.SweepExpiredLeasesAsync(LeaseNow, cts.Token);
        DateTimeOffset afterWake = LeaseNow.AddMinutes(45);
        await engine.SweepExpiredLeasesAsync(afterWake, cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskListItem local = (await query.LoadAsync<TaskListItem>(localTaskId, cts.Token))!;
        local.State.Value.Should().Be("Claimed",
            "the wake-time race is closed: local heartbeats refresh before expiry is evaluated");
        (await query.LoadAsync<TaskLease>(localTaskId, cts.Token))!.HeartbeatAt.Should().Be(afterWake);

        TaskListItem remote = (await query.LoadAsync<TaskListItem>(remoteTaskId, cts.Token))!;
        remote.State.Value.Should().Be("Queued",
            "this node's sleep says nothing about a remote node's silence — the timeout still rules there");
    }

    [Fact]
    public async Task A_claim_is_refused_while_the_previous_generations_process_is_alive_here()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        FakeProcessManager processes = new();
        DispatchEngine engine = NewEngine(store, node, processes);

        // A requeue slipped through (the pre-fix wake race), so the task is Queued while
        // generation 1's agent still runs in its worktree on this node.
        Guid taskId = DomainId.New();
        Guid firstRunId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, DomainId.New(), "Single flight", ["done"], TaskType.Chore,
                    null, null, null, LeaseNow, node.OwnerId),
                node.OwnerId, LeaseNow);
            var claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, firstRunId, LeaseNow);
            task.Apply(claimed);
            session.Events.StartStream<TaskAggregate>(taskId,
                [.. lifecycle, claimed, TaskDecider.Requeue(task, RequeueReason.LeaseExpired, LeaseNow)]);

            session.Events.StartStream<RunAggregate>(firstRunId,
                new RunDispatched(firstRunId, taskId, node.NodeId, node.OwnerId, 1, DomainId.New(),
                    "/wt/single-flight", "task/single-flight", ExecutorMode.Subscription, LeaseNow),
                new RunProcessStarted(firstRunId, 61237, LeaseNow));
            await session.SaveChangesAsync(cts.Token);
        }

        processes.MarkAlive(61237);
        IReadOnlyList<ClaimedWork> refused = await engine.ClaimEligibleAsync(cts.Token);
        refused.Should().NotContain(work => work.TaskId == taskId,
            "one task gets one agent per node while the previous generation is still alive");

        await using (IQuerySession query = store.QuerySession())
        {
            (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Value.Should().Be("Queued");
        }

        // The agent finishes (or dies): the very next cycle claims normally.
        processes.MarkDead(61237);
        IReadOnlyList<ClaimedWork> granted = await engine.ClaimEligibleAsync(cts.Token);
        granted.Should().Contain(work => work.TaskId == taskId, "the refusal is per cycle, not a ban");
        granted.Single(work => work.TaskId == taskId).LeaseGeneration.Should().Be(2);
    }


    private DispatchEngine NewEngine(DocumentStore store, NodeContext node, FakeProcessManager processes) =>
        new(store, node, new DaemonConnection(postgres.ConnectionString), processes,
            new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance),
            Options.Create(new DaemonOptions { MaxConcurrentTaskRuns = 500, LeaseTimeout = TimeSpan.FromSeconds(60) }),
            NullLogger<DispatchEngine>.Instance);

    /// <summary>A claimed task whose lease heartbeat sits at the given time; no run stream, no pid.</summary>
    private static async Task<Guid> SeedClaimedTaskAsync(
        DocumentStore store, Guid nodeId, Guid ownerId, DateTimeOffset heartbeatAt, CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();

        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(taskId, DomainId.New(), "Sleep walk", ["done"], TaskType.Chore,
                null, null, null, LeaseNow.AddHours(-1), ownerId),
            ownerId, LeaseNow.AddHours(-1));
        session.Events.StartStream<TaskAggregate>(taskId,
            [.. lifecycle, TaskDecider.Claim(task, nodeId, ownerId, DomainId.New(), LeaseNow.AddHours(-1))]);
        session.Store(new TaskLease { Id = taskId, NodeId = nodeId, LeaseGeneration = 1, HeartbeatAt = heartbeatAt });
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    /// <summary>A claimed task whose current run has a recorded pid — the OS can be asked.</summary>
    private static async Task<(Guid TaskId, Guid RunId)> SeedClaimedTaskWithRunAsync(
        DocumentStore store, Guid nodeId, Guid ownerId, int processId, DateTimeOffset heartbeatAt,
        CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();

        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(taskId, DomainId.New(), "Sleep walk", ["done"], TaskType.Chore,
                null, null, null, LeaseNow.AddHours(-1), ownerId),
            ownerId, LeaseNow.AddHours(-1));
        session.Events.StartStream<TaskAggregate>(taskId,
            [.. lifecycle, TaskDecider.Claim(task, nodeId, ownerId, runId, LeaseNow.AddHours(-1))]);
        session.Store(new TaskLease { Id = taskId, NodeId = nodeId, LeaseGeneration = 1, HeartbeatAt = heartbeatAt });

        session.Events.StartStream<RunAggregate>(runId,
            new RunDispatched(runId, taskId, nodeId, ownerId, 1, DomainId.New(),
                "/wt/sleep-walk", "task/sleep-walk", ExecutorMode.Subscription, LeaseNow.AddHours(-1)),
            new RunProcessStarted(runId, processId, LeaseNow.AddHours(-1)));
        await session.SaveChangesAsync(cancellationToken);
        return (taskId, runId);
    }
}
