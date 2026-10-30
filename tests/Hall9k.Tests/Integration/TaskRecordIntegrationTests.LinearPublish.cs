using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Credentials;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// What <c>h9k task publish</c> does after it commits on a project whose backlog policy is linear:
/// the one create, the read-back and link, the task record on every outcome, and the go signal that
/// waits for the link. It shares this class's container rather than starting its own, and drives the
/// tracking step through its two seams: a recorded Linear requester, and a <see cref="FakeLedger"/>, so
/// nothing here writes git, calls GitHub, or reaches a live Linear workspace (Brian, 2026-09-13).
/// </summary>
public sealed partial class TaskRecordIntegrationTests
{
    private const string LinearKeyFileName = "linear-publish-integration-key";
    private static readonly Uri LinearWorkspace = new("https://linear.app/hall9k");

    // One container serves every test of this class and a Linear issue is one live task, so each
    // test files its own identifier rather than sharing one.
    private static int _linearIssueNumber = 100;

    private sealed class RecordedLinear
    {
        private readonly Func<string, string, LinearResponse> _answer;

        private RecordedLinear(Func<string, string, LinearResponse> answer)
        {
            Identifier = $"ENG-{Interlocked.Increment(ref _linearIssueNumber)}";
            _answer = answer;
        }

        public string Identifier { get; }

        public List<string> CreateBodies { get; } = [];

        public int Calls { get; private set; }

        public ExternalReference Reference => new(WorkItemProvider.Linear, Identifier);

        public LinearWorkItemProvider Provider => new(
            LinearAccount.Registered(LinearWorkspace, "Brian Hall", CredentialReference.File(LinearKeyFileName)),
            (request, _) =>
            {
                Calls++;
                bool create = request.JsonBody.Contains("HallIssueCreate", StringComparison.Ordinal);
                if (create)
                {
                    CreateBodies.Add(request.JsonBody);
                }

                return Task.FromResult(_answer(create ? "create" : "read", Identifier));
            });

        public static string Created(string identifier) =>
            "{\"data\":{\"issueCreate\":{\"success\":true,\"issue\":{\"identifier\":\"" + identifier + "\"}}}}";

        public static string Issue(string identifier) =>
            "{\"data\":{\"issue\":{\"identifier\":\"" + identifier + "\",\"title\":\"Publishing creates the issue\","
            + "\"description\":\"d\",\"url\":\"https://linear.app/hall9k/issue/" + identifier + "/x\","
            + "\"previousIdentifiers\":[],\"state\":{\"name\":\"Backlog\",\"type\":\"backlog\"}}}}";

        /// <summary>Linear creates the issue and reads it back.</summary>
        public static RecordedLinear Creating() =>
            new((kind, identifier) => new LinearResponse(200, kind == "create" ? Created(identifier) : Issue(identifier)));

        /// <summary>The create is answered with <paramref name="status"/> and <paramref name="body"/>.</summary>
        public static RecordedLinear RefusingCreateWith(int status, string body) =>
            new((kind, identifier) => kind == "create"
                ? new LinearResponse(status, body)
                : new LinearResponse(200, Issue(identifier)));

        /// <summary>The create succeeds and the read-back is answered with <paramref name="body"/>.</summary>
        public static RecordedLinear CreatingButFailingToReadBack(string body) =>
            new((kind, identifier) => kind == "create"
                ? new LinearResponse(200, Created(identifier))
                : new LinearResponse(200, body));
    }

    private sealed record LinearInstall(Install Install, OwnerDetails Owner, BootstrapContext Context);

    private static void WriteLinearKey()
    {
        Directory.CreateDirectory(CredentialVault.Directory);
        File.WriteAllText(CredentialVault.FileFor(LinearKeyFileName), "lin_api_publish_integration_key");
    }

