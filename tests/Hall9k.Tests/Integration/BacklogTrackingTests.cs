using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// Backlog: every published task is tracked automatically. These exercise the two internal
/// helpers <c>h9k task publish</c> calls once it decides a project's backlog policy applies —
/// <see cref="TaskPushToJiraCommand.TryAutoRequestAsync"/> for jira, and
/// <see cref="TaskLinkIssueCommand.LinkAsync"/> for github-issues (the recording half, once the
/// platform's own <c>gh issue create</c> claim has been read back) — against a real Postgres
/// store rather than gh or Jira's HTTP. gh's own read-back is unit-tested with a recorded process
/// at the connector level instead (Hall9k.Tests.Connectors.GitHubWorkItemProviderTests), the same
/// split the rest of this codebase draws between deciders and adapters.
/// </summary>
// TryAutoRequestAsync's successful path rings the doorbell (Hall9k.Cli.Infrastructure.Doorbell),
// which resolves its connection through the ambient HALL9K_CONNECTION_STRING rather than this
// fixture, so the one test that reaches a successful request points it at the fixture for its
// duration. That is process-wide state, same as DatabaseDoctorTests, so this joins the same
// collection to serialize against every other test that redirects it.
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class BacklogTrackingTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Jira_auto_request_is_skipped_with_no_connection_registered()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = OpenStore();

        Guid taskId = await SeedTaskAsync(store, cts.Token);

        TaskPushToJiraCommand.AutoRequestOutcome outcome =
            await TaskPushToJiraCommand.TryAutoRequestAsync(store, taskId, DomainId.New(), cts.Token);

        outcome.Should().Be(TaskPushToJiraCommand.AutoRequestOutcome.NoJiraConnection);

        TaskAggregate? task = await LoadAsync(store, taskId, cts.Token);
        task!.PendingPublicationProvider.Should().BeNull("nothing was requested without a connection to verify against");
    }

    [Fact]
    public async Task Jira_auto_request_asks_for_a_card_once_a_connection_is_registered()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = OpenStore();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<ConnectionAggregate>(DomainId.New(), ConnectionDecider.Register(
                DomainId.New(), DomainId.New(), WorkItemProvider.Jira, "brian@example.com",
                CredentialReference.EnvironmentVariable("JIRA_TOKEN"), Now,
                new Uri("https://hall9k.atlassian.net")));
            await session.SaveChangesAsync(cts.Token);
        }

        Guid taskId = await SeedTaskAsync(store, cts.Token);

        // A successful request rings the doorbell (Hall9k.Cli.Infrastructure.Doorbell), which
        // resolves its connection off HALL9K_CONNECTION_STRING rather than this fixture, so it
        // has to be pointed at the fixture for the one call below that actually succeeds.
        string? previousConnectionString = Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, postgres.ConnectionString);
        TaskPushToJiraCommand.AutoRequestOutcome outcome;
        try
        {
            outcome = await TaskPushToJiraCommand.TryAutoRequestAsync(store, taskId, DomainId.New(), cts.Token);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previousConnectionString);
        }

        outcome.Should().Be(TaskPushToJiraCommand.AutoRequestOutcome.Requested);

        TaskAggregate? task = await LoadAsync(store, taskId, cts.Token);
        task!.PendingPublicationProvider.Should().Be(WorkItemProvider.Jira);

        // TaskPublishCommand.TrackInBacklogAsync calls this after the publish transaction has
        // already committed, and its own doc comment promises the failure is "reported and
        // swallowed" rather than left to escape. A second outstanding request on the same task
        // is exactly the shape that promise has to hold for: TaskDecider.RequestWorkItemPublication
        // refuses it with DomainConflictException rather than silently no-opping, and this asserts
        // the method lets that through as a DomainException the caller can catch — reusing the
        // connection above rather than registering a second one, since only one Jira connection is
        // supported per install and a second registration in this shared-database test class would
        // make WorkItemConnections.FindJiraConnectionAsync itself ambiguous. The refusal comes from
        // TaskDecider before RequestAsync ever reaches the doorbell, so this does not need the
        // connection string redirected the way the first call above did.
        Func<Task> secondRequest = () => TaskPushToJiraCommand.TryAutoRequestAsync(store, taskId, DomainId.New(), cts.Token);

        (await secondRequest.Should().ThrowAsync<DomainConflictException>()).Which.Message
            .Should().Contain("already has a jira publication outstanding");
    }

    [Fact]
    public async Task Linking_a_freshly_created_issue_records_it_and_a_repeat_is_quiet()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = OpenStore();

        Guid taskId = await SeedTaskAsync(store, cts.Token);
        ImportedWorkItem issue = new(
            new ExternalReference(WorkItemProvider.GitHub, "Hallmanac/hall9k#77"),
            "Track every published task",
            null,
            WorkItemStatus.Open,
            new Uri("https://github.com/Hallmanac/hall9k/issues/77"),
            Now);

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskLinkIssueCommand.LinkOutcome outcome =
                await TaskLinkIssueCommand.LinkAsync(session, taskId, issue, DomainId.New(), cts.Token);
            outcome.Should().Be(TaskLinkIssueCommand.LinkOutcome.Linked);
            await session.SaveChangesAsync(cts.Token);
        }

        TaskAggregate? task = await LoadAsync(store, taskId, cts.Token);
        task!.ExternalReference.Should().Be(issue.Reference);

        await using (IDocumentSession session = store.LightweightSession())
        {
            // An agent (or, here, TaskPublishCommand) that could not tell whether an earlier
            // attempt landed calls this again; the second call must not throw or double-append.
            TaskLinkIssueCommand.LinkOutcome outcome =
                await TaskLinkIssueCommand.LinkAsync(session, taskId, issue, DomainId.New(), cts.Token);
            outcome.Should().Be(TaskLinkIssueCommand.LinkOutcome.AlreadyLinked);
            await session.SaveChangesAsync(cts.Token);
        }
    }

    [Fact]
    public async Task Linking_an_issue_another_live_task_already_carries_is_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = OpenStore();

        ExternalReference reference = new(WorkItemProvider.GitHub, "Hallmanac/hall9k#88");
        Guid firstTaskId = await SeedTaskAsync(store, cts.Token);
        await using (IDocumentSession session = store.LightweightSession())
        {
            await TaskLinkIssueCommand.LinkAsync(
                session, firstTaskId,
                new ImportedWorkItem(reference, "First", null, WorkItemStatus.Open, null, Now),
                DomainId.New(), cts.Token);
            await session.SaveChangesAsync(cts.Token);
        }

        Guid secondTaskId = await SeedTaskAsync(store, cts.Token);
        await using (IDocumentSession session = store.LightweightSession())
        {
            Func<Task> secondLink = () => TaskLinkIssueCommand.LinkAsync(
                session, secondTaskId,
                new ImportedWorkItem(reference, "Second", null, WorkItemStatus.Open, null, Now),
                DomainId.New(), cts.Token);

            (await secondLink.Should().ThrowAsync<DomainConflictException>()).Which.Message
                .Should().Contain("github:Hallmanac/hall9k#88");
        }
    }

    private DocumentStore OpenStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    private static async Task<TaskAggregate?> LoadAsync(IDocumentStore store, Guid taskId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        return await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken);
    }

    private static async Task<Guid> SeedTaskAsync(IDocumentStore store, CancellationToken cancellationToken)
    {
        Guid ownerId = DomainId.New();
        Guid connectionId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();

        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<OwnerAggregate>(ownerId, OwnerDecider.Register(
            ownerId, "Brian Hall", "brian@hallmanac.com", Now));

        session.Events.StartStream<ConnectionAggregate>(connectionId, ConnectionDecider.Register(
            connectionId, ownerId, WorkItemProvider.GitHub, "Hallmanac", CredentialReference.GhCli, Now));

        session.Events.StartStream<ProjectAggregate>(projectId, ProjectDecider.Register(
            projectId, ownerId, connectionId, "hall9k", "/repos/hall9k.git",
            new Uri("https://github.com/Hallmanac/hall9k"), null, Now));

        session.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
            taskId, projectId, "Track every published task automatically",
            ["A project setting declares the backlog policy"], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null, Now, ownerId));

        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }
}
