using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Messaging;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Marten;
using Marten.Events;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <c>h9k status</c>'s cooperative-take pane naming this node's own outstanding ask even when the
/// holder never received or processed the request at all, so <c>TaskListItem.PendingTakeRequestedByNodeId</c>
/// stays null forever — the case the take-timeout wording exists for hardest (independent pre-PR
/// review, cycle 5, adversarial lens, medium). <see cref="StatusCommand.WriteCooperativeTakeAsync"/>
/// now falls back to <see cref="PendingOwnAskLookup"/>, which reads only this node's own outbox.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection("Hall9kHome")]
[Trait("Category", "Hall9kHome")]
public sealed class StatusCommandCooperativeTakeOwnAskTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
    private const string RepositoryPath = "/does/not/matter/on/a/fake/ledger";

    private readonly PostgresFixture _postgres;
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"hall9k-status-own-ask-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    public StatusCommandCooperativeTakeOwnAskTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", _home);
        await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();
    }

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", _previousHome);
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task An_unreplicated_own_ask_still_shows_up_in_the_pane()
    {
        (Guid taskId, Guid myNodeId, Guid holderNodeId) = await SeedTaskHeldByAnotherNodeAsync();

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        ClaimEnvelopeCodec.ClaimRequestRecord request = new(
            taskId, myNodeId, DomainId.New(), "my-fingerprint", "Picking this back up.", null);
        await MessageOutbox.QueueAsync(
            session, myNodeId, DomainId.New(), "my-fingerprint", MessageAudience.Node(holderNodeId),
            taskId.ToString(), MessageKind.ClaimRequest, ClaimEnvelopeCodec.Encode(request), Now, CancellationToken.None);

        string output;
        await using (IQuerySession querySession = _postgres.Store.QuerySession())
        {
            output = await ScopedAnsiConsoleCapture.CaptureAsync(
                () => StatusCommand.WriteCooperativeTakeAsync(querySession, Now, CancellationToken.None));
        }

        output.Should().Contain("Take requested").And.Contain("Picking this back up.");
    }

    [Fact]
    public async Task A_grant_already_replicated_on_the_domain_stream_suppresses_the_fallback_even_without_a_reply_message()
    {
        // Regression guard (self-review, this branch): the fallback's own first draft skipped only
        // when TaskListItem.PendingTakeRequestedByNodeId was still set — but a grant clears that
        // same field the moment it replicates, which reads identically to "never asked". Without
        // also checking the domain stream's own LastGrantedAt/LastTakeRefusedAt, a request already
        // granted here would still print as waiting for as long as the separate ClaimGranted reply
        // message happened to lag behind the domain-stream replication that answered it.
        (Guid taskId, Guid myNodeId, Guid holderNodeId) = await SeedTaskHeldByAnotherNodeAsync();

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Guid requesterOwnerId = DomainId.New();
        ClaimEnvelopeCodec.ClaimRequestRecord request = new(
            taskId, myNodeId, requesterOwnerId, "my-fingerprint", "Picking this back up.", null);
        await MessageOutbox.QueueAsync(
            session, myNodeId, DomainId.New(), "my-fingerprint", MessageAudience.Node(holderNodeId),
            taskId.ToString(), MessageKind.ClaimRequest, ClaimEnvelopeCodec.Encode(request), Now, CancellationToken.None);

        // The domain-stream side of the answer replicated onto this node (a TaskHolderReleased
        // recording the grant) with no ClaimGranted reply message ever separately arriving.
        StreamState fence = (await session.Events.FetchStreamStateAsync(taskId, CancellationToken.None))!;
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, version: fence.Version, token: CancellationToken.None))!;
        TaskHolderReleased granted = TaskDecider.GrantTake(task, myNodeId, requesterOwnerId, Now.AddMinutes(1));
        session.Events.Append(taskId, expectedVersion: fence.Version + 1, granted);
        await session.SaveChangesAsync(CancellationToken.None);

        string output;
        await using (IQuerySession querySession = _postgres.Store.QuerySession())
        {
            output = await ScopedAnsiConsoleCapture.CaptureAsync(
                () => StatusCommand.WriteCooperativeTakeAsync(querySession, Now.AddMinutes(2), CancellationToken.None));
        }

        output.Should().BeEmpty(
            "the grant already replicated onto this task's own domain stream, even though the ClaimGranted "
            + "reply message never separately arrived");
    }

    private async Task<(Guid TaskId, Guid MyNodeId, Guid HolderNodeId)> SeedTaskHeldByAnotherNodeAsync()
    {
        await using IDocumentSession bootstrapSession = _postgres.Store.LightweightSession();
        BootstrapContext context = await NodeBootstrap.EnsureAsync(bootstrapSession, CancellationToken.None);
        await bootstrapSession.SaveChangesAsync(CancellationToken.None);

        Guid projectId = DomainId.New();
        ProjectDetails project = new()
        {
            Id = projectId, Name = $"take-{projectId:N}"[..12], RepositoryPath = RepositoryPath, BaseBranch = "main",
            BranchNameTemplate = BranchNameTemplate.Default,
        };
        await using IDocumentSession projectSession = _postgres.Store.LightweightSession();
        projectSession.Store(project);
        await projectSession.SaveChangesAsync(CancellationToken.None);

        Guid taskId = DomainId.New();
        Guid holderNodeId = DomainId.New();
        await using IDocumentSession taskSession = _postgres.Store.LightweightSession();
        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(taskId, projectId, "Close me out", ["done"], TaskType.Chore, null, null, null, Now, context.OwnerId),
            context.OwnerId, Now);
        var claimed = TaskDecider.Claim(task, holderNodeId, context.OwnerId, DomainId.New(), Now.AddHours(-2));
        taskSession.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
        await taskSession.SaveChangesAsync(CancellationToken.None);

        return (taskId, context.NodeId, holderNodeId);
    }
}
