using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <c>h9k node vouch</c> and <c>h9k node revoke</c> (idea 202383dc, T1) against a real
/// Marten/Postgres session — Owner and Node are real event streams — but never a real git
/// repository: <see cref="FakeLedger"/> stands in for A1 and <see cref="FakeLedgerChainReader"/>
/// stands in for the chain reader, per Brian's 2026-09-13 testing rule (only <c>GitLedgerTests</c>
/// and the chain reader's own tests touch a real repository).
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection("Hall9kHome")]
public sealed class NodeVouchAndRevokeCommandTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private const string RepositoryPath = "/does/not/matter/on/a/fake/ledger";

    private readonly PostgresFixture _postgres;
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"hall9k-node-vouch-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    public NodeVouchAndRevokeCommandTests(PostgresFixture postgres) => _postgres = postgres;

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
    public async Task Vouch_refuses_before_any_push_when_this_node_is_not_enrolled_in_the_owner_chain()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        await SeedProjectAsync(cts.Token);
        FakeLedger ledger = new();
        FakeLedgerChainReader chainReader = new(TrustChain.Empty);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        await EstablishOwnRootAsync(session, cts.Token);

        NodeVouchCommand.Settings settings = new() { NodeId = Guid.NewGuid().ToString() };
        Func<Task> act = () => NodeVouchCommand.RunAsync(session, settings, ledger, chainReader, new NodeKeyStore(), cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>()).WithMessage("*enrolled*");
        ledger.Writes.Should().BeEmpty("a writer not yet enrolled in the owner's own chain is refused before any push");
    }

    [Fact]
    public async Task Vouch_writes_the_vouch_file_and_prints_the_targets_own_fingerprint_when_enrolled()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        await SeedProjectAsync(cts.Token);
        FakeLedger ledger = new();

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (string myFingerprint, string myPublicKeyLine) = await EstablishOwnRootAsync(session, cts.Token);
        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myFingerprint] = new(myFingerprint, myPublicKeyLine, []) }, []));

        Guid targetNodeId = Guid.NewGuid();
        NodeSigningKey targetKey = await new NodeKeyStore().EnsureAsync(targetNodeId, cts.Token);
        await SeedNodeFileAsync(ledger, targetNodeId, targetKey.PublicKeyLine);

        NodeVouchCommand.Settings settings = new() { NodeId = targetNodeId.ToString() };
        int exitCode = await NodeVouchCommand.RunAsync(session, settings, ledger, chainReader, new NodeKeyStore(), cts.Token);

        exitCode.Should().Be(ExitCodes.Ok);
        LedgerWriteRequest vouchWrite = ledger.Writes.Single(w => w.RefName == $"refs/hall9k/ledger/owners/{myFingerprint}");
        vouchWrite.Path.Should().Be($"owners/{myFingerprint}/nodes/{targetNodeId}.yaml");
        vouchWrite.Content.Should().Contain(targetKey.PublicKeyLine);

        OwnerDetails owner = (await session.LoadAsync<OwnerDetails>((await NodeBootstrap.EnsureAsync(session, cts.Token)).OwnerId, cts.Token))!;
        owner.VouchedNodes.Should().ContainKey(targetNodeId);
    }

    [Fact]
    public async Task Vouch_refuses_when_the_target_has_never_joined_any_of_this_owners_projects()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        await SeedProjectAsync(cts.Token);
        FakeLedger ledger = new();

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (string myFingerprint, string myPublicKeyLine) = await EstablishOwnRootAsync(session, cts.Token);
        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myFingerprint] = new(myFingerprint, myPublicKeyLine, []) }, []));

        NodeVouchCommand.Settings settings = new() { NodeId = Guid.NewGuid().ToString() };
        Func<Task> act = () => NodeVouchCommand.RunAsync(session, settings, ledger, chainReader, new NodeKeyStore(), cts.Token);

        await act.Should().ThrowAsync<DomainValidationException>();
        ledger.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Revoke_refuses_before_any_push_when_this_node_is_not_enrolled_in_the_owner_chain()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        await SeedProjectAsync(cts.Token);
        FakeLedger ledger = new();
        FakeLedgerChainReader chainReader = new(TrustChain.Empty);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        await EstablishOwnRootAsync(session, cts.Token);

        NodeRevokeCommand.Settings settings = new() { NodeId = Guid.NewGuid().ToString() };
        Func<Task> act = () => NodeRevokeCommand.RunAsync(session, settings, ledger, chainReader, new NodeKeyStore(), cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>()).WithMessage("*enrolled*");
        ledger.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Revoke_writes_the_revocation_file_when_enrolled()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        await SeedProjectAsync(cts.Token);
        FakeLedger ledger = new();

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (string myFingerprint, string myPublicKeyLine) = await EstablishOwnRootAsync(session, cts.Token);
        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myFingerprint] = new(myFingerprint, myPublicKeyLine, []) }, []));

        Guid targetNodeId = Guid.NewGuid();
        NodeRevokeCommand.Settings settings = new() { NodeId = targetNodeId.ToString() };
        int exitCode = await NodeRevokeCommand.RunAsync(session, settings, ledger, chainReader, new NodeKeyStore(), cts.Token);

        exitCode.Should().Be(ExitCodes.Ok);
        LedgerWriteRequest revokeWrite = ledger.Writes.Single(w => w.RefName == $"refs/hall9k/ledger/owners/{myFingerprint}");
        revokeWrite.Path.Should().Be($"owners/{myFingerprint}/revoked/{targetNodeId}.yaml");
    }

    [Fact]
    public async Task Vouch_still_succeeds_and_reports_the_landed_project_when_a_second_project_refuses_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        await SeedProjectAsync(cts.Token);
        const string secondRepositoryPath = "/does/not/matter/on/a/fake/ledger/second";
        await SeedSecondProjectAsync(secondRepositoryPath, cts.Token);
        FakeLedger ledger = new();

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (string myFingerprint, string myPublicKeyLine) = await EstablishOwnRootAsync(session, cts.Token);

        // Enrolled in the first project's own copy of the chain, but not yet in the second's — a
        // real shape (each project's ledger is independent), not a hypothetical.
        FakeLedgerChainReader chainReader = new(new Dictionary<string, TrustChain>
        {
            [RepositoryPath] = new(
                new Dictionary<string, TrustedOwner> { [myFingerprint] = new(myFingerprint, myPublicKeyLine, []) }, []),
            [secondRepositoryPath] = TrustChain.Empty,
        });

        Guid targetNodeId = Guid.NewGuid();
        NodeSigningKey targetKey = await new NodeKeyStore().EnsureAsync(targetNodeId, cts.Token);
        await SeedNodeFileAsync(ledger, targetNodeId, targetKey.PublicKeyLine);

        NodeVouchCommand.Settings settings = new() { NodeId = targetNodeId.ToString() };
        int exitCode = await NodeVouchCommand.RunAsync(session, settings, ledger, chainReader, new NodeKeyStore(), cts.Token);

        exitCode.Should().Be(ExitCodes.Ok, "the refusal in the second project must not undo the success already landed in the first");
        ledger.Writes.Should().ContainSingle(w => w.RefName == $"refs/hall9k/ledger/owners/{myFingerprint}");

        OwnerDetails owner = (await session.LoadAsync<OwnerDetails>((await NodeBootstrap.EnsureAsync(session, cts.Token)).OwnerId, cts.Token))!;
        owner.VouchedNodes.Should().ContainKey(targetNodeId, "the vouch that did land must still be recorded locally");
    }

    private async Task SeedSecondProjectAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        await using IDocumentSession bootstrapSession = _postgres.Store.LightweightSession();
        BootstrapContext context = await NodeBootstrap.EnsureAsync(bootstrapSession, cancellationToken);
        await bootstrapSession.SaveChangesAsync(cancellationToken);

        Guid projectId = DomainId.New();
        await using IDocumentSession projectSession = _postgres.Store.LightweightSession();
        projectSession.Events.StartStream<ProjectAggregate>(
            projectId,
            ProjectDecider.Register(
                projectId, context.OwnerId, DomainId.New(), "smoke-second", repositoryPath, null, null, Now));
        await projectSession.SaveChangesAsync(cancellationToken);
    }

    private async Task SeedNodeFileAsync(FakeLedger ledger, Guid nodeId, string publicKeyLine)
    {
        string refName = $"refs/hall9k/ledger/nodes/{nodeId}";
        string path = $"nodes/{nodeId}/node.yaml";
        string content = $"node_id: \"{nodeId}\"\npublic_key: \"{publicKeyLine}\"\n";
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, refName, path, content, ExpectedBlobId: null, "seed node file",
                new LedgerCommitter("Test Node", "node@test.local"), new LedgerSigningKey("/does/not/matter/key")),
            CancellationToken.None);
    }

    /// <summary>Bootstraps this node's owner and claims this node's own key as its root — the
    /// same shape <c>h9k project join</c> with no <c>--owner</c> records — without touching a
    /// real ledger.</summary>
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

    private async Task SeedProjectAsync(CancellationToken cancellationToken)
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
                RepositoryPath, null, null, Now));
        await projectSession.SaveChangesAsync(cancellationToken);
    }
}
