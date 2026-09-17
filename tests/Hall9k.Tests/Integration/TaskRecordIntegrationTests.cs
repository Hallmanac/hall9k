using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Ledger;
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
/// The task record's own integration surfaces — what it writes into the project's own ledger, what
/// <see cref="TaskRecordAdoption.LocateAsync"/> (and, through it,
/// <see cref="TaskAddCommand.RefuseIfRecordedElsewhereAsync"/>) makes of a record it finds there,
/// and what <c>h9k task resolve --pr</c> writes onto a run stream — sharing one container across
/// the three seams below. Each was its own class and so its own container for nine to thirteen
/// tests; every assertion here reads what its own test seeded, by id or by its own external
/// reference (no two seams here name the same issue), so a sibling seam's rows are as invisible as
/// a sibling test's already were.
/// <para>
/// The whole crossing, against a real store and <see cref="FakeLedger"/> (idea 202383dc, A3a: a
/// task's record moved from a collapsed section of its GitHub issue body into
/// <c>records/&lt;task-id&gt;.yaml</c> on <c>refs/hall9k/ledger/records</c>, ruled 2026-09-12): what
/// publish writes into the ledger, what revise rewrites there, and what
/// <see cref="TaskRecordAdoption.LocateAsync"/> makes of a record naming a task this store already
/// holds versus one it does not — including the one reference that had to change form to survive
/// the crossing, a dependency edge, now a task id rather than a tracker's own issue number (task ids
/// are the same on every node; Brian, 2026-09-13). Per Brian's 2026-09-13 testing rule, no test here
/// touches a real git repository or remote — <see cref="FakeLedger"/> is the seam every test below
/// drives instead; <c>GitLedgerTests</c> and the chain reader's own tests are the only ones allowed
/// to use real git.
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
    private const string RepositoryPath = "/repos/hall9k";
    private static readonly LedgerCommitter Committer = new("Ledger Test", "ledger-test@hall9k.local");
    // FakeLedger never reads this path — it only checks a signing key is present, the same gate
    // GitLedger itself enforces (RequireSigningKey) — so a real key on disk is never needed here.
    private static readonly LedgerSigningKey SigningKey = new("/fake/signing-key-never-read-by-fakeledger");

    [Fact]
    public async Task Publish_writes_the_record_to_the_ledger_with_every_field()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Install origin = await SeedAsync(store, cts.Token);

        Guid epicId = DomainId.New();
        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<EpicAggregate>(epicId, EpicDecider.Add(
                epicId, origin.ProjectId, "Distributed team on the tracker", Now, origin.OwnerId));
            await seed.SaveChangesAsync(cts.Token);
        }

        Guid firstBlocker = await AddTaskAsync(store, origin, "The first blocker", ["Something"], null, cts.Token);
        Guid secondBlocker = await AddTaskAsync(store, origin, "The second blocker", ["Something"], null, cts.Token);

        Guid taskId = await AddTaskAsync(
            store, origin, "Carry the whole task record in the ledger",
            ["Publishing writes it", "Any node that shares the project reads it back"],
            agentContext: "Origin: Brian. The Mac publishes, the Windows node adopts.",
            cts.Token, blockedBy: [firstBlocker, secondBlocker], model: AgentModel.FromInput("opus"), epicId: epicId);
        await LinkIssueAsync(store, taskId, 1266, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate withCaps = (await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.OverrideReviewCaps(
                withCaps, Optional<int?>.Of(3), Optional<int?>.None, Optional<int?>.Of(2), Optional<int?>.None,
                Now, origin.OwnerId));
            session.Events.Append(taskId, TaskDecider.OverrideSessionCap(withCaps, 4, Now, origin.OwnerId));
            await session.SaveChangesAsync(cts.Token);
        }

        FakeLedger ledger = new();
        TaskRecordPublication.WriteOutcome outcome = await WriteRecordAsync(store, origin, taskId, ledger, cts.Token);

        outcome.Should().Be(TaskRecordPublication.WriteOutcome.Written);
        ledger.Writes.Should().ContainSingle().Which.RefName.Should().Be(LedgerRefRegistry.Records.RefspecSource);
        ledger.Writes[0].Path.Should().Be(LedgerRefRegistry.RecordPath(taskId));

        LedgerFile stored = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId), cts.Token);
        stored.Exists.Should().BeTrue();

        TaskRecord record = TaskRecord.TryParse(stored.Content)!;
        record.TaskId.Should().Be(taskId);
        record.Project.Should().Be("hall9k");
        record.Type.Should().Be("feature");
        record.Objective.Should().Be("Carry the whole task record in the ledger");
        record.Criteria.Should().Equal("Publishing writes it", "Any node that shares the project reads it back");
        record.AgentContext.Should().Be("Origin: Brian. The Mac publishes, the Windows node adopts.");
        record.Model.Should().Be("opus");
        record.PreApproval.Should().Be(PreApprovalMode.Off);
        record.ExternalReference.Should().Be(new ExternalReference(WorkItemProvider.GitHub, $"{Repository}#1266"));
        record.Dependencies.Should().Equal(firstBlocker, secondBlocker);
        record.EpicId.Should().Be(epicId);
        record.EpicTitle.Should().Be("Distributed team on the tracker");
        record.Caps.MaxComplianceReviewCycles.Should().Be(3);
        record.Caps.MaxFinalFullPassRounds.Should().Be(2);
        record.Caps.SessionCap.Should().Be(4);
        record.Caps.MaxAdversarialReviewCycles.Should().BeNull();
        record.Caps.LifetimeReviewCycleBudget.Should().BeNull();
        record.OriginOwnerFingerprint.Should().Be("abc123fingerprint");
        record.Origin.NodeId.Should().Be(origin.NodeId);
        record.Origin.NodeName.Should().Be("HALLMANAC-MAC");
        record.Origin.TaskId.Should().Be(taskId);
        record.Origin.BranchName.Should().Be(
            BranchNameTemplate.Default.Render(taskId, "Carry the whole task record in the ledger", "1266"),
            "the record carries the branch the origin actually cuts, rendered through its own template");
        record.Origin.PublishedAt.Should().Be(Now);
        record.Holder.Should().BeNull("nothing has claimed this task yet — the holder lock (A3b) is not built");
    }

    [Fact]
    public async Task Revise_rewrites_the_same_ledger_path_with_the_new_content()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Install origin = await SeedAsync(store, cts.Token);
        Guid taskId = await AddTaskAsync(
            store, origin, "The first objective", ["The first criterion"], null, cts.Token);
        await LinkIssueAsync(store, taskId, 2266, cts.Token);

        FakeLedger ledger = new();
        (await WriteRecordAsync(store, origin, taskId, ledger, cts.Token))
            .Should().Be(TaskRecordPublication.WriteOutcome.Written);
        string firstContent = (await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId), cts.Token))
            .Content!;

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

        (await WriteRecordAsync(store, origin, taskId, ledger, cts.Token))
            .Should().Be(TaskRecordPublication.WriteOutcome.Written);

        LedgerFile rewritten = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId), cts.Token);
        rewritten.Content.Should().NotBe(firstContent, "the revision changed the objective the record carries");
        TaskRecord.TryParse(rewritten.Content)!.Objective.Should().Be("A reworded objective");
        ledger.Writes.Should().HaveCount(2, "publish wrote once and revise rewrote the same path");
        ledger.Writes.Select(write => write.Path).Distinct().Should().ContainSingle()
            .Which.Should().Be(LedgerRefRegistry.RecordPath(taskId), "revise rewrites the same file, never a second one");
    }

    [Fact]
    public async Task A_mirror_never_writes_to_the_ledger()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Install install = await SeedAsync(store, cts.Token);

        Guid taskId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
                taskId, install.ProjectId, "The adopted copy", ["Something"], TaskType.Feature,
                agentContext: null, constraints: null, externalReference: null, Now, install.OwnerId,
                origin: SomeOrigin()));
            await session.SaveChangesAsync(cts.Token);
        }

        FakeLedger ledger = new();
        TaskRecordPublication.WriteOutcome outcome = await WriteRecordAsync(store, install, taskId, ledger, cts.Token);

        outcome.Should().Be(TaskRecordPublication.WriteOutcome.Mirror);
        ledger.Writes.Should().BeEmpty(
            "this install never published this task — its record belongs to the install that did, and "
            + "writing over it here would overwrite that install's own record with this local copy");
        (await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId), cts.Token))
            .Exists.Should().BeFalse();
    }

    /// <summary>
    /// Brian, 2026-09-13: "a task with no tracker item gets a record too" — unlike the retired
    /// issue-block writer, which wrote a record only under the github-issues backlog policy, the
    /// ledger record has no backlog-policy gate at all (<see cref="TaskRecordPublication.WriteAsync"/>'s
    /// own doc comment).
    /// </summary>
    [Fact]
    public async Task A_project_that_tracks_nothing_still_gets_its_tasks_record_written()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Install install = await SeedAsync(store, cts.Token, BacklogPolicy.None);
        Guid taskId = await AddTaskAsync(store, install, "Local work", ["Something"], null, cts.Token);

        FakeLedger ledger = new();
        TaskRecordPublication.WriteOutcome outcome = await WriteRecordAsync(store, install, taskId, ledger, cts.Token);

        outcome.Should().Be(TaskRecordPublication.WriteOutcome.Written);
        (await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId), cts.Token))
            .Exists.Should().BeTrue();
    }

    /// <summary>
    /// The GitHub issue keeps only the human text it always had (idea 202383dc, A3a, ruled
    /// 2026-09-12): composing or regenerating its checklist never produces the collapsed
    /// <c>&lt;details&gt;</c> section the retired writer used to append, whatever the record itself
    /// now says — there is no code path left that could put one there at all
    /// (<see cref="GitHubIssueBody"/> exposes no method that writes a record block into a body).
    /// </summary>
    [Fact]
    public void The_composed_issue_body_never_carries_a_record_block()
    {
        string body = GitHubIssueBody.Compose(
            "Origin: Brian. The Mac publishes, the Windows node adopts.",
            ["Publishing writes it", "Any node that shares the project reads it back"]);

        body.Should().NotContain(TaskRecord.VersionKey).And.NotContain("<details>").And.NotContain("</details>");

        string rewritten = GitHubIssueBody.WithCriteriaChecklist(body, ["A new criterion"]);

        rewritten.Should().NotContain(TaskRecord.VersionKey).And.NotContain("<details>");
    }

    [Fact]
    public async Task LocateAsync_finds_a_record_whose_task_is_already_replicated_here()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Install install = await SeedAsync(store, cts.Token);

        ExternalReference reference = new(WorkItemProvider.GitHub, $"{Repository}#4001");
        Guid recordedTaskId = DomainId.New();
        // Seeded at the SAME id the ledger record names, but with NO external reference of its
        // own — this proves LocateAsync found it via the record's own task id (idea 202383dc, A3a's
        // third fork), never via the ordinary external-reference match
        // TaskAddCommand.RefuseSecondAdoptionAsync already covers.
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<TaskAggregate>(recordedTaskId, TaskDecider.Add(
                recordedTaskId, install.ProjectId, "Already replicated here", ["Something"], TaskType.Feature,
                agentContext: null, constraints: null, externalReference: null, Now, install.OwnerId));
            await session.SaveChangesAsync(cts.Token);
        }

        FakeLedger ledger = new();
        await SeedRecordAsync(ledger, recordedTaskId, reference, cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskRecordAdoption.Locate located = await TaskRecordAdoption.LocateAsync(
            query, ledger, RepositoryPath, reference, cts.Token);

        located.Outcome.Should().Be(TaskRecordAdoption.LocateOutcome.ExistingLocal);
        located.TaskId.Should().Be(recordedTaskId);
    }

    [Fact]
    public async Task LocateAsync_reports_a_record_whose_task_has_not_replicated_here_yet()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        await SeedAsync(store, cts.Token);

        ExternalReference reference = new(WorkItemProvider.GitHub, $"{Repository}#4002");
        Guid recordedTaskId = DomainId.New();
        FakeLedger ledger = new();
        await SeedRecordAsync(ledger, recordedTaskId, reference, cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskRecordAdoption.Locate located = await TaskRecordAdoption.LocateAsync(
            query, ledger, RepositoryPath, reference, cts.Token);

        located.Outcome.Should().Be(TaskRecordAdoption.LocateOutcome.StreamAbsent);
        located.TaskId.Should().Be(recordedTaskId);
    }

    /// <summary>
    /// An item with no record anywhere and no local task adopts exactly as it always did — the
    /// plain "title becomes objective, body becomes context" path
    /// <c>TaskAddCommand.ExecuteAsync</c> falls through to once neither
    /// <c>RefuseSecondAdoptionAsync</c> nor <c>RefuseIfRecordedElsewhereAsync</c> objects. That plain
    /// path itself is unaffected by this feature (a plain, unpublished issue never had a record to
    /// begin with) and is already covered elsewhere in the suite — <c>RepeatPullRequestAdoptionTests</c>
    /// and the ordinary <c>--from-issue</c>/<c>--from-jira</c> adoption tests exercise it; this test
    /// only pins <see cref="TaskRecordAdoption.LocateAsync"/>'s own half of "nothing found".
    /// </summary>
    [Fact]
    public async Task LocateAsync_with_no_record_anywhere_reports_none()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        await SeedAsync(store, cts.Token);

        FakeLedger ledger = new();
        await using IQuerySession query = store.QuerySession();
        TaskRecordAdoption.Locate located = await TaskRecordAdoption.LocateAsync(
            query, ledger, RepositoryPath, new ExternalReference(WorkItemProvider.GitHub, $"{Repository}#4003"), cts.Token);

        located.Outcome.Should().Be(TaskRecordAdoption.LocateOutcome.NoRecord);
        located.TaskId.Should().Be(Guid.Empty);
    }

    [Fact]
    public async Task RefuseIfRecordedElsewhereAsync_on_an_existing_local_task_names_it_and_creates_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Install install = await SeedAsync(store, cts.Token);

        ExternalReference reference = new(WorkItemProvider.GitHub, $"{Repository}#5001");
        Guid recordedTaskId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<TaskAggregate>(recordedTaskId, TaskDecider.Add(
                recordedTaskId, install.ProjectId, "Already replicated here", ["Something"], TaskType.Feature,
                agentContext: null, constraints: null, externalReference: null, Now, install.OwnerId));
            await session.SaveChangesAsync(cts.Token);
        }

        FakeLedger ledger = new();
        await SeedRecordAsync(ledger, recordedTaskId, reference, cts.Token);
        int before = await CountTasksAsync(store, cts.Token);

        await using IQuerySession query = store.QuerySession();
        Func<Task> refuse = () => TaskAddCommand.RefuseIfRecordedElsewhereAsync(
            query, ledger, RepositoryPath, reference, cts.Token);

        (await refuse.Should().ThrowAsync<DomainConflictException>()).Which.Message
            .Should().Contain(TaskListCommand.ShortId(recordedTaskId));
        (await CountTasksAsync(store, cts.Token)).Should().Be(before, "nothing new was created");
    }

    [Fact]
    public async Task RefuseIfRecordedElsewhereAsync_on_an_unreplicated_record_refuses_and_creates_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        await SeedAsync(store, cts.Token);

        ExternalReference reference = new(WorkItemProvider.GitHub, $"{Repository}#5002");
        Guid recordedTaskId = DomainId.New();
        FakeLedger ledger = new();
        await SeedRecordAsync(ledger, recordedTaskId, reference, cts.Token);
        int before = await CountTasksAsync(store, cts.Token);

        await using IQuerySession query = store.QuerySession();
        Func<Task> refuse = () => TaskAddCommand.RefuseIfRecordedElsewhereAsync(
            query, ledger, RepositoryPath, reference, cts.Token);

        (await refuse.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("published elsewhere").And.Contain(TaskListCommand.ShortId(recordedTaskId));
        (await CountTasksAsync(store, cts.Token)).Should().Be(before, "nothing is created while replication is pending");
    }

    private static TaskOrigin SomeOrigin() => new(
        Guid.Parse("01a07c7e-fed4-74bf-a0f8-ac5a7325335a"),
        "HALLMANAC-MAC",
        Guid.Parse("01a07909-b8a5-777d-9033-4318ba2a31b5"),
        "task/7325335a-carry-the-whole-task-record",
        Now);

    private static TaskRecord SomeRecord() => new(
        Guid.Parse("01a07909-b8a5-777d-9033-4318ba2a31b5"),
        "hall9k",
        "feature",
        "Carry the whole task record in the ledger",
        ["Publishing writes it", "Any node that shares the project reads it back"],
        "Origin: Brian. The Mac publishes, the Windows node adopts.",
        null,
        PreApprovalMode.Off,
        null,
        [],
        null,
        null,
        TaskRecordCaps.None,
        "abc123fingerprint",
        SomeOrigin(),
        null);

    /// <summary>Writes a record directly to <paramref name="ledger"/>, at the id and reference the caller wants — the seam <see cref="TaskRecordAdoption.LocateAsync"/>'s own tests read back through.</summary>
    private static async Task SeedRecordAsync(
        FakeLedger ledger, Guid taskId, ExternalReference reference, CancellationToken cancellationToken)
    {
        TaskRecord record = SomeRecord() with { TaskId = taskId, ExternalReference = reference };
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId),
                record.ToYaml(), null, "seed", Committer, SigningKey),
            cancellationToken);
    }

    private static async Task<int> CountTasksAsync(DocumentStore store, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        return await session.Query<TaskListItem>().CountAsync(cancellationToken);
    }

    private static async Task<TaskRecordPublication.WriteOutcome> WriteRecordAsync(
        DocumentStore store, Install install, Guid taskId, FakeLedger ledger, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, token: cancellationToken))!;
        ProjectDetails project = (await session.LoadAsync<ProjectDetails>(install.ProjectId, cancellationToken))!;

        return await TaskRecordPublication.WriteAsync(
            session, task, project, install.NodeId, "HALLMANAC-MAC", "abc123fingerprint", Now,
            ledger, Committer, SigningKey, cancellationToken);
    }

    private static async Task LinkIssueAsync(
        DocumentStore store, Guid taskId, int number, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        await TaskLinkIssueCommand.LinkAsync(
            session, taskId,
            new ImportedWorkItem(
                new ExternalReference(WorkItemProvider.GitHub, $"{Repository}#{number}"), $"Issue {number}", null,
                WorkItemStatus.Open, null, Now),
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
        IReadOnlyList<Guid>? blockedBy = null,
        AgentModel? model = null,
        Guid? epicId = null)
    {
        Guid taskId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
            taskId, install.ProjectId, objective, criteria, TaskType.Feature, agentContext,
            constraints: null, externalReference: null, Now, install.OwnerId, model: model, blockedBy: blockedBy,
            epicId: epicId));
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
            projectId, ownerId, connectionId, "hall9k", RepositoryPath,
            new Uri($"https://github.com/{Repository}"), null, Now);
        session.Events.StartStream<ProjectAggregate>(projectId, registered);

        // The ledger record has no backlog-policy gate at all (Brian, 2026-09-13: "a task with no
        // tracker item gets a record too") — backlogPolicy here only picks what a task's own
        // external reference looks like, never whether TaskRecordPublication.WriteAsync writes.
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
