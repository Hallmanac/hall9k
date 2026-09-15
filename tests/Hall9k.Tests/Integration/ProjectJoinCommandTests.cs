using System.ComponentModel;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon;
using Hall9k.Daemon.Dispatch;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Connection;
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
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
            session, project, claimedOwnerOverride: null, ledger, new NodeKeyStore(), GitHubAccessFakes.GrantingPush(), cts.Token);

        outcome.EstablishedRoot.Should().BeTrue();
        outcome.ClaimedOwnerFingerprint.Should().Be(outcome.KeyFingerprint);

        ledger.Writes.Should().HaveCount(3, "the root file, the genesis members file, and the node file each write once");
        ledger.Writes.Should().OnlyContain(
            write => write.SigningKey != null && write.SigningKey!.PrivateKeyPath == outcome.PrivateKeyPath,
            "every ledger commit is signed with this node's own key");

        LedgerWriteRequest rootWrite = ledger.Writes.Single(w => w.RefName == $"refs/hall9k/ledger/owners/{outcome.KeyFingerprint}");
        rootWrite.Path.Should().Be($"owners/{outcome.KeyFingerprint}/root.yaml");

        LedgerWriteRequest memberWrite = ledger.Writes.Single(w => w.RefName == "refs/hall9k/ledger/members");
        memberWrite.Path.Should().Be($"members/{outcome.KeyFingerprint}.yaml");
        memberWrite.Content.Should().Contain("owner");

        LedgerWriteRequest nodeWrite = ledger.Writes.Single(w => w.RefName == $"refs/hall9k/ledger/nodes/{outcome.NodeId}");
        nodeWrite.Path.Should().Be($"nodes/{outcome.NodeId}/node.yaml");
        nodeWrite.Content.Should().Contain(outcome.KeyFingerprint).And.Contain(outcome.NodeId.ToString());

        OwnerDetails owner = (await session.LoadAsync<OwnerDetails>(project.OwnerId, cts.Token))!;
        owner.RootFingerprint.Should().Be(outcome.KeyFingerprint);
        owner.RootFingerprintVerified.Should().BeTrue();

        ProjectDetails updatedProject = (await session.LoadAsync<ProjectDetails>(project.Id, cts.Token))!;
        updatedProject.Members.Should().ContainKey(outcome.KeyFingerprint);
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
            await ProjectJoinCommand.RunAsync(
                first, project, claimedOwnerOverride: null, ledger, keyStore, GitHubAccessFakes.GrantingPush(), cts.Token);
        }

        int writesAfterFirstJoin = ledger.Writes.Count;

        await using (IDocumentSession second = _postgres.Store.LightweightSession())
        {
            await ProjectJoinCommand.RunAsync(
                second, project, claimedOwnerOverride: null, ledger, keyStore, GitHubAccessFakes.GrantingPush(), cts.Token);
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
            session, project, claimedFingerprint, ledger, new NodeKeyStore(), GitHubAccessFakes.GrantingPush(), cts.Token);

        outcome.EstablishedRoot.Should().BeFalse();
        outcome.ClaimedOwnerFingerprint.Should().Be(claimedFingerprint);
        ledger.Writes.Should().ContainSingle("only the node file is written — this node holds no key for the claimed root");
        ledger.Writes.Single().RefName.Should().StartWith("refs/hall9k/ledger/nodes/");

        OwnerDetails owner = (await session.LoadAsync<OwnerDetails>(project.OwnerId, cts.Token))!;
        owner.RootFingerprint.Should().Be(claimedFingerprint);
        owner.RootFingerprintVerified.Should().BeFalse();
    }

    [Fact]
    public async Task Join_with_owner_naming_this_nodes_own_fingerprint_still_stays_unverified()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        Guid connectionId = await NodeBootstrapSeed.SeedGitHubConnectionAsync(_postgres.Store, cts.Token);

        await using IDocumentSession bootstrapSession = _postgres.Store.LightweightSession();
        BootstrapContext context = await NodeBootstrap.EnsureAsync(bootstrapSession, cts.Token);
        await bootstrapSession.SaveChangesAsync(cts.Token);

        NodeKeyStore keyStore = new();
        NodeSigningKey key = await keyStore.EnsureAsync(context.NodeId, cts.Token);

        Guid projectId = DomainId.New();
        await using IDocumentSession projectSession = _postgres.Store.LightweightSession();
        projectSession.Events.StartStream<ProjectAggregate>(
            projectId,
            ProjectDecider.Register(
                projectId, context.OwnerId, connectionId, "smoke",
                "/does/not/matter/on/a/fake/ledger", null, null, Now));
        await projectSession.SaveChangesAsync(cts.Token);
        ProjectDetails project = (await projectSession.LoadAsync<ProjectDetails>(projectId, cts.Token))!;

        FakeLedger ledger = new();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        ProjectJoinCommand.JoinOutcome outcome = await ProjectJoinCommand.RunAsync(
            session, project, key.Fingerprint, ledger, keyStore, GitHubAccessFakes.GrantingPush(), cts.Token);

        // An explicit --owner is a claim to be vouched for later, never a shortcut to
        // self-establishing a verified root — even when the fingerprint named happens to be this
        // node's own (independent pre-PR review finding, conformance lens).
        outcome.EstablishedRoot.Should().BeFalse(
            "an explicit --owner always stays an unverified claim, even when it names this node's own fingerprint");
        ledger.Writes.Should().ContainSingle("only the node file is written — no root.yaml for an explicit, still-unverified claim");

        OwnerDetails owner = (await session.LoadAsync<OwnerDetails>(project.OwnerId, cts.Token))!;
        owner.RootFingerprintVerified.Should().BeFalse();
    }

    [Fact]
    public async Task A_conflicting_node_file_write_retries_rather_than_silently_dropping_the_claim()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        ProjectDetails project = await SeedProjectAsync(cts.Token);
        FakeLedger inner = new();
        ConflictOnceLedger ledger = new(inner, pathMustContain: "node.yaml");

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        ProjectJoinCommand.JoinOutcome outcome = await ProjectJoinCommand.RunAsync(
            session, project, claimedOwnerOverride: null, ledger, new NodeKeyStore(), GitHubAccessFakes.GrantingPush(), cts.Token);

        // A transient conflict on node.yaml — another writer touched it between the read and the
        // write — must retry against a fresh tip, not silently report no write while the caller
        // above still saves the local claim events as though it landed.
        ledger.ConflictsInjected.Should().Be(1);
        outcome.WroteNodeFile.Should().BeTrue("the retry must land the node file once the transient conflict clears");

        LedgerFile nodeFile = await inner.ReadAsync(
            project.RepositoryPath, $"refs/hall9k/ledger/nodes/{outcome.NodeId}", $"nodes/{outcome.NodeId}/node.yaml", cts.Token);
        nodeFile.Content.Should().Contain(outcome.KeyFingerprint);
    }

    [Fact]
    public async Task Join_refuses_an_owner_value_that_is_not_a_real_fingerprint()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        ProjectDetails project = await SeedProjectAsync(cts.Token);
        FakeLedger ledger = new();

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Func<Task> act = () => ProjectJoinCommand.RunAsync(
            session, project, "not-a-real-fingerprint", ledger, new NodeKeyStore(), GitHubAccessFakes.GrantingPush(), cts.Token);

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
            genesis = await ProjectJoinCommand.RunAsync(
                first, project, claimedOwnerOverride: null, ledger, keyStore, GitHubAccessFakes.GrantingPush(), cts.Token);
        }

        ProjectJoinCommand.JoinOutcome reclaim;
        await using (IDocumentSession second = _postgres.Store.LightweightSession())
        {
            reclaim = await ProjectJoinCommand.RunAsync(
                second, project, realRootFingerprint, ledger, keyStore, GitHubAccessFakes.GrantingPush(), cts.Token);
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

    /// <summary>
    /// Root retirement runs in every project this owner is registered to, but it must never make
    /// the join the user actually asked for fail over a project it has nothing to do with — an
    /// archived project's deleted remote, or one behind a VPN that is down. The project being
    /// joined is required to retire; every other one is best-effort (independent pre-PR review,
    /// cycle 1, conformance and adversarial lenses, medium).
    /// </summary>
    [Fact]
    public async Task An_unreachable_other_project_is_skipped_while_retirement_in_the_project_being_joined_still_succeeds()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        ProjectDetails projectA = await SeedProjectAsync(cts.Token);
        FakeLedger inner = new();
        NodeKeyStore keyStore = new();
        string realRootFingerprint = new string('c', 64);

        await using (IDocumentSession first = _postgres.Store.LightweightSession())
        {
            await ProjectJoinCommand.RunAsync(
                first, projectA, claimedOwnerOverride: null, inner, keyStore, GitHubAccessFakes.GrantingPush(), cts.Token);
        }

        Guid projectBId = DomainId.New();
        Guid connectionId = await NodeBootstrapSeed.SeedGitHubConnectionAsync(_postgres.Store, cts.Token);
        await using (IDocumentSession seed = _postgres.Store.LightweightSession())
        {
            seed.Events.StartStream<ProjectAggregate>(
                projectBId,
                ProjectDecider.Register(
                    projectBId, projectA.OwnerId, connectionId, "smoke-b",
                    "/does/not/matter/on/a/fake/ledger/b", null, null, Now));
            await seed.SaveChangesAsync(cts.Token);
        }

        await using IDocumentSession loadSession = _postgres.Store.LightweightSession();
        ProjectDetails projectB = (await loadSession.LoadAsync<ProjectDetails>(projectBId, cts.Token))!;

        await using (IDocumentSession second = _postgres.Store.LightweightSession())
        {
            // Joined the same self-created-root way projectA was, so it too ends up with its own
            // local root.yaml — a candidate this owner's later retirement has to consider.
            await ProjectJoinCommand.RunAsync(
                second, projectB, claimedOwnerOverride: null, inner, keyStore, GitHubAccessFakes.GrantingPush(), cts.Token);
        }

        UnreachableRepositoryLedger unreachable = new(inner, projectB.RepositoryPath);

        ProjectJoinCommand.JoinOutcome reclaim;
        await using (IDocumentSession third = _postgres.Store.LightweightSession())
        {
            reclaim = await ProjectJoinCommand.RunAsync(
                third, projectA, realRootFingerprint, unreachable, keyStore, GitHubAccessFakes.GrantingPush(), cts.Token);
        }

        reclaim.RetiredPreviousRoot.Should().BeTrue("the required retirement in the project being joined still succeeds");
        inner.Writes.Should().Contain(
            w => w.RepositoryPath == projectA.RepositoryPath && w.Path.EndsWith("retired.yaml"),
            "the project being joined retires its own self-created root");
        inner.Writes.Should().NotContain(
            w => w.RepositoryPath == projectB.RepositoryPath && w.Path.EndsWith("retired.yaml"),
            "the unreachable project's own retirement never lands, but does not fail the join either");
    }

    [Fact]
    public async Task No_written_tree_ever_contains_the_private_key()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        ProjectDetails project = await SeedProjectAsync(cts.Token);
        FakeLedger ledger = new();

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        ProjectJoinCommand.JoinOutcome outcome = await ProjectJoinCommand.RunAsync(
            session, project, claimedOwnerOverride: null, ledger, new NodeKeyStore(), GitHubAccessFakes.GrantingPush(), cts.Token);

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
        Func<Task> act = () => ProjectJoinCommand.RunAsync(
            session, project, claimedOwnerOverride: null, ledger, brokenKeyStore, GitHubAccessFakes.GrantingPush(), cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>()).WithMessage("*h9k project join*");
        ledger.Writes.Should().BeEmpty("a node without a key never reaches a ledger write");
    }

    /// <summary>
    /// A non-zero exit is not the only way ssh-keygen can fail this node: when it is not on PATH
    /// at all, .NET's real process runner (ExternalProcess.RunAsync) throws Win32Exception before
    /// a ProcessResult ever exists to check an exit code against — starting the process happens
    /// outside any try block there. This drives NodeKeyStore through that exact failure mode
    /// rather than the exit-code stand-in above, so a regression that lets Win32Exception escape
    /// unhandled is caught here instead of surfacing as a raw crash in production (adversarial
    /// review, cycle 1, medium).
    /// </summary>
    [Fact]
    public async Task A_node_without_ssh_keygen_on_path_is_refused_naming_project_join_and_writes_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        ProjectDetails project = await SeedProjectAsync(cts.Token);
        FakeLedger ledger = new();
        ProcessRunner missingToolRunner = (_, _, _, _) =>
            throw new Win32Exception(2, "The system cannot find the file specified");
        NodeKeyStore brokenKeyStore = new(missingToolRunner);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Func<Task> act = () => ProjectJoinCommand.RunAsync(
            session, project, claimedOwnerOverride: null, ledger, brokenKeyStore, GitHubAccessFakes.GrantingPush(), cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>()).WithMessage("*h9k project join*");
        ledger.Writes.Should().BeEmpty("a node without a key never reaches a ledger write");
    }

    /// <summary>
    /// Join is refused when the project's account lacks push on the repository, with the
    /// repository and the rule named (idea 202383dc, A2b, item 2) — before any key is generated
    /// and before any ledger byte is written. This install's own role is still recorded on
    /// <see cref="ProjectGitHubMembers"/> despite the refusal: it is the one fact every install
    /// gets regardless of its own role (that projection's own doc comment), and it was previously
    /// lost with the refusal's own exception, thrown before the observation had ever been saved
    /// (independent pre-PR review, cycle 1, conformance and adversarial lenses, both medium).
    /// </summary>
    [Fact]
    public async Task Join_without_push_is_refused_naming_the_repository_and_the_rule()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        ProjectDetails project = await SeedProjectAsync(cts.Token);
        FakeLedger ledger = new();

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Func<Task> act = () => ProjectJoinCommand.RunAsync(
            session, project, claimedOwnerOverride: null, ledger, new NodeKeyStore(),
            GitHubAccessFakes.DenyingPush("acme/widgets", "READ"), cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>())
            .WithMessage("*acme/widgets*").WithMessage("*push*");
        ledger.Writes.Should().BeEmpty("a node without push never reaches a ledger write");

        await using IDocumentSession query = _postgres.Store.LightweightSession();
        ProjectGitHubMembers? members = await query.LoadAsync<ProjectGitHubMembers>(project.Id, cts.Token);
        members.Should().NotBeNull("this install's own role is saved before the push refusal can throw");
        members!.Members.Values.Should().Contain(member => member.Login == "test-user" && member.Role == GitHubRepositoryRole.Read);
    }

    /// <summary>
    /// Access is observed per project on the project stream even on an ordinary, successful join
    /// (idea 202383dc, A2b, item 3): this install's own role always, and the collaborator list
    /// (with roles) when this install's own account already has push.
    /// </summary>
    [Fact]
    public async Task A_successful_join_observes_this_installs_own_role_and_the_readable_collaborator_list()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        ProjectDetails project = await SeedProjectAsync(cts.Token);
        string collaborators = """[{"id":42,"login":"teammate","role_name":"write"}]""";

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        await ProjectJoinCommand.RunAsync(
            session, project, claimedOwnerOverride: null, new FakeLedger(), new NodeKeyStore(),
            GitHubAccessFakes.GrantingPush("acme/widgets", "ADMIN", collaborators), cts.Token);

        await using IDocumentSession query = _postgres.Store.LightweightSession();
        ProjectGitHubMembers? members = await query.LoadAsync<ProjectGitHubMembers>(project.Id, cts.Token);
        members.Should().NotBeNull();
        members!.Members.Values.Should().Contain(member => member.Login == "teammate" && member.Role == GitHubRepositoryRole.Write);
    }

    /// <summary>
    /// The account join runs gh as pairs GitHub's own numeric id with the login observed alongside
    /// it at the same identity read, never the registration-time placeholder login a connection was
    /// first registered under — a mismatch this same task's own fix pass found and closed
    /// (independent pre-PR review, cycle 1, conformance and adversarial lenses, both high): the
    /// recovery case is exactly an install whose genesis bootstrap ran with <c>gh</c>
    /// unauthenticated (registered under the <c>Environment.UserName</c> placeholder) and only later
    /// ran <c>gh auth login</c> as its real account, observed here onto the same connection.
    /// </summary>
    [Fact]
    public async Task Join_runs_gh_as_the_login_confirmed_alongside_the_confirmed_id_not_the_registration_placeholder()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));

        Guid connectionId = DomainId.New();
        await using IDocumentSession seedSession = _postgres.Store.LightweightSession();
        ConnectionRegistered registered = ConnectionDecider.Register(
            connectionId, Guid.Empty, WorkItemProvider.GitHub, "brianhallmanac", CredentialReference.GhCli, Now);
        seedSession.Events.StartStream<ConnectionAggregate>(connectionId, registered);
        seedSession.Events.Append(connectionId, new ConnectionGitHubIdentityObserved(connectionId, 4181388, "hallmanac", Now));
        await seedSession.SaveChangesAsync(cts.Token);

        await using IDocumentSession bootstrapSession = _postgres.Store.LightweightSession();
        BootstrapContext context = await NodeBootstrap.EnsureAsync(bootstrapSession, cts.Token);
        await bootstrapSession.SaveChangesAsync(cts.Token);

        Guid projectId = DomainId.New();
        await using IDocumentSession projectSession = _postgres.Store.LightweightSession();
        projectSession.Events.StartStream<ProjectAggregate>(
            projectId,
            ProjectDecider.Register(
                projectId, context.OwnerId, connectionId, "smoke",
                "/does/not/matter/on/a/fake/ledger", null, null, Now));
        await projectSession.SaveChangesAsync(cts.Token);
        ProjectDetails project = (await projectSession.LoadAsync<ProjectDetails>(projectId, cts.Token))!;

        RecordingProcessRunner tokenRunner = RecordingProcessRunner.Succeeding("gh-token-for-test\n");
        RecordingEnvironmentProcessRunner ghRunner = RecordingEnvironmentProcessRunner.Succeeding(
            """{"viewerPermission":"ADMIN","nameWithOwner":"acme/widgets"}""");
        ProjectGitHubAccessMirror access = new(new ProjectGitHubClient(ghRunner.Runner, tokenRunner.Runner));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        await ProjectJoinCommand.RunAsync(
            session, project, claimedOwnerOverride: null, new FakeLedger(), new NodeKeyStore(), access, cts.Token);

        tokenRunner.Calls.Should().Contain(
            call => call.Arguments.Contains("hallmanac"),
            "the token read must run as the login GitHub confirmed alongside the confirmed id, "
            + "not the registration-time placeholder");
        tokenRunner.Calls.Should().NotContain(call => call.Arguments.Contains("brianhallmanac"));
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
        Guid connectionId = await NodeBootstrapSeed.SeedGitHubConnectionAsync(_postgres.Store, cts.Token);

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
                    projectId, preExistingOwnerId, connectionId, "smoke", "/does/not/matter/on/a/fake/ledger",
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

        ProjectJoinCommand.JoinOutcome outcome;
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            outcome = await ProjectJoinCommand.RunAsync(
                session, project, claimedOwnerOverride: null, new FakeLedger(), new NodeKeyStore(),
                GitHubAccessFakes.GrantingPush(), cts.Token);
        }

        await using IDocumentSession query = _postgres.Store.LightweightSession();
        OwnerDetails owner = (await query.LoadAsync<OwnerDetails>(preExistingOwnerId, cts.Token))!;
        owner.RootFingerprint.Should().NotBeNullOrEmpty("the pre-existing Guid owner is mapped onto the new root fingerprint");

        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.AssignedOwnerId.Should().Be(
            preExistingOwnerId, "the task's own Guid assignment is untouched by the fingerprint mapping, so a plain Guid dispatch comparison still matches");

        NodeDetails node = (await query.LoadAsync<NodeDetails>(outcome.NodeId, cts.Token))!;
        node.OwnerId.Should().Be(
            preExistingOwnerId, "join must map this node onto the pre-existing owner's own Guid rather than minting a second owner");

        // The migration criterion is that a task already assigned before the join keeps dispatching
        // after it — proven here by actually running the daemon's own claim seam
        // (DispatchEngine.ClaimEligibleAsync, which applies TryClaimAsync's queued-state,
        // assigned-owner, project-archived, lease, and expected-version checks), not by calling
        // TaskDecider.Claim directly: that bypasses every one of those and would pass even if the
        // real dispatcher could no longer claim this task (Copilot review, PR #366).
        NodeContext claimingNode = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, cts.Token);
        DispatchEngine engine = new(
            _postgres.Store, claimingNode, new DaemonConnection(_postgres.ConnectionString), new FakeProcessManager(),
            new LaunchHoldEngine(_postgres.Store, NullLogger<LaunchHoldEngine>.Instance),
            Options.Create(new DaemonOptions { MaxConcurrentTaskRuns = 1, LeaseTimeout = TimeSpan.FromSeconds(60) }),
            NullLogger<DispatchEngine>.Instance);

        IReadOnlyList<ClaimedWork> claimed = await engine.ClaimEligibleAsync(cts.Token);

        claimed.Select(work => work.TaskId).Should().Contain(
            taskId, "the node this join just enrolled can still claim the pre-existing owner's already-assigned task");
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
                projectId, context.OwnerId, context.ConnectionId, "smoke",
                "/does/not/matter/on/a/fake/ledger", null, null, Now));
        await projectSession.SaveChangesAsync(cancellationToken);

        return (await projectSession.LoadAsync<ProjectDetails>(projectId, cancellationToken))!;
    }

    /// <summary>
    /// Wraps another <see cref="ILedger"/> and returns <see cref="LedgerWriteOutcome.Conflict"/>
    /// the first time a write's path contains <paramref name="pathMustContain"/>, without ever
    /// touching the wrapped ledger's own store — simulating another writer that raced this call
    /// and, by the time this call re-reads and retries, has already lost its own race too, the
    /// same shape <see cref="GitLedger.WriteAsync"/>'s own retry loop resolves in practice. Every
    /// other write passes straight through.
    /// </summary>
    private sealed class ConflictOnceLedger(ILedger inner, string pathMustContain) : ILedger
    {
        private bool _injected;

        public int ConflictsInjected { get; private set; }

        public Task<LedgerFile> ReadAsync(string repositoryPath, string refName, string path, CancellationToken cancellationToken) =>
            inner.ReadAsync(repositoryPath, refName, path, cancellationToken);

        public async Task<LedgerWriteOutcome> WriteAsync(LedgerWriteRequest request, CancellationToken cancellationToken)
        {
            if (!_injected && request.Path.Contains(pathMustContain, StringComparison.Ordinal))
            {
                _injected = true;
                ConflictsInjected++;
                LedgerFile current = await inner.ReadAsync(request.RepositoryPath, request.RefName, request.Path, cancellationToken);
                return LedgerWriteOutcome.Conflict(current);
            }

            return await inner.WriteAsync(request, cancellationToken);
        }

        public Task<LedgerWriteOutcome> DeleteAsync(LedgerDeleteRequest request, CancellationToken cancellationToken) =>
            inner.DeleteAsync(request, cancellationToken);
    }

    /// <summary>
    /// Wraps another <see cref="ILedger"/> and fails every write targeting
    /// <paramref name="unreachableRepositoryPath"/> with <see cref="LedgerPushRejectedException"/>,
    /// simulating a project whose remote is gone or unreachable (an archived project's deleted
    /// GitHub repository, or one behind a VPN that is down) — every other repository passes
    /// straight through to <paramref name="inner"/>.
    /// </summary>
    private sealed class UnreachableRepositoryLedger(ILedger inner, string unreachableRepositoryPath) : ILedger
    {
        public Task<LedgerFile> ReadAsync(string repositoryPath, string refName, string path, CancellationToken cancellationToken) =>
            inner.ReadAsync(repositoryPath, refName, path, cancellationToken);

        public Task<LedgerWriteOutcome> WriteAsync(LedgerWriteRequest request, CancellationToken cancellationToken) =>
            request.RepositoryPath == unreachableRepositoryPath
                ? throw new LedgerPushRejectedException(request.RefName, attempts: 5, gitError: "could not read from remote repository")
                : inner.WriteAsync(request, cancellationToken);

        public Task<LedgerWriteOutcome> DeleteAsync(LedgerDeleteRequest request, CancellationToken cancellationToken) =>
            request.RepositoryPath == unreachableRepositoryPath
                ? throw new LedgerPushRejectedException(request.RefName, attempts: 5, gitError: "could not read from remote repository")
                : inner.DeleteAsync(request, cancellationToken);
    }
}
