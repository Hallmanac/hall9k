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
/// <c>h9k project member reaffirm</c> (idea 6be68ee2) against a real Marten/Postgres session —
/// Owner, Node, and Project are all real event streams — but never a real git repository:
/// <see cref="FakeLedger"/> stands in for A1 and <see cref="FakeLedgerChainReader"/> stands in for
/// the chain reader throughout, per Brian's 2026-09-13 testing rule.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class ProjectMemberReaffirmCommandTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private const string RepositoryPath = "/does/not/matter/on/a/fake/ledger";
    private const string MembersRefName = "refs/hall9k/ledger/members";

    private readonly PostgresFixture _postgres;
    private readonly ScopedTestHome _scopedHome = new();

    public ProjectMemberReaffirmCommandTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync()
    {
        _scopedHome.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task The_root_reaffirms_a_member_with_a_fresh_root_signed_content_changing_commit()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        FakeLedger ledger = new();
        ProjectDetails project = await SeedProjectAsync(cts.Token);
        (string myRoot, NodeSigningKey myKey) = await EstablishOwnRootAsync(cts.Token);

        DateTimeOffset originalIssuedAt = Now.AddDays(-30);
        string targetFingerprint = new('b', 64);
        await WriteMemberFileAsync(ledger, targetFingerprint, "member", originalIssuedAt, cts.Token);

        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myRoot] = new(myRoot, myKey.PublicKeyLine, []) },
            [new ProjectMember(myRoot, MembershipRole.Owner, Now), new ProjectMember(targetFingerprint, MembershipRole.Member, originalIssuedAt)]));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        int exitCode = await ProjectMemberReaffirmCommand.RunAsync(
            session, project, targetFingerprint, ledger, chainReader, new NodeKeyStore(), cts.Token);

        exitCode.Should().Be(ExitCodes.Ok);
        LedgerWriteRequest write = ledger.Writes.Last(w => w.RefName == MembersRefName && w.Path == $"members/{targetFingerprint}.yaml");
        write.Content.Should().Contain($"root_fingerprint: \"{targetFingerprint}\"").And.Contain("role: \"member\"");
        write.Content.Should().NotContain(
            originalIssuedAt.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            "issued_at is bumped so the rewrite is content-changing — a same-role re-invite would write byte-identical content and make no commit at all");
        write.SigningKey.Should().NotBeNull("the reaffirm commit is signed the same as every other ledger write");
    }

    [Fact]
    public async Task Reaffirm_refuses_when_the_raw_files_role_differs_from_the_chains_own_authorized_role()
    {
        // Independent pre-PR review, cycle 4, conformance and adversarial lenses, high: a compromised,
        // merely vouched fleet node can rewrite members/<fingerprint>.yaml's own role — a write the
        // stricter reader refuses and chain.Members never reflects — so reaffirm must never root-sign
        // whatever role currently sits at the ledger's tip when the chain's own authorized view
        // already has an opinion about this fingerprint's role that disagrees with it.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        FakeLedger ledger = new();
        ProjectDetails project = await SeedProjectAsync(cts.Token);
        (string myRoot, NodeSigningKey myKey) = await EstablishOwnRootAsync(cts.Token);

        string targetFingerprint = new('b', 64);
        // The chain's own authorized read still has the target as "member", but the raw tip file — a
        // compromised node's own tampered write — now declares "owner".
        await WriteMemberFileAsync(ledger, targetFingerprint, "owner", Now.AddDays(-1), cts.Token);

        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myRoot] = new(myRoot, myKey.PublicKeyLine, []) },
            [new ProjectMember(myRoot, MembershipRole.Owner, Now), new ProjectMember(targetFingerprint, MembershipRole.Member, Now.AddDays(-30))]));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Func<Task> act = () => ProjectMemberReaffirmCommand.RunAsync(
            session, project, targetFingerprint, ledger, chainReader, new NodeKeyStore(), cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>()).WithMessage("*role*");
        ledger.Writes.Should().HaveCount(1, "only the seed write (the tampered file itself) landed; "
            + "reaffirm never root-signs a role change no live root key ever authorized");
    }

    [Fact]
    public async Task Reaffirm_falls_back_to_the_raw_role_when_the_fingerprint_is_absent_from_the_chains_own_members()
    {
        // Commit f035ee73a's own case: a member whose own last write was signed by a node that is now
        // merely vouched is refused by the stricter reader and so never reaches chain.Members at all —
        // reaffirm must still be able to re-land that exact file, root-signed, from its raw role.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        FakeLedger ledger = new();
        ProjectDetails project = await SeedProjectAsync(cts.Token);
        (string myRoot, NodeSigningKey myKey) = await EstablishOwnRootAsync(cts.Token);

        string targetFingerprint = new('b', 64);
        await WriteMemberFileAsync(ledger, targetFingerprint, "member", Now.AddDays(-30), cts.Token);

        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myRoot] = new(myRoot, myKey.PublicKeyLine, []) },
            [new ProjectMember(myRoot, MembershipRole.Owner, Now)]));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        int exitCode = await ProjectMemberReaffirmCommand.RunAsync(
            session, project, targetFingerprint, ledger, chainReader, new NodeKeyStore(), cts.Token);

        exitCode.Should().Be(ExitCodes.Ok);
        LedgerWriteRequest write = ledger.Writes.Last(w => w.RefName == MembersRefName && w.Path == $"members/{targetFingerprint}.yaml");
        write.Content.Should().Contain("role: \"member\"");
    }

    [Fact]
    public async Task Reaffirm_preserves_the_project_key_on_the_genesis_fingerprints_own_file()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        FakeLedger ledger = new();
        ProjectDetails project = await SeedProjectAsync(cts.Token);
        (string myRoot, NodeSigningKey myKey) = await EstablishOwnRootAsync(cts.Token);
        await WriteMemberFileAsync(ledger, myRoot, "owner", Now.AddDays(-90), cts.Token);

        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myRoot] = new(myRoot, myKey.PublicKeyLine, []) },
            [new ProjectMember(myRoot, MembershipRole.Owner, Now)],
            GenesisRootFingerprint: myRoot, ProjectKey: "01ARZ3NDEKTSV4RRFFQ69G5FAV"));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        int exitCode = await ProjectMemberReaffirmCommand.RunAsync(
            session, project, myRoot, ledger, chainReader, new NodeKeyStore(), cts.Token);

        exitCode.Should().Be(ExitCodes.Ok);
        LedgerWriteRequest write = ledger.Writes.Last(w => w.RefName == MembersRefName && w.Path == $"members/{myRoot}.yaml");
        write.Content.Should().Contain("project_key: \"01ARZ3NDEKTSV4RRFFQ69G5FAV\"", "the genesis fingerprint's own project_key must survive a reaffirm");
    }

    [Fact]
    public async Task Reaffirm_refuses_before_any_push_when_this_owner_does_not_hold_the_owner_role()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        FakeLedger ledger = new();
        ProjectDetails project = await SeedProjectAsync(cts.Token);
        (string myRoot, NodeSigningKey myKey) = await EstablishOwnRootAsync(cts.Token);

        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myRoot] = new(myRoot, myKey.PublicKeyLine, []) },
            [new ProjectMember(myRoot, MembershipRole.Member, Now)]));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Func<Task> act = () => ProjectMemberReaffirmCommand.RunAsync(
            session, project, new string('a', 64), ledger, chainReader, new NodeKeyStore(), cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>()).WithMessage("*owner role*");
        ledger.Writes.Should().BeEmpty("a member-role owner is refused before any push");
    }

    [Fact]
    public async Task Reaffirm_refuses_before_any_push_when_this_nodes_key_is_not_a_live_root_key()
    {
        // idea 6be68ee2, trust-ledger finding 2: this node's own owner DOES hold the owner role,
        // but this node's own key is merely vouched into that owner's chain, never the owner's own
        // live root key — the whole reason this command exists (re-landing a member file whose
        // last write was signed by a node like this one) must itself be root-only. Never checked
        // against the identity fingerprint or TrustChain.RootNodeId (null on an older ledger).
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        FakeLedger ledger = new();
        ProjectDetails project = await SeedProjectAsync(cts.Token);

        Guid nodeId;
        NodeSigningKey myKey;
        NodeSigningKey rootKey = await new NodeKeyStore().EnsureAsync(Guid.NewGuid(), cts.Token);
        await using (IDocumentSession claimSession = _postgres.Store.LightweightSession())
        {
            BootstrapContext context = await NodeBootstrap.EnsureAsync(claimSession, cts.Token);
            await claimSession.SaveChangesAsync(cts.Token);
            nodeId = context.NodeId;
            myKey = await new NodeKeyStore().EnsureAsync(context.NodeId, cts.Token);

            OwnerAggregate owner = await claimSession.Events.AggregateStreamAsync<OwnerAggregate>(context.OwnerId, token: cts.Token)
                ?? throw new InvalidOperationException("Owner bootstrap did not create an owner stream.");
            claimSession.Events.Append(context.OwnerId, OwnerDecider.ClaimRoot(owner, rootKey.Fingerprint, verified: true, Now));
            await claimSession.SaveChangesAsync(cts.Token);
        }

        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner>
            {
                [rootKey.Fingerprint] = new(
                    rootKey.Fingerprint, rootKey.PublicKeyLine,
                    [new TrustedNode(nodeId.ToString(), myKey.PublicKeyLine, myKey.Fingerprint, Now)]),
            },
            [new ProjectMember(rootKey.Fingerprint, MembershipRole.Owner, Now)]));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Func<Task> act = () => ProjectMemberReaffirmCommand.RunAsync(
            session, project, new string('a', 64), ledger, chainReader, new NodeKeyStore(), cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>()).WithMessage("*root key*");
        ledger.Writes.Should().BeEmpty("this node is merely vouched, never a live root key, so nothing is pushed");
    }

    [Fact]
    public async Task Reaffirm_refuses_a_fingerprint_that_is_not_currently_a_member()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        FakeLedger ledger = new();
        ProjectDetails project = await SeedProjectAsync(cts.Token);
        (string myRoot, NodeSigningKey myKey) = await EstablishOwnRootAsync(cts.Token);

        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myRoot] = new(myRoot, myKey.PublicKeyLine, []) },
            [new ProjectMember(myRoot, MembershipRole.Owner, Now)]));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Func<Task> act = () => ProjectMemberReaffirmCommand.RunAsync(
            session, project, new string('c', 64), ledger, chainReader, new NodeKeyStore(), cts.Token);

        await act.Should().ThrowAsync<DomainValidationException>();
        ledger.Writes.Should().BeEmpty();
    }

    private static async Task WriteMemberFileAsync(
        FakeLedger ledger, string fingerprint, string role, DateTimeOffset issuedAt, CancellationToken cancellationToken)
    {
        string path = $"members/{fingerprint}.yaml";
        string content =
            $"root_fingerprint: \"{fingerprint}\"\nrole: \"{role}\"\n"
            + $"issued_at: \"{issuedAt.ToString("o", System.Globalization.CultureInfo.InvariantCulture)}\"\n";
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, MembersRefName, path, content, ExpectedBlobId: null, "seed member",
                new LedgerCommitter("Test", "test@test.local"), new LedgerSigningKey("/does/not/matter/key")),
            cancellationToken);
    }

    /// <summary>Bootstraps this node's owner and claims this node's own key as its root, verified
    /// — the same shape <c>h9k project join</c> with no <c>--owner</c> records — without touching
    /// a real ledger.</summary>
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
