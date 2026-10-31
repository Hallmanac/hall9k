using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// A Linear connection that never recorded the viewer's user id (one registered by hand, or by an
/// earlier build). It is its own class because an install holds a single Linear connection, and the
/// other class's connection records the id.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class LinearClaimGateWithoutViewerIdTests : IClassFixture<PostgresFixture>, IDisposable
{
    private const string KeyVariable = "HALL9K_TEST_LINEAR_NO_VIEWER_ID_KEY";
    private static readonly ExternalReference Issue = new(WorkItemProvider.Linear, "ENG-123");

    private readonly PostgresFixture postgres;

    public LinearClaimGateWithoutViewerIdTests(PostgresFixture postgres)
    {
        this.postgres = postgres;
        Environment.SetEnvironmentVariable(KeyVariable, "lin_api_test_key");
    }

    public void Dispose() => Environment.SetEnvironmentVariable(KeyVariable, null);

    [Fact]
    public async Task A_connection_with_no_viewer_id_is_unreadable_names_the_remedy_and_makes_no_call()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await RegisterWithoutViewerIdAsync(cts.Token);
        RecordingLinearTracker tracker = RecordingLinearTracker.HeldBy(
            RecordingLinearTracker.ViewerId, RecordingLinearTracker.ViewerName);
        TrackerClaimGate gate = new(
            RecordingProcessRunner.NeverInvoked(), FakeJiraRequester.NeverInvoked(), linearRequester: tracker.Requester);

        TrackerClaimDecision decision = await gate.CheckAsync(
            postgres.Store, ClaimGate.TrackerAssignee, Issue, Path.GetTempPath(), cts.Token);

        decision.Verdict.Should().Be(TrackerClaimVerdict.Unreadable);
        decision.Lever.Should().Contain("h9k connection add linear");
        decision.RefusalLine.Should().Contain("Linear could not be read").And.Contain("ENG-123");
        tracker.Requests.Should().BeEmpty("no viewer read is made, and the issue is not read without an id to compare");
    }

    [Fact]
    public async Task A_take_on_a_connection_with_no_viewer_id_writes_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await RegisterWithoutViewerIdAsync(cts.Token);
        RecordingLinearTracker tracker = RecordingLinearTracker.Unassigned();
        TrackerClaimGate gate = new(
            RecordingProcessRunner.NeverInvoked(), FakeJiraRequester.NeverInvoked(), linearRequester: tracker.Requester);

        TrackerTake take = await new TrackerAssignmentTake(gate, RecordingProcessRunner.NeverInvoked()).TakeAsync(
            postgres.Store, ClaimGate.TrackerAssignee, Issue, Path.GetTempPath(), cts.Token);

        take.Verdict.Should().Be(TrackerTakeVerdict.Unreadable);
        tracker.Requests.Should().BeEmpty();
    }

    private async Task RegisterWithoutViewerIdAsync(CancellationToken cancellationToken)
    {
        await using IDocumentSession session = postgres.Store.LightweightSession();
        if (await WorkItemConnections.FindLinearConnectionAsync(session, cancellationToken) is not null)
        {
            return;
        }

        Guid connectionId = DomainId.New();
        session.Events.StartStream<ConnectionAggregate>(
            connectionId,
            ConnectionDecider.Register(
                connectionId, DomainId.New(), WorkItemProvider.Linear, "Brian Hall",
                CredentialReference.EnvironmentVariable(KeyVariable),
                new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero), new Uri("https://linear.app/hall9k")));
        await session.SaveChangesAsync(cancellationToken);
    }
}
