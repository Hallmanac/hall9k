using FluentAssertions;
using Hall9k.Connectors.Replication;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Marten;
using Marten.Services;
using Npgsql;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// Postgres hands out an event's global sequence number inside the writer's transaction, so a lower
/// number can commit after a higher one. The outbox must not move past a number it has not seen
/// committed, and must not wait on a number nobody will ever write for longer than
/// <see cref="EventReplicationOutbox.MaxSequenceGapWait"/>.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class EventReplicationOutboxSequenceGapTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string OwnerFingerprint = "owner-a-fingerprint";
    private const int EventsPerSeededTask = 3;
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;
    private readonly EventReplicationOutbox _outbox = new(new ReplicationProjectResolver());
    private readonly Guid _nodeId = DomainId.New();
    private readonly Guid _ownerId = DomainId.New();
    private readonly Guid _projectId = DomainId.New();

    public EventReplicationOutboxSequenceGapTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_write_that_got_its_number_first_but_commits_last_is_still_sent_and_nothing_goes_ahead_of_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await SwitchReplicationOnAsync(cts.Token);

        // A write held open on its own transaction: SaveChanges has taken its sequence numbers and
        // written its rows, but nothing is committed until the transaction is. (Marten 9's session
        // listeners have no hook between the write and the commit, so the transaction is the seam.)
        Guid heldTaskId = DomainId.New();
        Guid laterTaskId;
        await using (NpgsqlConnection heldConnection = new(_postgres.ConnectionString))
        {
            await heldConnection.OpenAsync(cts.Token);
            await using NpgsqlTransaction heldTransaction = await heldConnection.BeginTransactionAsync(cts.Token);
            await using (IDocumentSession held = _postgres.Store.LightweightSession(SessionOptions.ForTransaction(heldTransaction)))
            {
                AppendSeededTask(held, heldTaskId, Now.AddSeconds(1));
                await held.SaveChangesAsync(cts.Token);
            }

            laterTaskId = await SeedTaskAsync(Now.AddSeconds(2), cts.Token);

            (await SweepAsync(Now.AddSeconds(3), cts.Token)).EventsQueued.Should().Be(
                0, "the later write committed, but a lower number is still being written");

            await heldTransaction.CommitAsync(cts.Token);
        }

        long heldFirstSequence = await FirstSequenceOfAsync(heldTaskId, cts.Token);
        long laterFirstSequence = await FirstSequenceOfAsync(laterTaskId, cts.Token);
        heldFirstSequence.Should().BeLessThan(laterFirstSequence, "the held write took its number first");

        (await SweepAsync(Now.AddSeconds(4), cts.Token)).EventsQueued.Should().Be(
            2 * EventsPerSeededTask, "both writes are sent once the held one has committed");
    }

    [Fact]
    public async Task A_number_that_is_taken_and_never_written_holds_the_outbox_back_only_for_the_bounded_wait()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await SwitchReplicationOnAsync(cts.Token);

        await TakeUnwrittenSequenceNumberAsync(cts.Token);
        await SeedTaskAsync(Now.AddSeconds(1), cts.Token);

        (await SweepAsync(Now.AddSeconds(2), cts.Token)).EventsQueued.Should().Be(0, "the gap is first seen here");
        (await SweepAsync(Now.AddSeconds(2) + EventReplicationOutbox.MaxSequenceGapWait - TimeSpan.FromSeconds(1), cts.Token))
            .EventsQueued.Should().Be(0, "the wait has not passed yet");
        (await SweepAsync(Now.AddSeconds(2) + EventReplicationOutbox.MaxSequenceGapWait, cts.Token))
            .EventsQueued.Should().Be(0, "the deadline is only acted on by the sweep after the one that first sees it pass");
        (await SweepAsync(Now.AddSeconds(3) + EventReplicationOutbox.MaxSequenceGapWait, cts.Token))
            .EventsQueued.Should().Be(EventsPerSeededTask, "the wait has passed, so the later event is sent");
    }

    [Fact]
    public async Task Several_missing_numbers_seen_together_are_waited_out_together_not_one_after_another()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await SwitchReplicationOnAsync(cts.Token);

        await TakeUnwrittenSequenceNumberAsync(cts.Token);
        await SeedTaskAsync(Now.AddSeconds(1), cts.Token);
        await TakeUnwrittenSequenceNumberAsync(cts.Token);
        await SeedTaskAsync(Now.AddSeconds(2), cts.Token);
        await TakeUnwrittenSequenceNumberAsync(cts.Token);
        await SeedTaskAsync(Now.AddSeconds(3), cts.Token);

        DateTimeOffset firstSweep = Now.AddSeconds(4);
        (await SweepAsync(firstSweep, cts.Token)).EventsQueued.Should().Be(0, "the gaps are first seen here");
        (await SweepAsync(firstSweep + EventReplicationOutbox.MaxSequenceGapWait, cts.Token))
            .EventsQueued.Should().Be(0, "the deadline has only just been seen to pass");
        (await SweepAsync(firstSweep + EventReplicationOutbox.MaxSequenceGapWait.Add(TimeSpan.FromSeconds(1)), cts.Token))
            .EventsQueued.Should().Be(3 * EventsPerSeededTask, "one wait covers every hole that was visible when it started");
    }

    [Fact]
    public async Task A_missing_number_below_what_was_already_queued_is_never_waited_on()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await SwitchReplicationOnAsync(cts.Token);

        await HoldBackAPrivateIdeaAsync(Now.AddSeconds(1), cts.Token);
        await TakeUnwrittenSequenceNumberAsync(cts.Token);
        await SeedTaskAsync(Now.AddSeconds(3), cts.Token);

        DateTimeOffset firstSweep = Now.AddSeconds(3);
        await SweepAsync(firstSweep, cts.Token);
        await SweepAsync(firstSweep + EventReplicationOutbox.MaxSequenceGapWait, cts.Token);
        (await SweepAsync(firstSweep + EventReplicationOutbox.MaxSequenceGapWait.Add(TimeSpan.FromSeconds(1)), cts.Token))
            .EventsQueued.Should().Be(EventsPerSeededTask, "the first hole is waited out once");

        // A position written before the waited-out mark existed, rescanning a region whose holes an
        // earlier sweep had already moved past.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            EventReplicationOutboxPosition position =
                (await session.LoadAsync<EventReplicationOutboxPosition>(_projectId, cts.Token))!;
            position.GapsWaitedOutThroughSequence = 0;
            session.Store(position);
            await session.SaveChangesAsync(cts.Token);
        }

        await SeedTaskAsync(Now.AddSeconds(5), cts.Token);
        (await SweepAsync(firstSweep + EventReplicationOutbox.MaxSequenceGapWait.Add(TimeSpan.FromSeconds(2)), cts.Token))
            .EventsQueued.Should().Be(EventsPerSeededTask, "the hole sits below the high-water mark, where waiting recovers nothing");
    }

    [Fact]
    public async Task A_gap_already_waited_out_is_not_waited_on_again_while_a_held_back_private_event_causes_rescans()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await SwitchReplicationOnAsync(cts.Token);

        await HoldBackAPrivateIdeaAsync(Now.AddSeconds(1), cts.Token);

        await TakeUnwrittenSequenceNumberAsync(cts.Token);
        await SeedTaskAsync(Now.AddSeconds(3), cts.Token);

        DateTimeOffset firstSweep = Now.AddSeconds(4);
        (await SweepAsync(firstSweep, cts.Token)).EventsQueued.Should().Be(0, "the gap is first seen here");
        (await SweepAsync(firstSweep + EventReplicationOutbox.MaxSequenceGapWait, cts.Token)).EventsQueued.Should().Be(0);
        DateTimeOffset waitedOut = firstSweep + EventReplicationOutbox.MaxSequenceGapWait.Add(TimeSpan.FromSeconds(1));
        (await SweepAsync(waitedOut, cts.Token)).EventsQueued.Should().Be(EventsPerSeededTask);

        await using (IQuerySession session = _postgres.Store.QuerySession())
        {
            EventReplicationOutboxPosition? position =
                await session.LoadAsync<EventReplicationOutboxPosition>(_projectId, cts.Token);
            position!.PendingPrivateSequences.Should().NotBeEmpty("the private idea is still held back, so sweeps rescan past the gap");
        }

        await SeedTaskAsync(Now.AddSeconds(5), cts.Token);
        (await SweepAsync(waitedOut.AddSeconds(1), cts.Token)).EventsQueued.Should().Be(
            EventsPerSeededTask, "the rescan finds the same dead number but never waits on it a second time");
    }

    private async Task HoldBackAPrivateIdeaAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        Guid ideaId = DomainId.New();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<IdeaAggregate>(ideaId, IdeaDecider.Capture(
                ideaId, _ownerId, "Private for now", projectId: _projectId, now, ProjectHome.None));
            await session.SaveChangesAsync(cancellationToken);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            IdeaAggregate idea = (await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cancellationToken))!;
            session.Events.Append(ideaId, IdeaDecider.SetPrivate(idea, isPrivate: true, now.AddSeconds(1), _ownerId));
            await session.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task SwitchReplicationOnAsync(CancellationToken cancellationToken)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream<NodeAggregate>(_nodeId, new NodeRegistered(_nodeId, _ownerId, "node-a", "macOS", Now));
        await session.SaveChangesAsync(cancellationToken);
        await _outbox.QueuePendingAsync(session, _nodeId, _projectId, OwnerFingerprint, Now, cancellationToken);
    }

    private async Task<EventReplicationQueueResult> SweepAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        return await _outbox.QueuePendingAsync(session, _nodeId, _projectId, OwnerFingerprint, now, cancellationToken);
    }

    private void AppendSeededTask(IDocumentSession session, Guid taskId, DateTimeOffset now)
    {
        TaskAdded added = TaskDecider.Add(
            taskId, _projectId, "Ship the thing", ["it ships"], TaskType.Feature, null, null, null, now, _ownerId);
        session.Events.StartStream<TaskAggregate>(taskId, added);
        session.Events.Append(taskId, new TaskPublished(taskId, now, _ownerId));
        session.Events.Append(taskId, new TaskAssigned(taskId, _ownerId, [], now, _ownerId));
    }

    private async Task<Guid> SeedTaskAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        AppendSeededTask(session, taskId, now);
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    private async Task<long> FirstSequenceOfAsync(Guid streamId, CancellationToken cancellationToken)
    {
        await using IQuerySession session = _postgres.Store.QuerySession();
        return (await session.Events.QueryAllRawEvents().Where(e => e.StreamId == streamId).ToListAsync(cancellationToken))
            .Min(e => e.Sequence);
    }

    /// <summary>A raw nextval, never a Marten write that fails: Marten fills the numbers of a
    /// rolled-back write with tombstone events, which would close the gap instead of leaving it.</summary>
    private async Task TakeUnwrittenSequenceNumberAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = new(
            $"select nextval('{_postgres.Store.Options.DatabaseSchemaName}.mt_events_sequence')", connection);
        await command.ExecuteScalarAsync(cancellationToken);
    }
}
