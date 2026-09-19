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
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <c>h9k task take --force</c> (idea 202383dc, item 4), against real Marten/Postgres but a
/// <see cref="FakeLedger"/>/<see cref="FakeLedgerChainReader"/> stand-in for the ledger and the
/// chain read, and a scripted <see cref="ProcessRunner"/> stand-in for <c>gh</c> (Brian's
/// 2026-09-13 testing rule).
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection("Hall9kHome")]
[Trait("Category", "Hall9kHome")]
public sealed class TaskTakeCommandTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private const string RepositoryPath = "/does/not/matter/on/a/fake/ledger";

    private readonly PostgresFixture _postgres;
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"hall9k-task-take-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    public TaskTakeCommandTests(PostgresFixture postgres) => _postgres = postgres;

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
        int exitCode = await TaskTakeCommand.RunAsync(
            _postgres.Store, session, settings, ledger, chainReader, take, new NodeKeyStore(), CancellationToken.None);

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
        int exitCode = await TaskTakeCommand.RunAsync(
            _postgres.Store, session, settings, ledger, chainReader, take: null, new NodeKeyStore(), CancellationToken.None);

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
