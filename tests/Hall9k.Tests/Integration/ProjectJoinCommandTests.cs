using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Processes;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// h9k project join's whole flow (idea 202383dc, A2a) against a real Marten/Postgres session —
/// Owner and Node are real event streams — but never a real git repository: <see cref="FakeLedger"/>
/// stands in for A1 throughout, per Brian's 2026-09-13 testing rule that only GitLedgerTests and
/// the message-transport chain reader's own tests touch a real repository. The node key itself is
/// real (a real ed25519 keypair via ssh-keygen, the same dependency GitLedgerTests already has),
/// generated under a throwaway HALL9K_HOME so nothing here touches this machine's real
/// ~/.hall9k/keys.
/// <para>
/// Every test wipes the shared <see cref="PostgresFixture"/> database in <see cref="InitializeAsync"/>:
/// <c>NodeBootstrap.EnsureAsync</c> resolves the owner and the node by "the first one on file", with
/// no per-test scoping of its own, so a database left holding an earlier test's rows would hand this
/// test back someone else's identity rather than a fresh one — the same reason
/// <see cref="Node.NodeAggregate.PublicKey"/> would otherwise mismatch a freshly generated key under a
/// freshly rotated <c>HALL9K_HOME</c>.
/// </para>
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection("Hall9kHome")]
public sealed class ProjectJoinCommandTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"hall9k-project-join-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    public ProjectJoinCommandTests(PostgresFixture postgres) => _postgres = postgres;

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
    public async Task First_join_with_no_owner_establishes_the_root_and_writes_the_node_file_both_signed()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        ProjectDetails project = await SeedProjectAsync(cts.Token);
        FakeLedger ledger = new();

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        ProjectJoinCommand.JoinOutcome outcome = await ProjectJoinCommand.RunAsync(
            session, project, claimedOwnerOverride: null, ledger, new NodeKeyStore(), cts.Token);

        outcome.EstablishedRoot.Should().BeTrue();
        outcome.ClaimedOwnerFingerprint.Should().Be(outcome.KeyFingerprint);

        ledger.Writes.Should().HaveCount(2, "the root file and the node file each write once");
        ledger.Writes.Should().OnlyContain(
            write => write.SigningKey != null && write.SigningKey!.PrivateKeyPath == outcome.PrivateKeyPath,
            "every ledger commit is signed with this node's own key");

        LedgerWriteRequest rootWrite = ledger.Writes.Single(w => w.RefName == $"refs/hall9k/ledger/owners/{outcome.KeyFingerprint}");
        rootWrite.Path.Should().Be($"owners/{outcome.KeyFingerprint}/root.yaml");

        LedgerWriteRequest nodeWrite = ledger.Writes.Single(w => w.RefName == $"refs/hall9k/ledger/nodes/{outcome.NodeId}");
        nodeWrite.Path.Should().Be($"nodes/{outcome.NodeId}/node.yaml");
        nodeWrite.Content.Should().Contain(outcome.KeyFingerprint).And.Contain(outcome.NodeId.ToString());

        OwnerDetails owner = (await session.LoadAsync<OwnerDetails>(project.OwnerId, cts.Token))!;
        owner.RootFingerprint.Should().Be(outcome.KeyFingerprint);
        owner.RootFingerprintVerified.Should().BeTrue();
    }

    [Fact]
    public async Task Joining_twice_writes_the_ledger_once()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        ProjectDetails project = await SeedProjectAsync(cts.Token);
        FakeLedger ledger = new();
        NodeKeyStore keyStore = new();

        await using (IDocumentSession first = _postgres.Store.LightweightSession())
        {
            await ProjectJoinCommand.RunAsync(first, project, claimedOwnerOverride: null, ledger, keyStore, cts.Token);
        }

        int writesAfterFirstJoin = ledger.Writes.Count;

        await using (IDocumentSession second = _postgres.Store.LightweightSession())
        {
            await ProjectJoinCommand.RunAsync(second, project, claimedOwnerOverride: null, ledger, keyStore, cts.Token);
        }

        ledger.Writes.Should().HaveCount(writesAfterFirstJoin, "nothing about this node's facts changed the second time");
    }

    [Fact]
    public async Task Join_with_owner_records_the_claim_unverified_and_never_writes_a_root_file()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        ProjectDetails project = await SeedProjectAsync(cts.Token);
        FakeLedger ledger = new();
        string claimedFingerprint = new string('a', 64);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        ProjectJoinCommand.JoinOutcome outcome = await ProjectJoinCommand.RunAsync(
            session, project, claimedFingerprint, ledger, new NodeKeyStore(), cts.Token);

        outcome.EstablishedRoot.Should().BeFalse();
        outcome.ClaimedOwnerFingerprint.Should().Be(claimedFingerprint);
        ledger.Writes.Should().ContainSingle("only the node file is written — this node holds no key for the claimed root");
        ledger.Writes.Single().RefName.Should().StartWith("refs/hall9k/ledger/nodes/");

        OwnerDetails owner = (await session.LoadAsync<OwnerDetails>(project.OwnerId, cts.Token))!;
        owner.RootFingerprint.Should().Be(claimedFingerprint);
        owner.RootFingerprintVerified.Should().BeFalse();
    }

    [Fact]
    public async Task Join_refuses_an_owner_value_that_is_not_a_real_fingerprint()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        ProjectDetails project = await SeedProjectAsync(cts.Token);
        FakeLedger ledger = new();

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Func<Task> act = () => ProjectJoinCommand.RunAsync(
            session, project, "not-a-real-fingerprint", ledger, new NodeKeyStore(), cts.Token);

        await act.Should().ThrowAsync<DomainValidationException>();
        ledger.Writes.Should().BeEmpty("a malformed claim is refused before anything touches the ledger");
    }

    [Fact]
    public async Task Rerunning_with_a_different_owner_retires_the_self_created_root()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        ProjectDetails project = await SeedProjectAsync(cts.Token);
        FakeLedger ledger = new();
        NodeKeyStore keyStore = new();
        string realRootFingerprint = new string('b', 64);

        ProjectJoinCommand.JoinOutcome genesis;
        await using (IDocumentSession first = _postgres.Store.LightweightSession())
        {
            genesis = await ProjectJoinCommand.RunAsync(first, project, claimedOwnerOverride: null, ledger, keyStore, cts.Token);
        }

        ProjectJoinCommand.JoinOutcome reclaim;
        await using (IDocumentSession second = _postgres.Store.LightweightSession())
        {
            reclaim = await ProjectJoinCommand.RunAsync(second, project, realRootFingerprint, ledger, keyStore, cts.Token);
        }

        reclaim.RetiredPreviousRoot.Should().BeTrue();
        reclaim.ClaimedOwnerFingerprint.Should().Be(realRootFingerprint);

        LedgerWriteRequest retirement = ledger.Writes.Single(
            w => w.RefName == $"refs/hall9k/ledger/owners/{genesis.KeyFingerprint}" && w.Path.EndsWith("retired.yaml"));
        retirement.Content.Should().Contain(realRootFingerprint);
        retirement.SigningKey!.PrivateKeyPath.Should().Be(genesis.PrivateKeyPath);

        await using IDocumentSession query = _postgres.Store.LightweightSession();
        OwnerDetails owner = (await query.LoadAsync<OwnerDetails>(project.OwnerId, cts.Token))!;
        owner.RootFingerprint.Should().Be(realRootFingerprint);
        owner.RootFingerprintVerified.Should().BeFalse();
    }

    [Fact]
    public async Task No_written_tree_ever_contains_the_private_key()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        ProjectDetails project = await SeedProjectAsync(cts.Token);
        FakeLedger ledger = new();

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        ProjectJoinCommand.JoinOutcome outcome = await ProjectJoinCommand.RunAsync(
            session, project, claimedOwnerOverride: null, ledger, new NodeKeyStore(), cts.Token);

        string privateKeyText = await File.ReadAllTextAsync(outcome.PrivateKeyPath, cts.Token);
        ledger.Writes.Should().OnlyContain(write => !write.Content.Contains(privateKeyText));
        ledger.Writes.Should().OnlyContain(write => !write.Content.Contains("PRIVATE KEY"));
    }

    [Fact]
    public async Task A_node_that_cannot_produce_a_key_is_refused_naming_project_join_and_writes_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        ProjectDetails project = await SeedProjectAsync(cts.Token);
        FakeLedger ledger = new();
        ProcessRunner failingRunner = (_, _, _, _) =>
            Task.FromResult(new ProcessResult(1, string.Empty, "ssh-keygen: command not found"));
        NodeKeyStore brokenKeyStore = new(failingRunner);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Func<Task> act = () => ProjectJoinCommand.RunAsync(session, project, claimedOwnerOverride: null, ledger, brokenKeyStore, cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>()).WithMessage("*h9k project join*");
        ledger.Writes.Should().BeEmpty("a node without a key never reaches a ledger write");
    }

    /// <summary>
    /// A pre-existing local install's owner (a Guid, as every owner is today) is on file before
    /// this test's first bootstrap ever runs — matching an existing install upgrading onto this
    /// feature, not a fresh one — and join must map that same Guid onto the new root fingerprint
    /// rather than minting a second owner, so a task already assigned to it keeps dispatching
    /// (idea 202383dc, A2a, criterion 3).
    /// </summary>
    [Fact]
    public async Task A_pre_existing_local_owner_keeps_dispatching_the_same_task_after_it_claims_a_root()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        await NodeBootstrapSeed.SeedGitHubConnectionAsync(_postgres.Store, cts.Token);

        Guid preExistingOwnerId = DomainId.New();
        await using (IDocumentSession seed = _postgres.Store.LightweightSession())
        {
            seed.Events.StartStream<OwnerAggregate>(
                preExistingOwnerId, OwnerDecider.Register(preExistingOwnerId, "Pre-existing Owner", "owner@test.local", Now));
            await seed.SaveChangesAsync(cts.Token);
        }

        Guid projectId = DomainId.New();
        await using (IDocumentSession seed = _postgres.Store.LightweightSession())
        {
            seed.Events.StartStream<ProjectAggregate>(
                projectId,
                ProjectDecider.Register(
                    projectId, preExistingOwnerId, DomainId.New(), "smoke", "/does/not/matter/on/a/fake/ledger",
                    null, null, Now));
            await seed.SaveChangesAsync(cts.Token);
        }

        await using IDocumentSession loadSession = _postgres.Store.LightweightSession();
        ProjectDetails project = (await loadSession.LoadAsync<ProjectDetails>(projectId, cts.Token))!;

        Guid taskId = DomainId.New();
        await using (IDocumentSession seed = _postgres.Store.LightweightSession())
        {
            seed.Events.StartStream<TaskAggregate>(
                taskId,
                TaskSeed.Dispatchable(
                    TaskDecider.Add(
                        taskId, project.Id, "Pre-existing work", ["done"], TaskType.Chore,
                        null, null, null, Now, preExistingOwnerId),
                    preExistingOwnerId, Now));
            await seed.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await ProjectJoinCommand.RunAsync(session, project, claimedOwnerOverride: null, new FakeLedger(), new NodeKeyStore(), cts.Token);
        }

        await using IDocumentSession query = _postgres.Store.LightweightSession();
        OwnerDetails owner = (await query.LoadAsync<OwnerDetails>(preExistingOwnerId, cts.Token))!;
        owner.RootFingerprint.Should().NotBeNullOrEmpty("the pre-existing Guid owner is mapped onto the new root fingerprint");

        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.AssignedOwnerId.Should().Be(
            preExistingOwnerId, "the task's own Guid assignment is untouched by the fingerprint mapping, so a plain Guid dispatch comparison still matches");
    }

    /// <summary>
    /// Mints the project's owner the same way h9k project add really does: bootstrap first, then
    /// register the project against whatever owner bootstrap resolved — never a fresh, unrelated
    /// Guid of the test's own, which <c>NodeBootstrap.EnsureAsync</c>'s own "first owner on file"
    /// lookup would silently ignore in favour of the one it resolves for itself.
    /// </summary>
    private async Task<ProjectDetails> SeedProjectAsync(CancellationToken cancellationToken)
    {
        await NodeBootstrapSeed.SeedGitHubConnectionAsync(_postgres.Store, cancellationToken);

        await using IDocumentSession bootstrapSession = _postgres.Store.LightweightSession();
        BootstrapContext context = await NodeBootstrap.EnsureAsync(bootstrapSession, cancellationToken);
        await bootstrapSession.SaveChangesAsync(cancellationToken);

        Guid projectId = DomainId.New();
        await using IDocumentSession projectSession = _postgres.Store.LightweightSession();
        projectSession.Events.StartStream<ProjectAggregate>(
            projectId,
            ProjectDecider.Register(
                projectId, context.OwnerId, DomainId.New(), "smoke",
                "/does/not/matter/on/a/fake/ledger", null, null, Now));
        await projectSession.SaveChangesAsync(cancellationToken);

        return (await projectSession.LoadAsync<ProjectDetails>(projectId, cancellationToken))!;
    }
}
