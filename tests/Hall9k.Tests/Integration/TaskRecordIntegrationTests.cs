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
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The task record's own integration surfaces — what it carries out to a GitHub issue and back,
/// what adoption refuses to take twice, and what <c>h9k task resolve --pr</c> writes onto a run
/// stream — sharing one container across the three seams below. Each was its own class and so its
/// own container for nine to thirteen tests; every assertion here reads what its own test seeded,
/// by id or by its own external reference (no two seams here name the same issue), so a sibling
/// seam's rows are as invisible as a sibling test's already were.
/// <para>
/// The whole crossing, against a real store and a scripted <c>gh</c> (task: a published task's
/// GitHub issue carries the whole task record): what publish writes into the issue, what revise
/// rewrites and what it leaves alone, and what a second install makes of the block when it adopts
/// the issue — including the two references that had to change form to survive the crossing, the
/// dependency edges (issue numbers) and the epic (a title). gh is a
/// <see cref="RecordingProcessRunner"/> serving a mutable issue body rather than the real CLI,
/// which is what lets the round trip be asserted end to end: the body publish wrote is literally
/// the body adoption reads.
/// </para>
/// <para>
/// Adoption is selective, never mirroring (PLAN.md §3.1a): the platform tracks only the external
/// work someone will actually take on, and it tracks each item once. The duplicate refusal needs
/// the real projection because it is a query over what <c>TaskAdded</c> wrote, and the canonical
/// reference is the join.
/// </para>
/// <para>
/// <see cref="TaskResolveCommand.RecordPullRequestOnRunStreamAsync"/> — the run-side counterpart
/// to <c>h9k task resolve --pr</c> (backlog: a pull request recorded by h9k task resolve --pr is
/// observed to merge like every other pull request the platform knows about). The defect those
/// tests guard (independent pre-PR review, cycle 1, high): an interactive claim
/// (<c>h9k task work</c>) whose worktree cut fails leaves a Failed task with
/// <see cref="TaskAggregate.CurrentRunId"/> naming a run whose stream was never started
/// (<c>TaskWorkCommand.FailInteractiveClaimAsync</c> appends only to the task stream). Appending
/// unconditionally onto that run id would implicitly create the stream and materialize a stub
/// <c>RunDetails</c> row, which drops the task out of
/// <c>CloseoutEngine.TasksWithMissingRunRecordsAsync</c>'s own candidate set — the one sweep
/// actually built to complete closeout for exactly this shape.
/// </para>
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class TaskRecordIntegrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 15, 31, 33, TimeSpan.Zero);
    private const string Repository = "Hallmanac/hall9k";

    [Fact]
    public async Task Publish_writes_the_record_below_the_checklist_and_a_second_install_reads_it_back()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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

    // ── adoption is selective, never mirroring ──
    private static readonly DateTimeOffset AdoptionNow = new(2026, 8, 21, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_second_adoption_of_the_same_issue_points_at_the_task_that_already_has_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        ExternalReference issue = new(WorkItemProvider.GitHub, "Hallmanac/hall9k#42");
        Guid firstTaskId = await AdoptAsync(store, issue, "Adopt existing GitHub issues", cts.Token);

        await using IQuerySession session = store.QuerySession();
        Func<Task> secondAdoption = () =>
            TaskAddCommand.RefuseSecondAdoptionAsync(session, issue, cts.Token);

        (await secondAdoption.Should().ThrowAsync<DomainConflictException>()).Which.Message
            .Should().Contain("github:Hallmanac/hall9k#42")
            .And.Contain(TaskListCommand.ShortId(firstTaskId))
            .And.Contain("Adopt existing GitHub issues", "the refusal names the work, not just an id");
    }

    [Fact]
    public async Task An_issue_nobody_adopted_passes_even_when_other_adoptions_exist()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        await AdoptAsync(store, new ExternalReference(WorkItemProvider.GitHub, "Hallmanac/hall9k#7"),
            "Something else entirely", cts.Token);

        await using IQuerySession session = store.QuerySession();
        Func<Task> adoption = () => TaskAddCommand.RefuseSecondAdoptionAsync(
            session, new ExternalReference(WorkItemProvider.GitHub, "Hallmanac/hall9k#8"), cts.Token);

        await adoption.Should().NotThrowAsync();
    }

    [Fact]
    public async Task An_issue_whose_only_task_was_abandoned_can_be_adopted_again()
    {
        // The reason to refuse a second adoption is the contradiction two closeouts would make.
        // An abandoned task will never close out and will never run again, so holding the issue
        // hostage to it makes the work permanently unadoptable and buys nothing: walking away is
        // exactly how a human says they are done with it.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        ExternalReference issue = new(WorkItemProvider.GitHub, "Hallmanac/hall9k#99");
        Guid abandonedTaskId = await AdoptAsync(store, issue, "A first pass nobody finished", cts.Token);
        await AbandonAsync(store, abandonedTaskId, cts.Token);

        await using IQuerySession session = store.QuerySession();
        Func<Task> secondAdoption = () =>
            TaskAddCommand.RefuseSecondAdoptionAsync(session, issue, cts.Token);

        await secondAdoption.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_failed_first_adoption_still_holds_the_issue()
    {
        // Failed is a waypoint, not an ending (Decisions Log #27): retry, resolve and abandon are
        // all still open on the task that has the issue, so a second task against it would be the
        // duplicate the refusal exists to prevent.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        ExternalReference issue = new(WorkItemProvider.GitHub, "Hallmanac/hall9k#100");
        Guid failedTaskId = await AdoptAsync(store, issue, "A pass that failed", cts.Token);
        await AppendAsync(store, failedTaskId,
            new TaskFailed(failedTaskId, DomainId.New(), "The verification never passed.", AdoptionNow), cts.Token);

        await using IQuerySession session = store.QuerySession();
        Func<Task> secondAdoption = () =>
            TaskAddCommand.RefuseSecondAdoptionAsync(session, issue, cts.Token);

        (await secondAdoption.Should().ThrowAsync<DomainConflictException>()).Which.Message
            .Should().Contain("Failed", "the refusal says which task holds the issue and where it stands")
            .And.Contain("h9k task abandon", "and how to release it");
    }

    [Fact]
    public async Task A_done_first_adoption_is_told_to_write_a_separate_task_rather_than_to_abandon()
    {
        // A reopened GitHub issue lands here: the first adoption closed out, so it still holds the
        // reference, and TaskDecider.Abandon refuses an already-terminal task. Naming abandon would
        // send the human straight into a second refusal, which is the one error shape an agent
        // cannot self-correct from — so the route that works is the one the message offers.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        ExternalReference issue = new(WorkItemProvider.GitHub, "Hallmanac/hall9k#101");
        Guid doneTaskId = await AdoptAsync(store, issue, "A pass that shipped", cts.Token);
        await AppendAsync(store, doneTaskId,
            new TaskCompleted(doneTaskId, DomainId.New(), "https://github.com/Hallmanac/hall9k/pull/9", AdoptionNow),
            cts.Token);

        await using IQuerySession session = store.QuerySession();
        Func<Task> secondAdoption = () =>
            TaskAddCommand.RefuseSecondAdoptionAsync(session, issue, cts.Token);

        (await secondAdoption.Should().ThrowAsync<DomainConflictException>()).Which.Message
            .Should().Contain("Done", "the refusal says where the holding task stands")
            .And.Contain("write a separate task")
            .And.NotContain("h9k task abandon", "a Done task cannot be abandoned, so offering it is a dead end");
    }

    [Fact]
    public async Task A_done_pr_review_does_not_block_a_fresh_review_of_the_same_pull_request()
    {
        // TaskDecider.Reopen refuses to reopen a Done pr-review task, and points the owner here
        // instead: "Start a fresh review instead with h9k task add --from-pr." A completed review
        // is not the same claim a Done adopted-work task makes — it says the review finished, not
        // that the pull request's own work is done — so it must not hold its reference the way an
        // ordinary Done adoption does, or that lever the decider names would refuse too and strand
        // the owner with no way to re-review the pull request.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        ExternalReference pullRequest = new(WorkItemProvider.GitHub, "Hallmanac/hall9k#142");
        Guid firstReviewId = await AdoptAsync(
            store, pullRequest, "Review pull request #142", TaskType.PrReview, cts.Token);
        await AppendAsync(store, firstReviewId,
            new TaskCompleted(firstReviewId, DomainId.New(), null, AdoptionNow), cts.Token);

        await using IQuerySession session = store.QuerySession();
        Func<Task> secondAdoption = () =>
            TaskAddCommand.RefuseSecondAdoptionAsync(session, pullRequest, cts.Token);

        await secondAdoption.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_live_pr_review_still_blocks_a_second_adoption_of_the_same_pull_request()
    {
        // The exception is for a *completed* review only. While a review is still in flight there
        // is no reason to run a second one over the same pull request concurrently.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        ExternalReference pullRequest = new(WorkItemProvider.GitHub, "Hallmanac/hall9k#155");
        Guid reviewId = await AdoptAsync(
            store, pullRequest, "Review pull request #155", TaskType.PrReview, cts.Token);

        await using IQuerySession session = store.QuerySession();
        Func<Task> secondAdoption = () =>
            TaskAddCommand.RefuseSecondAdoptionAsync(session, pullRequest, cts.Token);

        (await secondAdoption.Should().ThrowAsync<DomainConflictException>()).Which.Message
            .Should().Contain(TaskListCommand.ShortId(reviewId));
    }

    [Fact]
    public async Task A_live_pr_review_blocks_a_third_adoption_even_behind_an_older_done_one()
    {
        // Regression: the guard used to take the *oldest* non-abandoned holder and only then check
        // whether it was a Done pr-review, so an older Done review permanently shadowed a newer
        // live one on the same pull request — the exclusion now lives in the query itself, so a
        // live holder still blocks even when a Done pr-review on the same reference sorts first.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        ExternalReference pullRequest = new(WorkItemProvider.GitHub, "Hallmanac/hall9k#168");
        Guid firstReviewId = await AdoptAsync(
            store, pullRequest, "Review pull request #168", TaskType.PrReview, AdoptionNow, cts.Token);
        await AppendAsync(store, firstReviewId,
            new TaskCompleted(firstReviewId, DomainId.New(), null, AdoptionNow), cts.Token);

        Guid secondReviewId = await AdoptAsync(
            store, pullRequest, "Review pull request #168 again", TaskType.PrReview,
            AdoptionNow.AddMinutes(5), cts.Token);

        await using IQuerySession session = store.QuerySession();
        Func<Task> thirdAdoption = () =>
            TaskAddCommand.RefuseSecondAdoptionAsync(session, pullRequest, cts.Token);

        (await thirdAdoption.Should().ThrowAsync<DomainConflictException>()).Which.Message
            .Should().Contain(TaskListCommand.ShortId(secondReviewId))
            .And.NotContain(
                TaskListCommand.ShortId(firstReviewId), "the Done review no longer holds the reference");
    }

    /// <summary>
    /// The rule is about the item, not about how the task got it — so a card a task caused to
    /// exist holds that card exactly as an imported one does, and <c>h9k task link-jira</c> asks
    /// the same question <c>--from-jira</c> does before recording a key.
    /// <para>
    /// The publication prompt makes this the likely mistake rather than an exotic one: a session
    /// is told to search for an earlier attempt's card before creating a second, and a session
    /// that finds another task's card and reports its key back in good faith would otherwise put
    /// two live tasks on one card, with two sets of runs and two closeout comments on it. Origin
    /// incident (2026-08-21): the pre-PR review of this branch found link-jira checking only the
    /// task in front of it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_card_a_task_was_linked_to_is_held_as_firmly_as_one_it_imported()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        ExternalReference card = new(WorkItemProvider.Jira, "PROJ-123");
        Guid holderId = await AdoptAsync(store, reference: null, "The task the card was made for", cts.Token);
        await AppendAsync(store, holderId,
            new WorkItemLinked(holderId, card, "Publish me", "To Do (open)", AdoptionNow, AdoptionNow, DomainId.New()),
            cts.Token);

        await using IQuerySession session = store.QuerySession();
        Func<Task> secondClaim = () => TaskAddCommand.RefuseSecondAdoptionAsync(session, card, cts.Token);

        (await secondClaim.Should().ThrowAsync<DomainConflictException>()).Which.Message
            .Should().Contain("jira:PROJ-123")
            .And.Contain(TaskListCommand.ShortId(holderId))
            .And.Contain("The task the card was made for");
    }

    private static Task AbandonAsync(IDocumentStore store, Guid taskId, CancellationToken cancellationToken) =>
        AppendAsync(store, taskId, new TaskAbandoned(taskId, "Overtaken by events.", AdoptionNow, DomainId.New()),
            cancellationToken);

    private static async Task AppendAsync(
        IDocumentStore store, Guid taskId, object @event, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(taskId, @event);
        await session.SaveChangesAsync(cancellationToken);
    }

    private static Task<Guid> AdoptAsync(
        IDocumentStore store, ExternalReference? reference, string objective, CancellationToken cancellationToken) =>
        AdoptAsync(store, reference, objective, TaskType.Feature, AdoptionNow, cancellationToken);

    private static Task<Guid> AdoptAsync(
        IDocumentStore store, ExternalReference? reference, string objective, TaskType type,
        CancellationToken cancellationToken) =>
        AdoptAsync(store, reference, objective, type, AdoptionNow, cancellationToken);

    private static async Task<Guid> AdoptAsync(
        IDocumentStore store, ExternalReference? reference, string objective, TaskType type,
        DateTimeOffset addedAt, CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
            taskId,
            DomainId.New(),
            objective,
            ["The importer refuses a closed issue"],
            type,
            agentContext: null,
            constraints: null,
            reference,
            addedAt,
            DomainId.New()));
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    // ── h9k task resolve --pr, on the run stream ──
    private static readonly DateTimeOffset ResolveNow = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_failed_task_whose_run_stream_never_started_records_nothing_on_the_run_side()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = DomainId.New();
        Guid runId = DomainId.New();

        TaskAggregate task = await SeedFailedInteractiveClaimWithNoRunStreamAsync(store, ownerId, runId, cts.Token);

        await using IDocumentSession session = store.LightweightSession();
        TaskResolveCommand.RunStreamPullRequestOutcome outcome =
            await TaskResolveCommand.RecordPullRequestOnRunStreamAsync(
                session, task, "https://github.com/x/y/pull/24", ResolveNow, new Uri("https://github.com/x/y"), cts.Token);
        await session.SaveChangesAsync(cts.Token);

        outcome.Should().Be(TaskResolveCommand.RunStreamPullRequestOutcome.NoRunStream,
            "the run stream was never started, so there is nothing here to append onto");

        await using IQuerySession query = store.QuerySession();
        (await query.Events.FetchStreamStateAsync(runId, cts.Token)).Should().BeNull(
            "appending here must never implicitly create the run stream — that would materialize a stub " +
            "RunDetails row and hide the task from CloseoutEngine's missing-run sweep");
        (await query.LoadAsync<RunDetails>(runId, cts.Token)).Should().BeNull();
    }

    [Fact]
    public async Task A_failed_runs_own_stream_records_the_pull_request_when_it_names_the_projects_own_repository()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = DomainId.New();
        Guid runId = DomainId.New();

        TaskAggregate task = await SeedFailedDispatchedRunAsync(store, ownerId, runId, cts.Token);

        await using IDocumentSession session = store.LightweightSession();
        TaskResolveCommand.RunStreamPullRequestOutcome outcome =
            await TaskResolveCommand.RecordPullRequestOnRunStreamAsync(
                session, task, "https://github.com/x/y/pull/24", ResolveNow, new Uri("https://github.com/x/y"), cts.Token);
        await session.SaveChangesAsync(cts.Token);

        outcome.Should().Be(TaskResolveCommand.RunStreamPullRequestOutcome.Recorded);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.PullRequestNumber.Should().Be(24);
        run.State.Should().Be(RunState.Failed, "recording the pull request must never move the run off Failed");
    }

    [Fact]
    public async Task A_pull_request_naming_a_different_repository_than_the_project_records_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = DomainId.New();
        Guid runId = DomainId.New();

        TaskAggregate task = await SeedFailedDispatchedRunAsync(store, ownerId, runId, cts.Token);

        await using IDocumentSession session = store.LightweightSession();
        TaskResolveCommand.RunStreamPullRequestOutcome outcome =
            await TaskResolveCommand.RecordPullRequestOnRunStreamAsync(
                session, task, "https://github.com/other-org/other-repo/pull/24", ResolveNow,
                new Uri("https://github.com/x/y"), cts.Token);
        await session.SaveChangesAsync(cts.Token);

        outcome.Should().Be(TaskResolveCommand.RunStreamPullRequestOutcome.NotRecorded,
            "a pull request from a repository other than the project's own must never become this run's merge signal");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.PullRequestNumber.Should().BeNull();
    }

    /// <summary>
    /// A pr-review task's PullRequestUrl names the pull request it reviewed, not one of its own
    /// (adversarial review, cycle 3, high): recording it here would enroll a foreign pull request
    /// as this run's merge signal, letting that pull request's own unrelated merge complete this
    /// task's closeout and run the remote branch-delete cleanup TaskDecider.Reopen already refuses
    /// the type to prevent. This must hold even when the URL names the project's own repository,
    /// which is the ordinary case for a pr-review task (it reviews a pull request in its own
    /// project) — the guard cannot rely on the repository check to catch it.
    /// </summary>
    [Fact]
    public async Task A_pr_review_tasks_failed_run_records_nothing_even_when_the_pull_request_names_the_projects_own_repository()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = DomainId.New();
        Guid runId = DomainId.New();

        TaskAggregate task = await SeedFailedDispatchedRunAsync(store, ownerId, runId, cts.Token, TaskType.PrReview);

        await using IDocumentSession session = store.LightweightSession();
        TaskResolveCommand.RunStreamPullRequestOutcome outcome =
            await TaskResolveCommand.RecordPullRequestOnRunStreamAsync(
                session, task, "https://github.com/x/y/pull/24", ResolveNow, new Uri("https://github.com/x/y"), cts.Token);
        await session.SaveChangesAsync(cts.Token);

        outcome.Should().Be(TaskResolveCommand.RunStreamPullRequestOutcome.NotRecorded,
            "a pr-review task's --pr names the pull request it reviewed, never one of its own");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.PullRequestNumber.Should().BeNull();
    }

    /// <summary>
    /// A --repo-only project (no --repo-url, so ProjectDetails.RepositoryUrl stays null forever)
    /// falls back to the same ambient `gh repo view` observation RunLauncher and TaskPublishCommand
    /// already use for the identical shape (independent pre-PR review, cycle 3, medium): an earlier
    /// version of this guard read only RepositoryUrl and proceeded — treated the URL as safe —
    /// whenever that was null, which is exactly what --repo-only registration leaves forever.
    /// </summary>
    [Fact]
    public async Task A_repo_only_project_observes_its_repository_through_gh_and_still_rejects_a_foreign_pull_request()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = DomainId.New();
        Guid runId = DomainId.New();

        TaskAggregate task = await SeedFailedDispatchedRunWithoutRepositoryUrlAsync(store, ownerId, runId, cts.Token);
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{\"url\":\"https://github.com/x/y\"}");

        await using IDocumentSession session = store.LightweightSession();
        Uri? projectRepositoryUrl = await TaskResolveCommand.ResolveProjectRepositoryUrlAsync(
            session, task, "https://github.com/other-org/other-repo/pull/24", cts.Token, gh.Runner);
        TaskResolveCommand.RunStreamPullRequestOutcome outcome =
            await TaskResolveCommand.RecordPullRequestOnRunStreamAsync(
                session, task, "https://github.com/other-org/other-repo/pull/24", ResolveNow, projectRepositoryUrl, cts.Token);
        await session.SaveChangesAsync(cts.Token);

        outcome.Should().Be(TaskResolveCommand.RunStreamPullRequestOutcome.NotRecorded,
            "gh observed the project's real repository, and the --pr URL names a different one");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.PullRequestNumber.Should().BeNull();
    }

    /// <summary>
    /// The routed defect this fix closes (independent pre-PR review, cycle 2, adversarial, medium):
    /// <see cref="TaskResolveCommand.ResolveProjectRepositoryUrlAsync"/> used to short-circuit on
    /// <see cref="Hall9k.Connectors.WorkItems.PullRequestUrls.ParseNumber"/> returning zero or less,
    /// which is exactly what a non-pull-request-shaped URL (a commit link) does — so it never
    /// resolved the project's repository at all for this shape, and
    /// <see cref="TaskResolveCommand.SafeTaskStreamPullRequestUrl"/>'s own
    /// <see cref="Hall9k.Connectors.WorkItems.PullRequestUrls.NamesForeignRepository"/> check, fed a
    /// null project repository, treated a foreign commit link as "no mismatch" and let it reach the
    /// task stream verbatim. Exercised through the full pipeline exactly as <c>ExecuteAsync</c> calls
    /// it (<c>ResolveProjectRepositoryUrlAsync</c> then <c>SafeTaskStreamPullRequestUrl</c>), not with
    /// an explicit <c>Uri</c> handed to <c>SafeTaskStreamPullRequestUrl</c> directly, since that
    /// shortcut is exactly what let the defect through the unit tier undetected.
    /// </summary>
    [Fact]
    public async Task A_repo_only_project_observes_its_repository_through_gh_and_still_rejects_a_foreign_commit_link()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = DomainId.New();
        Guid runId = DomainId.New();

        TaskAggregate task = await SeedFailedDispatchedRunWithoutRepositoryUrlAsync(store, ownerId, runId, cts.Token);
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{\"url\":\"https://github.com/x/y\"}");
        const string foreignCommitLink = "https://github.com/other-org/other-repo/commit/deadbeef";

        await using IDocumentSession session = store.LightweightSession();
        Uri? projectRepositoryUrl = await TaskResolveCommand.ResolveProjectRepositoryUrlAsync(
            session, task, foreignCommitLink, cts.Token, gh.Runner);

        projectRepositoryUrl.Should().Be(new Uri("https://github.com/x/y"),
            "the project's repository must still be resolved through gh for a URL that is not " +
            "pull-request-shaped, since SafeTaskStreamPullRequestUrl's own repository-mismatch check " +
            "applies to every --pr shape, not only ones that parse to a pull request number");

        TaskResolveCommand.RunStreamPullRequestOutcome runStreamOutcome =
            await TaskResolveCommand.RecordPullRequestOnRunStreamAsync(
                session, task, foreignCommitLink, ResolveNow, projectRepositoryUrl, cts.Token);
        string? taskStreamPullRequestUrl = TaskResolveCommand.SafeTaskStreamPullRequestUrl(
            task, foreignCommitLink, runStreamOutcome, projectRepositoryUrl);
        await session.SaveChangesAsync(cts.Token);

        taskStreamPullRequestUrl.Should().BeNull(
            "a commit link naming a foreign repository must never reach the task stream, exactly like " +
            "a foreign pull request — the class of defect this whole task exists to close");
    }

    [Fact]
    public async Task A_repo_only_project_observes_its_repository_through_gh_and_still_accepts_its_own_pull_request()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = DomainId.New();
        Guid runId = DomainId.New();

        TaskAggregate task = await SeedFailedDispatchedRunWithoutRepositoryUrlAsync(store, ownerId, runId, cts.Token);
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{\"url\":\"https://github.com/x/y\"}");

        await using IDocumentSession session = store.LightweightSession();
        Uri? projectRepositoryUrl = await TaskResolveCommand.ResolveProjectRepositoryUrlAsync(
            session, task, "https://github.com/x/y/pull/24", cts.Token, gh.Runner);
        TaskResolveCommand.RunStreamPullRequestOutcome outcome =
            await TaskResolveCommand.RecordPullRequestOnRunStreamAsync(
                session, task, "https://github.com/x/y/pull/24", ResolveNow, projectRepositoryUrl, cts.Token);
        await session.SaveChangesAsync(cts.Token);

        outcome.Should().Be(TaskResolveCommand.RunStreamPullRequestOutcome.Recorded);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.PullRequestNumber.Should().Be(24);
    }

    /// <summary>
    /// A resolve with no --pr must stay exactly as inert as it was before this guard existed
    /// (independent pre-PR review, cycle 2, medium): with no URL to check safety for, there is
    /// nothing worth resolving the project's repository for at all, so a --repo-only project
    /// (no --repo-url) must never pay ResolveProjectRepositoryUrlAsync's gh fallback just to
    /// discard the answer immediately. Resolved exactly once now (independent pre-PR review,
    /// cycle 1, adversarial, medium), so this guard lives in ResolveProjectRepositoryUrlAsync
    /// itself rather than in each of its two callers.
    /// </summary>
    [Fact]
    public async Task A_resolve_with_no_pull_request_url_never_shells_out_to_gh()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = DomainId.New();
        Guid runId = DomainId.New();

        TaskAggregate task = await SeedFailedDispatchedRunWithoutRepositoryUrlAsync(store, ownerId, runId, cts.Token);
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{\"url\":\"https://github.com/x/y\"}");

        await using IDocumentSession session = store.LightweightSession();
        Uri? projectRepositoryUrl = await TaskResolveCommand.ResolveProjectRepositoryUrlAsync(
            session, task, null, cts.Token, gh.Runner);
        projectRepositoryUrl.Should().BeNull();
        gh.Calls.Should().BeEmpty("no --pr was given, so there is nothing to resolve the repository for");

        TaskResolveCommand.RunStreamPullRequestOutcome outcome =
            await TaskResolveCommand.RecordPullRequestOnRunStreamAsync(
                session, task, null, ResolveNow, projectRepositoryUrl, cts.Token);
        await session.SaveChangesAsync(cts.Token);

        outcome.Should().Be(TaskResolveCommand.RunStreamPullRequestOutcome.NotRecorded);
    }

    /// <summary>
    /// A pr-review task with no <see cref="TaskAggregate.CurrentRunId"/> at all — a Failed task
    /// that never reached a live run, the same shape the class-level doc comment above describes —
    /// pays neither the <c>ProjectDetails</c> load nor the <c>gh</c> fallback for its repository:
    /// both downstream guards discard the answer regardless (independent pre-PR review, cycle 1,
    /// adversarial, low). <see cref="RecordPullRequestOnRunStreamAsync"/> returns
    /// <see cref="TaskResolveCommand.RunStreamPullRequestOutcome.NoRunStream"/> before ever touching
    /// it, and <see cref="TaskResolveCommand.SafeTaskStreamPullRequestUrl"/> excludes a pr-review
    /// task's URL outright in exactly that shape.
    /// </summary>
    [Fact]
    public async Task A_pr_review_task_with_no_current_run_never_resolves_a_repository()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = DomainId.New();

        TaskAggregate task = SeedQueuedTask(ownerId, TaskType.PrReview).Task;
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{\"url\":\"https://github.com/x/y\"}");

        await using IDocumentSession session = store.LightweightSession();
        Uri? projectRepositoryUrl = await TaskResolveCommand.ResolveProjectRepositoryUrlAsync(
            session, task, "https://github.com/x/y/pull/24", cts.Token, gh.Runner);

        projectRepositoryUrl.Should().BeNull();
        gh.Calls.Should().BeEmpty(
            "a pr-review task with no current run has no downstream guard left that would ever use " +
            "the answer, so resolving it — gh fallback included — is pure waste");
    }

    /// <summary>
    /// The routed defect this method exists to close: with a run stream that DID exist,
    /// <see cref="TaskResolveCommand.RecordPullRequestOnRunStreamAsync"/> refuses to append a
    /// foreign --pr onto the run stream (it comes back <c>NotRecorded</c>), but before this fix the
    /// task stream's own copy was written from <c>settings.PullRequestUrl</c> verbatim in that case —
    /// reasoning only "a run stream exists, so this must already be safe". A task later reopened
    /// through <c>h9k pr resolve</c> would carry that unguarded URL into
    /// <c>RunLauncher.TryCloseOutMergedPullRequestAsync</c>'s own dispatch-time recheck, which parses
    /// only the number and asks <c>gh</c> about it inside the project's own repository — so an
    /// unrelated repository's merged pull request could falsely close this task out.
    /// </summary>
    [Fact]
    public async Task A_foreign_pull_request_whose_run_stream_exists_records_nothing_on_the_task_stream_either()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = DomainId.New();
        Guid runId = DomainId.New();

        TaskAggregate task = await SeedFailedDispatchedRunAsync(store, ownerId, runId, cts.Token);
        Uri projectRepositoryUrl = new("https://github.com/x/y");

        await using IDocumentSession session = store.LightweightSession();
        TaskResolveCommand.RunStreamPullRequestOutcome runStreamOutcome =
            await TaskResolveCommand.RecordPullRequestOnRunStreamAsync(
                session, task, "https://github.com/other-org/other-repo/pull/24", ResolveNow, projectRepositoryUrl,
                cts.Token);
        runStreamOutcome.Should().Be(TaskResolveCommand.RunStreamPullRequestOutcome.NotRecorded,
            "the run stream existed, but the URL names a foreign repository");

        string? recorded = TaskResolveCommand.SafeTaskStreamPullRequestUrl(
            task, "https://github.com/other-org/other-repo/pull/24", runStreamOutcome, projectRepositoryUrl);

        recorded.Should().BeNull(
            "a run stream existing must never be read as \"this URL is already safe\" — the task " +
            "stream's own guard has to be checked independently, or a later h9k pr resolve could " +
            "watch an unrelated repository's pull request and falsely close this task out");
    }


    /// <summary>
    /// Mirrors TaskWorkCommand.ClaimInteractivelyAsync's own shape up to the exact point its
    /// worktree cut can fail: TaskClaimed lands, then TaskWorkCommand.FailInteractiveClaimAsync
    /// appends TaskFailed to the task stream alone — the run stream is never started, because
    /// RunDispatched is only ever appended after the checkout succeeds.
    /// </summary>
    private static async Task<TaskAggregate> SeedFailedInteractiveClaimWithNoRunStreamAsync(
        DocumentStore store, Guid ownerId, Guid runId, CancellationToken cancellationToken)
    {
        (Guid taskId, TaskAggregate task, List<object> taskEvents, Guid projectId) = SeedQueuedTask(ownerId);

        Hall9k.Domain.Features.Tasks.Events.TaskClaimed claimed =
            TaskDecider.ClaimInteractively(task, ownerId, runId, ResolveNow);
        task.Apply(claimed);
        taskEvents.Add(claimed);

        Hall9k.Domain.Features.Tasks.Events.TaskFailed failed =
            TaskDecider.Fail(task, runId, "cancelled while preparing the worktree", ResolveNow);
        task.Apply(failed);
        taskEvents.Add(failed);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, [.. taskEvents]);
        SeedProject(session, projectId);
        await session.SaveChangesAsync(cancellationToken);

        return task;
    }

    /// <summary>
    /// The ordinary shape a headless dispatch leaves a Failed task in: the run stream did start
    /// (RunDispatched), and later failed on its own (RunFailed) — the case
    /// RecordPullRequestOnRunStreamAsync's guard must still append onto.
    /// </summary>
    private static async Task<TaskAggregate> SeedFailedDispatchedRunAsync(
        DocumentStore store, Guid ownerId, Guid runId, CancellationToken cancellationToken,
        TaskType? type = null)
    {
        (Guid taskId, TaskAggregate task, List<object> taskEvents, Guid projectId) =
            SeedQueuedTask(ownerId, type ?? TaskType.Chore);
        Guid nodeId = DomainId.New();

        Hall9k.Domain.Features.Tasks.Events.TaskClaimed claimed =
            TaskDecider.Claim(task, nodeId, ownerId, runId, ResolveNow);
        task.Apply(claimed);
        taskEvents.Add(claimed);

        Hall9k.Domain.Features.Tasks.Events.TaskFailed failed =
            TaskDecider.Fail(task, runId, "the gates never went green", ResolveNow);
        task.Apply(failed);
        taskEvents.Add(failed);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, [.. taskEvents]);
        session.Events.StartStream<RunAggregate>(runId,
            new RunDispatched(
                runId, taskId, nodeId, ownerId, task.LeaseGeneration, DomainId.New(),
                "/tmp/resolve-worktree", "task/resolve-branch", ExecutorMode.Subscription, ResolveNow),
            new RunFailed(runId, "the gates never went green", ResolveNow));
        SeedProject(session, projectId);
        await session.SaveChangesAsync(cancellationToken);

        return task;
    }

    /// <summary>
    /// The same shape as <see cref="SeedFailedDispatchedRunAsync"/>, but registered with --repo
    /// and no --repo-url — the shape ResolveProjectRepositoryUrlAsync's gh fallback exists for
    /// (ProjectDetails.RepositoryUrl stays null forever, since nothing backfills it and there is
    /// no h9k project set --repo-url).
    /// </summary>
    private static async Task<TaskAggregate> SeedFailedDispatchedRunWithoutRepositoryUrlAsync(
        DocumentStore store, Guid ownerId, Guid runId, CancellationToken cancellationToken)
    {
        (Guid taskId, TaskAggregate task, List<object> taskEvents, Guid projectId) =
            SeedQueuedTask(ownerId, TaskType.Chore);
        Guid nodeId = DomainId.New();

        Hall9k.Domain.Features.Tasks.Events.TaskClaimed claimed =
            TaskDecider.Claim(task, nodeId, ownerId, runId, ResolveNow);
        task.Apply(claimed);
        taskEvents.Add(claimed);

        Hall9k.Domain.Features.Tasks.Events.TaskFailed failed =
            TaskDecider.Fail(task, runId, "the gates never went green", ResolveNow);
        task.Apply(failed);
        taskEvents.Add(failed);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, [.. taskEvents]);
        session.Events.StartStream<RunAggregate>(runId,
            new RunDispatched(
                runId, taskId, nodeId, ownerId, task.LeaseGeneration, DomainId.New(),
                "/tmp/resolve-worktree", "task/resolve-branch", ExecutorMode.Subscription, ResolveNow),
            new RunFailed(runId, "the gates never went green", ResolveNow));
        SeedProjectWithRepositoryUrl(session, projectId, repositoryUrl: null);
        await session.SaveChangesAsync(cancellationToken);

        return task;
    }

    private static (Guid TaskId, TaskAggregate Task, List<object> Events, Guid ProjectId) SeedQueuedTask(
        Guid ownerId, TaskType? type = null)
    {
        Guid taskId = DomainId.New();
        Guid projectId = DomainId.New();

        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(
                taskId, projectId, "Close me out", ["merged"], type ?? TaskType.Chore, null, null,
                null, ResolveNow, ownerId),
            ownerId, ResolveNow);

        return (taskId, task, [.. lifecycle], projectId);
    }

    private static void SeedProject(IDocumentSession session, Guid projectId) =>
        SeedProjectWithRepositoryUrl(session, projectId, new Uri("https://github.com/x/y"));

    private static void SeedProjectWithRepositoryUrl(IDocumentSession session, Guid projectId, Uri? repositoryUrl)
    {
        var registered = ProjectDecider.Register(
            projectId, Guid.Empty, DomainId.New(), $"resolve-{projectId:N}", "/tmp/resolve-repo",
            repositoryUrl, "main", ResolveNow);
        session.Events.StartStream<Hall9k.Domain.Features.Project.ProjectAggregate>(registered.Id, registered);
    }
}
