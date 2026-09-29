using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// Independent pre-PR review, cycle 5, conformance lens (RunLauncher.cs:161): a pr-review task's
/// very first claim can now dispatch nothing but a pre-flight and requeue before any run is ever
/// opened, so that claim's own run id lands in <c>TaskDetails.RunIds</c> / <c>TaskAggregate.RunIds</c>
/// with no <see cref="RunDetails"/> ever written for it. Every caller that used to trust
/// <c>RunIds[0]</c> as "the original review" must walk past a run id like that instead of stopping
/// on it — this tier, not a pure unit one, because the whole thing this resolver decides is whether
/// a <see cref="RunDetails"/> document exists for a given id, which no fake can answer for.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class OriginalReviewRunResolverTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_run_id_with_no_run_details_is_skipped_for_the_first_one_that_has_one()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        // Never opened into a run — exactly a first claim whose own dispatch never got past the
        // pre-flight gate: it named a run id, but no RunDispatched, and so no RunDetails, was ever
        // written for it.
        Guid preflightOnlyRunId = DomainId.New();

        Guid realReviewRunId = DomainId.New();
        Guid taskId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<RunAggregate>(realReviewRunId, new RunDispatched(
                realReviewRunId, taskId, DomainId.New(), DomainId.New(), LeaseGeneration: 1,
                SessionId: DomainId.New(), WorktreePath: "/tmp/does-not-exist", Branch: "pr/1",
                ExecutorMode.Subscription, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession query = store.QuerySession();
        Guid? resolved = await OriginalReviewRunResolver.ResolveAsync(
            query, [preflightOnlyRunId, realReviewRunId], cts.Token);

        resolved.Should().Be(
            realReviewRunId,
            "the first claim's own run id was never opened into a real run, so the resolver must walk "
            + "past it to the run that actually carries the review");
    }

    [Fact]
    public async Task No_run_details_anywhere_resolves_to_null()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        await using IQuerySession query = store.QuerySession();
        Guid? resolved = await OriginalReviewRunResolver.ResolveAsync(
            query, [DomainId.New(), DomainId.New()], cts.Token);

        resolved.Should().BeNull(
            "every claim so far dispatched nothing but a pre-flight — there is no review yet to name");
    }
}
