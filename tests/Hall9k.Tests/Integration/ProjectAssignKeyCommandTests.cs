using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// h9k project assign-key (idea 202383dc, M2 backfill; Brian's ruling 2026-09-17) against a real
/// Marten/Postgres session — Owner, Node, and Project are all real event streams — but never a
/// real git repository: <see cref="FakeLedger"/> stands in for A1 and
/// <see cref="FakeLedgerChainReader"/> stands in for the chain reader throughout, per Brian's
/// 2026-09-13 testing rule.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class ProjectAssignKeyCommandTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
    private const string RepositoryPath = "/does/not/matter/on/a/fake/ledger";

    private readonly PostgresFixture _postgres;
    private readonly ScopedTestHome _scopedHome = new();

    public ProjectAssignKeyCommandTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync()
    {
        _scopedHome.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task The_genesis_owner_backfills_a_key_for_a_ledger_that_predates_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        FakeLedger ledger = new();
        ProjectDetails project = await SeedProjectAsync(cts.Token);
        (string myRoot, NodeSigningKey myKey) = await EstablishOwnRootAsync(cts.Token);
        await WriteGenesisMemberFileWithNoKeyAsync(ledger, myRoot, cts.Token);

        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myRoot] = new(myRoot, myKey.PublicKeyLine, []) },
            [new ProjectMember(myRoot, MembershipRole.Owner, Now)],
            GenesisRootFingerprint: myRoot));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        int exitCode = await ProjectAssignKeyCommand.RunAsync(
            session, project, ledger, chainReader, new NodeKeyStore(), cts.Token);

        exitCode.Should().Be(ExitCodes.Ok);
        // The seed helper's own write and the command's actual backfill both land on this exact
        // ref and path — the last one is the backfill's own.
        LedgerWriteRequest write = ledger.Writes.Last(w => w.RefName == "refs/hall9k/ledger/members");
        write.Content.Should().Contain("project_key").And.Contain("root_fingerprint").And.Contain(myRoot);
        write.SigningKey.Should().NotBeNull("the backfill commit is signed the same as every other ledger write");

        ProjectDetails updated = (await session.LoadAsync<ProjectDetails>(project.Id, cts.Token))!;
        updated.ProjectKey.Should().NotBeNull();
        updated.ProjectKey!.Length.Should().Be(26, "a project key is the 26-character ULID idea 202383dc, M2 mints");
    }

    [Fact]
    public async Task Assign_key_is_refused_for_anyone_but_the_genesis_owner()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        FakeLedger ledger = new();
        ProjectDetails project = await SeedProjectAsync(cts.Token);
        (string myRoot, NodeSigningKey myKey) = await EstablishOwnRootAsync(cts.Token);
        string genesisRoot = new('c', 64);

        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myRoot] = new(myRoot, myKey.PublicKeyLine, []) },
            [new ProjectMember(myRoot, MembershipRole.Owner, Now), new ProjectMember(genesisRoot, MembershipRole.Owner, Now)],
            GenesisRootFingerprint: genesisRoot));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Func<Task> act = () => ProjectAssignKeyCommand.RunAsync(
            session, project, ledger, chainReader, new NodeKeyStore(), cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>()).WithMessage("*genesis owner*");
        ledger.Writes.Should().BeEmpty("a non-genesis owner's attempt is refused before any push");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, adversarial lens, medium: <c>genesisRoot != myRoot</c>
    /// alone only rules out a mismatched literal string — <c>owner.RootFingerprint</c> is this
    /// node's own unverified claim about itself (<c>h9k project join --owner</c>), never something
    /// a vouch proved, and the genesis owner's public root fingerprint is printed in plain sight
    /// (<c>h9k owner show</c>). A node that merely claims it, with no enrollment in that owner's
    /// real chain, must be refused the identical way the sibling <c>ProjectMemberRemoveCommand</c>
    /// already refuses an unenrolled claimant on the same ref.
    /// </summary>
    [Fact]
    public async Task Assign_key_is_refused_for_a_node_that_merely_claims_the_genesis_roots_fingerprint()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        FakeLedger ledger = new();
        ProjectDetails project = await SeedProjectAsync(cts.Token);
        const string genesisRoot = "genuine-genesis-owner-root";

        // This node claims the genesis owner's own public root fingerprint (h9k project join
        // --owner) without ever being vouched into that owner's real chain — the unverified claim
        // an attacker who merely read h9k owner show could make too.
        await using IDocumentSession claimSession = _postgres.Store.LightweightSession();
        BootstrapContext context = await NodeBootstrap.EnsureAsync(claimSession, cts.Token);
        await claimSession.SaveChangesAsync(cts.Token);
        _ = await new NodeKeyStore().EnsureAsync(context.NodeId, cts.Token);
        OwnerAggregate owner = await claimSession.Events.AggregateStreamAsync<OwnerAggregate>(context.OwnerId, token: cts.Token)
            ?? throw new InvalidOperationException("Owner bootstrap did not create an owner stream.");
        claimSession.Events.Append(context.OwnerId, OwnerDecider.ClaimRoot(owner, genesisRoot, verified: false, Now));
        await claimSession.SaveChangesAsync(cts.Token);

        // The real genesis member file the real owner actually wrote at genesis, still keyless —
        // present in the ledger so a missing gate would actually succeed in minting and pushing a
        // key onto it, rather than merely failing downstream for an unrelated reason. Counted here
        // as the one write this setup itself makes, so the assertion below can tell it apart from
        // any write the command under test performs.
        await WriteGenesisMemberFileWithNoKeyAsync(ledger, genesisRoot, cts.Token);
        int writesBeforeAssignKey = ledger.Writes.Count;

        // The genesis owner's real chain, published by the real owner: this node's own key was
        // never vouched into it, only the real owner's own root and an empty node list.
        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [genesisRoot] = new(genesisRoot, "ssh-ed25519 AAAAREAL real-owner-key", []) },
            [new ProjectMember(genesisRoot, MembershipRole.Owner, Now)],
            GenesisRootFingerprint: genesisRoot));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Func<Task> act = () => ProjectAssignKeyCommand.RunAsync(
            session, project, ledger, chainReader, new NodeKeyStore(), cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>()).WithMessage("*vouched*");
        ledger.Writes.Should().HaveCount(
            writesBeforeAssignKey,
            "a mere claim of the genesis owner's root, never vouched, must never mint a key no other install agreed to");
    }

    [Fact]
    public async Task Assign_key_is_refused_once_a_key_already_exists()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        FakeLedger ledger = new();
        ProjectDetails project = await SeedProjectAsync(cts.Token);
        (string myRoot, NodeSigningKey myKey) = await EstablishOwnRootAsync(cts.Token);

        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myRoot] = new(myRoot, myKey.PublicKeyLine, []) },
            [new ProjectMember(myRoot, MembershipRole.Owner, Now)],
            GenesisRootFingerprint: myRoot, ProjectKey: "01ARZ3NDEKTSV4RRFFQ69G5FAV"));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Func<Task> act = () => ProjectAssignKeyCommand.RunAsync(
            session, project, ledger, chainReader, new NodeKeyStore(), cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>()).WithMessage("*already has a key*");
        ledger.Writes.Should().BeEmpty("a project key is a single, load-bearing fact — never overwritten");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, conformance lens, high: the genesis member file's own
    /// RAW content is never authorized the way <see cref="ILedgerChainReader"/>'s own replay is, so
    /// a <c>project_key</c> line a stranger forged onto the ref (exactly the rewrite
    /// <c>GitLedgerChainReader</c>'s own authorization hardening refuses to read back —
    /// <see cref="TrustChain.ProjectKey"/> stays null here even though the raw file carries one)
    /// must never block this backfill, and the command must never report a key that was never
    /// actually written to the ledger.
    /// </summary>
    [Fact]
    public async Task Assign_key_backfills_past_an_unauthorized_project_key_line_already_in_the_raw_file()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        FakeLedger ledger = new();
        ProjectDetails project = await SeedProjectAsync(cts.Token);
        (string myRoot, NodeSigningKey myKey) = await EstablishOwnRootAsync(cts.Token);
        await WriteGenesisMemberFileWithForgedKeyAsync(ledger, myRoot, "forged-not-authorized-key", cts.Token);

        // The chain reader's own authorized replay never accepted the forged line above, so it
        // still reports no project key — the exact disagreement between raw content and authorized
        // state the forged-line hazard depends on.
        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myRoot] = new(myRoot, myKey.PublicKeyLine, []) },
            [new ProjectMember(myRoot, MembershipRole.Owner, Now)],
            GenesisRootFingerprint: myRoot));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        int exitCode = await ProjectAssignKeyCommand.RunAsync(
            session, project, ledger, chainReader, new NodeKeyStore(), cts.Token);

        exitCode.Should().Be(ExitCodes.Ok);
        LedgerWriteRequest write = ledger.Writes.Last(w => w.RefName == "refs/hall9k/ledger/members");
        write.Content.Should().NotContain("forged-not-authorized-key", "the forged line is stripped, never buried under a real one");
        write.Content.Should().Contain("project_key").And.Contain("root_fingerprint").And.Contain(myRoot);

        ProjectDetails updated = (await session.LoadAsync<ProjectDetails>(project.Id, cts.Token))!;
        updated.ProjectKey.Should().NotBeNull();
        updated.ProjectKey!.Length.Should().Be(26, "the recorded key is the ULID actually written, never a fabricated one");
        write.Content.Should().Contain(updated.ProjectKey!, "the command must record and report only the key it actually wrote");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 3, both lenses, high: the genesis member file's own RAW
    /// content is never authorized the way <see cref="ILedgerChainReader"/>'s own replay is, so an
    /// unauthorized push rewriting <c>role</c> (not only <c>project_key</c>) to demote the genesis
    /// owner must never be signed and re-pushed by the backfill itself — that would launder a write
    /// the chain replay refuses into one it accepts, permanently orphaning the project's own
    /// owner-role member. The written content must come from the authorized chain's own
    /// <see cref="TrustChain.Members"/> entry, never from the raw tip.
    /// </summary>
    [Fact]
    public async Task Assign_key_never_launders_an_unauthorized_role_rewrite_already_in_the_raw_file()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        FakeLedger ledger = new();
        ProjectDetails project = await SeedProjectAsync(cts.Token);
        (string myRoot, NodeSigningKey myKey) = await EstablishOwnRootAsync(cts.Token);
        await WriteGenesisMemberFileWithForgedRoleAsync(ledger, myRoot, cts.Token);

        // The chain reader's own authorized replay refused the forged role rewrite above, so it
        // still reports the genesis owner as Owner — the exact disagreement between raw content and
        // authorized state the role-laundering hazard depends on.
        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myRoot] = new(myRoot, myKey.PublicKeyLine, []) },
            [new ProjectMember(myRoot, MembershipRole.Owner, Now)],
            GenesisRootFingerprint: myRoot));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        int exitCode = await ProjectAssignKeyCommand.RunAsync(
            session, project, ledger, chainReader, new NodeKeyStore(), cts.Token);

        exitCode.Should().Be(ExitCodes.Ok);
        LedgerWriteRequest write = ledger.Writes.Last(w => w.RefName == "refs/hall9k/ledger/members");
        write.Content.Should().Contain("role: \"owner\"", "the authorized chain's own role must win, never the raw file's forged one");
        write.Content.Should().NotContain("member", "a demoted role forged onto the raw file must never survive into the signed write");
        write.Content.Should().Contain($"issued_at: \"{Now:O}\"", "the authorized chain's own issued_at must win, never a forged one");
    }

    /// <summary>Bootstraps this node's owner and claims this node's own key as its root, verified
    /// — the same shape <c>h9k project join</c> with no <c>--owner</c> records — without touching
    /// a real ledger (mirrors <c>InviteCommandsTests.EstablishOwnRootAsync</c>).</summary>
    private async Task<(string Fingerprint, NodeSigningKey Key)> EstablishOwnRootAsync(CancellationToken cancellationToken)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        NodeSigningKey key = await new NodeKeyStore().EnsureAsync(context.NodeId, cancellationToken);

        OwnerAggregate owner = await session.Events.AggregateStreamAsync<OwnerAggregate>(context.OwnerId, token: cancellationToken)
            ?? throw new InvalidOperationException("Owner bootstrap did not create an owner stream.");
        session.Events.Append(context.OwnerId, OwnerDecider.ClaimRoot(owner, key.Fingerprint, verified: true, Now));
        await session.SaveChangesAsync(cancellationToken);

        return (key.Fingerprint, key);
    }

    /// <summary>What a genesis join before this piece shipped would have left in the ledger:
    /// <c>members/&lt;fingerprint&gt;.yaml</c> with no <c>project_key</c> field at all — the exact
    /// shape <c>h9k project assign-key</c> exists to backfill.</summary>
    private static async Task WriteGenesisMemberFileWithNoKeyAsync(
        FakeLedger ledger, string fingerprint, CancellationToken cancellationToken)
    {
        string refName = "refs/hall9k/ledger/members";
        string path = $"members/{fingerprint}.yaml";
        string content = $"root_fingerprint: \"{fingerprint}\"\nrole: \"owner\"\nissued_at: \"{Now:O}\"\n";
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, refName, path, content, ExpectedBlobId: null, "seed genesis member with no key",
                new LedgerCommitter("Test Owner", "owner@test.local"), new LedgerSigningKey("/does/not/matter/key")),
            cancellationToken);
    }

    /// <summary>What a stranger's unauthorized push of <c>refs/hall9k/ledger/members</c> can leave
    /// behind: a <c>project_key</c> line the chain reader's own authorized replay refuses to read
    /// back, since it never came from a commit the genesis root's own chain actually authorized.</summary>
    private static async Task WriteGenesisMemberFileWithForgedKeyAsync(
        FakeLedger ledger, string fingerprint, string forgedKey, CancellationToken cancellationToken)
    {
        string refName = "refs/hall9k/ledger/members";
        string path = $"members/{fingerprint}.yaml";
        string content =
            $"root_fingerprint: \"{fingerprint}\"\nrole: \"owner\"\nissued_at: \"{Now:O}\"\nproject_key: \"{forgedKey}\"\n";
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, refName, path, content, ExpectedBlobId: null, "seed genesis member with a forged key",
                new LedgerCommitter("Stranger", "stranger@test.local"), new LedgerSigningKey("/does/not/matter/key")),
            cancellationToken);
    }

    /// <summary>What a stranger's unauthorized push of <c>refs/hall9k/ledger/members</c> can leave
    /// behind: a <c>role</c> demoting the genesis owner that the chain reader's own authorized
    /// replay refuses to read back, since it never came from a commit an owner-role member's own
    /// chain actually authorized.</summary>
    private static async Task WriteGenesisMemberFileWithForgedRoleAsync(
        FakeLedger ledger, string fingerprint, CancellationToken cancellationToken)
    {
        string refName = "refs/hall9k/ledger/members";
        string path = $"members/{fingerprint}.yaml";
        string content = $"root_fingerprint: \"{fingerprint}\"\nrole: \"member\"\nissued_at: \"{Now:O}\"\n";
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, refName, path, content, ExpectedBlobId: null, "seed genesis member with a forged role",
                new LedgerCommitter("Stranger", "stranger@test.local"), new LedgerSigningKey("/does/not/matter/key")),
            cancellationToken);
    }

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
                projectId, context.OwnerId, context.ConnectionId, "smoke", RepositoryPath, null, null, Now));
        await projectSession.SaveChangesAsync(cancellationToken);

        return (await projectSession.LoadAsync<ProjectDetails>(projectId, cancellationToken))!;
    }
}
