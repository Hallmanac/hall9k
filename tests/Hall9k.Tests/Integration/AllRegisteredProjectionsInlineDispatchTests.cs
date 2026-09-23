using FluentAssertions;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Courier;
using Hall9k.Domain.Features.Decision;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Invite;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Trust;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// Task 29b0ca1a (Marten 9 security upgrade): Marten 9's projection dispatch needs a source-
/// generated partial-class companion for every <c>SingleStreamProjection</c> subclass — there is
/// no reflection fallback, and a projection class the generator never ran against throws
/// <c>InvalidProjectionException</c> the first time an inline session tries to apply it. Every DB-
/// free unit test in this suite exercises a projection's own <c>Create</c>/<c>Apply</c> methods
/// directly and would never notice a missing dispatcher; only a real store, built exactly through
/// <see cref="MartenConfiguration.ConfigureHall9k"/> the way every production store is, can prove
/// the twenty registered projections actually dispatch. One event per registered projection
/// (<see cref="TaskAdded"/> and <see cref="RunDispatched"/> each drive two, so eighteen events
/// cover all twenty), all inline, in one session.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class AllRegisteredProjectionsInlineDispatchTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Every_registered_projection_opens_and_writes_inline_with_no_missing_dispatcher()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Guid ownerId = DomainId.New();
        Guid nodeId = DomainId.New();
        Guid connectionId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid ideaId = DomainId.New();
        Guid epicId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid courierRunId = DomainId.New();
        Guid decisionId = DomainId.New();
        Guid learningId = DomainId.New();
        Guid inviteId = DomainId.New();
        Guid legacyAdoptionStreamId = DomainId.New();
        Guid messageStreamId = DomainId.New();
        Guid inboxStreamId = DomainId.New();
        Guid orchestratorPresenceStreamId = DomainId.New();
        Guid unverifiedWriteStreamId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream(ownerId, new OwnerRegistered(ownerId, "projection-owner", null, now));
            session.Events.StartStream(nodeId, new NodeRegistered(nodeId, ownerId, "machine", "macOS", now));
            session.Events.StartStream(connectionId,
                new ConnectionRegistered(connectionId, ownerId, WorkItemProvider.GitHub, "acct-1", CredentialReference.GhCli, now));
            session.Events.StartStream(projectId, [
                new ProjectRegistered(projectId, ownerId, connectionId, "hall9k", "/repos/hall9k.git", null, "main", now),
                new ProjectGitHubAccessObserved(projectId, 1, "octocat", GitHubRepositoryRole.Write, now),
            ]);
            session.Events.StartStream(ideaId, new IdeaCaptured(ideaId, ownerId, "a captured idea", projectId, now));
            session.Events.StartStream(epicId, new EpicAdded(epicId, projectId, "an epic", now, ownerId));
            session.Events.StartStream(taskId, new TaskAdded(
                taskId, projectId, "objective", [], TaskType.Chore, null, null, null, now, ownerId));
            session.Events.StartStream(runId, new RunDispatched(
                runId, taskId, nodeId, ownerId, 1, DomainId.New(), "/worktrees/w", "task/w",
                ExecutorMode.Subscription, now));
            session.Events.StartStream(courierRunId, new CourierRunDispatched(courierRunId, projectId, nodeId, AgentModel.Sonnet, now));
            session.Events.StartStream(decisionId, new DecisionRecorded(
                decisionId, KnowledgeScope.Project, projectId, "a decision", null, [],
                RecordedProvenance.FromShell(ownerId), now, null));
            session.Events.StartStream(learningId, new LearningRecorded(
                learningId, KnowledgeScope.Project, projectId, "a lesson", RecordedProvenance.FromShell(ownerId), now));
            session.Events.StartStream(inviteId, new InviteMinted(
                inviteId, nodeId, "root-fingerprint", InviteClaimKind.NodeOfOwner, null, null, null,
                "secret", "secret-hash", now, now.AddDays(1)));
            session.Events.StartStream(legacyAdoptionStreamId, new LegacyMessageAdoptionAssigned(projectId, now));
            session.Events.StartStream(messageStreamId,
                new MessageQueued(nodeId, 1, "owner", "recipient", null, "note", "body", now, projectId));
            session.Events.StartStream(inboxStreamId, new InboxCursorAdvanced(nodeId, projectId, 1, now));
            session.Events.StartStream(orchestratorPresenceStreamId,
                new OrchestratorLaunched(nodeId, projectId, "orchestrator", 4242, "claude", null, now));
            session.Events.StartStream(unverifiedWriteStreamId,
                new UnverifiedLedgerWriteObserved(projectId, "vouch", nodeId.ToString(), "root-fingerprint", "unverified", now));

            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession query = store.QuerySession())
        {
            (await query.LoadAsync<OwnerDetails>(ownerId, cts.Token)).Should().NotBeNull();
            (await query.LoadAsync<NodeDetails>(nodeId, cts.Token)).Should().NotBeNull();
            (await query.LoadAsync<ConnectionDetails>(connectionId, cts.Token)).Should().NotBeNull();
            (await query.LoadAsync<ProjectDetails>(projectId, cts.Token)).Should().NotBeNull();
            (await query.LoadAsync<ProjectGitHubMembers>(projectId, cts.Token)).Should().NotBeNull();
            (await query.LoadAsync<IdeaDetails>(ideaId, cts.Token)).Should().NotBeNull();
            (await query.LoadAsync<EpicDetails>(epicId, cts.Token)).Should().NotBeNull();
            (await query.LoadAsync<TaskDetails>(taskId, cts.Token)).Should().NotBeNull();
            (await query.LoadAsync<TaskListItem>(taskId, cts.Token)).Should().NotBeNull();
            (await query.LoadAsync<RunDetails>(runId, cts.Token)).Should().NotBeNull();
            (await query.LoadAsync<RunListItem>(runId, cts.Token)).Should().NotBeNull();
            (await query.LoadAsync<CourierRunDetails>(courierRunId, cts.Token)).Should().NotBeNull();
            (await query.LoadAsync<DecisionDetails>(decisionId, cts.Token)).Should().NotBeNull();
            (await query.LoadAsync<LearningDetails>(learningId, cts.Token)).Should().NotBeNull();
            (await query.LoadAsync<InviteDetails>(inviteId, cts.Token)).Should().NotBeNull();
            (await query.LoadAsync<LegacyMessageAdoptionDetails>(legacyAdoptionStreamId, cts.Token)).Should().NotBeNull();
            (await query.LoadAsync<MessageDetails>(messageStreamId, cts.Token)).Should().NotBeNull();
            (await query.LoadAsync<MessageInboxDetails>(inboxStreamId, cts.Token)).Should().NotBeNull();
            (await query.LoadAsync<OrchestratorPresenceDetails>(orchestratorPresenceStreamId, cts.Token)).Should().NotBeNull();
            (await query.LoadAsync<UnverifiedLedgerWriteDetails>(unverifiedWriteStreamId, cts.Token)).Should().NotBeNull();
        }
    }
}
