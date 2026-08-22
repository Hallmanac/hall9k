using FluentAssertions;
using Hall9k.Domain.Features.Run.Documents;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Xunit;

using Hall9k.Tests.Fakes;

namespace Hall9k.Tests.Integration;

[Trait("Category", "RequiresDocker")]
public sealed class TaskClaimConcurrencyTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Two_racing_claims_produce_exactly_one_winner_and_one_generation()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

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
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

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
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

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
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

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
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

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
}