    private static async Task<LinearInstall> SeedLinearAsync(DocumentStore store, CancellationToken cancellationToken)
    {
        Guid ownerId = DomainId.New();
        Guid connectionId = DomainId.New();
        Guid projectId = DomainId.New();

        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<OwnerAggregate>(ownerId, OwnerDecider.Register(
            ownerId, "Brian Hall", "brian@hallmanac.com", Now));
        session.Events.StartStream<ConnectionAggregate>(connectionId, ConnectionDecider.Register(
            connectionId, ownerId, WorkItemProvider.Linear, "hall9k", CredentialReference.File(LinearKeyFileName), Now,
            LinearWorkspace));
        ProjectRegistered registered = ProjectDecider.Register(
            projectId, ownerId, connectionId, "hall9k", RepositoryPath, null, null, Now);
        session.Events.StartStream<ProjectAggregate>(projectId, registered);

        ProjectAggregate project = new();
        project.Apply(registered);
        session.Events.Append(projectId, ProjectDecider.ChangeSettings(
            project,
            Optional<IReadOnlyList<VerifyCommand>>.None,
            Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None,
            Now,
            ownerId,
            backlogPolicy: Optional<BacklogPolicy>.Of(BacklogPolicy.Linear),
            linearTeam: Optional<LinearTeamKey>.Of(LinearTeamKey.Parse("ENG"))));
        await session.SaveChangesAsync(cancellationToken);

        OwnerDetails owner = (await session.LoadAsync<OwnerDetails>(ownerId, cancellationToken))!;
        Install install = new(ownerId, projectId, DomainId.New());
        return new LinearInstall(install, owner, new BootstrapContext(ownerId, install.NodeId, connectionId));
    }

