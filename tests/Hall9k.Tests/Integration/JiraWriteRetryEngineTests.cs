using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon;
using Hall9k.Daemon.JiraWrites;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using JasperFx;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The acceptance criterion this daemon sweep is built around (Brian's design, 2026-08-28): an
/// expired or missing twg login is a handled, expected state, and the identical payload succeeds
/// on retry once <c>twg login</c> runs, rather than being lost. Nothing exercised
/// <see cref="JiraWriteRetryEngine.PollOnceAsync"/> before this (independent pre-PR review, cycle
/// 1) — <c>TwgJiraExecutorTests</c> covers only argument construction and failure classification
/// against a fake process, and <c>TaskJiraWriteTests</c> covers only the decider and projections
/// in isolation — so a regression in the sweep's own filter, its payload round-trip, or which
/// write id an outcome gets recorded against could ship green. Against a real Postgres store, the
/// same pattern <c>BacklogTrackingTests</c> and <c>CardPublicationEngineTests</c> already use for
/// this class of daemon sweep.
/// </summary>
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class JiraWriteRetryEngineTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_auth_stuck_write_succeeds_on_retry_once_the_sweep_reattempts_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = OpenStore();

        Guid taskId = await SeedTaskAsync(store, new ExternalReference(WorkItemProvider.Jira, "PROJ-123"), cts.Token);

        RecordingProcessRunner refusingTwg = RecordingProcessRunner.Failing("twg is not authenticated: run 'twg login'");
        await using (IDocumentSession session = store.LightweightSession())
        {
            JiraWriteAttemptResult submitted = await JiraWriteCoordinator.SubmitAsync(
                session, taskId, JiraWriteOperation.Comment, issueKey: null,
                new JiraWritePayload(null, null, "The pull request merged."), JiraProjectKey.None,
                DomainId.New(), new TwgJiraExecutor(refusingTwg.Runner), "/repo", cts.Token);

            submitted.Outcome.Should().Be(JiraWriteOutcome.PendingAuthentication);
        }

        TaskAggregate? stuck = await LoadAsync(store, taskId, cts.Token);
        stuck!.PendingJiraWriteIsAuthFailure.Should().BeTrue("the payload has to survive to be retried, not be lost");

        // A second node's twg, freshly logged in — the identical comment goes through this time.
        RecordingProcessRunner reauthenticatedTwg = RecordingProcessRunner.RespondingTo(
            _ => new ProcessResult(0, """{"key":"PROJ-123"}""", string.Empty));
        NodeContext node = await NewNodeAsync(store, cts.Token);
        JiraWriteRetryEngine engine = new(store, node, reauthenticatedTwg.Runner, NullLogger<JiraWriteRetryEngine>.Instance);

        JiraWriteRetrySweepResult sweep = await engine.PollOnceAsync(cts.Token);

        sweep.Should().Be(new JiraWriteRetrySweepResult(Retried: 1, Succeeded: 1));
        TaskAggregate? resolved = await LoadAsync(store, taskId, cts.Token);
        resolved!.PendingJiraWriteId.Should().BeNull("the retry finished the request it already made rather than losing it");
    }

    [Fact]
    public async Task A_failure_that_is_not_about_authentication_is_left_for_a_freshly_composed_write()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = OpenStore();

        Guid taskId = await SeedTaskAsync(store, new ExternalReference(WorkItemProvider.Jira, "PROJ-456"), cts.Token);

        RecordingProcessRunner refusingTwg = RecordingProcessRunner.Failing("field 'customfield_10010' is required");
        await using (IDocumentSession session = store.LightweightSession())
        {
            JiraWriteAttemptResult submitted = await JiraWriteCoordinator.SubmitAsync(
                session, taskId, JiraWriteOperation.Comment, issueKey: null,
                new JiraWritePayload(null, null, "The pull request merged."), JiraProjectKey.None,
                DomainId.New(), new TwgJiraExecutor(refusingTwg.Runner), "/repo", cts.Token);

            submitted.Outcome.Should().Be(JiraWriteOutcome.Failed);
        }

        RecordingProcessRunner mustNotRun = RecordingProcessRunner.RespondingTo(
            _ => throw new InvalidOperationException("a non-auth failure must not be retried by the sweep"));
        NodeContext node = await NewNodeAsync(store, cts.Token);
        JiraWriteRetryEngine engine = new(store, node, mustNotRun.Runner, NullLogger<JiraWriteRetryEngine>.Instance);

        JiraWriteRetrySweepResult sweep = await engine.PollOnceAsync(cts.Token);

        sweep.Should().Be(new JiraWriteRetrySweepResult(Retried: 0, Succeeded: 0));
    }

    [Fact]
    public async Task A_queued_merge_notice_waits_behind_an_outstanding_write_then_drains_once_it_clears()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = OpenStore();

        Guid taskId = await SeedTaskAsync(store, new ExternalReference(WorkItemProvider.Jira, "PROJ-789"), cts.Token);

        // Closeout tried to submit the merge comment while another write was still outstanding on
        // this task and queued it instead of losing it (CloseoutEngine.QueueJiraMergeNoticeAsync).
        RecordingProcessRunner refusingTwg = RecordingProcessRunner.Failing("twg is not authenticated: run 'twg login'");
        await using (IDocumentSession session = store.LightweightSession())
        {
            JiraWriteAttemptResult submitted = await JiraWriteCoordinator.SubmitAsync(
                session, taskId, JiraWriteOperation.Comment, issueKey: null,
                new JiraWritePayload(null, null, "An earlier comment."), JiraProjectKey.None,
                DomainId.New(), new TwgJiraExecutor(refusingTwg.Runner), "/repo", cts.Token);
            submitted.Outcome.Should().Be(JiraWriteOutcome.PendingAuthentication);

            TaskAggregate outstanding = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.QueueJiraMergeNotice(outstanding, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        RecordingProcessRunner reauthenticatedTwg = RecordingProcessRunner.RespondingTo(
            _ => new ProcessResult(0, """{"key":"PROJ-789"}""", string.Empty));
        NodeContext node = await NewNodeAsync(store, cts.Token);
        JiraWriteRetryEngine engine = new(store, node, reauthenticatedTwg.Runner, NullLogger<JiraWriteRetryEngine>.Instance);

        // First sweep: the outstanding write clears, but the queued notice was read as still
        // blocked (PendingJiraWriteId was set at query time) and is not drained in the same pass.
        JiraWriteRetrySweepResult first = await engine.PollOnceAsync(cts.Token);
        first.Should().Be(new JiraWriteRetrySweepResult(Retried: 1, Succeeded: 1, MergeNoticesDrained: 0));

        TaskAggregate? afterFirst = await LoadAsync(store, taskId, cts.Token);
        afterFirst!.PendingJiraWriteId.Should().BeNull("the outstanding write finished");
        afterFirst.HasQueuedJiraMergeNotice.Should().BeTrue("nothing has attempted the notice yet");

        // Second sweep: nothing blocks the notice any more, so it drains.
        JiraWriteRetrySweepResult second = await engine.PollOnceAsync(cts.Token);
        second.Should().Be(new JiraWriteRetrySweepResult(Retried: 0, Succeeded: 0, MergeNoticesDrained: 1));

        TaskAggregate? afterSecond = await LoadAsync(store, taskId, cts.Token);
        afterSecond!.HasQueuedJiraMergeNotice.Should().BeFalse("the retry sweep drained it exactly once");
        afterSecond.PendingJiraWriteId.Should().BeNull("the merge comment itself went through");
    }

    private DocumentStore OpenStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    private static async Task<NodeContext> NewNodeAsync(DocumentStore store, CancellationToken cancellationToken)
    {
        NodeContext node = new();
        await node.InitializeAsync(store, cancellationToken);
        return node;
    }

    private static async Task<TaskAggregate?> LoadAsync(IDocumentStore store, Guid taskId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        return await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken);
    }

    private static async Task<Guid> SeedTaskAsync(
        IDocumentStore store, ExternalReference externalReference, CancellationToken cancellationToken)
    {
        Guid ownerId = DomainId.New();
        Guid connectionId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();

        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<OwnerAggregate>(ownerId, OwnerDecider.Register(
            ownerId, "Brian Hall", "brian@hallmanac.com", Now));

        session.Events.StartStream<ConnectionAggregate>(connectionId, ConnectionDecider.Register(
            connectionId, ownerId, WorkItemProvider.Jira, "brian@hallmanac.com",
            CredentialReference.EnvironmentVariable("JIRA_TOKEN"), Now, new Uri("https://hall9k.atlassian.net")));

        session.Events.StartStream<ProjectAggregate>(projectId, ProjectDecider.Register(
            projectId, ownerId, connectionId, "hall9k", "/repos/hall9k.git",
            new Uri("https://github.com/Hallmanac/hall9k"), null, Now));

        session.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
            taskId, projectId, "Comment on the linked Jira card", ["A comment is posted"], TaskType.Feature,
            agentContext: null, constraints: null, externalReference, Now, ownerId));

        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }
}
