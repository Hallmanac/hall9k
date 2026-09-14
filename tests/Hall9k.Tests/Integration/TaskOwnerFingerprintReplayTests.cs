using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using JasperFx.Events;
using Marten;
using Npgsql;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// Acceptance criterion 4 (idea 202383dc, event stamping): <see cref="TaskAssigned"/> and
/// <see cref="TaskClaimed"/> carry the assigned or claiming owner's root fingerprint as an added
/// field, and an event written before that field existed replays with it absent — resolved
/// instead through the identity core's own Guid-to-fingerprint mapping
/// (<see cref="OwnerRootFingerprintResolver"/>), so a replicated event compares owners across
/// nodes either way.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class TaskOwnerFingerprintReplayTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_pre_existing_assignment_and_claim_replay_with_the_fingerprint_absent_and_resolve_through_the_identity_core()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        const string fingerprint = "pre-existing-owner-fingerprint";
        await using (IDocumentSession claimSession = store.LightweightSession())
        {
            OwnerAggregate owner = (await claimSession.Events.AggregateStreamAsync<OwnerAggregate>(
                node.OwnerId, token: cts.Token))!;
            claimSession.Events.Append(
                node.OwnerId, OwnerDecider.ClaimRoot(owner, fingerprint, verified: true, DateTimeOffset.UtcNow));
            await claimSession.SaveChangesAsync(cts.Token);
        }

        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAdded added = TaskDecider.Add(
                taskId, DomainId.New(), "A task assigned and claimed before fingerprint stamping shipped",
                acceptanceCriteria: [], TaskType.Feature, agentContext: null, constraints: null,
                externalReference: null, DateTimeOffset.UtcNow, node.OwnerId);

            // The pre-existing shape, constructed directly rather than through TaskDecider: every
            // field this record carried before this task added AssignedOwnerRootFingerprint/
            // OwnerRootFingerprint, and nothing else — exactly what a stream this platform wrote
            // before today carries forever.
            TaskAssigned assigned = new(taskId, node.OwnerId, UnmetDependencies: [], DateTimeOffset.UtcNow, node.OwnerId);
            TaskClaimed claimed = new(taskId, node.NodeId, node.OwnerId, LeaseGeneration: 1, runId, DateTimeOffset.UtcNow);

            session.Events.StartStream<TaskAggregate>(taskId, added, assigned, claimed);
            await session.SaveChangesAsync(cts.Token);
        }

        // Constructing TaskAssigned/TaskClaimed with the trailing fingerprint parameter omitted
        // still serializes an explicit JSON null for it (TaskLifecycleProjectionBackfill.cs's own
        // shape) — a real pre-existing event has no key at all. Stripping the keys here, directly
        // against the stored row, is what actually exercises System.Text.Json's missing-key
        // fallback to the constructor default rather than its explicit-null path (cycle-1
        // conformance review finding on this test).
        await using (NpgsqlConnection connection = new(postgres.ConnectionString))
        {
            await connection.OpenAsync(cts.Token);
            await using NpgsqlCommand stripKeys = new(
                "update public.mt_events set data = (data - 'assignedOwnerRootFingerprint') - 'ownerRootFingerprint' "
                + "where stream_id = @streamId",
                connection);
            stripKeys.Parameters.AddWithValue("streamId", taskId);
            await stripKeys.ExecuteNonQueryAsync(cts.Token);
        }

        await using (IQuerySession session = store.QuerySession())
        {
            IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(taskId, token: cts.Token);
            IEvent<TaskAssigned> assignedEvent = events.OfType<IEvent<TaskAssigned>>().Single();
            IEvent<TaskClaimed> claimedEvent = events.OfType<IEvent<TaskClaimed>>().Single();

            assignedEvent.Data.AssignedOwnerRootFingerprint.Should().BeNull(
                "this event predates the field and replays exactly as it was written");
            claimedEvent.Data.OwnerRootFingerprint.Should().BeNull(
                "this event predates the field and replays exactly as it was written");

            string? resolvedForAssignment = await OwnerRootFingerprintResolver.ResolveAsync(
                session, assignedEvent.Data.AssignedOwnerId, cts.Token);
            string? resolvedForClaim = await OwnerRootFingerprintResolver.ResolveAsync(
                session, claimedEvent.Data.OwnerId, cts.Token);

            resolvedForAssignment.Should().Be(
                fingerprint, "the identity core's own Guid-to-fingerprint mapping resolves it after the fact");
            resolvedForClaim.Should().Be(fingerprint);
        }
    }
}