    /// <summary>Publishes the way h9k task publish does under the linear policy, and returns what the tracking step is handed.</summary>
    private static async Task<(TaskAggregate Task, TaskPublished Published, ProjectDetails Project)> PublishUnderLinearAsync(
        DocumentStore store, LinearInstall linear, Guid taskId, bool noExistingItem, bool untracked,
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken))!;
        ProjectDetails project = (await session.LoadAsync<ProjectDetails>(linear.Install.ProjectId, cancellationToken))!;
        TaskDependencyGraph graph = await TaskDependencyQuery.LoadGraphAsync(session, task.BlockedBy, cancellationToken);
        TaskPublished published = TaskDecider.Publish(
            task, graph, Now, linear.Install.OwnerId, project.BacklogPolicy, noExistingItem, untracked);
        session.Events.Append(taskId, published);
        task.Apply(published);
        await session.SaveChangesAsync(cancellationToken);
        return (task, published, project);
    }

    private static async Task<(LinearTrackingResult Result, FakeLedger Ledger, string Output, Guid TaskId, LinearInstall Linear)>
        TrackAsync(
            DocumentStore store,
            RecordedLinear linear,
            CancellationToken cancellationToken,
            bool noExistingItem = true,
            bool untracked = false,
            bool queue = false,
            ExternalReference? alreadyLinked = null,
            ExternalReference? heldByAnotherTask = null,
            string? objective = null,
            string? agentContext = null)
    {
        LinearInstall seeded = await SeedLinearAsync(store, cancellationToken);
        if (heldByAnotherTask is not null)
        {
            await AddTaskAsync(
                store, seeded.Install, "Another task already holds that issue", ["It does"], null, cancellationToken,
                externalReference: heldByAnotherTask);
        }

        Guid taskId = await AddTaskAsync(
            store, seeded.Install, objective ?? "Publishing creates the issue", ["It is created", "It is linked"],
            agentContext, cancellationToken, externalReference: alreadyLinked);
        (TaskAggregate task, TaskPublished published, ProjectDetails project) = await PublishUnderLinearAsync(
            store, seeded, taskId, noExistingItem, untracked, cancellationToken);

        FakeLedger ledger = new();
        using ScopedAnsiConsoleCapture capture = ScopedAnsiConsoleCapture.Begin();
        LinearPublishTarget target = new(linear.Provider, new LinearTeam("team-1", "ENG", "Engineering", "state-backlog"));
        LinearTrackingResult result = await LinearPublishTracking.TrackAsync(
            store, taskId, task, published, project, seeded.Context,
            published.NoExistingItemAttested ? target : null,
            queue && published.NoExistingItemAttested ? seeded.Owner : null,
            ledger, cancellationToken);
        return (result, ledger, capture.Text, taskId, seeded);
    }

    private static async Task<Type[]> StreamAsync(DocumentStore store, Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        return [.. (await session.Events.FetchStreamAsync(taskId, token: cancellationToken)).Select(@event => @event.Data.GetType())];
    }

    private static TaskRecord RecordIn(FakeLedger ledger, Guid taskId)
    {
        LedgerWriteRequest write = ledger.Writes.Should().ContainSingle().Subject;
        write.Path.Should().Be(LedgerRefRegistry.RecordPath(taskId));
        return TaskRecord.TryParse(write.Content)!;
    }

    private static string ShortId(Guid taskId) => TaskListCommand.ShortId(taskId);

    [Fact]
    public async Task A_created_issue_is_read_back_and_linked_and_the_task_record_names_it()
    {
        using ScopedTestHome home = new();
        WriteLinearKey();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        RecordedLinear linear = RecordedLinear.Creating();

        (LinearTrackingResult result, FakeLedger ledger, string output, Guid taskId, _) =
            await TrackAsync(store, linear, cts.Token);

        result.Linked.Should().BeTrue(output);
        linear.CreateBodies.Should().ContainSingle("the create is sent once");
        await using IQuerySession session = store.QuerySession();
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        task.ExternalReference.Should().Be(linear.Reference);
        RecordIn(ledger, taskId).ExternalReference.Should().Be(task.ExternalReference);
        output.Should().Contain($"created and linked linear:{linear.Identifier}");
    }

    [Fact]
    public async Task The_issueCreate_carries_the_team_and_state_the_objective_as_title_and_the_composed_description()
    {
        using ScopedTestHome home = new();
        WriteLinearKey();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        RecordedLinear linear = RecordedLinear.Creating();

        await TrackAsync(
            postgres.Store, linear, cts.Token,
            agentContext: "Why this matters.\n\nDiscovery workspace (research notes, gathered files, and prototypes from "
                + "before this was a task; may be empty):\n/Users/someone/.hall9k/projects/x/ideas/y/workspace");

        linear.CreateBodies.Should().ContainSingle().Which.Should().Be(
            "{\"query\":\"mutation HallIssueCreate($input: IssueCreateInput!) "
            + "{ issueCreate(input: $input) { success issue { identifier } } }\","
            + "\"variables\":{\"input\":{\"teamId\":\"team-1\",\"stateId\":\"state-backlog\","
            + "\"title\":\"Publishing creates the issue\","
            + "\"description\":\"Why this matters.\\n\\n## Acceptance criteria\\n\\n- [ ] It is created\\n- [ ] It is linked\"}}}",
            "no assignee, label, project, or cycle, and no discovery-workspace path");
    }

    [Fact]
    public async Task A_title_over_255_characters_is_cut_and_the_description_carries_the_full_objective()
    {
        using ScopedTestHome home = new();
        WriteLinearKey();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        RecordedLinear linear = RecordedLinear.Creating();
        string objective = string.Join(' ', Enumerable.Repeat("Publishing creates the issue", 12));

        await TrackAsync(postgres.Store, linear, cts.Token, objective: objective);

        using System.Text.Json.JsonDocument body = System.Text.Json.JsonDocument.Parse(linear.CreateBodies.Single());
        System.Text.Json.JsonElement input = body.RootElement.GetProperty("variables").GetProperty("input");
        input.GetProperty("title").GetString()!.Length.Should().Be(255);
        input.GetProperty("description").GetString().Should().StartWith("## Objective\n\n" + objective);
    }

    [Fact]
    public async Task A_refused_create_names_a_by_hand_create_then_the_link_and_still_writes_the_record()
    {
        using ScopedTestHome home = new();
        WriteLinearKey();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        RecordedLinear linear = RecordedLinear.RefusingCreateWith(
            200, """{"errors":[{"message":"teamId must be a UUID"}],"data":null}""");

        (LinearTrackingResult result, FakeLedger ledger, string output, Guid taskId, _) =
            await TrackAsync(store, linear, cts.Token, queue: true);

        result.Linked.Should().BeFalse();
        result.QueueDeferred.Should().BeTrue();
        result.Queued.Should().BeNull("no issue was linked, so the task stays published and not queued");
        linear.CreateBodies.Should().ContainSingle("a refused create is never retried");
        output.Should().Contain("Linear refused to create the issue").And.Contain("Create it by hand")
            .And.Contain($"h9k task link-linear {ShortId(taskId)} <identifier>")
            .And.Contain($"h9k task queue {ShortId(taskId)}");
        RecordIn(ledger, taskId).ExternalReference.Should().BeNull();
        (await StreamAsync(store, taskId, cts.Token)).Should().NotContain(typeof(TaskAssigned));
    }

    [Fact]
    public async Task An_unknown_outcome_says_the_issue_may_exist_and_names_a_search_before_any_link_or_create()
    {
        using ScopedTestHome home = new();
        WriteLinearKey();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        RecordedLinear linear = RecordedLinear.RefusingCreateWith(503, "Service Unavailable");

        (LinearTrackingResult result, FakeLedger ledger, string output, Guid taskId, _) =
            await TrackAsync(store, linear, cts.Token);

        result.Linked.Should().BeFalse();
        linear.CreateBodies.Should().ContainSingle("an unknown outcome is never retried by the platform");
        output.Should().Contain("may exist").And.Contain("searching team ENG")
            .And.Contain($"h9k task link-linear {ShortId(taskId)} <identifier>");
        output.Should().NotContain("Create it by hand and link");
        RecordIn(ledger, taskId);
    }

    [Fact]
    public async Task A_read_back_failure_after_Linear_returned_an_identifier_names_link_linear_with_it()
    {
        using ScopedTestHome home = new();
        WriteLinearKey();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        RecordedLinear linear = RecordedLinear.CreatingButFailingToReadBack(
            """{"errors":[{"message":"Entity not found"}],"data":null}""");

        (LinearTrackingResult result, FakeLedger ledger, string output, Guid taskId, _) =
            await TrackAsync(store, linear, cts.Token);

        result.Linked.Should().BeFalse();
        linear.CreateBodies.Should().ContainSingle();
        output.Should().Contain($"Created {linear.Identifier}").And.Contain($"h9k task link-linear {ShortId(taskId)} {linear.Identifier}");
        RecordIn(ledger, taskId).ExternalReference.Should().BeNull();
        (await StreamAsync(store, taskId, cts.Token)).Should().NotContain(typeof(WorkItemLinked));
    }

    [Fact]
    public async Task A_link_failure_after_the_read_back_names_link_linear_with_the_identifier_and_never_files_a_second_issue()
    {
        using ScopedTestHome home = new();
        WriteLinearKey();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        RecordedLinear linear = RecordedLinear.Creating();

        // Another live task of the project already holds the issue, which the link step refuses.
        (LinearTrackingResult result, FakeLedger ledger, string output, Guid taskId, _) = await TrackAsync(
            store, linear, cts.Token, heldByAnotherTask: linear.Reference);

        result.Linked.Should().BeFalse();
        linear.CreateBodies.Should().ContainSingle();
        output.Should().Contain("could not link it").And.Contain($"h9k task link-linear {ShortId(taskId)} {linear.Identifier}");
        RecordIn(ledger, taskId);
    }

    [Fact]
    public async Task A_task_that_already_carries_an_issue_makes_no_Linear_call_and_still_gets_its_record()
    {
        using ScopedTestHome home = new();
        WriteLinearKey();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        RecordedLinear linear = RecordedLinear.Creating();

        (LinearTrackingResult result, FakeLedger ledger, _, Guid taskId, _) = await TrackAsync(
            postgres.Store, linear, cts.Token, noExistingItem: false,
            alreadyLinked: new ExternalReference(WorkItemProvider.Linear, "ENG-7"));

        result.Linked.Should().BeTrue();
        result.QueueDeferred.Should().BeFalse("nothing is created, so the go signal stays in the publish transaction");
        linear.Calls.Should().Be(0);
        RecordIn(ledger, taskId).ExternalReference.Should().Be(new ExternalReference(WorkItemProvider.Linear, "ENG-7"));
    }

    [Fact]
    public async Task An_untracked_publish_makes_no_Linear_call_names_Linear_and_still_gets_its_record()
    {
        using ScopedTestHome home = new();
        WriteLinearKey();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        RecordedLinear linear = RecordedLinear.Creating();

        (LinearTrackingResult result, FakeLedger ledger, string output, Guid taskId, _) = await TrackAsync(
            store, linear, cts.Token, noExistingItem: false, untracked: true);

        result.Linked.Should().BeFalse();
        result.QueueDeferred.Should().BeFalse();
        linear.Calls.Should().Be(0);
        output.Should().Contain("Linear").And.Contain("--untracked").And.Contain("no issue will be created or linked");
        RecordIn(ledger, taskId).ExternalReference.Should().BeNull();
        await using IQuerySession session = store.QuerySession();
        (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!
            .ExternalReference.Should().BeNull();
    }

    [Fact]
    public async Task The_go_signal_is_appended_only_after_the_link_so_dispatch_never_cuts_a_branch_before_the_key_exists()
    {
        using ScopedTestHome home = new();
        WriteLinearKey();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        RecordedLinear linear = RecordedLinear.Creating();

        (LinearTrackingResult result, _, string output, Guid taskId, _) = await TrackAsync(store, linear, cts.Token, queue: true);

        result.Linked.Should().BeTrue(output);
        result.QueueDeferred.Should().BeTrue();
        result.Queued.Should().NotBeNull();
        Type[] stream = await StreamAsync(store, taskId, cts.Token);
        stream.TakeLast(3).Should().Equal(typeof(TaskPublished), typeof(WorkItemLinked), typeof(TaskAssigned));
        await using IQuerySession session = store.QuerySession();
        (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!.State
            .Should().Be(TaskState.Queued);
    }

    [Fact]
    public async Task A_publish_fenced_on_the_version_it_read_still_appends_its_go_signal_and_refuses_a_task_that_moved()
    {
        using ScopedTestHome home = new();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        LinearInstall linear = await SeedLinearAsync(store, cts.Token);
        Guid taskId = await AddTaskAsync(
            store, linear.Install, "Fenced publish", ["It is fenced"], null, cts.Token);

        await using IDocumentSession session = store.LightweightSession();
        StreamState fence = (await session.Events.FetchStreamStateAsync(taskId, cts.Token))!;
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, version: fence.Version, token: cts.Token))!;
        TaskPublished published = TaskDecider.Publish(
            task, new TaskDependencyGraph([]), Now, linear.Install.OwnerId, BacklogPolicy.Linear,
            true, false);

        // Another writer moves the stream after the read, which is what a link-linear or a second
        // publish landing during the Linear lookup looks like.
        await using (IDocumentSession other = store.LightweightSession())
        {
            other.Events.Append(taskId, TaskDecider.Publish(
                task, new TaskDependencyGraph([]), Now, linear.Install.OwnerId, BacklogPolicy.Linear,
                true, false));
            await other.SaveChangesAsync(cts.Token);
        }

        session.Events.Append(taskId, expectedVersion: fence.Version + 1, published);
        task.Apply(published);
        await TaskAssignCommand.AppendAsync(session, task, linear.Owner, linear.Install.OwnerId, cts.Token);

        Func<Task> save = () => session.SaveChangesAsync(cts.Token);

        await save.Should().ThrowAsync<EventStreamUnexpectedMaxEventIdException>();
        (await StreamAsync(store, taskId, cts.Token)).Count(type => type == typeof(TaskPublished)).Should().Be(1);
    }

    [Fact]
    public async Task The_queue_refusals_of_a_publish_that_creates_an_issue_land_before_the_commit()
    {
        using ScopedTestHome home = new();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        LinearInstall linear = await SeedLinearAsync(store, cts.Token);
        Guid taskId = await AddTaskAsync(
            store, linear.Install, "Queue behind an archived project", ["It is refused"], null, cts.Token);
        await using (IDocumentSession archive = store.LightweightSession())
        {
            ProjectAggregate project = (await archive.Events.AggregateStreamAsync<ProjectAggregate>(
                linear.Install.ProjectId, token: cts.Token))!;
            archive.Events.Append(
                linear.Install.ProjectId, ProjectDecider.Archive(project, null, Now, linear.Install.OwnerId));
            await archive.SaveChangesAsync(cts.Token);
        }

        await using IDocumentSession session = store.LightweightSession();
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        task.Apply(TaskDecider.Publish(
            task, new TaskDependencyGraph([]), Now, linear.Install.OwnerId, BacklogPolicy.Linear,
            true, false));

        Func<Task> decide = () => TaskAssignCommand.DecideAsync(
            session, task, linear.Owner, linear.Install.OwnerId, cts.Token);

        await decide.Should().ThrowAsync<DomainValidationException>().WithMessage("*is archived*");
        (await StreamAsync(store, taskId, cts.Token)).Should().NotContain(typeof(TaskAssigned));
    }
}
