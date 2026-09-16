using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Trust;
using Hall9k.Daemon;
using Hall9k.Daemon.Invites;
using Hall9k.Domain.Features.Invite;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// Invites (idea 202383dc, T2) against a real Marten/Postgres session for whichever node's own
/// local identity is active — Owner, Node, Project, and Invite are all real event streams — but
/// never a real git repository: <see cref="FakeLedger"/> stands in for A1 and
/// <see cref="FakeLedgerChainReader"/> stands in for the chain reader throughout, per Brian's
/// 2026-09-13 testing rule.
/// <para>
/// <c>NodeBootstrap.EnsureAsync</c> resolves "this device's own identity" as the first Owner/Node
/// on whatever Postgres schema it is handed, with no way to run two distinct local identities in
/// the same schema at once. A genuine three-node scenario is built the same way
/// <see cref="NodeVouchAndRevokeCommandTests"/> already does: the shared Postgres identity plays
/// ONE real, locally-bootstrapped role at a time (wiped and re-seeded with fresh key material
/// between roles that must be genuinely distinct devices), while a role that never needs to run a
/// local command of its own — the joining node proving possession — is represented purely by its
/// own key pair (<see cref="NodeKeyStore"/>, keyed by node id, coexists fine for any number of
/// "devices" under one shared <c>HALL9K_HOME</c>) and the node file it would have self-announced,
/// written straight into the shared <see cref="FakeLedger"/>.
/// </para>
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection("Hall9kHome")]
public sealed class InviteCommandsTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private const string RepositoryPath = "/does/not/matter/on/a/fake/ledger";

    private readonly PostgresFixture _postgres;
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"hall9k-invites-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    public InviteCommandsTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", _home);
        await WipeAsync();
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
    public async Task A_node_of_owner_invite_round_trips_across_three_nodes_and_ends_verified()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        FakeLedger ledger = new();

        // Node 1: the root — genesis join establishes it, self-signed, and this project's own
        // first (owner-role) member.
        ProjectDetails projectForRoot = await SeedProjectAsync(cts.Token);
        ProjectJoinCommand.JoinOutcome genesis;
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            genesis = await ProjectJoinCommand.RunAsync(
                session, projectForRoot, claimedOwnerOverride: null, invite: null, ledger, new NodeKeyStore(),
                GitHubAccessFakes.GrantingPush(), cts.Token);
        }

        string root = genesis.ClaimedOwnerFingerprint;
        NodeSigningKey rootKey = await new NodeKeyStore().EnsureAsync(genesis.NodeId, cts.Token);

        // Node 2: an already-enrolled node of the SAME owner — joins claiming the root's own
        // fingerprint (the unverified "team half" claim, idea 202383dc's own shape), its own
        // separate key material under the identical HALL9K_HOME (keyed by node id, so this never
        // collides with node 1's own key).
        await WipeAsync();
        ProjectDetails projectForMinter = await SeedProjectAsync(cts.Token);
        ProjectJoinCommand.JoinOutcome minterJoin;
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            minterJoin = await ProjectJoinCommand.RunAsync(
                session, projectForMinter, claimedOwnerOverride: root, invite: null, ledger, new NodeKeyStore(),
                GitHubAccessFakes.GrantingPush(), cts.Token);
        }

        NodeSigningKey minterKey = await new NodeKeyStore().EnsureAsync(minterJoin.NodeId, cts.Token);
        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner>
            {
                [root] = new(root, rootKey.PublicKeyLine, [new TrustedNode(minterJoin.NodeId.ToString(), minterKey.PublicKeyLine, minterKey.Fingerprint, Now)]),
            },
            []));

        string secret;
        Guid inviteId;
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            int exitCode = await NodeInviteCommand.RunAsync(session, ledger, chainReader, new NodeKeyStore(), cts.Token);
            exitCode.Should().Be(ExitCodes.Ok);

            InviteDetails minted = (await session.Query<InviteDetails>().ToListAsync(cts.Token)).Single();
            minted.Claim.Should().Be(InviteClaimKind.NodeOfOwner);
            minted.Spent.Should().BeFalse();
            inviteId = minted.Id;

            InviteAggregate aggregate = (await session.Events.AggregateStreamAsync<InviteAggregate>(inviteId, token: cts.Token))!;
            secret = aggregate.Secret;
        }

        ledger.Writes.Should().Contain(w => w.Path == $"owners/{root}/invites/{inviteId}.yaml", "the invite's own hash is published to the ledger");

        // Node 3: the joiner. Never runs its own local command — it proves possession purely by
        // what it would have self-announced in its own node file, written directly into the
        // shared ledger the same way ProjectJoinCommand.RunAsync itself would.
        Guid joinerNodeId = DomainId.New();
        NodeSigningKey joinerKey = await new NodeKeyStore().EnsureAsync(joinerNodeId, cts.Token);
        string proof = InviteSecret.ComputeProof(secret, joinerKey.Fingerprint);
        await WriteSelfAnnouncedNodeFileAsync(ledger, joinerNodeId, joinerKey, ownerFingerprint: root, inviteProof: proof, cts.Token);

        // Node 2's own daemon sweep — same local identity as the minter phase above, so
        // NodeContext resolves back to the identical owner/node this invite was minted under.
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, cts.Token);
        InviteSweepEngine engine = new(_postgres.Store, node, ledger, new NodeKeyStore(), NullLogger<InviteSweepEngine>.Instance);

        InviteSweepResult sweep = await engine.SweepOnceAsync(cts.Token);
        sweep.InvitesSpent.Should().Be(1, "the joiner's own proof matches the one outstanding invite");

        LedgerWriteRequest vouchWrite = ledger.Writes.Single(w => w.Path == $"owners/{root}/nodes/{joinerNodeId}.yaml");
        vouchWrite.Content.Should().Contain(joinerKey.PublicKeyLine);

        LedgerFile invitesFile = await ledger.ReadAsync(RepositoryPath, $"refs/hall9k/ledger/owners/{root}", $"owners/{root}/invites/{inviteId}.yaml", cts.Token);
        InviteLedgerRecord? spentRecord = InviteLedgerRecord.Parse(invitesFile.Content);
        spentRecord!.Spent.Should().BeTrue("the ledger's own record, not only the local one, ends up marked spent");

        await using (IDocumentSession assertSession = _postgres.Store.LightweightSession())
        {
            InviteDetails after = (await assertSession.LoadAsync<InviteDetails>(inviteId, cts.Token))!;
            after.Spent.Should().BeTrue();
            after.ClaimedByNodeId.Should().Be(joinerNodeId);

            OwnerDetails minterOwner = (await assertSession.LoadAsync<OwnerDetails>((await NodeBootstrap.EnsureAsync(assertSession, cts.Token)).OwnerId, cts.Token))!;
            minterOwner.VouchedNodes.Should().ContainKey(joinerNodeId, "the minting node's own owner stream records having vouched it");
        }
    }

    [Fact]
    public async Task A_wrong_proof_never_vouches()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        FakeLedger ledger = new();
        (string root, Guid inviteId, string secret, _) = await MintNodeOfOwnerInviteAsSelfAsync(ledger, cts.Token);

        // A candidate whose proof does not derive from this invite's own secret — a different
        // secret entirely, the shape a stranger with no knowledge of the real one would produce.
        Guid strangerNodeId = DomainId.New();
        NodeSigningKey strangerKey = await new NodeKeyStore().EnsureAsync(strangerNodeId, cts.Token);
        string wrongProof = InviteSecret.ComputeProof("not-the-real-secret", strangerKey.Fingerprint);
        await WriteSelfAnnouncedNodeFileAsync(ledger, strangerNodeId, strangerKey, ownerFingerprint: root, inviteProof: wrongProof, cts.Token);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, cts.Token);
        InviteSweepEngine engine = new(_postgres.Store, node, ledger, new NodeKeyStore(), NullLogger<InviteSweepEngine>.Instance);

        InviteSweepResult sweep = await engine.SweepOnceAsync(cts.Token);

        sweep.InvitesSpent.Should().Be(0, "a wrong proof never matches, so nothing is vouched");
        ledger.Writes.Should().NotContain(w => w.Path.Contains("/nodes/", StringComparison.Ordinal) && w.Path.Contains(strangerNodeId.ToString(), StringComparison.Ordinal));

        InviteDetails details = (await session.LoadAsync<InviteDetails>(inviteId, cts.Token))!;
        details.Spent.Should().BeFalse();
    }

    [Fact]
    public async Task A_candidate_whose_vouch_write_failed_is_still_offered_on_the_same_engines_next_sweep()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        FakeLedger ledger = new();
        (string root, Guid inviteId, string secret, _) = await MintNodeOfOwnerInviteAsSelfAsync(ledger, cts.Token);

        Guid joinerNodeId = DomainId.New();
        NodeSigningKey joinerKey = await new NodeKeyStore().EnsureAsync(joinerNodeId, cts.Token);
        string proof = InviteSecret.ComputeProof(secret, joinerKey.Fingerprint);
        await WriteSelfAnnouncedNodeFileAsync(ledger, joinerNodeId, joinerKey, ownerFingerprint: root, inviteProof: proof, cts.Token);

        // The vouch write fails exactly once — a transient push rejection, the same kind
        // InviteSweepEngine's own catch clause treats as "retry next sweep" rather than abandon.
        // The joiner's own node ref never moves again after this: its tip is exactly what a
        // singleton-lifetime engine would have already cached from this first, failed tick.
        string vouchPath = $"owners/{root}/nodes/{joinerNodeId}.yaml";
        FailFirstWriteLedger flakyLedger = new(ledger, vouchPath);

        // One engine instance for both ticks — InviteSweepEngine is registered AddSingleton in
        // Program.cs, so this is the shape a real process actually runs, unlike every other test
        // in this file, which builds a fresh engine (and so a fresh, empty candidate-tip cache)
        // per sweep.
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, cts.Token);
        InviteSweepEngine engine = new(_postgres.Store, node, flakyLedger, new NodeKeyStore(), NullLogger<InviteSweepEngine>.Instance);

        InviteSweepResult firstTick = await engine.SweepOnceAsync(cts.Token);
        firstTick.InvitesSpent.Should().Be(0, "the vouch write failed partway through, so this tick could not spend the invite");

        InviteSweepResult secondTick = await engine.SweepOnceAsync(cts.Token);
        secondTick.InvitesSpent.Should().Be(
            1, "the candidate's node ref tip never moved between ticks, but a read that fully failed to land must still be retried");

        ledger.Writes.Should().Contain(w => w.Path == vouchPath);

        await using IDocumentSession assertSession = _postgres.Store.LightweightSession();
        InviteDetails after = (await assertSession.LoadAsync<InviteDetails>(inviteId, cts.Token))!;
        after.Spent.Should().BeTrue();
    }

    [Fact]
    public async Task A_node_of_owner_invite_refuses_to_overwrite_an_existing_node()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        FakeLedger ledger = new();
        (string root, Guid inviteId, string secret, _) = await MintNodeOfOwnerInviteAsSelfAsync(ledger, cts.Token);

        // An existing enrolled node, vouched some other way (a direct h9k node vouch, or the plain
        // join genesis path) — never through this invite, so this invite's own local record never
        // recorded this project as already vouched into.
        Guid victimNodeId = DomainId.New();
        const string victimPublicKeyLine = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAVICTIMKEYLINE victim";
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, $"refs/hall9k/ledger/owners/{root}", $"owners/{root}/nodes/{victimNodeId}.yaml",
                $"node_id: \"{victimNodeId}\"\npublic_key: \"{victimPublicKeyLine}\"\nissued_at: \"{Now:O}\"\n",
                ExpectedBlobId: null, "seed existing enrolled node", new LedgerCommitter("Test Node", "node@test.local"),
                new LedgerSigningKey("/does/not/matter/key")),
            cts.Token);

        // The invite holder's own key checks out (the HMAC proof matches), but it self-announces
        // under the VICTIM's own already-enrolled node id — exactly the unverified ref-name claim
        // the sweep must refuse rather than silently honor by evicting the real node's key.
        NodeSigningKey attackerKey = await new NodeKeyStore().EnsureAsync(DomainId.New(), cts.Token);
        string proof = InviteSecret.ComputeProof(secret, attackerKey.Fingerprint);
        await WriteSelfAnnouncedNodeFileAsync(ledger, victimNodeId, attackerKey, ownerFingerprint: root, inviteProof: proof, cts.Token);

        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, cts.Token);
        InviteSweepEngine engine = new(_postgres.Store, node, ledger, new NodeKeyStore(), NullLogger<InviteSweepEngine>.Instance);

        InviteSweepResult sweep = await engine.SweepOnceAsync(cts.Token);
        sweep.InvitesSpent.Should().Be(0, "the sweep refuses to overwrite an already-enrolled node's own file");

        LedgerFile victimFile = await ledger.ReadAsync(
            RepositoryPath, $"refs/hall9k/ledger/owners/{root}", $"owners/{root}/nodes/{victimNodeId}.yaml", cts.Token);
        victimFile.Content.Should().Contain(victimPublicKeyLine, "the existing node's own key must survive untouched");

        await using IDocumentSession assertSession = _postgres.Store.LightweightSession();
        InviteDetails after = (await assertSession.LoadAsync<InviteDetails>(inviteId, cts.Token))!;
        after.Spent.Should().BeFalse("the invite stays outstanding so a legitimate holder can still be retried");
    }

    [Fact]
    public async Task A_spent_invite_is_refused_at_join_and_ignored_at_the_next_sweep()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        FakeLedger ledger = new();
        (string root, _, string secret, _) = await MintNodeOfOwnerInviteAsSelfAsync(ledger, cts.Token);

        // First claimant spends it for real, through the sweep.
        Guid firstJoinerId = DomainId.New();
        NodeSigningKey firstJoinerKey = await new NodeKeyStore().EnsureAsync(firstJoinerId, cts.Token);
        string firstProof = InviteSecret.ComputeProof(secret, firstJoinerKey.Fingerprint);
        await WriteSelfAnnouncedNodeFileAsync(ledger, firstJoinerId, firstJoinerKey, root, firstProof, cts.Token);

        {
            NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, cts.Token);
            InviteSweepEngine engine = new(_postgres.Store, node, ledger, new NodeKeyStore(), NullLogger<InviteSweepEngine>.Instance);
            (await engine.SweepOnceAsync(cts.Token)).InvitesSpent.Should().Be(1);
        }

        // Refused at join: a second, genuine holder of the same leaked secret tries to prove
        // possession too, against the now-spent invite.
        ProjectDetails secondProject = await SeedProjectAsync(cts.Token);
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            Func<Task> act = () => ProjectJoinCommand.RunAsync(
                session, secondProject, claimedOwnerOverride: null, invite: secret, ledger, new NodeKeyStore(),
                GitHubAccessFakes.GrantingPush(), cts.Token);
            (await act.Should().ThrowAsync<DomainValidationException>()).WithMessage("*spent*");
        }

        // Ignored at the next sweep: a second candidate with a genuinely matching proof appears,
        // but the invite is already spent locally, so it is never even considered.
        Guid secondJoinerId = DomainId.New();
        NodeSigningKey secondJoinerKey = await new NodeKeyStore().EnsureAsync(secondJoinerId, cts.Token);
        string secondProof = InviteSecret.ComputeProof(secret, secondJoinerKey.Fingerprint);
        await WriteSelfAnnouncedNodeFileAsync(ledger, secondJoinerId, secondJoinerKey, root, secondProof, cts.Token);

        {
            NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, cts.Token);
            InviteSweepEngine engine = new(_postgres.Store, node, ledger, new NodeKeyStore(), NullLogger<InviteSweepEngine>.Instance);
            (await engine.SweepOnceAsync(cts.Token)).InvitesSpent.Should().Be(0, "an already-spent invite is filtered out before any candidate is even read");
        }

        ledger.Writes.Should().NotContain(w => w.Path == $"owners/{root}/nodes/{secondJoinerId}.yaml");
    }

    [Fact]
    public async Task An_expired_invite_is_refused_at_join_and_ignored_at_the_next_sweep()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        FakeLedger ledger = new();

        ProjectDetails project = await SeedProjectAsync(cts.Token);
        ProjectJoinCommand.JoinOutcome genesis;
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            genesis = await ProjectJoinCommand.RunAsync(
                session, project, claimedOwnerOverride: null, invite: null, ledger, new NodeKeyStore(),
                GitHubAccessFakes.GrantingPush(), cts.Token);
        }

        string root = genesis.ClaimedOwnerFingerprint;
        Guid inviteId = DomainId.New();
        string secret = InviteSecret.Generate(root, inviteId);
        string secretHash = InviteSecret.Hash(secret);
        DateTimeOffset expiresAt = Now.AddHours(-1);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<InviteAggregate>(
                inviteId,
                InviteDecider.Mint(
                    inviteId, genesis.NodeId, root, InviteClaimKind.NodeOfOwner, role: null, projectId: null,
                    projectRepositoryPath: null, secret, secretHash, Now.AddHours(-72), expiresAt));
            await session.SaveChangesAsync(cts.Token);
        }

        InviteLedgerRecord expiredRecord = new(secretHash, InviteClaimKind.NodeOfOwner, Role: null, expiresAt, Spent: false);
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, InviteLedgerRecord.RefName(root), InviteLedgerRecord.PathFor(root, inviteId), expiredRecord.ToYaml(),
                ExpectedBlobId: null, "seed expired invite", new LedgerCommitter("Test Node", "node@test.local"),
                new LedgerSigningKey("/does/not/matter/key")),
            cts.Token);

        ProjectDetails joinerProject = await SeedProjectAsync(cts.Token);
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            Func<Task> act = () => ProjectJoinCommand.RunAsync(
                session, joinerProject, claimedOwnerOverride: null, invite: secret, ledger, new NodeKeyStore(),
                GitHubAccessFakes.GrantingPush(), cts.Token);
            (await act.Should().ThrowAsync<DomainValidationException>()).WithMessage("*expired*");
        }

        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, cts.Token);
        InviteSweepEngine engine = new(_postgres.Store, node, ledger, new NodeKeyStore(), NullLogger<InviteSweepEngine>.Instance);
        (await engine.SweepOnceAsync(cts.Token)).InvitesSpent.Should().Be(0, "an expired invite is filtered out of the outstanding query before any candidate is read");
    }

    [Fact]
    public async Task A_member_of_project_invite_with_no_existing_root_gets_the_joiners_own_root_at_join()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        FakeLedger ledger = new();

        ProjectDetails project = await SeedProjectAsync(cts.Token);
        ProjectJoinCommand.JoinOutcome genesis;
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            genesis = await ProjectJoinCommand.RunAsync(
                session, project, claimedOwnerOverride: null, invite: null, ledger, new NodeKeyStore(),
                GitHubAccessFakes.GrantingPush(), cts.Token);
        }

        string root = genesis.ClaimedOwnerFingerprint;
        Guid inviteId = DomainId.New();
        string secret = InviteSecret.Generate(root, inviteId);
        string secretHash = InviteSecret.Hash(secret);
        InviteLedgerRecord record = new(secretHash, InviteClaimKind.MemberOfProject, ProjectMemberRole.Member, Now.AddHours(72), Spent: false);
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, InviteLedgerRecord.RefName(root), InviteLedgerRecord.PathFor(root, inviteId), record.ToYaml(),
                ExpectedBlobId: null, "seed member-of-project invite", new LedgerCommitter("Test Node", "node@test.local"),
                new LedgerSigningKey("/does/not/matter/key")),
            cts.Token);

        await WipeAsync();
        ProjectDetails joinerProject = await SeedProjectAsync(cts.Token);
        ProjectJoinCommand.JoinOutcome joinOutcome;
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            joinOutcome = await ProjectJoinCommand.RunAsync(
                session, joinerProject, claimedOwnerOverride: null, invite: secret, ledger, new NodeKeyStore(),
                GitHubAccessFakes.GrantingPush(), cts.Token);
        }

        joinOutcome.EstablishedRoot.Should().BeTrue("a member-of-project invite creates the joiner's own root when it has none yet");
        joinOutcome.ClaimedOwnerFingerprint.Should().Be(joinOutcome.KeyFingerprint);
    }

    [Fact]
    public async Task Project_invite_refuses_a_member_role_owner()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        FakeLedger ledger = new();

        ProjectDetails project = await SeedProjectAsync(cts.Token);
        (string myRoot, NodeSigningKey myKey) = await EstablishOwnRootAsync(cts.Token);

        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myRoot] = new(myRoot, myKey.PublicKeyLine, []) },
            [new ProjectMember(myRoot, MembershipRole.Member, Now)]));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Func<Task> act = () => ProjectInviteCommand.RunAsync(
            session, project, roleInput: null, ledger, chainReader, new NodeKeyStore(), cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>()).WithMessage("*owner role*");
        ledger.Writes.Should().BeEmpty("a member-role owner is refused before any push");
    }

    [Fact]
    public async Task A_member_of_project_invite_round_trips_through_the_sweep_and_ends_verified()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        FakeLedger ledger = new();

        ProjectDetails project = await SeedProjectAsync(cts.Token);
        (string myRoot, NodeSigningKey myKey) = await EstablishOwnRootAsync(cts.Token);
        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myRoot] = new(myRoot, myKey.PublicKeyLine, []) },
            [new ProjectMember(myRoot, MembershipRole.Owner, Now)]));

        string secret;
        Guid inviteId;
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            int exitCode = await ProjectInviteCommand.RunAsync(
                session, project, roleInput: "member", ledger, chainReader, new NodeKeyStore(), cts.Token);
            exitCode.Should().Be(ExitCodes.Ok);

            InviteDetails minted = (await session.Query<InviteDetails>().ToListAsync(cts.Token)).Single();
            minted.Claim.Should().Be(InviteClaimKind.MemberOfProject);
            minted.Spent.Should().BeFalse();
            inviteId = minted.Id;

            InviteAggregate aggregate = (await session.Events.AggregateStreamAsync<InviteAggregate>(inviteId, token: cts.Token))!;
            secret = aggregate.Secret;
        }

        // The joiner: a brand-new member with no existing root, proving possession purely through
        // its own self-announced node file — the same "target node is ledger-only" pattern the
        // node-of-owner tests already use.
        Guid joinerNodeId = DomainId.New();
        NodeSigningKey joinerKey = await new NodeKeyStore().EnsureAsync(joinerNodeId, cts.Token);
        string proof = InviteSecret.ComputeProof(secret, joinerKey.Fingerprint);
        await WriteSelfAnnouncedNodeFileAsync(ledger, joinerNodeId, joinerKey, ownerFingerprint: joinerKey.Fingerprint, inviteProof: proof, cts.Token);

        // The minting node's own daemon sweep — same local identity as the mint phase above.
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, cts.Token);
        InviteSweepEngine engine = new(_postgres.Store, node, ledger, new NodeKeyStore(), NullLogger<InviteSweepEngine>.Instance);

        InviteSweepResult sweep = await engine.SweepOnceAsync(cts.Token);
        sweep.InvitesSpent.Should().Be(1, "the joiner's own proof matches the one outstanding member-of-project invite");

        LedgerWriteRequest memberWrite = ledger.Writes.Single(w => w.Path == $"members/{joinerKey.Fingerprint}.yaml");
        memberWrite.Content.Should().Contain($"role: \"{ProjectMemberRole.Member.Value}\"");

        LedgerFile invitesFile = await ledger.ReadAsync(
            RepositoryPath, InviteLedgerRecord.RefName(myRoot), InviteLedgerRecord.PathFor(myRoot, inviteId), cts.Token);
        InviteLedgerRecord? spentRecord = InviteLedgerRecord.Parse(invitesFile.Content);
        spentRecord!.Spent.Should().BeTrue("the ledger's own record, not only the local one, ends up marked spent");

        await using IDocumentSession assertSession = _postgres.Store.LightweightSession();
        InviteDetails after = (await assertSession.LoadAsync<InviteDetails>(inviteId, cts.Token))!;
        after.Spent.Should().BeTrue();
        after.ClaimedByRootFingerprint.Should().Be(joinerKey.Fingerprint);
    }

    [Fact]
    public async Task A_member_of_project_invite_retries_after_its_own_spend_record_write_fails()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        FakeLedger ledger = new();

        ProjectDetails project = await SeedProjectAsync(cts.Token);
        (string myRoot, NodeSigningKey myKey) = await EstablishOwnRootAsync(cts.Token);
        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myRoot] = new(myRoot, myKey.PublicKeyLine, []) },
            [new ProjectMember(myRoot, MembershipRole.Owner, Now)]));

        string secret;
        Guid inviteId;
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            int exitCode = await ProjectInviteCommand.RunAsync(
                session, project, roleInput: "member", ledger, chainReader, new NodeKeyStore(), cts.Token);
            exitCode.Should().Be(ExitCodes.Ok);

            InviteDetails minted = (await session.Query<InviteDetails>().ToListAsync(cts.Token)).Single();
            inviteId = minted.Id;

            InviteAggregate aggregate = (await session.Events.AggregateStreamAsync<InviteAggregate>(inviteId, token: cts.Token))!;
            secret = aggregate.Secret;
        }

        Guid joinerNodeId = DomainId.New();
        NodeSigningKey joinerKey = await new NodeKeyStore().EnsureAsync(joinerNodeId, cts.Token);
        string proof = InviteSecret.ComputeProof(secret, joinerKey.Fingerprint);
        await WriteSelfAnnouncedNodeFileAsync(ledger, joinerNodeId, joinerKey, ownerFingerprint: joinerKey.Fingerprint, inviteProof: proof, cts.Token);

        // The member vouch itself lands for real; only the spend-record write that follows it
        // fails — the exact partial-failure shape this invite's own local InviteProjectVouched
        // record exists to survive on retry, rather than reading its own prior write back as a
        // foreign entry and wedging forever (independent pre-PR review, cycle 5, verify pass,
        // medium — this shape had no regression test even though VouchMemberAsync's guard was
        // rewritten for it).
        string memberPath = $"members/{joinerKey.Fingerprint}.yaml";
        string spendPath = InviteLedgerRecord.PathFor(myRoot, inviteId);
        FailFirstWriteLedger flakyLedger = new(ledger, spendPath);

        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, cts.Token);
        InviteSweepEngine engine = new(_postgres.Store, node, flakyLedger, new NodeKeyStore(), NullLogger<InviteSweepEngine>.Instance);

        InviteSweepResult firstTick = await engine.SweepOnceAsync(cts.Token);
        firstTick.InvitesSpent.Should().Be(0, "the spend-record write failed after the member vouch had already landed");

        LedgerFile memberFileAfterFirstTick = await ledger.ReadAsync(RepositoryPath, "refs/hall9k/ledger/members", memberPath, cts.Token);
        memberFileAfterFirstTick.Exists.Should().BeTrue("the member vouch itself landed before the spend-record write failed");

        InviteSweepResult secondTick = await engine.SweepOnceAsync(cts.Token);
        secondTick.InvitesSpent.Should().Be(
            1, "the retry must recognize its own already-landed member write by its own local InviteProjectVouched record rather than refusing it as a foreign entry");

        ledger.Writes.Where(w => w.Path == memberPath).Should().ContainSingle(
            "the retry's own member write reproduces byte-identical content and so never pushes a second, redundant commit");
        ledger.Writes.Should().Contain(w => w.Path == spendPath);

        await using IDocumentSession assertSession = _postgres.Store.LightweightSession();
        InviteDetails after = (await assertSession.LoadAsync<InviteDetails>(inviteId, cts.Token))!;
        after.Spent.Should().BeTrue();
        after.ClaimedByRootFingerprint.Should().Be(joinerKey.Fingerprint);
    }

    [Fact]
    public async Task A_member_of_project_invite_refuses_to_overwrite_an_existing_member()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        FakeLedger ledger = new();

        ProjectDetails project = await SeedProjectAsync(cts.Token);
        (string myRoot, NodeSigningKey myKey) = await EstablishOwnRootAsync(cts.Token);

        // An existing owner-role member already in this project's own ledger, distinct from the
        // minting node's own root — the invite holder below tries to redirect the grant onto it.
        NodeSigningKey victimKey = await new NodeKeyStore().EnsureAsync(DomainId.New(), cts.Token);
        string victimRoot = victimKey.Fingerprint;
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, "refs/hall9k/ledger/members", $"members/{victimRoot}.yaml",
                $"root_fingerprint: \"{victimRoot}\"\nrole: \"{ProjectMemberRole.Owner.Value}\"\nissued_at: \"{Now:O}\"\n",
                ExpectedBlobId: null, "seed existing owner member", new LedgerCommitter("Test Node", "node@test.local"),
                new LedgerSigningKey("/does/not/matter/key")),
            cts.Token);

        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myRoot] = new(myRoot, myKey.PublicKeyLine, []) },
            [new ProjectMember(myRoot, MembershipRole.Owner, Now), new ProjectMember(victimRoot, MembershipRole.Owner, Now)]));

        string secret;
        Guid inviteId;
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            int exitCode = await ProjectInviteCommand.RunAsync(
                session, project, roleInput: "member", ledger, chainReader, new NodeKeyStore(), cts.Token);
            exitCode.Should().Be(ExitCodes.Ok);

            InviteDetails minted = (await session.Query<InviteDetails>().ToListAsync(cts.Token)).Single();
            inviteId = minted.Id;
            InviteAggregate aggregate = (await session.Events.AggregateStreamAsync<InviteAggregate>(inviteId, token: cts.Token))!;
            secret = aggregate.Secret;
        }

        // The invite holder's own key checks out (the HMAC proof matches), but it self-declares
        // the VICTIM's own root fingerprint as owner_fingerprint in its node file — exactly the
        // unverified redirection the sweep must refuse rather than silently honor.
        Guid joinerNodeId = DomainId.New();
        NodeSigningKey joinerKey = await new NodeKeyStore().EnsureAsync(joinerNodeId, cts.Token);
        string proof = InviteSecret.ComputeProof(secret, joinerKey.Fingerprint);
        await WriteSelfAnnouncedNodeFileAsync(ledger, joinerNodeId, joinerKey, ownerFingerprint: victimRoot, inviteProof: proof, cts.Token);

        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, cts.Token);
        InviteSweepEngine engine = new(_postgres.Store, node, ledger, new NodeKeyStore(), NullLogger<InviteSweepEngine>.Instance);

        InviteSweepResult sweep = await engine.SweepOnceAsync(cts.Token);
        sweep.InvitesSpent.Should().Be(0, "the sweep refuses to overwrite an existing member's own role file");

        LedgerFile victimFile = await ledger.ReadAsync(RepositoryPath, "refs/hall9k/ledger/members", $"members/{victimRoot}.yaml", cts.Token);
        victimFile.Content.Should().Contain($"role: \"{ProjectMemberRole.Owner.Value}\"", "the existing member's own role must survive untouched");

        await using IDocumentSession assertSession = _postgres.Store.LightweightSession();
        InviteDetails after = (await assertSession.LoadAsync<InviteDetails>(inviteId, cts.Token))!;
        after.Spent.Should().BeFalse("the invite stays outstanding so a legitimate holder can still be retried");
    }

    /// <summary>Mints a node-of-owner invite where the caller's own single local identity plays
    /// both the root and the minting node — the root's own key always counts as "enrolled" under
    /// its own chain, so this needs no second device at all, unlike the full three-node test.</summary>
    private async Task<(string Root, Guid InviteId, string Secret, NodeSigningKey RootKey)> MintNodeOfOwnerInviteAsSelfAsync(
        FakeLedger ledger, CancellationToken cancellationToken)
    {
        _ = await SeedProjectAsync(cancellationToken);
        (string root, NodeSigningKey rootKey) = await EstablishOwnRootAsync(cancellationToken);
        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [root] = new(root, rootKey.PublicKeyLine, []) }, []));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        int exitCode = await NodeInviteCommand.RunAsync(session, ledger, chainReader, new NodeKeyStore(), cancellationToken);
        exitCode.Should().Be(ExitCodes.Ok);

        InviteDetails minted = (await session.Query<InviteDetails>().ToListAsync(cancellationToken)).Single();
        InviteAggregate aggregate = (await session.Events.AggregateStreamAsync<InviteAggregate>(minted.Id, token: cancellationToken))!;
        return (root, minted.Id, aggregate.Secret, rootKey);
    }

    /// <summary>Bootstraps this node's owner and claims this node's own key as its root, verified
    /// — the same shape <c>h9k project join</c> with no <c>--owner</c> records — without touching
    /// a real ledger (mirrors <c>NodeVouchAndRevokeCommandTests.EstablishOwnRootAsync</c>).</summary>
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

    /// <summary>What a joining node's own <c>h9k project join --invite</c> would have written into
    /// its own self-announced node file — mirrors <c>ProjectJoinCommand.WriteNodeFileAsync</c>'s
    /// exact field shape, written directly so a node playing this role needs no local Postgres
    /// identity of its own (the same "target node is ledger-only" pattern
    /// <c>NodeVouchAndRevokeCommandTests.SeedNodeFileAsync</c> already establishes).</summary>
    private static async Task WriteSelfAnnouncedNodeFileAsync(
        FakeLedger ledger, Guid nodeId, NodeSigningKey key, string ownerFingerprint, string inviteProof,
        CancellationToken cancellationToken)
    {
        string refName = $"refs/hall9k/ledger/nodes/{nodeId}";
        string path = $"nodes/{nodeId}/node.yaml";
        string content =
            $"node_id: \"{nodeId}\"\n"
            + $"public_key: \"{key.PublicKeyLine}\"\n"
            + $"key_fingerprint: \"{key.Fingerprint}\"\n"
            + $"owner_fingerprint: \"{ownerFingerprint}\"\n"
            + "machine_name: \"joiner-machine\"\n"
            + "operating_system: \"linux\"\n"
            + $"joined_at: \"{Now:O}\"\n"
            + $"invite_proof: \"{inviteProof}\"\n";
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, refName, path, content, ExpectedBlobId: null, "seed self-announced node file",
                new LedgerCommitter("Test Node", "node@test.local"), new LedgerSigningKey("/does/not/matter/key")),
            cancellationToken);
    }

    private async Task WipeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

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

    /// <summary>An <see cref="ILedger"/> that throws <see cref="LedgerPushRejectedException"/> the
    /// first time (and only the first time) anything writes to <paramref name="failPath"/>, then
    /// delegates every call, including that same path's own retry, straight to
    /// <paramref name="inner"/> — the "transient push failure, succeeds on retry" shape
    /// <see cref="InviteSweepEngine"/>'s own catch clause is written to tolerate.</summary>
    private sealed class FailFirstWriteLedger(ILedger inner, string failPath) : ILedger
    {
        private bool _alreadyFailed;

        public Task<LedgerFile> ReadAsync(string repositoryPath, string refName, string path, CancellationToken cancellationToken) =>
            inner.ReadAsync(repositoryPath, refName, path, cancellationToken);

        public Task<LedgerWriteOutcome> WriteAsync(LedgerWriteRequest request, CancellationToken cancellationToken)
        {
            if (!_alreadyFailed && request.Path == failPath)
            {
                _alreadyFailed = true;
                throw new LedgerPushRejectedException(request.RefName, attempts: 5, gitError: "simulated transient push failure");
            }

            return inner.WriteAsync(request, cancellationToken);
        }

        public Task<LedgerWriteOutcome> DeleteAsync(LedgerDeleteRequest request, CancellationToken cancellationToken) =>
            inner.DeleteAsync(request, cancellationToken);

        public Task<bool> HasAnyAsync(string repositoryPath, string refName, string pathPrefix, CancellationToken cancellationToken) =>
            inner.HasAnyAsync(repositoryPath, refName, pathPrefix, cancellationToken);

        public Task<IReadOnlyList<LedgerRef>> ListRefsAsync(string repositoryPath, string refPrefix, CancellationToken cancellationToken) =>
            inner.ListRefsAsync(repositoryPath, refPrefix, cancellationToken);
    }
}
