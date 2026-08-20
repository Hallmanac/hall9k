using FluentAssertions;
using Hall9k.Domain.Features.Run.Documents;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using JasperFx;
using JasperFx.Events;
using Marten;
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
}
