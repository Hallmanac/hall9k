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
using Hall9k.Tests.TestSupport;
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
public sealed class ClaimRequestEngineTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
    private const string RepositoryPath = "/does/not/matter/on/a/fake/ledger";

    private readonly PostgresFixture _postgres;
    private readonly ScopedTestHome _scopedHome = new();

    public ClaimRequestEngineTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync()
    {
        _scopedHome.Dispose();
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
        //
        // The race is injected through the ledger's own release write now, via
        // RaceOnFirstWriteLedger below, rather than through the tracker move's own scripted gh
        // call as before (independent pre-PR review, cycle 5, adversarial lens): GrantAsync now
        // runs the tracker move only after its own append has already landed
        // (ClaimRequestEngine.cs), so a gh call can no longer land inside the release-to-append
        // window at all — this test's own race injection has to move with it.
        ExternalReference reference = new(WorkItemProvider.GitHub, "acme/web#7");
        (ProjectDetails project, Guid taskId, Guid myNodeId, BootstrapContext context) =
            await SeedHeldTaskAsync(TakePolicy.Auto, ClaimGate.TrackerAssignee, reference);
        FakeLedger innerLedger = new();
        await SeedRecordAsync(innerLedger, taskId, new TaskRecordHolder("my-fingerprint", myNodeId, "MY-NODE", Now.AddHours(-2)));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (LedgerCommitter committer, LedgerSigningKey signingKey, string ownerFingerprint) =
            await TaskRecordPublication.ResolveIdentityAsync(session, context, CancellationToken.None);

        RaceOnFirstWriteLedger ledger = new(innerLedger, async () =>
        {
            await using IDocumentSession raceSession = _postgres.Store.LightweightSession();
            TaskAggregate raceTask = (await raceSession.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: CancellationToken.None))!;
            raceSession.Events.Append(taskId, TaskDecider.LeaveHandoff(raceTask, "racing", myNodeId, "my-fingerprint", Now));
            await raceSession.SaveChangesAsync(CancellationToken.None);
        });

        // Never scripted to race — a plain, always-empty read/write — since the tracker move now
        // runs strictly after the append, and this test asserts below that it never runs at all.
        bool ghCalled = false;
        ProcessRunner gh = (_, _, _, _) =>
        {
            ghCalled = true;
            return Task.FromResult(new ProcessResult(0, "{\"assignees\":[]}", string.Empty));
        };
        TrackerAssignmentTake take = new(gh, requester: null);

        Guid requesterNodeId = DomainId.New();
        Guid requesterOwnerId = DomainId.New();

        Func<Task> act = () => ClaimRequestEngine.GrantAsync(
            _postgres.Store, session, project, taskId, requesterNodeId, requesterOwnerId, "teammate-login", ledger,
            committer, signingKey, take, myNodeId, ownerFingerprint, Now, CancellationToken.None);

        await act.Should().ThrowAsync<DomainConflictException>().WithMessage("*rolled back*");

        ghCalled.Should().BeFalse(
            "the tracker move must never run before the grant's own append lands — otherwise a lost append race "
            + "leaves the tracker permanently handed to a requester who was never actually granted the task");

        LedgerFile record = await innerLedger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId), CancellationToken.None);
        TaskRecord.TryParse(record.Content)!.Holder!.NodeId.Should().Be(
            myNodeId, "the release is rolled back rather than left holder-less for a stream that never recorded it");
    }

    /// <summary>
    /// Runs <paramref name="onFirstWrite"/> once, ahead of this ledger's own very first
    /// <see cref="WriteAsync"/> call, then delegates everything to <paramref name="inner"/> — the
    /// test-only way to land a concurrent task-stream write inside the exact window between
    /// <see cref="ClaimRequestEngine.GrantAsync"/>'s own ledger release and its domain event
    /// append, now that the gated tracker move (the previous injection point) runs after that
    /// append instead of before it.
    /// </summary>
    private sealed class RaceOnFirstWriteLedger(ILedger inner, Func<Task> onFirstWrite) : ILedger
    {
        private bool _raced;

        public Task<LedgerFile> ReadAsync(string repositoryPath, string refName, string path, CancellationToken cancellationToken) =>
            inner.ReadAsync(repositoryPath, refName, path, cancellationToken);

        public async Task<LedgerWriteOutcome> WriteAsync(LedgerWriteRequest request, CancellationToken cancellationToken)
        {
            if (!_raced)
            {
                _raced = true;
                await onFirstWrite();
            }

            return await inner.WriteAsync(request, cancellationToken);
        }

        public Task<LedgerWriteOutcome> WriteManyAsync(LedgerManyWriteRequest request, CancellationToken cancellationToken) =>
            inner.WriteManyAsync(request, cancellationToken);

        public Task<LedgerWriteOutcome> DeleteAsync(LedgerDeleteRequest request, CancellationToken cancellationToken) =>
            inner.DeleteAsync(request, cancellationToken);

        public Task<bool> HasAnyAsync(string repositoryPath, string refName, string pathPrefix, CancellationToken cancellationToken) =>
            inner.HasAnyAsync(repositoryPath, refName, pathPrefix, cancellationToken);

        public Task<IReadOnlyList<LedgerRef>> ListRefsAsync(string repositoryPath, string refPrefix, CancellationToken cancellationToken) =>
            inner.ListRefsAsync(repositoryPath, refPrefix, cancellationToken);

        public Task<IReadOnlyList<LedgerEntry>> ReadAllAsync(string repositoryPath, string refName, string pathPrefix, CancellationToken cancellationToken) =>
            inner.ReadAllAsync(repositoryPath, refName, pathPrefix, cancellationToken);
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
