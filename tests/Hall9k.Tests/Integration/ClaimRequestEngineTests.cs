using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Marten;
using Marten.Events;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The cooperative take's own shared engine (idea 202383dc, item 5, "a member can ask a holder for
/// a task"), against real Marten/Postgres but a <see cref="FakeLedger"/> stand-in for the ledger
/// and a scripted <see cref="ProcessRunner"/> stand-in for <c>gh</c> (Brian's 2026-09-13 testing
/// rule) — every send here is <see cref="MessageOutbox.QueueAsync"/> alone, which never touches
/// the transport, so no transport fake is needed for these tests either.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection("Hall9kHome")]
[Trait("Category", "Hall9kHome")]
public sealed class ClaimRequestEngineTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
    private const string RepositoryPath = "/does/not/matter/on/a/fake/ledger";

    private readonly PostgresFixture _postgres;
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"hall9k-claim-request-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    public ClaimRequestEngineTests(PostgresFixture postgres) => _postgres = postgres;

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
    public async Task Auto_grants_when_no_run_is_live()
    {
        (ProjectDetails project, Guid taskId, Guid myNodeId, BootstrapContext context) =
            await SeedHeldTaskAsync(TakePolicy.Auto, ClaimGate.Off, externalReference: null);
        FakeLedger ledger = new();
        await SeedRecordAsync(ledger, taskId, new TaskRecordHolder("my-fingerprint", myNodeId, "MY-NODE", Now.AddHours(-2)));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (LedgerCommitter committer, LedgerSigningKey signingKey, string ownerFingerprint) =
            await TaskRecordPublication.ResolveIdentityAsync(session, context, CancellationToken.None);

        Guid requesterNodeId = DomainId.New();
        Guid requesterOwnerId = DomainId.New();
        ClaimEnvelopeCodec.ClaimRequestRecord request = new(
            taskId, requesterNodeId, requesterOwnerId, "requester-fingerprint", "Picking this back up.", null);

        ClaimRequestOutcome outcome = await ClaimRequestEngine.ReceiveRequestAsync(
            _postgres.Store, session, project, taskId, request, ledger, committer, signingKey, take: null, myNodeId,
            ownerFingerprint, Now, CancellationToken.None);

        outcome.Verdict.Should().Be(ClaimRequestVerdict.Granted);
        ledger.Writes.Should().Contain(write => write.Path.Contains(taskId.ToString()));

        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: CancellationToken.None))!;
        task.HolderNodeId.Should().BeNull("a grant is an ordinary release — the requester claims through the ordinary lock");
        task.AssignedOwnerId.Should().Be(requesterOwnerId);
        task.State.Should().Be(TaskState.Queued);
        task.PendingTakeRequestedByNodeId.Should().BeNull();

        IReadOnlyList<MessageDetails> queued = await session.Query<MessageDetails>().ToListAsync(CancellationToken.None);
        queued.Should().ContainSingle(message =>
            message.Kind == MessageKind.ClaimGranted.Value && message.To == MessageAudience.Node(requesterNodeId).Value);
    }

    [Fact]
    public async Task Auto_refuses_when_a_run_is_live()
    {
        (ProjectDetails project, Guid taskId, Guid myNodeId, BootstrapContext context) =
            await SeedHeldTaskAsync(TakePolicy.Auto, ClaimGate.Off, externalReference: null);
        FakeLedger ledger = new();
        await SeedRecordAsync(ledger, taskId, new TaskRecordHolder("my-fingerprint", myNodeId, "MY-NODE", Now.AddHours(-2)));

        await using IDocumentSession runSession = _postgres.Store.LightweightSession();
        runSession.Store(new RunListItem
        {
            Id = DomainId.New(), TaskId = taskId, NodeId = myNodeId, State = RunState.Running, DispatchedAt = Now.AddMinutes(-10),
        });
        await runSession.SaveChangesAsync(CancellationToken.None);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (LedgerCommitter committer, LedgerSigningKey signingKey, string ownerFingerprint) =
            await TaskRecordPublication.ResolveIdentityAsync(session, context, CancellationToken.None);

        Guid requesterNodeId = DomainId.New();
        Guid requesterOwnerId = DomainId.New();
        ClaimEnvelopeCodec.ClaimRequestRecord request = new(
            taskId, requesterNodeId, requesterOwnerId, "requester-fingerprint", "Picking this back up.", null);
        int writesBeforeRefusal = ledger.Writes.Count;

        ClaimRequestOutcome outcome = await ClaimRequestEngine.ReceiveRequestAsync(
            _postgres.Store, session, project, taskId, request, ledger, committer, signingKey, take: null, myNodeId,
            ownerFingerprint, Now, CancellationToken.None);

        outcome.Verdict.Should().Be(ClaimRequestVerdict.Refused);
        outcome.RefusalReason.Should().Contain("live");
        ledger.Writes.Should().HaveCount(writesBeforeRefusal, "a refusal never touches the ledger holder");

        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: CancellationToken.None))!;
        task.HolderNodeId.Should().Be(myNodeId, "a refusal changes nothing about who holds the task");
        task.LastTakeRefusedRequesterOwnerId.Should().Be(requesterOwnerId);

        IReadOnlyList<MessageDetails> queued = await session.Query<MessageDetails>().ToListAsync(CancellationToken.None);
        queued.Should().ContainSingle(message =>
            message.Kind == MessageKind.ClaimRefused.Value && message.To == MessageAudience.Node(requesterNodeId).Value);
    }

    [Fact]
    public async Task Ask_parks_and_grant_answers()
    {
        (ProjectDetails project, Guid taskId, Guid myNodeId, BootstrapContext context) =
            await SeedHeldTaskAsync(TakePolicy.Ask, ClaimGate.Off, externalReference: null);
        FakeLedger ledger = new();
        await SeedRecordAsync(ledger, taskId, new TaskRecordHolder("my-fingerprint", myNodeId, "MY-NODE", Now.AddHours(-2)));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (LedgerCommitter committer, LedgerSigningKey signingKey, string ownerFingerprint) =
            await TaskRecordPublication.ResolveIdentityAsync(session, context, CancellationToken.None);

        Guid requesterNodeId = DomainId.New();
        Guid requesterOwnerId = DomainId.New();
        ClaimEnvelopeCodec.ClaimRequestRecord request = new(
            taskId, requesterNodeId, requesterOwnerId, "requester-fingerprint", "Picking this back up.", null);
        int writesBeforeRequest = ledger.Writes.Count;

        ClaimRequestOutcome parked = await ClaimRequestEngine.ReceiveRequestAsync(
            _postgres.Store, session, project, taskId, request, ledger, committer, signingKey, take: null, myNodeId,
            ownerFingerprint, Now, CancellationToken.None);

        parked.Verdict.Should().Be(ClaimRequestVerdict.Parked);
        ledger.Writes.Should().HaveCount(writesBeforeRequest, "ask parks it for the human — nothing decided yet");

        TaskAggregate parkedTask = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: CancellationToken.None))!;
        parkedTask.PendingTakeRequestedByNodeId.Should().Be(requesterNodeId);
        parkedTask.PendingTakeReason.Should().Be("Picking this back up.");

        // h9k task grant answers it — the identical method an auto grant runs (GrantAsync).
        await using IDocumentSession grantSession = _postgres.Store.LightweightSession();
        ClaimRequestOutcome granted = await ClaimRequestEngine.GrantAsync(
            _postgres.Store, grantSession, project, taskId, requesterNodeId, requesterOwnerId, null, ledger,
            committer, signingKey, take: null, myNodeId, ownerFingerprint, Now.AddMinutes(5), CancellationToken.None);

        granted.Verdict.Should().Be(ClaimRequestVerdict.Granted);
        ledger.Writes.Should().NotBeEmpty();

        TaskAggregate grantedTask = (await grantSession.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: CancellationToken.None))!;
        grantedTask.HolderNodeId.Should().BeNull();
        grantedTask.AssignedOwnerId.Should().Be(requesterOwnerId);
        grantedTask.State.Should().Be(TaskState.Queued);

        IReadOnlyList<MessageDetails> queued = await grantSession.Query<MessageDetails>().ToListAsync(CancellationToken.None);
        queued.Should().ContainSingle(message => message.Kind == MessageKind.ClaimGranted.Value);
    }

    [Fact]
    public async Task Grant_moves_the_gated_tracker_assignee_to_the_requesters_own_account()
    {
        ExternalReference reference = new(WorkItemProvider.GitHub, "acme/web#7");
        (ProjectDetails project, Guid taskId, Guid myNodeId, BootstrapContext context) =
            await SeedHeldTaskAsync(TakePolicy.Auto, ClaimGate.TrackerAssignee, reference);
        FakeLedger ledger = new();
        await SeedRecordAsync(ledger, taskId, new TaskRecordHolder("my-fingerprint", myNodeId, "MY-NODE", Now.AddHours(-2)));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (LedgerCommitter committer, LedgerSigningKey signingKey, string ownerFingerprint) =
            await TaskRecordPublication.ResolveIdentityAsync(session, context, CancellationToken.None);

        // The realistic starting shape (independent pre-PR review, cycle 1, both lenses): a gated
        // project's holder could only have claimed the ledger because the tracker already showed
        // the item assigned to THIS install's own identity — never unassigned. The scripted gh
        // below plays exactly that: "who am I" answers granter-login, the fresh read before the
        // grant shows granter-login already on the item, --remove-assignee takes it off,
        // --add-assignee puts teammate-login on, and the final read-back confirms teammate-login
        // alone.
        List<IReadOnlyList<string>> ghCalls = [];
        bool removed = false;
        bool assigned = false;
        ProcessRunner gh = (_, arguments, _, _) =>
        {
            ghCalls.Add(arguments);
            if (arguments.Contains("user"))
            {
                return Task.FromResult(new ProcessResult(0, "granter-login", string.Empty));
            }

            if (arguments.Contains("--remove-assignee"))
            {
                removed = true;
                return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            }

            if (arguments.Contains("--add-assignee"))
            {
                assigned = true;
                return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            }

            string body = assigned
                ? "{\"assignees\":[{\"login\":\"teammate-login\"}]}"
                : removed
                    ? "{\"assignees\":[]}"
                    : "{\"assignees\":[{\"login\":\"granter-login\"}]}";
            return Task.FromResult(new ProcessResult(0, body, string.Empty));
        };
        TrackerAssignmentTake take = new(gh, requester: null);

        Guid requesterNodeId = DomainId.New();
        Guid requesterOwnerId = DomainId.New();
        ClaimEnvelopeCodec.ClaimRequestRecord request = new(
            taskId, requesterNodeId, requesterOwnerId, "requester-fingerprint", "Picking this back up.",
            "teammate-login");

        ClaimRequestOutcome outcome = await ClaimRequestEngine.ReceiveRequestAsync(
            _postgres.Store, session, project, taskId, request, ledger, committer, signingKey, take, myNodeId,
            ownerFingerprint, Now, CancellationToken.None);

        outcome.Verdict.Should().Be(ClaimRequestVerdict.Granted);
        outcome.TrackerFailureReason.Should().BeNull("the tracker move landed and confirmed, so nothing here failed");
        ghCalls.Should().Contain(call => call.Contains("--remove-assignee") && call.Contains("granter-login"));
        ghCalls.Should().Contain(call => call.Contains("--add-assignee") && call.Contains("teammate-login"));
    }

    [Fact]
    public async Task Grant_names_the_hand_step_when_the_requesters_tracker_identity_is_unknown()
    {
        ExternalReference reference = new(WorkItemProvider.GitHub, "acme/web#7");
        (ProjectDetails project, Guid taskId, Guid myNodeId, BootstrapContext context) =
            await SeedHeldTaskAsync(TakePolicy.Auto, ClaimGate.TrackerAssignee, reference);
        FakeLedger ledger = new();
        await SeedRecordAsync(ledger, taskId, new TaskRecordHolder("my-fingerprint", myNodeId, "MY-NODE", Now.AddHours(-2)));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (LedgerCommitter committer, LedgerSigningKey signingKey, string ownerFingerprint) =
            await TaskRecordPublication.ResolveIdentityAsync(session, context, CancellationToken.None);

        Guid requesterNodeId = DomainId.New();
        Guid requesterOwnerId = DomainId.New();
        // No RequesterTrackerIdentity carried — the requester's own read failed or the request
        // predates this build's own field.
        ClaimEnvelopeCodec.ClaimRequestRecord request = new(
            taskId, requesterNodeId, requesterOwnerId, "requester-fingerprint", "Picking this back up.", null);

        ClaimRequestOutcome outcome = await ClaimRequestEngine.ReceiveRequestAsync(
            _postgres.Store, session, project, taskId, request, ledger, committer, signingKey, take: null, myNodeId,
            ownerFingerprint, Now, CancellationToken.None);

        outcome.Verdict.Should().Be(ClaimRequestVerdict.Granted, "the grant itself never blocks on the tracker move");
        outcome.TrackerFailureReason.Should().Contain("by hand");
    }

    [Fact]
    public async Task Receiving_a_request_for_a_task_this_node_no_longer_holds_records_nothing()
    {
        // A stale or misdirected claim request — the holder moved on since the requester sent it —
        // must never park a request nothing here can actually answer (self-review, this branch).
        (ProjectDetails project, Guid taskId, Guid myNodeId, BootstrapContext context) =
            await SeedHeldTaskAsync(TakePolicy.Auto, ClaimGate.Off, externalReference: null);
        FakeLedger ledger = new();
        await SeedRecordAsync(ledger, taskId, new TaskRecordHolder("my-fingerprint", myNodeId, "MY-NODE", Now.AddHours(-2)));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (LedgerCommitter committer, LedgerSigningKey signingKey, string ownerFingerprint) =
            await TaskRecordPublication.ResolveIdentityAsync(session, context, CancellationToken.None);

        ClaimEnvelopeCodec.ClaimRequestRecord request = new(
            taskId, DomainId.New(), DomainId.New(), "requester-fingerprint", "Picking this back up.", null);
        // A different node than the one this envelope was actually addressed to.
        Guid someOtherNodeId = DomainId.New();

        Func<Task> act = () => ClaimRequestEngine.ReceiveRequestAsync(
            _postgres.Store, session, project, taskId, request, ledger, committer, signingKey, take: null,
            someOtherNodeId, ownerFingerprint, Now, CancellationToken.None);

        await act.Should().ThrowAsync<DomainConflictException>();

        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: CancellationToken.None))!;
        task.PendingTakeRequestedByNodeId.Should().BeNull("nothing here can actually grant or refuse on behalf of the real holder");
    }

    [Fact]
    public async Task A_lost_append_race_during_the_grant_rolls_the_ledger_release_back()
    {
        // The high-severity defect this test guards against (self-review, this branch): the
        // ledger release lands before the domain event does, so a task-stream write racing in
        // between (a handoff note, say) leaves the append below conflicting on a stale fence —
        // and without a rollback, the ledger would sit holder-less forever while this task's own
        // stream never recorded releasing it, the identical double-claim hazard TaskTakeCommand's
        // own --force rollback exists to prevent for the analogous override race.
        ExternalReference reference = new(WorkItemProvider.GitHub, "acme/web#7");
        (ProjectDetails project, Guid taskId, Guid myNodeId, BootstrapContext context) =
            await SeedHeldTaskAsync(TakePolicy.Auto, ClaimGate.TrackerAssignee, reference);
        FakeLedger ledger = new();
        await SeedRecordAsync(ledger, taskId, new TaskRecordHolder("my-fingerprint", myNodeId, "MY-NODE", Now.AddHours(-2)));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (LedgerCommitter committer, LedgerSigningKey signingKey, string ownerFingerprint) =
            await TaskRecordPublication.ResolveIdentityAsync(session, context, CancellationToken.None);

        // The gated tracker move's own read (ahead of any write) is reached only after the ledger
        // release above has already landed and only before this call's own append — exactly the
        // window the race needs to land in, mirroring TaskTakeCommandTests's own injection through
        // a scripted gh call.
        bool raced = false;
        ProcessRunner gh = async (_, arguments, _, cancellationToken) =>
        {
            if (!raced && !arguments.Contains("edit"))
            {
                raced = true;
                await using IDocumentSession raceSession = _postgres.Store.LightweightSession();
                TaskAggregate raceTask = (await raceSession.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken))!;
                raceSession.Events.Append(taskId, TaskDecider.LeaveHandoff(raceTask, "racing", myNodeId, "my-fingerprint", Now));
                await raceSession.SaveChangesAsync(cancellationToken);
            }

            return new ProcessResult(0, "{\"assignees\":[]}", string.Empty);
        };
        TrackerAssignmentTake take = new(gh, requester: null);

        Guid requesterNodeId = DomainId.New();
        Guid requesterOwnerId = DomainId.New();

        Func<Task> act = () => ClaimRequestEngine.GrantAsync(
            _postgres.Store, session, project, taskId, requesterNodeId, requesterOwnerId, "teammate-login", ledger,
            committer, signingKey, take, myNodeId, ownerFingerprint, Now, CancellationToken.None);

        await act.Should().ThrowAsync<DomainConflictException>().WithMessage("*rolled back*");

        LedgerFile record = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId), CancellationToken.None);
        TaskRecord.TryParse(record.Content)!.Holder!.NodeId.Should().Be(
            myNodeId, "the release is rolled back rather than left holder-less for a stream that never recorded it");
    }

    private async Task<(ProjectDetails Project, Guid TaskId, Guid MyNodeId, BootstrapContext Context)> SeedHeldTaskAsync(
        TakePolicy takePolicy, ClaimGate gate, ExternalReference? externalReference)
    {
        if (gate == ClaimGate.TrackerAssignee)
        {
            await NodeBootstrapSeed.SeedGitHubConnectionAsync(_postgres.Store, CancellationToken.None);
        }

        await using IDocumentSession bootstrapSession = _postgres.Store.LightweightSession();
        BootstrapContext context = await NodeBootstrap.EnsureAsync(bootstrapSession, CancellationToken.None);
        await bootstrapSession.SaveChangesAsync(CancellationToken.None);

        Guid projectId = DomainId.New();
        ProjectDetails project = new()
        {
            Id = projectId,
            Name = $"take-{projectId:N}"[..12],
            RepositoryPath = RepositoryPath,
            BaseBranch = "main",
            ClaimGate = gate,
            TakePolicy = takePolicy,
            BranchNameTemplate = BranchNameTemplate.Default,
        };
        await using IDocumentSession projectSession = _postgres.Store.LightweightSession();
        projectSession.Store(project);
        await projectSession.SaveChangesAsync(CancellationToken.None);

        Guid taskId = DomainId.New();
        await using IDocumentSession taskSession = _postgres.Store.LightweightSession();
        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(
                taskId, projectId, "Close me out", ["done"], TaskType.Chore, null, null,
                externalReference, Now, context.OwnerId),
            context.OwnerId, Now);
        var claimed = TaskDecider.Claim(task, context.NodeId, context.OwnerId, DomainId.New(), Now.AddHours(-2));
        taskSession.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
        await taskSession.SaveChangesAsync(CancellationToken.None);

        return (project, taskId, context.NodeId, context);
    }

    private static async Task SeedRecordAsync(FakeLedger ledger, Guid taskId, TaskRecordHolder? holder)
    {
        TaskRecord record = new(
            taskId, "take-test", "chore", "Close me out", ["done"], null, null,
            PreApprovalMode.Off, null, [], null, null, TaskRecordCaps.None, "owner-a-fingerprint",
            new TaskOrigin(DomainId.New(), "ORIGIN-NODE", taskId, "task/close-me-out", Now), holder);
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId),
                record.ToYaml(), null, "seed", new LedgerCommitter("Test", "test@hall9k.local"),
                new LedgerSigningKey("/does/not/matter/key")),
            CancellationToken.None);
    }
}
