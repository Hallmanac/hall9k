using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Trust;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <c>h9k task take --force</c> (idea 202383dc, item 4), against real Marten/Postgres but a
/// <see cref="FakeLedger"/>/<see cref="FakeLedgerChainReader"/> stand-in for the ledger and the
/// chain read, and a scripted <see cref="ProcessRunner"/> stand-in for <c>gh</c> (Brian's
/// 2026-09-13 testing rule).
/// </summary>
// The two success-path tests drive RunAsync all the way through, which rings the doorbell
// (Hall9k.Cli.Infrastructure.Doorbell). That resolves its connection off Hall9kDatabase.Resolve
// rather than this fixture, so each points it at the fixture for the duration of its own call
// through ScopedConnectionString, the same way ClaimRefusalTests and StoreBackedCommandTests do.
[Trait("Category", "RequiresDocker")]
public sealed class TaskTakeCommandTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private const string RepositoryPath = "/does/not/matter/on/a/fake/ledger";

    private readonly PostgresFixture _postgres;
    private readonly ScopedTestHome _scopedHome = new();

    public TaskTakeCommandTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync()
    {
        _scopedHome.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Refuses_when_this_owner_does_not_hold_the_owner_role()
    {
        (ProjectDetails project, Guid taskId, Guid previousHolderNodeId) = await SeedClaimedTaskAsync(ClaimGate.Off, externalReference: null);
        FakeLedger ledger = new();

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (string myFingerprint, string myPublicKeyLine) = await EstablishOwnRootAsync(session, CancellationToken.None);

        // This owner's own root exists in the chain, but only as a plain member, never owner.
        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myFingerprint] = new(myFingerprint, myPublicKeyLine, []) },
            [new ProjectMember(myFingerprint, MembershipRole.Member, Now)]));

        TaskTakeCommand.Settings settings = new() { Id = taskId.ToString(), Force = true, Reason = "Offline for six hours." };
        Func<Task> act = () => TaskTakeCommand.RunAsync(
            _postgres.Store, session, settings, ledger, chainReader, take: null, new NodeKeyStore(), CancellationToken.None);

        await act.Should().ThrowAsync<DomainValidationException>().WithMessage("*owner role*");
        ledger.Writes.Should().BeEmpty("nothing is ever touched before the owner-role gate passes");
    }

    [Fact]
    public async Task A_gated_projects_own_tracker_refusal_stops_the_override_before_the_ledger_is_touched()
    {
        ExternalReference reference = new(WorkItemProvider.GitHub, "acme/web#7");
        (ProjectDetails project, Guid taskId, Guid previousHolderNodeId) =
            await SeedClaimedTaskAsync(ClaimGate.TrackerAssignee, reference);
        FakeLedger ledger = new();
        await SeedRecordAsync(ledger, taskId, new TaskRecordHolder("holder-fingerprint", previousHolderNodeId, "OLD-NODE", Now.AddHours(-6)));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (string myFingerprint, string myPublicKeyLine) = await EstablishOwnRootAsync(session, CancellationToken.None);
        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myFingerprint] = new(myFingerprint, myPublicKeyLine, []) },
            [new ProjectMember(myFingerprint, MembershipRole.Owner, Now)]));

        // gh already shows the issue assigned to a teammate's own login — the tracker's own
        // refusal, never touched by this install at all.
        ProcessRunner gh = (_, arguments, _, _) => Task.FromResult(
            arguments switch
            {
                ["api", "user", ..] => new ProcessResult(0, "this-install-login\n", string.Empty),
                _ when arguments.Contains("edit") =>
                    new ProcessResult(1, string.Empty, "a teammate already holds this item"),
                _ => new ProcessResult(0, "{\"assignees\":[{\"login\":\"a-teammate\"}]}", string.Empty),
            });
        TrackerAssignmentTake take = new(gh, requester: null);
        int writesBeforeOverride = ledger.Writes.Count;

        TaskTakeCommand.Settings settings = new() { Id = taskId.ToString(), Force = true, Reason = "Offline for six hours." };
        Func<Task> act = () => TaskTakeCommand.RunAsync(
            _postgres.Store, session, settings, ledger, chainReader, take, new NodeKeyStore(), CancellationToken.None);

        await act.Should().ThrowAsync<DomainBusinessRuleException>();
        ledger.Writes.Should().HaveCount(
            writesBeforeOverride, "the tracker's own refusal stops the override before the ledger holder is ever touched");
    }

    [Fact]
    public async Task A_gated_projects_own_tracker_take_that_succeeds_still_appends_the_takeover()
    {
        // The stale-fence bug this test guards against (conformance pre-PR review, cycle 1): the
        // tracker take's own successful write appends TrackerAssignmentWritten onto this exact
        // task stream, ahead of the takeover's own append — an expected version read before that
        // write runs would conflict every time, so a gated project's take could never succeed.
        ExternalReference reference = new(WorkItemProvider.GitHub, "acme/web#7");
        (ProjectDetails project, Guid taskId, Guid previousHolderNodeId) =
            await SeedClaimedTaskAsync(ClaimGate.TrackerAssignee, reference);
        FakeLedger ledger = new();
        await SeedRecordAsync(ledger, taskId, new TaskRecordHolder("holder-fingerprint", previousHolderNodeId, "OLD-NODE", Now.AddHours(-6)));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (string myFingerprint, string myPublicKeyLine) = await EstablishOwnRootAsync(session, CancellationToken.None);
        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myFingerprint] = new(myFingerprint, myPublicKeyLine, []) },
            [new ProjectMember(myFingerprint, MembershipRole.Owner, Now)]));

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, CancellationToken.None);

        // The item starts unassigned, so the take writes this install's own identity into it and
        // the read-back afterward shows it holding — the "Taken" verdict, the one path that
        // appends an event (TrackerAssignmentWritten) onto the task's own stream.
        bool written = false;
        ProcessRunner gh = (_, arguments, _, _) =>
        {
            if (arguments.Contains("edit"))
            {
                written = true;
                return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            }

            return Task.FromResult(arguments switch
            {
                ["api", "user", ..] => new ProcessResult(0, "this-install-login\n", string.Empty),
                _ => new ProcessResult(
                    0,
                    written ? "{\"assignees\":[{\"login\":\"this-install-login\"}]}" : "{\"assignees\":[]}",
                    string.Empty),
            });
        };
        TrackerAssignmentTake take = new(gh, requester: null);

        TaskTakeCommand.Settings settings = new() { Id = taskId.ToString(), Force = true, Reason = "Offline for six hours." };
        int exitCode;
        using (ScopedConnectionString scope = new(_postgres.ConnectionString))
        {
            exitCode = await TaskTakeCommand.RunAsync(
                _postgres.Store, session, settings, ledger, chainReader, take, new NodeKeyStore(), CancellationToken.None);
        }

        exitCode.Should().Be(
            ExitCodes.Ok, "the tracker take's own event append must not permanently stale the takeover's own expected version");

        LedgerFile record = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId), CancellationToken.None);
        TaskRecord.TryParse(record.Content)!.Holder!.NodeId.Should().Be(context.NodeId);

        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: CancellationToken.None))!;
        task.HolderNodeId.Should().Be(context.NodeId);
        task.TakenOverFromNodeId.Should().Be(previousHolderNodeId);
        task.State.Should().Be(TaskState.Queued);
    }

    [Fact]
    public async Task A_lease_expiry_requeue_landing_during_the_tracker_take_rolls_the_override_back_to_no_holder()
    {
        // The high-severity defect this test guards against (adversarial pre-PR review, cycle 6):
        // a race landing between the tracker take and the takeover's own re-validated read can
        // move the task out of Claimed — a lease-expiry requeue, simulated here — and the
        // rollback must then release the ledger to no holder rather than restore the ORIGINAL
        // holder, a value the domain no longer recognises and no live command could ever clear
        // again.
        ExternalReference reference = new(WorkItemProvider.GitHub, "acme/web#7");
        (ProjectDetails project, Guid taskId, Guid previousHolderNodeId) =
            await SeedClaimedTaskAsync(ClaimGate.TrackerAssignee, reference);
        FakeLedger ledger = new();
        await SeedRecordAsync(ledger, taskId, new TaskRecordHolder("holder-fingerprint", previousHolderNodeId, "OLD-NODE", Now.AddHours(-6)));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (string myFingerprint, string myPublicKeyLine) = await EstablishOwnRootAsync(session, CancellationToken.None);
        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myFingerprint] = new(myFingerprint, myPublicKeyLine, []) },
            [new ProjectMember(myFingerprint, MembershipRole.Owner, Now)]));

        // The same "unassigned, then written" shape as the gated-project success test above: the
        // item starts unassigned, this install's own edit writes it, and the read-back afterwards
        // shows it holding — the one path that reaches TrackerAssignmentWritten rather than
        // refusing outright. The lease-expiry requeue is injected on that read-back, after the
        // tracker write itself has already landed, mirroring a sweep racing the round trip rather
        // than something that happens before this install's own write.
        bool written = false;
        bool requeued = false;
        ProcessRunner gh = async (_, arguments, _, cancellationToken) =>
        {
            if (arguments.Contains("edit"))
            {
                written = true;
                return new ProcessResult(0, string.Empty, string.Empty);
            }

            if (written && !requeued)
            {
                requeued = true;
                // The previous holder's own daemon, still alive locally, sweeping its own expired
                // lease during this command's tracker round trip — DispatchEngine's own pairing
                // (Requeue + ReleaseHolder together, in the one case a node's own lease expiry
                // clears its own holder) through a separate session, exactly like the real sweep
                // would use.
                await using IDocumentSession requeueSession = _postgres.Store.LightweightSession();
                TaskAggregate task = (await requeueSession.Events.AggregateStreamAsync<TaskAggregate>(
                    taskId, token: cancellationToken))!;
                requeueSession.Events.Append(
                    taskId,
                    TaskDecider.Requeue(task, RequeueReason.LeaseExpired, Now),
                    TaskDecider.ReleaseHolder(task, Now));
                await requeueSession.SaveChangesAsync(cancellationToken);
            }

            return arguments switch
            {
                ["api", "user", ..] => new ProcessResult(0, "this-install-login\n", string.Empty),
                _ => new ProcessResult(
                    0,
                    written ? "{\"assignees\":[{\"login\":\"this-install-login\"}]}" : "{\"assignees\":[]}",
                    string.Empty),
            };
        };
        TrackerAssignmentTake take = new(gh, requester: null);

        TaskTakeCommand.Settings settings = new() { Id = taskId.ToString(), Force = true, Reason = "Offline for six hours." };
        Func<Task> act = () => TaskTakeCommand.RunAsync(
            _postgres.Store, session, settings, ledger, chainReader, take, new NodeKeyStore(), CancellationToken.None);

        await act.Should().ThrowAsync<DomainConflictException>();

        LedgerFile record = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId), CancellationToken.None);
        TaskRecord.TryParse(record.Content)!.Holder.Should().BeNull(
            "the domain no longer recognises any holder for this task, so the rollback releases it "
            + "rather than restoring the original holder, which nothing could ever clear again");
    }

    [Fact]
    public async Task A_lost_override_race_leaves_the_tracker_assignment_the_winner_is_claiming_on()
    {
        // The high-severity defect this test guards against (adversarial + conformance pre-PR
        // review, cycle 3): the tracker take only ever writes onto an item the gate read as
        // UNASSIGNED, so on a gated project a competing holder can only have passed its own claim
        // gate on that very assignment. Clearing it back off after losing the ledger race leaves
        // the winner holding a task its own dispatch sweep reads as Unassigned — which holds —
        // and never claims.
        ExternalReference reference = new(WorkItemProvider.GitHub, "acme/web#7");
        (ProjectDetails project, Guid taskId, Guid previousHolderNodeId) =
            await SeedClaimedTaskAsync(ClaimGate.TrackerAssignee, reference);
        FakeLedger ledger = new();
        await SeedRecordAsync(ledger, taskId, new TaskRecordHolder("holder-fingerprint", previousHolderNodeId, "OLD-NODE", Now.AddHours(-6)));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (string myFingerprint, string myPublicKeyLine) = await EstablishOwnRootAsync(session, CancellationToken.None);
        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myFingerprint] = new(myFingerprint, myPublicKeyLine, []) },
            [new ProjectMember(myFingerprint, MembershipRole.Owner, Now)]));

        // Another owner-role member's own override, landing on the record between this one's read
        // and its write: the interloper's write moves the blob, so this override's own write
        // conflicts on its own terms and its re-read finds the third-party winner.
        Guid winnerNodeId = DomainId.New();
        LedgerLosingTheFirstOverride racedLedger = new(
            ledger,
            () => OverwriteHolderAsync(ledger, taskId, new TaskRecordHolder("winner-fingerprint", winnerNodeId, "WINNER-NODE", Now)));

        bool written = false;
        bool unassigned = false;
        ProcessRunner gh = (_, arguments, _, _) =>
        {
            if (arguments.Contains("edit"))
            {
                unassigned |= arguments.Contains("--remove-assignee");
                written = true;
                return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            }

            return Task.FromResult(arguments switch
            {
                ["api", "user", ..] => new ProcessResult(0, "this-install-login\n", string.Empty),
                _ => new ProcessResult(
                    0,
                    written ? "{\"assignees\":[{\"login\":\"this-install-login\"}]}" : "{\"assignees\":[]}",
                    string.Empty),
            });
        };
        TrackerAssignmentTake take = new(gh, requester: null);

        TaskTakeCommand.Settings settings = new() { Id = taskId.ToString(), Force = true, Reason = "Offline for six hours." };
        Func<Task> act = () => TaskTakeCommand.RunAsync(
            _postgres.Store, session, settings, racedLedger, chainReader, take, new NodeKeyStore(), CancellationToken.None);

        await act.Should().ThrowAsync<DomainConflictException>().WithMessage("*deliberately been left there*");

        unassigned.Should().BeFalse(
            "the winner's own claim gate passes on the assignment this take wrote, so clearing it back "
            + "off would leave it holding a task nothing can dispatch");

        LedgerFile record = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId), CancellationToken.None);
        TaskRecord.TryParse(record.Content)!.Holder!.NodeId.Should().Be(
            winnerNodeId, "the override that landed first is the one every later re-read reports back");
    }

    [Fact]
    public async Task An_owner_role_member_forces_the_takeover_writes_the_new_holder_and_appends_the_event()
    {
        (ProjectDetails project, Guid taskId, Guid previousHolderNodeId) = await SeedClaimedTaskAsync(ClaimGate.Off, externalReference: null);
        FakeLedger ledger = new();
        await SeedRecordAsync(ledger, taskId, new TaskRecordHolder("holder-fingerprint", previousHolderNodeId, "OLD-NODE", Now.AddHours(-6)));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (string myFingerprint, string myPublicKeyLine) = await EstablishOwnRootAsync(session, CancellationToken.None);
        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myFingerprint] = new(myFingerprint, myPublicKeyLine, []) },
            [new ProjectMember(myFingerprint, MembershipRole.Owner, Now)]));

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, CancellationToken.None);

        TaskTakeCommand.Settings settings = new() { Id = taskId.ToString(), Force = true, Reason = "Offline for six hours." };
        int exitCode;
        using (ScopedConnectionString scope = new(_postgres.ConnectionString))
        {
            exitCode = await TaskTakeCommand.RunAsync(
                _postgres.Store, session, settings, ledger, chainReader, take: null, new NodeKeyStore(), CancellationToken.None);
        }

        exitCode.Should().Be(ExitCodes.Ok);

        LedgerFile record = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId), CancellationToken.None);
        TaskRecord.TryParse(record.Content)!.Holder!.NodeId.Should().Be(context.NodeId, "the ledger holder now names this overriding node");

        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: CancellationToken.None))!;
        task.HolderNodeId.Should().Be(context.NodeId);
        task.AssignedOwnerId.Should().Be(context.OwnerId);
        task.State.Should().Be(TaskState.Queued, "the overrider's own next dispatch sweep claims it through the ordinary path");
        task.TakenOverFromNodeId.Should().Be(previousHolderNodeId);
        task.TakenOverReason.Should().Be("Offline for six hours.");
    }

    private async Task<(ProjectDetails Project, Guid TaskId, Guid PreviousHolderNodeId)> SeedClaimedTaskAsync(
        ClaimGate gate, ExternalReference? externalReference)
    {
        await NodeBootstrapSeed.SeedGitHubConnectionAsync(_postgres.Store, CancellationToken.None);

        await using IDocumentSession bootstrapSession = _postgres.Store.LightweightSession();
        BootstrapContext context = await NodeBootstrap.EnsureAsync(bootstrapSession, CancellationToken.None);
        await bootstrapSession.SaveChangesAsync(CancellationToken.None);

        // A plain document, deliberately never a real ProjectAggregate stream (the identical
        // shape TaskHolderClaimTests.SeedProjectAsync already proves): ClaimGate has to stick
        // exactly as set, and nothing here needs a project-stream event to exist for it.
        Guid projectId = DomainId.New();
        ProjectDetails project = new()
        {
            Id = projectId,
            Name = $"take-{projectId:N}"[..12],
            RepositoryPath = RepositoryPath,
            BaseBranch = "main",
            ClaimGate = gate,
            BranchNameTemplate = BranchNameTemplate.Default,
        };
        await using IDocumentSession projectSession = _postgres.Store.LightweightSession();
        projectSession.Store(project);
        await projectSession.SaveChangesAsync(CancellationToken.None);

        Guid taskId = DomainId.New();
        Guid previousHolderNodeId = DomainId.New();
        await using IDocumentSession taskSession = _postgres.Store.LightweightSession();
        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(
                taskId, projectId, "Close me out", ["done"], TaskType.Chore, null, null,
                externalReference, Now, context.OwnerId),
            context.OwnerId, Now);
        var claimed = TaskDecider.Claim(task, previousHolderNodeId, context.OwnerId, DomainId.New(), Now.AddHours(-6));
        taskSession.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
        await taskSession.SaveChangesAsync(CancellationToken.None);

        return (project, taskId, previousHolderNodeId);
    }

    private async Task<(string Fingerprint, string PublicKeyLine)> EstablishOwnRootAsync(
        IDocumentSession session, CancellationToken cancellationToken)
    {
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        NodeSigningKey key = await new NodeKeyStore().EnsureAsync(context.NodeId, cancellationToken);

        OwnerAggregate owner = await session.Events.AggregateStreamAsync<OwnerAggregate>(context.OwnerId, token: cancellationToken)
            ?? throw new InvalidOperationException("Owner bootstrap did not create an owner stream.");
        session.Events.Append(context.OwnerId, OwnerDecider.ClaimRoot(owner, key.Fingerprint, verified: true, Now));
        await session.SaveChangesAsync(cancellationToken);

        return (key.Fingerprint, key.PublicKeyLine);
    }

    /// <summary>
    /// Another node's own holder write landing on a record this test already seeded — read,
    /// replace the holder, write against the blob just read, exactly the conditional write every
    /// real caller makes. Distinct from <see cref="SeedRecordAsync"/>, whose null expected blob id
    /// only ever lands on a path that does not exist yet.
    /// </summary>
    private static async Task OverwriteHolderAsync(FakeLedger ledger, Guid taskId, TaskRecordHolder holder)
    {
        LedgerFile current = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId),
            CancellationToken.None);
        TaskRecord record = TaskRecord.TryParse(current.Content)! with { Holder = holder };
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId),
                record.ToYaml(), current.BlobId, $"Take over task {taskId}",
                new LedgerCommitter("Test", "test@hall9k.local"), new LedgerSigningKey("/does/not/matter/key")),
            CancellationToken.None);
    }

    /// <summary>
    /// A <see cref="FakeLedger"/> whose first takeover write loses its race: <paramref name="interloper"/>
    /// runs first — another override landing on the same record — and moves the blob, so the write
    /// that follows conflicts on the fake's own ordinary compare-and-swap rather than on anything
    /// scripted here, and <c>TaskLedgerHolder.TryOverrideAsync</c>'s own re-read finds the winner.
    /// The one shape no seeding can arrange, because it has to happen between that method's own
    /// read and its own write.
    /// </summary>
    private sealed class LedgerLosingTheFirstOverride(FakeLedger inner, Func<Task> interloper) : ILedger
    {
        private bool _raced;

        public Task<LedgerFile> ReadAsync(string repositoryPath, string refName, string path, CancellationToken cancellationToken) =>
            inner.ReadAsync(repositoryPath, refName, path, cancellationToken);

        public async Task<LedgerWriteOutcome> WriteAsync(LedgerWriteRequest request, CancellationToken cancellationToken)
        {
            if (!_raced && request.CommitMessage.StartsWith("Take over task", StringComparison.Ordinal))
            {
                _raced = true;
                await interloper();
            }

            return await inner.WriteAsync(request, cancellationToken);
        }

        public Task<LedgerWriteOutcome> DeleteAsync(LedgerDeleteRequest request, CancellationToken cancellationToken) =>
            inner.DeleteAsync(request, cancellationToken);

        public Task<bool> HasAnyAsync(string repositoryPath, string refName, string pathPrefix, CancellationToken cancellationToken) =>
            inner.HasAnyAsync(repositoryPath, refName, pathPrefix, cancellationToken);

        public Task<IReadOnlyList<LedgerRef>> ListRefsAsync(string repositoryPath, string refPrefix, CancellationToken cancellationToken) =>
            inner.ListRefsAsync(repositoryPath, refPrefix, cancellationToken);

        public Task<IReadOnlyList<LedgerEntry>> ReadAllAsync(
            string repositoryPath, string refName, string pathPrefix, CancellationToken cancellationToken) =>
            inner.ReadAllAsync(repositoryPath, refName, pathPrefix, cancellationToken);
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
