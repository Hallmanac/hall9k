using FluentAssertions;
using Hall9k.Connectors.Orchestrator;
using Hall9k.Daemon.Courier;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Courier;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The feed courier's own ending, against a real event store (idea 89471598, piece 3): what the
/// unit tier cannot answer for <see cref="CourierEngine.RecordOutcomeAsync"/> — that a delivered
/// outcome really advances <see cref="OrchestratorFeedCursor"/> and a failed one really leaves it,
/// and that the run's own record and token spend really land on the store. No agent is spawned
/// anywhere here: this method starts after a session has already ended.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class CourierEngineTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;

    public CourierEngineTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Guid> DispatchedRunAsync(Guid projectId, CancellationToken cancellationToken)
    {
        Guid runId = DomainId.New();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream(
            runId, new CourierRunDispatched(runId, projectId, DomainId.New(), AgentModel.CourierDefault, Now));
        await session.SaveChangesAsync(cancellationToken);
        return runId;
    }

    [Fact]
    public async Task A_delivered_outcome_advances_the_feed_cursor_and_records_the_run_as_delivered()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid projectId = DomainId.New();
        Guid runId = await DispatchedRunAsync(projectId, cts.Token);
        AgentResult result = new(false, 500, 50, 0, 200, null, 4, "Sent it.");

        await CourierEngine.RecordOutcomeAsync(
            _postgres.Store, projectId, runId, AgentModel.CourierDefault, delivered: true, "Delivered.",
            drainableThroughSequence: 42, result, Now.AddSeconds(30), cts.Token);

        await using IQuerySession session = _postgres.Store.QuerySession();
        long cursor = await OrchestratorFeedReader.CursorAsync(session, projectId, cts.Token);
        cursor.Should().Be(42);

        CourierRunDetails? run = await session.LoadAsync<CourierRunDetails>(runId, cts.Token);
        run.Should().NotBeNull();
        run!.Delivered.Should().BeTrue();
        run.CompletedAt.Should().Be(Now.AddSeconds(30));
        run.Outcome.Should().Be("Delivered.");
    }

    [Fact]
    public async Task A_failed_outcome_leaves_the_feed_cursor_exactly_where_it_stood()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid projectId = DomainId.New();
        Guid runId = await DispatchedRunAsync(projectId, cts.Token);

        await CourierEngine.RecordOutcomeAsync(
            _postgres.Store, projectId, runId, AgentModel.CourierDefault, delivered: false,
            "The session ended without a delivered marker.", drainableThroughSequence: 42, result: null,
            Now.AddSeconds(30), cts.Token);

        await using IQuerySession session = _postgres.Store.QuerySession();
        long cursor = await OrchestratorFeedReader.CursorAsync(session, projectId, cts.Token);
        cursor.Should().Be(
            OrchestratorFeedCursor.NeverDrained, "a failed delivery must leave the same items for the next courier");

        CourierRunDetails? run = await session.LoadAsync<CourierRunDetails>(runId, cts.Token);
        run.Should().NotBeNull();
        run!.Delivered.Should().BeFalse();
        run.CompletedAt.Should().Be(Now.AddSeconds(30));
    }

    [Fact]
    public async Task A_result_that_reported_usage_is_recorded_even_when_delivery_failed()
    {
        // Real tokens were spent whether or not the send landed — the failed outcome test above
        // passes a null result (the timeout/no-result path); this covers the other failure shape,
        // a session that ran, reported usage, and simply never said the delivered marker.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid projectId = DomainId.New();
        Guid runId = await DispatchedRunAsync(projectId, cts.Token);
        AgentResult result = new(false, 300, 20, 0, 100, null, 2, "I couldn't find that tool.");

        await CourierEngine.RecordOutcomeAsync(
            _postgres.Store, projectId, runId, AgentModel.CourierDefault, delivered: false,
            "The session ended without a delivered marker. It said: I couldn't find that tool.",
            drainableThroughSequence: 7, result, Now.AddSeconds(10), cts.Token);

        await using IQuerySession session = _postgres.Store.QuerySession();
        Hall9k.Domain.Features.Run.PeriodSpend spend =
            await Hall9k.Domain.Features.Run.PeriodSpend.ReadAsync(session, Now.AddDays(-1), cts.Token);

        spend.TotalInputTokens.Should().Be(320, "300 fresh plus 20 cache-read input tokens, the same total every other role sums");
    }
}
