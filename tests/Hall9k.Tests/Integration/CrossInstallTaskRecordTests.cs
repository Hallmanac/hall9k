using System.Text.Json;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using JasperFx;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The whole crossing, against a real store and a scripted <c>gh</c> (task: a published task's
/// GitHub issue carries the whole task record): what publish writes into the issue, what revise
/// rewrites and what it leaves alone, and what a second install makes of the block when it adopts
/// the issue — including the two references that had to change form to survive the crossing, the
/// dependency edges (issue numbers) and the epic (a title).
/// <para>
/// gh is a <see cref="RecordingProcessRunner"/> serving a mutable issue body rather than the real
/// CLI, which is what lets the round trip be asserted end to end: the body publish wrote is
/// literally the body adoption reads.
/// </para>
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class CrossInstallTaskRecordTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 15, 31, 33, TimeSpan.Zero);
    private const string Repository = "Hallmanac/hall9k";

    [Fact]
    public async Task Publish_writes_the_record_below_the_checklist_and_a_second_install_reads_it_back()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = OpenStore();
        Install origin = await SeedAsync(store, cts.Token);
        Guid taskId = await AddTaskAsync(
            store, origin, "Carry the whole task record on the issue",
            ["Publishing writes the block", "Adoption reads it once"],
            agentContext: "Origin: Brian. The Mac publishes, the Windows node adopts.",
            cts.Token);
        await LinkIssueAsync(store, taskId, 1266, cts.Token);

        FakeGitHub gh = new(1266, GitHubIssueBody.Compose(
            "Origin: Brian. The Mac publishes, the Windows node adopts.",
            ["Publishing writes the block", "Adoption reads it once"]));

        await WriteRecordAsync(store, origin, taskId, gh, criteriaChanged: false, cts.Token);

        gh.Body.Should().Contain("## Acceptance criteria")
            .And.Contain(GitHubIssueBody.RecordSummary);
        gh.Body.IndexOf("- [ ] Publishing writes the block", StringComparison.Ordinal)
            .Should().BeLessThan(gh.Body.IndexOf("<details>", StringComparison.Ordinal));

        TaskRecord? record = TaskRecord.TryParse(GitHubIssueBody.TryReadRecordYaml(gh.Body));

        record.Should().NotBeNull();
        record!.Project.Should().Be("hall9k");
        record.Type.Should().Be("feature");
        record.Objective.Should().Be("Carry the whole task record on the issue");
        record.Criteria.Should().Equal("Publishing writes the block", "Adoption reads it once");
        record.AgentContext.Should().Be("Origin: Brian. The Mac publishes, the Windows node adopts.");
        record.Origin.TaskId.Should().Be(taskId);
        record.Origin.NodeId.Should().Be(origin.NodeId);
        record.Origin.BranchName.Should().Be(
            BranchNameTemplate.Default.Render(taskId, "Carry the whole task record on the issue", "1266"),
            "the record carries the branch the origin actually cuts, rendered through its own template");
    }

    [Fact]
    public async Task Revise_rewrites_only_the_record_and_leaves_a_humans_prose_alone()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = OpenStore();
        Install origin = await SeedAsync(store, cts.Token);
        Guid taskId = await AddTaskAsync(
            store, origin, "The first objective", ["The first criterion"], null, cts.Token);
        await LinkIssueAsync(store, taskId, 2266, cts.Token);

        FakeGitHub gh = new(2266, "## Objective\n\nThe first objective\n\n## Notes from Brian\n\nStill mine.");
        await WriteRecordAsync(store, origin, taskId, gh, criteriaChanged: false, cts.Token);
        string beforeRevision = gh.Body[..gh.Body.IndexOf("<details>", StringComparison.Ordinal)];

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.Revise(
                task,
                Optional<string>.Of("A reworded objective"),
                Optional<IReadOnlyList<string>>.None,
                Optional<string>.None,
                Optional<IReadOnlyList<Guid>>.None,
                Optional<TaskType>.None,
                Optional<AgentModel>.None,
                Now,
                origin.OwnerId));
            await session.SaveChangesAsync(cts.Token);
        }

        await WriteRecordAsync(store, origin, taskId, gh, criteriaChanged: false, cts.Token);

        gh.Body[..gh.Body.IndexOf("<details>", StringComparison.Ordinal)]
            .Should().Be(beforeRevision, "a human's edits to the issue's prose are theirs to keep");
        TaskRecord.TryParse(GitHubIssueBody.TryReadRecordYaml(gh.Body))!.Objective
            .Should().Be("A reworded objective");
        gh.Body.Split(GitHubIssueBody.RecordSummary).Length.Should().Be(2, "exactly one record section");
    }

    [Fact]
    public async Task Revise_regenerates_the_checklist_only_when_the_criteria_changed()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = OpenStore();
        Install origin = await SeedAsync(store, cts.Token);
        Guid taskId = await AddTaskAsync(store, origin, "An objective", ["The old criterion"], null, cts.Token);
        await LinkIssueAsync(store, taskId, 3266, cts.Token);

        FakeGitHub gh = new(3266, GitHubIssueBody.Compose(null, ["The old criterion"]));
        await WriteRecordAsync(store, origin, taskId, gh, criteriaChanged: false, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.Revise(
                task,
                Optional<string>.None,
                Optional<IReadOnlyList<string>>.Of(["A new criterion"]),
                Optional<string>.None,
                Optional<IReadOnlyList<Guid>>.None,
                Optional<TaskType>.None,
                Optional<AgentModel>.None,
                Now,
                origin.OwnerId));
            await session.SaveChangesAsync(cts.Token);
        }

        await WriteRecordAsync(store, origin, taskId, gh, criteriaChanged: true, cts.Token);

        gh.Body.Should().Contain("- [ ] A new criterion").And.NotContain("- [ ] The old criterion");
    }

    /// <summary>
    /// The write side of the edge convention: a dependency becomes an ISSUE NUMBER, and a blocker
    /// that cannot be named that way here is counted rather than dropped. It also pins the one
    /// thing that would regress quietly now that the blockers load in a single round trip — the
    /// numbers stay in the task's own <c>BlockedBy</c> order, so rewriting the record of a task
    /// nobody changed produces the same list rather than a reshuffled one.
    /// </summary>
    [Fact]
    public async Task The_dependency_edges_are_written_as_issue_numbers_in_order_and_the_rest_counted()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = OpenStore();
        Install origin = await SeedAsync(store, cts.Token);

        Guid first = await AddTaskAsync(store, origin, "The first blocker", ["Something"], null, cts.Token);
        Guid second = await AddTaskAsync(store, origin, "The second blocker", ["Something"], null, cts.Token);
        Guid unpublished = await AddTaskAsync(
            store, origin, "A blocker nobody published", ["Something"], null, cts.Token);
        await LinkIssueAsync(store, first, 12001, cts.Token);
        await LinkIssueAsync(store, second, 12002, cts.Token);

        Guid taskId = await AddTaskAsync(
            store, origin, "The blocked work", ["Something"], null, cts.Token,
            blockedBy: [second, unpublished, first]);
        await LinkIssueAsync(store, taskId, 12266, cts.Token);

        FakeGitHub gh = new(12266, GitHubIssueBody.Compose(null, ["Something"]));
        await WriteRecordAsync(store, origin, taskId, gh, criteriaChanged: false, cts.Token);

        TaskRecord record = TaskRecord.TryParse(GitHubIssueBody.TryReadRecordYaml(gh.Body))!;

        record.BlockedByIssues.Should().Equal(12002, 12001);
        record.DependenciesWithoutIssues.Should().Be(
            1, "a blocker never published to an issue has no identifier that means anything there");
    }

    [Fact]
    public async Task An_issue_carrying_a_record_reconstructs_the_whole_draft_criteria_and_all()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = OpenStore();
        Install adopting = await SeedAsync(store, cts.Token);

        TaskRecord published = SomeRecord() with
        {
            Model = "claude-opus-5",
            Caps = new TaskRecordCaps(3, null, null, null, 4),
            PreApproval = PreApprovalMode.AfterHumanReview,
        };
        ImportedWorkItem issue = IssueCarrying(published);

        TaskRecord? read = TaskRecordAdoption.Read(WorkItemProvider.GitHub, issue);
        read.Should().NotBeNull();

        await using IQuerySession session = store.QuerySession();
        TaskRecordAdoption.Resolution resolution = await TaskRecordAdoption.ResolveAsync(
            session, read!, issue.Reference, adopting.ProjectId, cts.Token);
        TaskRecordAdoption.Reconstruction draft = TaskRecordAdoption.Reconstruct(
            read!, resolution, NothingOnTheCommandLine, issue.Reference.ToString());

        draft.Objective.Should().Be(published.Objective);
        draft.Criteria.Should().Equal(published.Criteria, "criteria become criteria, never context");
        draft.AgentContext.Should().Be(published.AgentContext);
        draft.Type.Should().Be("feature");
        draft.Model.Should().Be("claude-opus-5");
        read!.Caps.Should().Be(new TaskRecordCaps(3, null, null, null, 4));
        read.PreApproval.Should().Be(PreApprovalMode.AfterHumanReview,
            "the record states the ORIGIN install's own answer, and states which of the three modes it "
            + "was — a boolean could not have said after-human-review at all");
    }

    [Fact]
    public async Task An_issue_with_no_record_behaves_exactly_as_it_always_did()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = OpenStore();
        await SeedAsync(store, cts.Token);

        ImportedWorkItem plain = new(
            new ExternalReference(WorkItemProvider.GitHub, $"{Repository}#7"),
            "Something a person filed",
            "## Objective\n\nThe login times out.\n\n- [ ] not a hall9k checklist",
            WorkItemStatus.Open,
            null,
            Now);

        TaskRecordAdoption.Read(WorkItemProvider.GitHub, plain).Should().BeNull(
            "an issue nobody published from hall9k adopts by title and body, as it always has");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_blocked_by_chain_adopted_parent_first_resolves_the_edge()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = OpenStore();
        Install adopting = await SeedAsync(store, cts.Token);

        Guid parentId = await AddTaskAsync(store, adopting, "The parent", ["Something"], null, cts.Token);
        await LinkIssueAsync(store, parentId, 5081, cts.Token);

        TaskRecord child = SomeRecord() with { BlockedByIssues = [5081] };
        await using IQuerySession session = store.QuerySession();
        TaskRecordAdoption.Resolution resolution = await TaskRecordAdoption.ResolveAsync(
            session, child, Issue(5266), adopting.ProjectId, cts.Token);

        resolution.Dependencies.Should().Equal(parentId);
        resolution.UnresolvedIssues.Should().BeEmpty();
    }

    [Fact]
    public async Task A_blocked_by_chain_adopted_child_first_records_the_edge_as_unresolved()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = OpenStore();
        Install adopting = await SeedAsync(store, cts.Token);

        TaskRecord child = SomeRecord() with { BlockedByIssues = [6081] };
        await using IQuerySession session = store.QuerySession();
        TaskRecordAdoption.Resolution resolution = await TaskRecordAdoption.ResolveAsync(
            session, child, Issue(6266), adopting.ProjectId, cts.Token);

        resolution.Dependencies.Should().BeEmpty();
        // Nobody here has adopted issue 6081 yet, and inventing an edge onto a look-alike task would
        // be worse than saying so.
        resolution.UnresolvedIssues.Should().Equal([6081]);
    }

    [Fact]
    public async Task The_epic_maps_to_the_local_epic_of_that_title_and_otherwise_says_it_did_not()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = OpenStore();
        Install adopting = await SeedAsync(store, cts.Token);

        Guid epicId = DomainId.New();
        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<EpicAggregate>(epicId, EpicDecider.Add(
                epicId, adopting.ProjectId, "Distributed team on the tracker", Now, adopting.OwnerId));
            await seed.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession session = store.QuerySession();
        TaskRecordAdoption.Resolution matched = await TaskRecordAdoption.ResolveAsync(
            session,
            SomeRecord() with { EpicTitle = "Distributed team on the tracker" },
            Issue(266),
            adopting.ProjectId,
            cts.Token);
        TaskRecordAdoption.Resolution unmatched = await TaskRecordAdoption.ResolveAsync(
            session,
            SomeRecord() with { EpicTitle = "An epic this install has never heard of" },
            Issue(266),
            adopting.ProjectId,
            cts.Token);

        matched.EpicId.Should().Be(epicId);
        matched.UnmatchedEpicTitle.Should().BeNull();
        unmatched.EpicId.Should().BeNull();
        unmatched.UnmatchedEpicTitle.Should().Be("An epic this install has never heard of");
    }

    [Fact]
    public async Task The_adopted_task_records_which_install_published_it_and_under_what_id()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = OpenStore();
        Install adopting = await SeedAsync(store, cts.Token);

        TaskRecord record = SomeRecord();
        Guid taskId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
                taskId, adopting.ProjectId, record.Objective, record.Criteria, TaskType.Feature,
                record.AgentContext, constraints: null,
                externalReference: Issue(266), Now, adopting.OwnerId,
                origin: record.Origin));
            await session.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession query = store.QuerySession();
        TaskAggregate? task = await query.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token);
        TaskDetails? details = await query.LoadAsync<TaskDetails>(taskId, cts.Token);

        task!.Origin!.NodeName.Should().Be("HALLMANAC-MAC");
        task.Origin.TaskId.Should().Be(record.Origin.TaskId);
        details!.Origin!.BranchName.Should().Be(record.Origin.BranchName,
            "h9k task show reads the projection, so the origin has to survive the projection too");
        TaskShowCommand.OriginMarkup(details.Origin).Should().Contain("HALLMANAC-MAC")
            .And.Contain(TaskListCommand.ShortId(record.Origin.TaskId))
            .And.Contain("adopt again");
    }

    /// <summary>
    /// The caps ride on their own events rather than on <c>TaskAdded</c>, which means adoption
    /// appends them onto a stream it is starting in the same transaction — worth exercising against
    /// a real store rather than assumed, since that is the one shape here that could fail at
    /// runtime while every unit test passes.
    /// </summary>
    [Fact]
    public async Task The_origins_cap_overrides_land_on_the_adopted_copy()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = OpenStore();
        Install adopting = await SeedAsync(store, cts.Token);

        TaskRecord record = SomeRecord() with { Caps = new TaskRecordCaps(3, null, 2, null, 4) };
        Guid taskId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAdded added = TaskDecider.Add(
                taskId, adopting.ProjectId, record.Objective, record.Criteria, TaskType.Feature,
                record.AgentContext, constraints: null, externalReference: Issue(11266), Now,
                adopting.OwnerId, origin: record.Origin);
            session.Events.StartStream<TaskAggregate>(taskId, added);
            TaskAddCommand.AppendRecordCaps(session, taskId, added, record.Caps, adopting.OwnerId);
            await session.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession query = store.QuerySession();
        TaskAggregate task = (await query.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, token: cts.Token))!;
        IReadOnlyList<JasperFx.Events.IEvent> stream =
            await query.Events.FetchStreamAsync(taskId, token: cts.Token);

        // One transaction, one observed moment: the cap events are part of the adoption that
        // created the task, so they carry TaskAdded's own stamp rather than a second clock reading.
        stream.Select(@event => @event.Data).OfType<TaskReviewCapsOverridden>()
            .Should().ContainSingle().Which.OverriddenAt.Should().Be(Now);
        stream.Select(@event => @event.Data).OfType<TaskSessionCapOverridden>()
            .Should().ContainSingle().Which.OverriddenAt.Should().Be(Now);

        task.MaxComplianceReviewCycles.Should().Be(3);
        task.MaxFinalFullPassRounds.Should().Be(2);
        task.SessionCap.Should().Be(4);
        task.MaxAdversarialReviewCycles.Should().BeNull(
            "a cap the record did not name is left to the levels above this task, exactly as it was "
            + "on the origin");
        task.LifetimeReviewCycleBudget.Should().BeNull();
    }

    [Fact]
    public async Task A_mirror_never_writes_the_record_it_was_adopted_from()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = OpenStore();
        Install adopting = await SeedAsync(store, cts.Token);

        Guid taskId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
                taskId, adopting.ProjectId, "The adopted copy", ["Something"], TaskType.Feature,
                agentContext: null, constraints: null, externalReference: Issue(9266), Now,
                adopting.OwnerId, origin: SomeRecord().Origin));
            await session.SaveChangesAsync(cts.Token);
        }

        FakeGitHub gh = new(9266, GitHubIssueBody.WithRecord(
            GitHubIssueBody.Compose(null, ["Something"]), SomeRecord()));
        string before = gh.Body;

        await using IQuerySession query = store.QuerySession();
        TaskAggregate task = (await query.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, token: cts.Token))!;
        ProjectDetails project = (await query.LoadAsync<ProjectDetails>(adopting.ProjectId, cts.Token))!;

        TaskRecordPublication.WriteOutcome outcome = await TaskRecordPublication.WriteAsync(
            query, task, project, adopting.NodeId, "HALLMANAC-WIN", Now, criteriaChanged: true,
            gh.Provider, cts.Token);

        outcome.Should().Be(TaskRecordPublication.WriteOutcome.NotTracked);
        gh.Body.Should().Be(before,
            "the issue belongs to the install that published it — a mirror rewriting that block would "
            + "overwrite the origin's own record with its local copy");
        gh.Runner.Calls.Should().BeEmpty("and it does not even ask gh");
    }

    [Fact]
    public async Task A_project_that_tracks_nothing_gets_no_record_written_into_its_adopted_issue()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = OpenStore();
        Install install = await SeedAsync(store, cts.Token, BacklogPolicy.None);
        Guid taskId = await AddTaskAsync(store, install, "Local work", ["Something"], null, cts.Token);
        await LinkIssueAsync(store, taskId, 10266, cts.Token);

        FakeGitHub gh = new(10266, "Somebody else's issue, in somebody else's repository.");

        await using IQuerySession query = store.QuerySession();
        TaskAggregate task = (await query.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, token: cts.Token))!;
        ProjectDetails project = (await query.LoadAsync<ProjectDetails>(install.ProjectId, cts.Token))!;

        TaskRecordPublication.WriteOutcome outcome = await TaskRecordPublication.WriteAsync(
            query, task, project, install.NodeId, "HALLMANAC-WIN", Now, criteriaChanged: false,
            gh.Provider, cts.Token);

        outcome.Should().Be(TaskRecordPublication.WriteOutcome.NotTracked);
        gh.Runner.Calls.Should().BeEmpty();
    }

    private static readonly TaskRecordAdoption.Overrides NothingOnTheCommandLine =
        new(null, [], null, null, null, null, []);

    private static ExternalReference Issue(int number) =>
        new(WorkItemProvider.GitHub, $"{Repository}#{number}");

    private static TaskRecord SomeRecord() => new(
        "hall9k",
        "feature",
        "Carry the whole task record on the issue",
        ["Publishing writes the block", "Adoption reads it once"],
        "Origin: Brian. The Mac publishes, the Windows node adopts.",
        null,
        PreApprovalMode.Off,
        [],
        0,
        null,
        null,
        TaskRecordCaps.None,
        new TaskOrigin(
            Guid.Parse("01a07c7e-fed4-74bf-a0f8-ac5a7325335a"),
            "HALLMANAC-MAC",
            Guid.Parse("01a07909-b8a5-777d-9033-4318ba2a31b5"),
            "task/7325335a-carry-the-whole-task-record",
            Now));

    private static ImportedWorkItem IssueCarrying(TaskRecord record) => new(
        Issue(266),
        "Carry the whole task record on the issue",
        GitHubIssueBody.WithRecord(
            GitHubIssueBody.Compose(record.AgentContext, record.Criteria), record),
        WorkItemStatus.Open,
        null,
        Now);

    private static async Task WriteRecordAsync(
        DocumentStore store,
        Install install,
        Guid taskId,
        FakeGitHub gh,
        bool criteriaChanged,
        CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, token: cancellationToken))!;
        ProjectDetails project = (await session.LoadAsync<ProjectDetails>(install.ProjectId, cancellationToken))!;

        TaskRecordPublication.WriteOutcome outcome = await TaskRecordPublication.WriteAsync(
            session, task, project, install.NodeId, "HALLMANAC-MAC", Now, criteriaChanged,
            gh.Provider, cancellationToken);

        outcome.Should().Be(TaskRecordPublication.WriteOutcome.Written);
    }

    private static async Task LinkIssueAsync(
        DocumentStore store, Guid taskId, int number, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        await TaskLinkIssueCommand.LinkAsync(
            session, taskId,
            new ImportedWorkItem(Issue(number), $"Issue {number}", null, WorkItemStatus.Open, null, Now),
            DomainId.New(), cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }

    private static async Task<Guid> AddTaskAsync(
        DocumentStore store,
        Install install,
        string objective,
        IReadOnlyList<string> criteria,
        string? agentContext,
        CancellationToken cancellationToken,
        IReadOnlyList<Guid>? blockedBy = null)
    {
        Guid taskId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
            taskId, install.ProjectId, objective, criteria, TaskType.Feature, agentContext,
            constraints: null, externalReference: null, Now, install.OwnerId, blockedBy: blockedBy));
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    private sealed record Install(Guid OwnerId, Guid ProjectId, Guid NodeId);

    private static async Task<Install> SeedAsync(
        DocumentStore store,
        CancellationToken cancellationToken,
        BacklogPolicy? backlogPolicy = null)
    {
        Guid ownerId = DomainId.New();
        Guid connectionId = DomainId.New();
        Guid projectId = DomainId.New();

        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<OwnerAggregate>(ownerId, OwnerDecider.Register(
            ownerId, "Brian Hall", "brian@hallmanac.com", Now));
        session.Events.StartStream<ConnectionAggregate>(connectionId, ConnectionDecider.Register(
            connectionId, ownerId, WorkItemProvider.GitHub, "Hallmanac", CredentialReference.GhCli, Now));
        ProjectRegistered registered = ProjectDecider.Register(
            projectId, ownerId, connectionId, "hall9k", "/repos/hall9k",
            new Uri($"https://github.com/{Repository}"), null, Now);
        session.Events.StartStream<ProjectAggregate>(projectId, registered);

        // The record is written only under the github-issues policy, so the seed states it: a
        // project that tracks nothing has said nothing about wanting hall9k to write into an issue.
        ProjectAggregate project = new();
        project.Apply(registered);
        session.Events.Append(projectId, ProjectDecider.ChangeSettings(
            project,
            Optional<IReadOnlyList<VerifyCommand>>.None,
            Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None,
            Now,
            ownerId,
            backlogPolicy: Optional<BacklogPolicy>.Of(backlogPolicy ?? BacklogPolicy.GitHubIssues)));
        await session.SaveChangesAsync(cancellationToken);

        return new Install(ownerId, projectId, DomainId.New());
    }

    private DocumentStore OpenStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    /// <summary>
    /// A <c>gh</c> that serves one issue out of memory: <c>issue view --json</c> hands back the
    /// current body and <c>issue edit --body-file</c> replaces it. That is what makes the round trip
    /// real here rather than asserted twice — the body publish wrote is the body adoption reads.
    /// </summary>
    private sealed class FakeGitHub
    {
        private readonly int number;

        public FakeGitHub(int number, string body)
        {
            this.number = number;
            Body = body;
            Runner = new RecordingProcessRunner(Respond);
            Provider = new GitHubWorkItemProvider(Runner.Runner);
        }

        public string Body { get; private set; }

        public RecordingProcessRunner Runner { get; }

        public GitHubWorkItemProvider Provider { get; }

        private ProcessResult Respond(IReadOnlyList<string> arguments)
        {
            if (arguments.Contains("view"))
            {
                return new ProcessResult(0, JsonSerializer.Serialize(new
                {
                    number,
                    title = "Carry the whole task record on the issue",
                    body = Body,
                    state = "OPEN",
                    url = $"https://github.com/{Repository}/issues/{number}",
                }), string.Empty);
            }

            if (arguments.Contains("edit"))
            {
                int file = arguments.ToList().IndexOf("--body-file");
                Body = File.ReadAllText(arguments[file + 1]);
                return new ProcessResult(0, string.Empty, string.Empty);
            }

            return new ProcessResult(1, string.Empty, $"unexpected gh call: {string.Join(' ', arguments)}");
        }
    }
}
