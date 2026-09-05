using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <see cref="ReviewBoundaryApproved"/> and <see cref="ReviewParked.IsInteractiveGate"/> against
/// a real store (task: interactive mode becomes a recorded property of the task) — the one thing
/// the in-memory <c>RunAggregateTests</c> coverage cannot confirm: that the new event and fields
/// actually round-trip through Marten's schema and JSON serialization, and that
/// <c>RunDetailsProjection</c>'s inline Apply methods fire correctly against a real database.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class ReviewBoundaryApprovedTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Review_boundary_approved_round_trips_and_restores_the_parked_phase_through_a_real_store()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid runId = DomainId.New();
        Guid taskId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<RunAggregate>(runId,
                new RunDispatched(
                    runId, taskId, DomainId.New(), DomainId.New(), 1, DomainId.New(),
                    "/wt/interactive", "task/interactive", ExecutorMode.Subscription, Now),
                new ReviewDispatched(runId, DomainId.New(), 1, 5001, Now, Now),
                new ReviewCompleted(runId, 1, ReviewVerdict.NeedsFixes, Now),
                new ReviewParked(
                    runId, "Interactive mode is on for this task: the review verdict calls for a fix session.",
                    Now, IsInteractiveGate: true));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession query = store.QuerySession())
        {
            RunDetails? parkedDetails = await query.LoadAsync<RunDetails>(runId, cts.Token);
            parkedDetails.Should().NotBeNull();
            parkedDetails!.State.Should().Be(RunState.ReviewParked);
            parkedDetails.ParkedIsInteractiveGate.Should().BeTrue();
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new ReviewBoundaryApproved(runId, Now, DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession query = store.QuerySession())
        {
            RunAggregate run = (await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cts.Token))!;
            run.ReviewPhase.Should().Be(ReviewPhase.FixNeeded, "restored exactly where the park interrupted the loop");
            run.State.Should().Be(RunState.UnderReview);
            run.InteractiveGateCleared.Should().BeTrue();
            run.ParkedIsInteractiveGate.Should().BeFalse();

            RunDetails? details = await query.LoadAsync<RunDetails>(runId, cts.Token);
            details.Should().NotBeNull();
            details!.State.Should().Be(RunState.UnderReview);
            details.ParkedReason.Should().BeNull();
            details.ParkedIsInteractiveGate.Should().BeFalse();
            details.BoundaryApprovals.Should().ContainSingle().Which.ApprovedAt.Should().Be(Now);
        }
    }

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });
}
